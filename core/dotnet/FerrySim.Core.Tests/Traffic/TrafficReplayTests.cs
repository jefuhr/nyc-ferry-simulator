using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// The replay against an independent Python implementation's golden vectors (python_traffic.py), and its rules directly.
	/// Failure modes guarded:
	/// <list type="bullet">
	/// <item>The interpolation wrong at a segment's ends (the first and last report, a one-report segment) or in its middle.</item>
	/// <item>Invalid or inconsistent SOG, COG or heading used: Hermite where the rule says linear, an AIS heading where it is NaN,
	/// the long way round through north.</item>
	/// <item>A vessel present outside its segments: before its first report, after its last, in a gap, a microsecond past an end.</item>
	/// <item>A static vessel that moves, or one shown between its segments.</item>
	/// <item>The wrong time base: pack time not simulation time plus (scenario start − epoch).</item>
	/// <item>A non-deterministic or allocating query.</item>
	/// <item>A slow vessel without an AIS heading turned by GPS jitter (the tangent or the chord below the slow limit); its held
	/// heading taken from after the stretch instead of before it, from a COG reported below the limit, or as the linear median of
	/// COGs either side of north; a slow vessel with one AIS heading given the tangent instead of it.</item>
	/// <item>A vessel ruled out of an area it does enter (a Hermite curve that bulges in while its reports and chords stay out), or
	/// the bound so loose it rules nothing out.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class TrafficReplayTests
	{
		private string _root;
		private TrafficPack _pack;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "ferrysim-core-tests", Guid.NewGuid().ToString("N"));
			_pack = TrafficPackJson.Load(PythonTrafficFixture.Write(_root));
		}

		[TearDown]
		public void TearDown()
		{
			try
			{
				Directory.Delete(_root, true);
			}
			catch (IOException)
			{
			}
		}

		private static string VectorsPath => Path.Combine(RepoPaths.Root, "core", "dotnet", "FerrySim.Core.Tests", "Traffic", "PythonTrafficVectors.csv");

		private static readonly Dictionary<string, TrafficMotion> Motions = new Dictionary<string, TrafficMotion>
		{
			["static"] = TrafficMotion.Static, ["report"] = TrafficMotion.Report, ["hermite"] = TrafficMotion.Hermite, ["linear"] = TrafficMotion.Linear,
		};

		private static readonly Dictionary<string, HeadingSource> Sources = new Dictionary<string, HeadingSource>
		{
			["ais"] = HeadingSource.Ais, ["tangent"] = HeadingSource.Tangent, ["ais-held"] = HeadingSource.AisHeld, ["chord"] = HeadingSource.Chord, ["unknown"] = HeadingSource.Unknown,
			["ais-last"] = HeadingSource.LastAis, ["cog-median"] = HeadingSource.MedianCog,
		};

		[Test]
		public void EveryGoldenVector_MatchesPython()
		{
			var replay = new TrafficReplay(_pack);
			var lines = File.ReadAllLines(VectorsPath);
			Assert.That(lines[0], Is.EqualTo("vessel,mmsi,t_s,present,motion,heading_source,north_m,east_m,velocity_north_mps,velocity_east_mps,heading_rad"));
			var compared = 0;
			var seen = new HashSet<string>();
			for (var i = 1; i < lines.Length; i++)
			{
				var f = lines[i].Split(',');
				var vessel = int.Parse(f[0], CultureInfo.InvariantCulture);
				var t = double.Parse(f[2], CultureInfo.InvariantCulture);
				var label = $"vessel {vessel} ({f[1]}) at {f[2]} s";
				Assert.That(_pack.Vessels[vessel].Mmsi, Is.EqualTo(int.Parse(f[1], CultureInfo.InvariantCulture)), label);
				var present = replay.TrySample(vessel, t, out var sample);
				Assert.That(present, Is.EqualTo(f[3] == "True"), label + ": present");
				Assert.That(replay.IsPresent(vessel, t), Is.EqualTo(present), label + ": IsPresent agrees");
				if (!present)
				{
					compared++;
					continue;
				}

				Assert.That(sample.Motion, Is.EqualTo(Motions[f[4]]), label + ": motion");
				Assert.That(sample.HeadingSource, Is.EqualTo(Sources[f[5]]), label + ": heading source");
				Assert.That(sample.North, Is.EqualTo(double.Parse(f[6], CultureInfo.InvariantCulture)).Within(1e-6), label + ": north");
				Assert.That(sample.East, Is.EqualTo(double.Parse(f[7], CultureInfo.InvariantCulture)).Within(1e-6), label + ": east");
				Assert.That(sample.VelocityNorth, Is.EqualTo(double.Parse(f[8], CultureInfo.InvariantCulture)).Within(1e-9), label + ": velocity north");
				Assert.That(sample.VelocityEast, Is.EqualTo(double.Parse(f[9], CultureInfo.InvariantCulture)).Within(1e-9), label + ": velocity east");
				Assert.That(Angles.WrapPi(sample.Heading - double.Parse(f[10], CultureInfo.InvariantCulture)), Is.EqualTo(0.0).Within(1e-9), label + ": heading");
				Assert.That(sample.Heading, Is.GreaterThanOrEqualTo(0.0).And.LessThan(2.0 * Math.PI), label + ": heading in [0, 2π)");
				seen.Add(f[4] + "/" + f[5]);
				compared++;
			}

			Assert.That(compared, Is.EqualTo(lines.Length - 1));
			Assert.That(compared, Is.GreaterThan(400));
			Assert.That(seen, Is.SupersetOf(new[] { "hermite/ais", "hermite/tangent", "hermite/chord", "hermite/ais-held", "hermite/unknown", "linear/ais", "linear/ais-held", "linear/tangent", "linear/ais-last", "linear/cog-median",
				"report/ais", "report/tangent", "report/unknown", "static/ais", "static/unknown" }), "the vectors cover every motion and heading source");
		}

		[Test]
		public void ASlowStretchWithoutAisHeadings_HoldsTheLastValidOne_AndOneValidHeadingIsHeld_NotTheJitter()
		{
			var replay = new TrafficReplay(_pack);
			var berthing = _pack.Vessel(100000016);
			var reports = _pack.Reports;
			var r = berthing.FirstReport;
			// Reports 2 to 5 (12120 to 12300 s) are under 0.5 m/s with no AIS heading, their COGs jittering through 85°, 300°
			// and 170°: the stretch holds 89°, report 1's heading, the last valid one before it. (At 12300 s the interval from
			// report 5 to report 6, which has a heading, takes over.)
			for (var t = 12120.0; t < 12300.0; t += 0.5)
			{
				Assert.That(replay.TrySample(berthing.Index, t, out var held), Is.True);
				Assert.That((held.HeadingSource, held.Heading), Is.EqualTo((HeadingSource.LastAis, reports.Heading(r + 1))), $"at {t} s");
			}

			// 12300 to 12360 s: still slow, report 6 has a heading (91°): held, though the curve moves.
			Assert.That(replay.TrySample(berthing.Index, 12330.0, out var one), Is.True);
			Assert.That((one.HeadingSource, one.Heading), Is.EqualTo((HeadingSource.AisHeld, reports.Heading(r + 6))));
			// 12060 to 12120 s: arriving at 3 m/s, not slow: the old rule, the tangent.
			Assert.That(replay.TrySample(berthing.Index, 12090.0, out var arriving), Is.True);
			Assert.That(arriving.HeadingSource, Is.EqualTo(HeadingSource.Tangent));
			// The position is not the heading's business: the stretch still follows its reports.
			Assert.That(replay.TrySample(berthing.Index, 12180.0, out var at), Is.True);
			Assert.That((at.North, at.East), Is.EqualTo((reports.North(r + 3), reports.East(r + 3))));
		}

		[Test]
		public void WithNoAisHeadingAtAll_TheStretchTakesTheSegmentsMedianCogAtSpeed_ThroughNorth()
		{
			var replay = new TrafficReplay(_pack);
			var moored = _pack.Vessel(100000017);
			var reports = _pack.Reports;
			var r = moored.FirstReport;
			// At speed it reported COGs 350°, 10° and 20°; their median through north is 10° (a plain sort would give 20°). The
			// slow reports' COGs (250°, 120°, 300°) are jitter and take no part.
			for (var t = 13180.0; t <= 13360.0; t += 0.5)
			{
				Assert.That(replay.TrySample(moored.Index, t, out var held), Is.True);
				Assert.That(held.HeadingSource, Is.EqualTo(HeadingSource.MedianCog), $"at {t} s");
				Assert.That(Angles.WrapPi(held.Heading - reports.CourseOverGround(r + 1)), Is.EqualTo(0.0).Within(1e-12), $"at {t} s: 10°");
			}
		}

		[Test]
		public void NothingToGoBy_IsNorth_AndASlowLoneReportGetsNoTangent()
		{
			var replay = new TrafficReplay(_pack);
			// Crawling at 0.03 m/s, no heading, no COG at speed: north, where the chord (80°) used to turn it.
			var crawler = _pack.Vessel(100000009);
			for (var t = 8000.0; t <= 8060.0; t += 0.5)
			{
				Assert.That(replay.TrySample(crawler.Index, t, out var sample), Is.True);
				Assert.That((sample.HeadingSource, sample.Heading), Is.EqualTo((HeadingSource.Unknown, 0.0)), $"at {t} s");
			}

			// A one-report segment at 0.3 m/s with a COG but no heading: below the slow limit, no tangent.
			var lone = _pack.Vessel(100000015);
			Assert.That(replay.TrySample(lone.Index, 12100.0, out var slow), Is.True);
			Assert.That((slow.Motion, slow.HeadingSource, slow.Heading), Is.EqualTo((TrafficMotion.Report, HeadingSource.Unknown, 0.0)));
			Assert.That(replay.TrySample(lone.Index, 11000.0, out var fast), Is.True);
			Assert.That(fast.HeadingSource, Is.EqualTo(HeadingSource.Tangent), "2 m/s: the tangent, as before");
		}

		[Test]
		public void AtEveryReport_TheVesselIsExactlyWhereItReported()
		{
			var replay = new TrafficReplay(_pack);
			var reports = _pack.Reports;
			for (var i = 0; i < reports.Count; i++)
			{
				var vessel = _pack.Vessels[reports.Vessel(i)];
				Assert.That(replay.TrySample(vessel.Index, reports.Time(i), out var sample), Is.True, $"report {i}");
				if (vessel.Moving)
				{
					Assert.That((sample.North, sample.East), Is.EqualTo((reports.North(i), reports.East(i))), $"report {i}: exactly, not within a tolerance");
				}
				else
				{
					Assert.That((sample.North, sample.East), Is.EqualTo((vessel.StaticNorth, vessel.StaticEast)), $"report {i}: a static vessel stays at its median");
					Assert.That((sample.VelocityNorth, sample.VelocityEast), Is.EqualTo((0.0, 0.0)));
				}
			}
		}

		[Test]
		public void AVessel_ExistsOnlyInsideItsSegments()
		{
			var replay = new TrafficReplay(_pack);
			var gappy = _pack.Vessel(100000006);
			Assert.That(gappy.Segments, Is.EqualTo(3));
			// Segment 0: 7000 to 7060; segment 1: the one report at 7400; segment 2: 7800 to 7860.
			Assert.That(replay.IsPresent(gappy.Index, 6999.999), Is.False, "before the first report");
			Assert.That(replay.IsPresent(gappy.Index, 7000.0), Is.True, "at the first report");
			Assert.That(replay.IsPresent(gappy.Index, 7060.0), Is.True, "at a segment's last report");
			Assert.That(replay.IsPresent(gappy.Index, 7060.0001), Is.False, "just after it");
			Assert.That(replay.IsPresent(gappy.Index, 7230.0), Is.False, "in the gap");
			Assert.That(replay.IsPresent(gappy.Index, 7400.0), Is.True, "a one-report segment at its time");
			Assert.That(replay.IsPresent(gappy.Index, 7399.99), Is.False, "and not just before");
			Assert.That(replay.IsPresent(gappy.Index, 7400.01), Is.False, "or just after");
			Assert.That(replay.TrySample(gappy.Index, 7400.0, out var lone), Is.True);
			Assert.That(lone.Motion, Is.EqualTo(TrafficMotion.Report));
			Assert.That(replay.IsPresent(gappy.Index, 7860.0), Is.True);
			Assert.That(replay.IsPresent(gappy.Index, 7860.5), Is.False, "after the last report");
			Assert.That(replay.TrySample(gappy.Index, 7230.0, out var absent), Is.False);
			Assert.That(absent, Is.EqualTo(default(TrafficSample)));

			var moored = _pack.Vessel(100000007);
			Assert.That(moored.Moving, Is.False);
			Assert.That(replay.IsPresent(moored.Index, 300.0), Is.True, "a static vessel inside its first segment");
			Assert.That(replay.IsPresent(moored.Index, 900.0), Is.False, "is absent in its gap like any other");
			Assert.That(replay.IsPresent(moored.Index, 1230.0), Is.True);
			Assert.That(replay.IsPresent(-1, 300.0), Is.False);
			Assert.That(replay.IsPresent(_pack.Vessels.Count, 300.0), Is.False);
			Assert.That(replay.IsPresent(0, double.NaN), Is.False);
		}

		[Test]
		public void AStaticVessel_SitsAtItsMedian_HeadingItsMeanHeading()
		{
			var moored = _pack.Vessel(100000007);
			Assert.That((moored.StaticNorth, moored.StaticEast), Is.EqualTo((3000.5, 3000.5)), "the componentwise median of six reports");
			// The circular mean of 355°, 5°, 15° and 2° (4.24°), not their arithmetic mean (94°): from the stored float32 headings.
			double sin = 0.0, cos = 0.0;
			for (var r = moored.FirstReport; r < moored.FirstReport + moored.Reports; r++)
			{
				if (!double.IsNaN(_pack.Reports.Heading(r)))
				{
					sin += Math.Sin(_pack.Reports.Heading(r));
					cos += Math.Cos(_pack.Reports.Heading(r));
				}
			}

			Assert.That(moored.StaticHeading, Is.EqualTo(Math.Atan2(sin, cos)).Within(1e-15));
			Assert.That(Angles.ToDegrees(moored.StaticHeading), Is.EqualTo(4.244).Within(1e-3));
			var replay = new TrafficReplay(_pack);
			Assert.That(replay.TrySample(moored.Index, 100.0, out var a), Is.True);
			Assert.That(replay.TrySample(moored.Index, 1250.0, out var b), Is.True);
			Assert.That((a.North, a.East, a.Heading), Is.EqualTo((b.North, b.East, b.Heading)), "it never moves");
			Assert.That(a.HeadingSource, Is.EqualTo(HeadingSource.Ais));
			var unknown = _pack.Vessel(100000008);
			Assert.That(double.IsNaN(unknown.StaticHeading), Is.True);
			Assert.That(replay.TrySample(unknown.Index, 150.0, out var c), Is.True);
			Assert.That((c.Heading, c.HeadingSource), Is.EqualTo((0.0, HeadingSource.Unknown)));
		}

		[Test]
		public void TheTurn_IsTheHermiteCurve_AndItsHeadingGoesTheShortWayThroughNorth()
		{
			var replay = new TrafficReplay(_pack);
			var turner = _pack.Vessel(100000002);
			for (var t = 3660.0; t <= 3720.0; t += 1.0)
			{
				Assert.That(replay.TrySample(turner.Index, t, out var sample), Is.True);
				Assert.That(sample.Motion, Is.EqualTo(TrafficMotion.Hermite));
				var degrees = Angles.ToDegrees(sample.Heading);
				Assert.That(degrees >= 350.0 - 1e-9 || degrees <= 20.0 + 1e-9, Is.True, $"heading {degrees:F3}° at {t} s stays between 350° and 20°, through north");
			}

			// At the middle report the curve's velocity is the report's own SOG and COG, from both sides.
			Assert.That(replay.TrySample(turner.Index, 3660.0, out var at), Is.True);
			Assert.That(at.SpeedOverGround, Is.EqualTo(5.0).Within(1e-6));
			Assert.That(Angles.ToDegrees(Math.Atan2(at.VelocityEast, at.VelocityNorth)), Is.EqualTo(-45.0).Within(1e-4));
			Assert.That(replay.TrySample(turner.Index, 3660.0 - 1e-7, out var before), Is.True);
			Assert.That(before.VelocityNorth, Is.EqualTo(at.VelocityNorth).Within(1e-5), "continuous velocity through a Hermite join");
			Assert.That(before.VelocityEast, Is.EqualTo(at.VelocityEast).Within(1e-5));
		}

		[Test]
		public void StaleOrMissingVelocities_AreLinear()
		{
			var replay = new TrafficReplay(_pack);
			var stale = _pack.Vessel(100000003);
			Assert.That(replay.TrySample(stale.Index, 4030.0, out var sample), Is.True);
			Assert.That(sample.Motion, Is.EqualTo(TrafficMotion.Linear), "5 m/s reported over 20 m in 60 s: inconsistent with the step");
			Assert.That((sample.North, sample.East), Is.EqualTo((-500.0, 410.0)));
			Assert.That((sample.VelocityNorth, sample.VelocityEast), Is.EqualTo((0.0, 20.0 / 60.0)));
			var noSog = _pack.Vessel(100000004);
			Assert.That(replay.TrySample(noSog.Index, 5030.0, out var linear), Is.True);
			Assert.That(linear.Motion, Is.EqualTo(TrafficMotion.Linear), "a NaN SOG at one end");
			Assert.That(linear.HeadingSource, Is.EqualTo(HeadingSource.Tangent));
			Assert.That(Angles.ToDegrees(linear.Heading), Is.EqualTo(180.0).Within(1e-9));
		}

		/// <summary>
		/// The wave 2b review's case: two reports 100 s apart at (200, 1001) and (1200, 1001), end velocities (10, −10.2) and
		/// (10, 10.2) m/s; with <paramref name="sog"/> NaN the velocities are missing and the interval is the straight line.
		/// </summary>
		private static TrafficPack Curve(bool sog = true)
		{
			var speed = sog ? Math.Sqrt(10.0 * 10.0 + 10.2 * 10.2) : double.NaN;
			var track = new TrafficFixtures.Track { Mmsi = 200000010, Class = TrafficClass.Other };
			track.Reports.Add(new TrafficFixtures.Report(0.0, 200.0, 1001.0, speed, Math.Atan2(-10.2, 10.0), double.NaN));
			track.Reports.Add(new TrafficFixtures.Report(100.0, 1200.0, 1001.0, speed, Math.Atan2(10.2, 10.0), double.NaN));
			return TrafficFixtures.Pack(track);
		}

		[Test]
		public void MayEnter_KeepsAHermiteCurveThatBulgesIntoTheArea_ThoughNoReportOrChordDoes()
		{
			var replay = new TrafficReplay(Curve());
			Assert.That(replay.TrySample(0, 50.0, out var middle), Is.True);
			Assert.That(middle.Motion, Is.EqualTo(TrafficMotion.Hermite), "both end velocities pass the consistency test");
			Assert.That(middle.North, Is.EqualTo(700.0).Within(0.01));
			Assert.That(middle.East, Is.EqualTo(746.0).Within(0.01), "254 m inside the area's east edge, though both reports and the chord lie 1 m east of it");
			Assert.That(replay.MayEnter(0, 0.0, 1000.0, 0.0, 1000.0), Is.True);
			// Without velocities the same reports are joined by the straight line along east 1001: provably never inside.
			var straight = new TrafficReplay(Curve(sog: false));
			Assert.That(straight.TrySample(0, 50.0, out var line), Is.True);
			Assert.That((line.Motion, line.East), Is.EqualTo((TrafficMotion.Linear, 1001.0)));
			Assert.That(straight.MayEnter(0, 0.0, 1000.0, 0.0, 1000.0), Is.False);
			Assert.That(straight.MayEnter(0, 0.0, 1000.0, 0.0, 1001.0), Is.True, "the edge counts as inside");
			Assert.That(replay.MayEnter(1, 0.0, 1000.0, 0.0, 1000.0), Is.False, "no such vessel");
		}

		[Test]
		public void MayEnter_NeverRulesOutAVesselSeenInside_AndStillRulesOutMost()
		{
			// Every fixture vessel (Hermite and linear intervals, a static vessel, one-report segments) sampled every 0.25 s through
			// its life, against a 250 m grid of areas over the whole fixture: a vessel seen inside an area is never ruled out of it.
			var replay = new TrafficReplay(_pack);
			var ruledOut = 0;
			for (var v = 0; v < replay.Count; v++)
			{
				var vessel = _pack.Vessels[v];
				var seen = 0;
				var north = new List<double>();
				var east = new List<double>();
				for (var t = vessel.FirstTime; t <= vessel.LastTime; t += 0.25)
				{
					if (replay.TrySample(v, t, out var sample))
					{
						north.Add(sample.North);
						east.Add(sample.East);
					}
				}

				for (var t = vessel.FirstReport; t < vessel.FirstReport + vessel.Reports; t++)
				{
					Assert.That(replay.TrySample(v, _pack.Reports.Time(t), out var atReport), Is.True);
					north.Add(atReport.North);
					east.Add(atReport.East);
				}

				for (var n0 = -7000.0; n0 < 7000.0; n0 += 250.0)
				{
					for (var e0 = -7000.0; e0 < 8000.0; e0 += 250.0)
					{
						var inside = false;
						for (var k = 0; k < north.Count && !inside; k++)
						{
							inside = north[k] >= n0 && north[k] <= n0 + 250.0 && east[k] >= e0 && east[k] <= e0 + 250.0;
						}

						var may = replay.MayEnter(v, n0, n0 + 250.0, e0, e0 + 250.0);
						Assert.That(!inside || may, Is.True, $"vessel {vessel.Mmsi} is inside [{n0}, {n0 + 250.0}] × [{e0}, {e0 + 250.0}] but was ruled out");
						seen += inside ? 1 : 0;
						ruledOut += may ? 0 : 1;
					}
				}

				Assert.That(seen, Is.GreaterThan(0), $"the grid catches vessel {vessel.Mmsi}");
			}

			Assert.That(ruledOut, Is.GreaterThan(replay.Count * 3000), "and the bound still rules out nearly every area a vessel never visits");
		}

		[Test]
		public void PackTime_IsSimulationTimePlusTheScenarioStartLessTheEpoch()
		{
			// 08:30 EDT on 2025-07-14 is 12:30 UTC, 30600 s after the 04:00 UTC epoch.
			var start = new DateTimeOffset(2025, 7, 14, 12, 30, 0, TimeSpan.Zero);
			var run = new TrafficRun(_pack, start, TrafficLeaveOut.Off, 0);
			Assert.That(run.EpochOffset, Is.EqualTo(30600.0));
			Assert.That(run.PackTime(12.34), Is.EqualTo(30612.34));
			var eastern = new TrafficRun(_pack, new DateTimeOffset(2025, 7, 14, 8, 30, 0, TimeSpan.FromHours(-4)), TrafficLeaveOut.Off, 0);
			Assert.That(eastern.EpochOffset, Is.EqualTo(30600.0), "the same instant given with an offset");
			var fraction = new TrafficRun(_pack, start.AddTicks(1234567), TrafficLeaveOut.Off, 0);
			Assert.That(fraction.EpochOffset, Is.EqualTo(30600.1234567).Within(1e-9), "to the tick");
			var before = new TrafficRun(_pack, _pack.EpochUtc.AddHours(-1), TrafficLeaveOut.Off, 0);
			Assert.That(before.EpochOffset, Is.EqualTo(-3600.0), "a start before local midnight reads the pack's padding hour");
		}

		[Test]
		public void Queries_AreDeterministic_AndAllocateNothing()
		{
			var a = new TrafficReplay(_pack);
			var b = new TrafficReplay(TrafficPackJson.Load(Path.Combine(_root, "harbor", "traffic", "2025-07-14")));
			for (var v = 0; v < a.Count; v++)
			{
				var vessel = _pack.Vessels[v];
				for (var t = vessel.FirstTime - 10.0; t <= vessel.LastTime + 10.0; t += 0.37)
				{
					var pa = a.TrySample(v, t, out var sa);
					var pb = b.TrySample(v, t, out var sb);
					Assert.That(pb, Is.EqualTo(pa));
					Assert.That(BitConverter.DoubleToInt64Bits(sb.North), Is.EqualTo(BitConverter.DoubleToInt64Bits(sa.North)));
					Assert.That(BitConverter.DoubleToInt64Bits(sb.East), Is.EqualTo(BitConverter.DoubleToInt64Bits(sa.East)));
					Assert.That(BitConverter.DoubleToInt64Bits(sb.Heading), Is.EqualTo(BitConverter.DoubleToInt64Bits(sa.Heading)));
				}
			}

			// Warm up, then count: no garbage over many queries, present and absent alike.
			var sum = 0.0;
			for (var k = 0; k < 1000; k++)
			{
				a.TrySample(k % a.Count, 3600.0 + k, out var warm);
				sum += warm.North;
			}

			var allocated = GC.GetAllocatedBytesForCurrentThread();
			for (var k = 0; k < 200000; k++)
			{
				if (a.TrySample(k % a.Count, 3000.0 + 0.05 * k, out var sample))
				{
					sum += sample.North + sample.Heading;
				}
			}

			Assert.That(GC.GetAllocatedBytesForCurrentThread() - allocated, Is.EqualTo(0), "allocation-free per query");
			Assert.That(double.IsNaN(sum), Is.False);
		}
	}
}
