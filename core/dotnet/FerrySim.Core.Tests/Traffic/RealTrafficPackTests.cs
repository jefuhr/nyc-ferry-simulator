using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Tests.Dynamics;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// The built traffic packs (gitignored, under data/processed/harbor/traffic) through the core: every day loads and verifies,
	/// the replay puts every vessel exactly at its reports, and how often each of the rule's branches is used on real AIS, with
	/// sizes for the players; a real moored ferry with no AIS heading holds one heading; and the Pier 11 start beside it is
	/// alongside, not a collision. Explicit: they need the pipeline's output, so they run by name and write their numbers to
	/// artifacts/traffic-unity/core.
	/// </summary>
	[TestFixture]
	[Explicit("needs the built packs: PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic build --all")]
	public class RealTrafficPackTests
	{
		private static string Packs => Path.Combine(RepoPaths.Root, "data", "processed", "harbor", "traffic");

		private static string Output
		{
			get
			{
				var directory = Path.Combine(RepoPaths.Root, "artifacts", "traffic-unity", "core");
				Directory.CreateDirectory(directory);
				return directory;
			}
		}

		[Test]
		public void EveryBuiltDay_LoadsVerifiesAndReplays()
		{
			var days = new TrafficPacks(Path.Combine(RepoPaths.Root, "data", "processed")).Days();
			Assert.That(days.Count, Is.EqualTo(28), "the 28 scenario days");
			var report = new StringBuilder("| Day | Digest | Vessels | Reports | Size, MB | Hermite | Linear | Static | One report | Heading: AIS | Tangent | Held | Chord | Last AIS | Median COG | Unknown |\n|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
			long totalBytes = 0;
			var invariant = CultureInfo.InvariantCulture;
			foreach (var day in days)
			{
				var folder = Path.Combine(Packs, day);
				var pack = TrafficPackJson.Load(folder);
				var replay = new TrafficReplay(pack);
				var reports = pack.Reports;
				var motions = new long[4];
				var headings = new long[7];
				for (var r = 0; r < reports.Count; r++)
				{
					var vessel = pack.Vessels[reports.Vessel(r)];
					Assert.That(replay.TrySample(vessel.Index, reports.Time(r), out var at), Is.True, $"{day} report {r}");
					if (vessel.Moving)
					{
						Assert.That((at.North, at.East), Is.EqualTo((reports.North(r), reports.East(r))), $"{day} report {r}: exactly at the report");
					}

					// The branch the replay uses half way to the next report of the segment (or at a lone report).
					var middle = r + 1 < vessel.FirstReport + vessel.Reports && reports.Segment(r + 1) == reports.Segment(r) ? 0.5 * (reports.Time(r) + reports.Time(r + 1)) : reports.Time(r);
					replay.TrySample(vessel.Index, middle, out var sample);
					motions[(int)sample.Motion]++;
					headings[(int)sample.HeadingSource]++;
				}

				long bytes = 0;
				foreach (var file in Directory.GetFiles(folder))
				{
					bytes += new FileInfo(file).Length;
				}

				totalBytes += bytes;
				var count = (double)reports.Count;
				report.Append(string.Format(invariant, "| {0} | `{1}` | {2} | {3} | {4:F2} | {5:P0} | {6:P0} | {7:P0} | {8:P1} | {9:P0} | {10:P0} | {11:P1} | {12:P1} | {13:P1} | {14:P1} | {15:P1} |\n",
					day, pack.Digest.Substring(0, 12), pack.Vessels.Count, reports.Count, bytes / 1e6,
					motions[(int)TrafficMotion.Hermite] / count, motions[(int)TrafficMotion.Linear] / count, motions[(int)TrafficMotion.Static] / count, motions[(int)TrafficMotion.Report] / count,
					headings[(int)HeadingSource.Ais] / count, headings[(int)HeadingSource.Tangent] / count, headings[(int)HeadingSource.AisHeld] / count, headings[(int)HeadingSource.Chord] / count,
					headings[(int)HeadingSource.LastAis] / count, headings[(int)HeadingSource.MedianCog] / count, headings[(int)HeadingSource.Unknown] / count));
			}

			report.Append(string.Format(invariant, "\nAll 28 days: {0:F1} MB ({1:F1} MiB) of pack files.\n", totalBytes / 1e6, totalBytes / 1048576.0));
			File.WriteAllText(Path.Combine(Output, "real-packs.md"), report.ToString());
			TestContext.Out.WriteLine(report.ToString());
		}

		/// <summary>MMSI 367434220 on 2025-07-14: a 25 m passenger ferry that never sends an AIS heading, berthed at Pier 11 around 18:00 EDT.</summary>
		private const int Berthed = 367434220;

		/// <summary>18:00 EDT on 2025-07-14: pack time 64800 s.</summary>
		private const double SixPm = 64800.0;

		[Test]
		public void AJitteryMooredFerry_HoldsOneHeading_WhileItIsMoored()
		{
			var pack = TrafficPackJson.Load(Path.Combine(Packs, "2025-07-14"));
			var replay = new TrafficReplay(pack);
			var ferry = pack.Vessel(Berthed);
			Assert.That(replay.TrySample(ferry.Index, SixPm, out var first), Is.True);
			Assert.That(first.HeadingSource, Is.EqualTo(HeadingSource.MedianCog), "no AIS heading in its segment: its median COG at speed");
			// From 18:00 to 18:02 it reports 0 to 0.1 m/s. Its rectangle holds still: one heading, and its position wanders only
			// with its reports. The direction of its motion, which the tangent used to give it, swings through tens of degrees.
			double minimumMotion = double.PositiveInfinity, maximumMotion = double.NegativeInfinity, farthest = 0.0;
			var lines = new StringBuilder("time_s,north_m,east_m,heading_deg,source,motion_direction_deg\n");
			for (var t = SixPm; t <= SixPm + 120.0; t += 0.5)
			{
				Assert.That(replay.TrySample(ferry.Index, t, out var sample), Is.True);
				Assert.That((sample.HeadingSource, BitConverter.DoubleToInt64Bits(sample.Heading)), Is.EqualTo((HeadingSource.MedianCog, BitConverter.DoubleToInt64Bits(first.Heading))), $"at {t} s");
				farthest = Math.Max(farthest, Math.Sqrt((sample.North - first.North) * (sample.North - first.North) + (sample.East - first.East) * (sample.East - first.East)));
				var motion = Angles.ToDegrees(Math.Atan2(sample.VelocityEast, sample.VelocityNorth));
				minimumMotion = Math.Min(minimumMotion, motion);
				maximumMotion = Math.Max(maximumMotion, motion);
				lines.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1:F3},{2:F3},{3:F3},{4},{5:F1}\n", t, sample.North, sample.East, Angles.ToDegrees(sample.Heading), sample.HeadingSource, motion));
			}

			File.WriteAllText(Path.Combine(Output, "moored-ferry-367434220.csv"), lines.ToString());
			Assert.That(farthest, Is.LessThan(5.0), "its position moves only with its reports, a metre or two");
			Assert.That(maximumMotion - minimumMotion, Is.GreaterThan(30.0), "the jitter's direction swings: a heading from it would turn the rectangle");
		}

		[Test]
		public void AtPier11At1800_TheFerryBerthedBesideTheStart_IsAlongside_NotACollision_AndRaisesNoAlarm()
		{
			var pack = TrafficPackJson.Load(Path.Combine(Packs, "2025-07-14"));
			// The catalog's Pier 11 start (the slice E2E's LeaveOut run), 18:00 EDT, its own ferry left out automatically.
			const double north = 298.866, east = 735.204;
			var heading = Angles.ToRadians(48.6429);
			var run = TrafficRun.ForScenario(pack, new DateTimeOffset(2025, 7, 14, 22, 0, 0, TimeSpan.Zero), TrafficLeaveOut.Automatic, 0, north, east);
			Assert.That(run.RemovedMmsi, Is.EqualTo(367779550));
			var monitor = run.StartRun(HullOutline.FromContact(ManeuverRunner.Load("t-class.json")), TrafficAlarmLimits.Default);
			var events = new List<TrafficEvent>();
			var sink = new Sink(events);
			// Held at its start pose for a minute.
			for (long step = 1; step <= 6000; step++)
			{
				monitor.Update(step, VesselState.Planar(SimulationClock.TimeAtStep(step), north, east, heading), sink);
			}

			var berthed = events.FindAll(e => e.Mmsi == Berthed);
			Assert.That(berthed.Count, Is.GreaterThan(0));
			Assert.That((berthed[0].Kind, berthed[0].Step), Is.EqualTo((TrafficEventKind.AlongsideStarted, 1L)));
			Assert.That(berthed.TrueForAll(e => e.Kind == TrafficEventKind.AlongsideStarted || e.Kind == TrafficEventKind.AlongsideEnded), Is.True,
				string.Join(", ", berthed.ConvertAll(e => TrafficEventLog.KindName(e.Kind) + "@" + e.Step)));
			Assert.That(monitor.IsAlongside(pack.Vessel(Berthed).Index), Is.True, "the learner held still and the ferry stayed berthed");
			TestContext.Out.WriteLine(string.Join("\n", events.ConvertAll(e => TrafficEventLog.FormatRow(e, pack.Vessels[e.Vessel]))));
		}

		private sealed class Sink : ITrafficEventSink
		{
			private readonly List<TrafficEvent> _events;

			public Sink(List<TrafficEvent> events) => _events = events;

			public void OnTrafficEvent(in TrafficEvent trafficEvent) => _events.Add(trafficEvent);
		}
	}
}
