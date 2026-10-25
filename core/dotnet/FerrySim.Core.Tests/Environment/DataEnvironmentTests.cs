using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// <see cref="DataEnvironment"/> against synthetic packs. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Bilinear depth off by a cell, with rows and columns (north and east) swapped, or wrong at the lattice edges.</item>
	/// <item>A point outside every grid or next to unknown bed giving NaN or a made-up depth instead of the outside depth,
	/// or not being counted.</item>
	/// <item>The gauge blend dividing by zero at a gauge, not equalling the gauge there, or not lying between the gauges.</item>
	/// <item>Depth with the wrong sign convention (depth = level − bed, bed negative under water), or a tide that does not
	/// change it.</item>
	/// <item>Simulation time not mapped through scenario start − epoch.</item>
	/// <item>Values outside the coverage window not held at its edge, or the hold not reported.</item>
	/// <item>Gaps interpolated past 1800 s, or held when they should be interpolated.</item>
	/// <item>Wind that is not the first station's mean wind, or that varies in space or applies gusts.</item>
	/// <item>Allocation per sample, or two environments from one pack disagreeing.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class DataEnvironmentTests
	{
		private static readonly WindStation StillAir = PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.Zero);
		private static readonly CurrentStation StillWater = PackFixtures.SteadyCurrent("C1", 0.0, 0.0, HorizontalVector.Zero);

		[Test]
		public void Depth_IsTheWaterLevelMinusTheBilinearBed()
		{
			// Four cells 10 m apart; row 1 is north of row 0, column 1 east of column 0.
			var grid = new DepthGrid("g", 0.0, 0.0, 10.0, 2, 2, new[] { -10.0f, -12.0f, -14.0f, -20.0f });
			var environment = Environment(PackFixtures.Depth(20.0, grid), new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.5) });

			Assert.That(environment.SampleDepth(0.0, 0.0, 0.0), Is.EqualTo(10.5), "cell (0, 0) exactly");
			Assert.That(environment.SampleDepth(10.0, 0.0, 0.0), Is.EqualTo(14.5), "north reads the next row");
			Assert.That(environment.SampleDepth(0.0, 10.0, 0.0), Is.EqualTo(12.5), "east reads the next column");
			Assert.That(environment.SampleDepth(10.0, 10.0, 0.0), Is.EqualTo(20.5), "the last centre is inside");

			// Bilinear, not planar: a quarter north and three quarters east.
			var lower = -10.0 + 0.75 * (-12.0 + 10.0);
			var upper = -14.0 + 0.75 * (-20.0 + 14.0);
			var bed = lower + 0.25 * (upper - lower);
			Assert.That(environment.SampleDepth(2.5, 7.5, 0.0), Is.EqualTo(0.5 - bed).Within(1e-12));
			Assert.That(environment.Coverage.DepthMisses, Is.EqualTo(0));
		}

		[Test]
		public void Depth_OnALargerGrid_FindsTheRightCell()
		{
			// A bed that is planar in north and east is reproduced exactly by bilinear interpolation, so any cell mix-up shows.
			var grid = PackFixtures.Grid("g", -653.25, -425.75, 5.0, 40, 30, (r, c) => (float)(-20.0 + 0.25 * r + 0.5 * c));
			var environment = Environment(PackFixtures.Depth(20.0, grid), new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) });
			foreach (var (north, east) in new[] { (-653.25, -425.75), (-600.0, -400.0), (-512.4, -301.9), (-458.25, -280.75) })
			{
				var row = (north + 653.25) / 5.0;
				var column = (east + 425.75) / 5.0;
				Assert.That(environment.SampleDepth(north, east, 0.0), Is.EqualTo(20.0 - 0.25 * row - 0.5 * column).Within(1e-9), $"({north}, {east})");
			}
		}

		[Test]
		public void Depth_OutsideEveryGrid_IsTheOutsideDepth_AndIsCounted()
		{
			var first = PackFixtures.FlatGrid("a", 0.0, 0.0, 10.0, 2, 2, -5.0f);
			var second = PackFixtures.FlatGrid("b", 0.0, 100.0, 10.0, 2, 2, -7.0f);
			var environment = Environment(PackFixtures.Depth(20.0, first, second), new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) });

			Assert.That(environment.SampleDepth(5.0, 105.0, 0.0), Is.EqualTo(7.0), "inside the second grid");
			Assert.That(environment.SampleDepth(10.001, 5.0, 0.0), Is.EqualTo(20.0), "just north of the last row of centres");
			Assert.That(environment.SampleDepth(-0.001, 5.0, 0.0), Is.EqualTo(20.0), "just south of the first");
			Assert.That(environment.SampleDepth(5.0, 50.0, 0.0), Is.EqualTo(20.0), "between the grids");

			var coverage = environment.Coverage;
			Assert.That(coverage.DepthSamples, Is.EqualTo(4));
			Assert.That(coverage.DepthMisses, Is.EqualTo(3));
			Assert.That(coverage.OutsideDepth, Is.EqualTo(20.0));
			Assert.That(coverage.HasMisses, Is.True);

			environment.ResetCounters();
			Assert.That(environment.Coverage.DepthMisses, Is.EqualTo(0));
			Assert.That(environment.Coverage.DepthSamples, Is.EqualTo(0));
		}

		[Test]
		public void Depth_NextToUnknownBed_IsTheOutsideDepth_AndIsCounted()
		{
			// Cell (1, 1) is unknown land; every point whose four surrounding centres include it is unknown.
			var grid = PackFixtures.Grid("g", 0.0, 0.0, 10.0, 3, 3, (r, c) => r == 1 && c == 1 ? float.NaN : -6.0f);
			var environment = Environment(PackFixtures.Depth(20.0, grid), new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 1.0) });

			Assert.That(environment.SampleDepth(5.0, 5.0, 0.0), Is.EqualTo(20.0));
			Assert.That(environment.SampleDepth(15.0, 15.0, 0.0), Is.EqualTo(20.0));
			Assert.That(environment.SampleDepth(0.0, 0.0, 0.0), Is.EqualTo(20.0), "a centre next to it is unknown too: its cell's corners include the NaN");
			Assert.That(environment.Coverage.DepthMisses, Is.EqualTo(3));
			Assert.That(double.IsNaN(environment.SampleDepth(5.0, 5.0, 0.0)), Is.False);
		}

		[Test]
		public void Depth_OnKnownLand_IsNegative()
		{
			// A bed above the water surface is dry land: zero or negative depth, as IEnvironment defines it.
			var grid = PackFixtures.FlatGrid("g", 0.0, 0.0, 10.0, 2, 2, 2.0f);
			var environment = Environment(PackFixtures.Depth(20.0, grid), new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.5) });
			Assert.That(environment.SampleDepth(5.0, 5.0, 0.0), Is.EqualTo(-1.5));
			Assert.That(environment.Coverage.DepthMisses, Is.EqualTo(0));
		}

		[Test]
		public void WaterLevel_AtAGauge_EqualsThatGauge_AndBlendsByInverseDistanceSquared()
		{
			var near = PackFixtures.Gauge("G1", 0.0, 0.0, t => 1.0 + 1e-5 * t);
			var far = PackFixtures.Gauge("G2", 0.0, 1000.0, t => 2.0);
			var environment = Environment(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 121, -8.0f)), new[] { near, far });

			foreach (var time in new[] { 0.0, 123.4, 4000.0 })
			{
				Assert.That(environment.SampleWaterLevel(0.0, 0.0, time), Is.EqualTo(near.Level.Sample(time)), $"at G1, t {time}");
				Assert.That(environment.SampleWaterLevel(0.0, 1000.0, time), Is.EqualTo(2.0), $"at G2, t {time}");
				Assert.That(environment.SampleDepth(0.0, 0.0, time), Is.EqualTo(near.Level.Sample(time) + 8.0), "depth at the gauge is its level minus the bed");
			}

			Assert.That(environment.SampleWaterLevel(0.0, 500.0, 0.0), Is.EqualTo(1.5).Within(1e-12), "halfway: equal weights");
			Assert.That(environment.SampleWaterLevel(0.0, 250.0, 0.0), Is.EqualTo((9.0 * 1.0 + 2.0) / 10.0).Within(1e-12), "a quarter of the way: weights 9 to 1");
			Assert.That(environment.SampleWaterLevel(300.0, -200.0, 0.0), Is.InRange(1.0, 2.0), "never outside the gauges' range");
		}

		[Test]
		public void Tide_RaisesAndLowersTheDepth()
		{
			// A semidiurnal tide of 0.7 m amplitude at the gauge: depth follows the level over the bed.
			var gauge = PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.7 * Math.Sin(2.0 * Math.PI * t / 44712.0));
			var environment = Environment(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)), new[] { gauge });
			var high = environment.SampleDepth(10.0, 10.0, 44712.0 / 4.0);
			var low = environment.SampleDepth(10.0, 10.0, 3.0 * 44712.0 / 4.0);
			Assert.That(high, Is.EqualTo(5.7).Within(0.01));
			Assert.That(low, Is.EqualTo(4.3).Within(0.01));
		}

		[Test]
		public void Time_IsTheSimulationTimePlusTheScenarioStartMinusTheEpoch()
		{
			// Level equal to pack time / 10 000, so the value read says which pack time was read.
			var gauge = PackFixtures.Gauge("G1", 0.0, 0.0, t => t / 10000.0);
			var pack = Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)), new[] { gauge });

			var morning = new DataEnvironment(pack, PackFixtures.Epoch.AddHours(3.0));
			Assert.That(morning.EpochOffset, Is.EqualTo(10800.0));
			Assert.That(morning.PackTime(5.0), Is.EqualTo(10805.0));
			Assert.That(morning.SampleWaterLevel(0.0, 0.0, 0.0), Is.EqualTo(1.08).Within(1e-12), "03:00 local is 10 800 s after the epoch");
			Assert.That(morning.SampleWaterLevel(0.0, 0.0, 180.0), Is.EqualTo(1.098).Within(1e-12), "between two samples");

			// An offset start time is the same instant: 07:00Z is 03:00 EDT.
			var sameInstant = new DataEnvironment(pack, new DateTimeOffset(2025, 7, 14, 3, 0, 0, TimeSpan.FromHours(-4.0)));
			Assert.That(sameInstant.EpochOffset, Is.EqualTo(10800.0));

			var lateEvening = new DataEnvironment(pack, PackFixtures.Epoch.AddMinutes(-30.0));
			Assert.That(lateEvening.EpochOffset, Is.EqualTo(-1800.0));
			Assert.That(lateEvening.SampleWaterLevel(0.0, 0.0, 0.0), Is.EqualTo(-0.18).Within(1e-12), "23:30 the evening before is inside the coverage");
			Assert.That(lateEvening.Coverage.TimeHolds, Is.EqualTo(0));
		}

		[Test]
		public void Time_OutsideTheCoverage_HoldsTheNearerEdge_AndIsReported()
		{
			var gauge = PackFixtures.Gauge("G1", 0.0, 0.0, t => t / 10000.0);
			var wind = PackFixtures.Wind("W1", 0.0, 0.0, t => new HorizontalVector(t / 10000.0, 1.0));
			var current = PackFixtures.Current("C1", 0.0, 0.0, t => new HorizontalVector(0.0, t / 100000.0));
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)),
				new[] { current }, new[] { gauge }, new[] { wind });

			var late = new DataEnvironment(pack, PackFixtures.Epoch.AddSeconds(89000.0));
			Assert.That(late.SampleWaterLevel(0.0, 0.0, 500.0), Is.EqualTo(8.95).Within(1e-12), "inside: 89 500 s");
			Assert.That(late.Coverage.TimeHolds, Is.EqualTo(0));
			Assert.That(late.SampleWaterLevel(0.0, 0.0, 2000.0), Is.EqualTo(9.0), "past the end: held at 90 000 s");
			Assert.That(late.SampleWind(0.0, 0.0, 5000.0).North, Is.EqualTo(9.0));
			Assert.That(late.SampleCurrent(0.0, 0.0, 5000.0).East, Is.EqualTo(0.9));
			Assert.That(late.Coverage.TimeHolds, Is.EqualTo(3));

			var early = new DataEnvironment(pack, PackFixtures.Epoch.AddHours(-2.0));
			Assert.That(early.SampleWaterLevel(0.0, 0.0, 0.0), Is.EqualTo(-0.36), "before the start: held at −3600 s");
			Assert.That(early.SampleDepth(0.0, 0.0, 0.0), Is.EqualTo(5.0 - 0.36).Within(1e-12));
			Assert.That(early.Coverage.TimeHolds, Is.EqualTo(2));

			var coverage = early.Coverage;
			Assert.That(coverage.StartUtc, Is.EqualTo(PackFixtures.Epoch.AddHours(-1.0)));
			Assert.That(coverage.EndUtc, Is.EqualTo(PackFixtures.Epoch.AddHours(25.0)));
			Assert.That(coverage.StartTime, Is.EqualTo(3600.0), "the coverage starts an hour into this run");
			Assert.That(coverage.EndTime, Is.EqualTo(97200.0));
			Assert.That(coverage.Samples, Is.EqualTo(2));
		}

		[Test]
		public void Gaps_UpTo1800s_AreInterpolated_AndLongerGapsHoldTheNearerSample()
		{
			// Samples every 360 s, except an 1800 s gap from 0 to 1800 and a 3600 s gap from 7200 to 10 800.
			var times = new List<double>();
			foreach (var t in PackFixtures.Times())
			{
				if ((t > 0.0 && t < 1800.0) || (t > 7200.0 && t < 10800.0))
				{
					continue;
				}

				times.Add(t);
			}

			var gauge = PackFixtures.Gauge("G1", 0.0, 0.0, t => t / 1000.0, times.ToArray());
			var environment = Environment(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)), new[] { gauge });

			Assert.That(environment.SampleWaterLevel(0.0, 0.0, 900.0), Is.EqualTo(0.9).Within(1e-12), "an 1800 s gap is interpolated");
			Assert.That(environment.SampleWaterLevel(0.0, 0.0, 8000.0), Is.EqualTo(7.2), "a longer gap holds the nearer sample");
			Assert.That(environment.SampleWaterLevel(0.0, 0.0, 9000.0), Is.EqualTo(7.2), "the earlier one at the midpoint");
			Assert.That(environment.SampleWaterLevel(0.0, 0.0, 9000.1), Is.EqualTo(10.8));
			Assert.That(environment.SampleWaterLevel(0.0, 0.0, 10800.0), Is.EqualTo(10.8));

			var gaps = environment.Coverage.Gaps;
			Assert.That(gaps.Count, Is.EqualTo(1), "only the long gap is held");
			Assert.That(gaps[0].Series, Is.EqualTo(EnvironmentSeries.WaterLevel));
			Assert.That(gaps[0].StationId, Is.EqualTo("G1"));
			Assert.That(gaps[0].Start, Is.EqualTo(7200.0));
			Assert.That(gaps[0].End, Is.EqualTo(10800.0));
		}

		[Test]
		public void Gaps_AtTheEndsOfTheCoverage_AreListed()
		{
			// A wind sensor that started an hour late and stopped at 20:00 local: both ends are held.
			var wind = PackFixtures.Wind("W1", 0.0, 0.0, t => new HorizontalVector(1.0, 0.0), PackFixtures.Times(0.0, 72000.0));
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)),
				new[] { StillWater }, new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) }, new[] { wind },
				new[] { "wind 8518750: no observations before 00:00 or after 20:00 local; held" });
			var coverage = new DataEnvironment(pack, PackFixtures.Epoch).Coverage;

			Assert.That(coverage.Notes, Is.EqualTo(new[] { "wind 8518750: no observations before 00:00 or after 20:00 local; held" }));
			Assert.That(coverage.Gaps.Count, Is.EqualTo(2));
			Assert.That((coverage.Gaps[0].Start, coverage.Gaps[0].End), Is.EqualTo((-3600.0, 0.0)));
			Assert.That((coverage.Gaps[1].Start, coverage.Gaps[1].End), Is.EqualTo((72000.0, 90000.0)));
			Assert.That(coverage.Gaps[1].Series, Is.EqualTo(EnvironmentSeries.Wind));
		}

		[Test]
		public void Wind_IsTheFirstStationsMeanWind_EverywhereAndWithoutGusts()
		{
			var first = PackFixtures.Wind("W1", 0.0, 0.0, t => t < 0.0 ? new HorizontalVector(3.0, 4.0) : new HorizontalVector(3.0 + t / 360.0, 4.0));
			var second = PackFixtures.Wind("W2", 1000.0, 1000.0, t => new HorizontalVector(-5.0, 0.0));
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)),
				new[] { StillWater }, new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) }, new[] { first, second });
			var environment = new DataEnvironment(pack, PackFixtures.Epoch);

			foreach (var (north, east) in new[] { (0.0, 0.0), (1000.0, 1000.0), (-5000.0, 7000.0) })
			{
				Assert.That(environment.SampleWind(north, east, -1800.0), Is.EqualTo(new HorizontalVector(3.0, 4.0)), $"uniform at ({north}, {east})");
			}

			Assert.That(environment.SampleWind(0.0, 0.0, 180.0).North, Is.EqualTo(3.5).Within(1e-12), "interpolated in time");
			Assert.That(environment.SampleWind(0.0, 0.0, -1800.0).Magnitude, Is.EqualTo(5.0).Within(1e-12), "the mean wind, not the gust");
		}

		[Test]
		public void ADayWithNoWindStation_HasStillAir_AndSaysSo()
		{
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -100.0, -100.0, 10.0, 21, 21, -5.0f)),
				new[] { StillWater }, new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) }, Array.Empty<WindStation>());
			var environment = new DataEnvironment(pack, PackFixtures.Epoch);
			Assert.That(environment.SampleWind(10.0, 20.0, 300.0), Is.EqualTo(HorizontalVector.Zero));
			Assert.That(environment.Coverage.WindMissing, Is.True);
			Assert.That(environment.Coverage.HasMisses, Is.True);
			Assert.That(new DataEnvironment(PackFixtures.DeepStillWater(), PackFixtures.Epoch).Coverage.WindMissing, Is.False);
		}

		[Test]
		public void Current_ComesFromTheFieldAtTheHeldPackTime()
		{
			var field = new RecordingField(new HorizontalVector(0.25, -0.5));
			var environment = new DataEnvironment(PackFixtures.DeepStillWater(), PackFixtures.Epoch.AddHours(1.0), field);
			Assert.That(environment.SampleCurrent(12.0, -34.0, 60.0), Is.EqualTo(new HorizontalVector(0.25, -0.5)));
			Assert.That((field.North, field.East, field.PackTime), Is.EqualTo((12.0, -34.0, 3660.0)));
			environment.SampleCurrent(0.0, 0.0, 100000.0);
			Assert.That(field.PackTime, Is.EqualTo(90000.0), "held at the coverage end before the field sees it");
			Assert.That(environment.CurrentField, Is.SameAs(field));
		}

		[Test]
		public void Sampling_DoesNotAllocate()
		{
			var environment = Busy(PackFixtures.Epoch.AddHours(9.0));
			var sink = 0.0;
			for (var i = 0; i < 100; i++)
			{
				sink += Sample(environment, i);
			}

			// The runtime may allocate once while it recompiles a hot method (tiered compilation); an allocation per
			// sample shows in every round.
			var allocated = -1L;
			for (var round = 0; round < 3 && allocated != 0; round++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var i = 0; i < 10000; i++)
				{
					sink += Sample(environment, i);
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			}

			Assert.That(allocated, Is.EqualTo(0));
			Assert.That(double.IsNaN(sink), Is.False);
		}

		[Test]
		public void TwoEnvironmentsFromOnePack_AgreeInEveryBit()
		{
			var first = Busy(PackFixtures.Epoch.AddHours(9.0));
			var second = new DataEnvironment(first.Pack, PackFixtures.Epoch.AddHours(9.0));
			for (var i = 0; i < 2000; i++)
			{
				// Interleave a third sampling time so the per-time caches are exercised.
				var a = Sample(first, i);
				first.SampleDepth(1.0, 2.0, 1e4);
				var b = Sample(second, i);
				Assert.That(BitConverter.DoubleToInt64Bits(a), Is.EqualTo(BitConverter.DoubleToInt64Bits(b)), $"sample {i}");
			}
		}

		[Test]
		public void Values_AreFiniteEverywhere()
		{
			var environment = Busy(PackFixtures.Epoch);
			for (var north = -3000.0; north <= 3000.0; north += 37.0)
			{
				for (var east = -3000.0; east <= 3000.0; east += 41.0)
				{
					var time = north + east;
					Assert.That(IsFinite(environment.SampleDepth(north, east, time)), Is.True);
					Assert.That(IsFinite(environment.SampleCurrent(north, east, time).North), Is.True);
					Assert.That(IsFinite(environment.SampleWind(north, east, time).East), Is.True);
				}
			}
		}

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

		private static EnvironmentPack Pack(DepthSet depth, IReadOnlyList<WaterLevelStation> gauges) =>
			PackFixtures.Pack(depth, new[] { StillWater }, gauges, new[] { StillAir });

		private static DataEnvironment Environment(DepthSet depth, IReadOnlyList<WaterLevelStation> gauges) =>
			new DataEnvironment(Pack(depth, gauges), PackFixtures.Epoch);

		// Three current stations, two gauges with tides, a gusty wind and a grid with unknown cells.
		private static DataEnvironment Busy(DateTimeOffset start)
		{
			var grid = PackFixtures.Grid("g", -2000.0, -2000.0, 5.0, 801, 801, (r, c) => (r + c) % 97 == 0 ? float.NaN : (float)(-3.0 - 0.01 * r));
			var currents = new[]
			{
				PackFixtures.Current("C1", 0.0, 0.0, t => HorizontalVector.FromSpeedToward(1.2 * Math.Sin(t / 7000.0), 0.4)),
				PackFixtures.Current("C2", 1500.0, 300.0, t => HorizontalVector.FromSpeedToward(0.8 * Math.Cos(t / 6000.0), 3.6)),
				PackFixtures.Current("C3", -700.0, 1800.0, t => new HorizontalVector(0.3, -0.2)),
			};
			var gauges = new[]
			{
				PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.7 * Math.Sin(t / 7116.0)),
				PackFixtures.Gauge("G2", 2500.0, 900.0, t => 0.6 * Math.Sin(t / 7116.0 - 0.3)),
			};
			var winds = new[] { PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.FromSpeedFrom(6.0 + Math.Sin(t / 900.0), 4.0)) };
			return new DataEnvironment(PackFixtures.Pack(PackFixtures.Depth(20.0, grid), currents, gauges, winds), start);
		}

		private static double Sample(DataEnvironment environment, int i)
		{
			var time = 0.37 * i;
			var north = -1500.0 + 1.3 * i;
			var east = 900.0 - 0.7 * i;
			return environment.SampleDepth(north, east, time) + environment.SampleCurrent(north, east, time).North +
				environment.SampleCurrent(north + 10.0, east, time).East + environment.SampleWind(north, east, time).East;
		}

		private sealed class RecordingField : ICurrentField
		{
			private readonly HorizontalVector _value;

			public RecordingField(HorizontalVector value)
			{
				_value = value;
			}

			public double North { get; private set; }

			public double East { get; private set; }

			public double PackTime { get; private set; }

			public HorizontalVector Sample(double north, double east, double packTime)
			{
				North = north;
				East = east;
				PackTime = packTime;
				return _value;
			}
		}
	}
}
