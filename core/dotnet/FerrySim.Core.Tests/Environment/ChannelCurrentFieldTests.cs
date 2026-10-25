using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// The channel-constrained current field on hand-made bases. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Not reproducing a station's velocity at the station (frame resolved wrongly, perpendicular turned the wrong way).</item>
	/// <item>A station's velocity not turning with the local axis, or its across part kept where the cross factor is zero
	/// (normal flow at a shoreline).</item>
	/// <item>Dry cells that do not pull the current to zero, or bilinear weights on the wrong corners (rows and columns swapped).</item>
	/// <item>Points outside every grid not falling back to the station blend, or not counted.</item>
	/// <item>A basis station missing from the day, or a malformed grid (non-unit axis, weights not summing to 1, slots out of
	/// range, weight in an empty slot, factors outside [0, 1], non-finite values), accepted.</item>
	/// <item>The per-time cache serving another time, allocation per sample, or the wrong field chosen by DataEnvironment.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class ChannelCurrentFieldTests
	{
		private static CurrentStation Reversing(string id, double north, double east, double amplitude, double degrees, double phase) =>
			PackFixtures.Current(id, north, east, t => HorizontalVector.FromSpeedToward(amplitude * Math.Sin(t / 7116.0 + phase), Angles.ToRadians(degrees)));

		// A 10 by 10 grid of 20 m cells: station A owns the west half, B the east half, with a blend between; the axis
		// turns from 30° in the west to 60° in the east; row 0 is a dry bank.
		private static (CurrentFieldBasis Basis, CurrentStation[] Stations) TwoStations()
		{
			var grid = FieldFixtures.Grid("g", 0.0, 0.0, 20.0, 10, 10, 2, (r, c) =>
			{
				if (r == 0)
				{
					return FieldFixtures.Cell.Dry;
				}

				var b = Math.Min(1.0, Math.Max(0.0, (c - 3) / 4.0));
				return FieldFixtures.Cell.Of(30.0 + 30.0 * c / 9.0, 1.0, 1.0, (0, 1.0 - b), (1, b));
			});
			var a = FieldFixtures.StationOn(grid, "A", 85.0, 30.0);
			var bStation = FieldFixtures.StationOn(grid, "B", 115.0, 170.0);
			var basis = new CurrentFieldBasis(PackFixtures.Region, new[] { a, bStation }, new[] { grid }, new[] { "synthetic" }, string.Empty);
			var stations = new[] { Reversing("A", 85.0, 30.0, 1.4, 25.0, 0.0), Reversing("B", 115.0, 170.0, 0.9, 70.0, 0.4) };
			return (basis, stations);
		}

		[Test]
		public void AtAStation_IsThatStationsVelocity()
		{
			var (basis, stations) = TwoStations();
			var field = new ChannelCurrentField(basis, stations);
			foreach (var time in new[] { -3600.0, 0.0, 777.7, 12345.6, 45000.0, 90000.0 })
			{
				foreach (var station in stations)
				{
					var expected = station.Velocity.Sample(time);
					var actual = field.Sample(station.North, station.East, time);
					Assert.That(actual.North, Is.EqualTo(expected.North).Within(1e-12), $"{station.Id} north at {time} s");
					Assert.That(actual.East, Is.EqualTo(expected.East).Within(1e-12), $"{station.Id} east at {time} s");
				}
			}

			Assert.That(field.OutsideSamples, Is.EqualTo(0));
		}

		[Test]
		public void AStationsVelocity_TurnsWithTheLocalAxis_AndTheCrossFactorRemovesTheAcrossPart()
		{
			// One station with a frame toward north; the cell under the second point has its axis toward east.
			var grid = FieldFixtures.Grid("g", 0.0, 0.0, 10.0, 3, 6, 1, (r, c) => c < 3 ? FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 1.0)) : FieldFixtures.Cell.Of(90.0, 0.5, c == 5 ? 0.0 : 0.25, (0, 1.0)));
			var basis = new CurrentFieldBasis(PackFixtures.Region, new[] { new CurrentFieldStation("S", 10.0, 10.0, 1.0, 0.0) }, new[] { grid }, null, string.Empty);
			// Toward north-east: along the frame 1.0, across it (turned clockwise, toward east) 0.5.
			var station = PackFixtures.SteadyCurrent("S", 10.0, 10.0, new HorizontalVector(1.0, 0.5));
			var field = new ChannelCurrentField(basis, new[] { station });

			Assert.That(field.Sample(10.0, 10.0, 0.0), Is.EqualTo(new HorizontalVector(1.0, 0.5)));
			// On an axis toward east the along part runs east, scaled by 0.5, and the across part (×0.25) runs south.
			var turned = field.Sample(10.0, 40.0, 0.0);
			Assert.That(turned.East, Is.EqualTo(0.5).Within(1e-7));
			Assert.That(turned.North, Is.EqualTo(-0.125).Within(1e-7));
			// Where the cross factor is zero only the along part is left: no flow across the axis.
			var tangent = field.Sample(10.0, 50.0, 0.0);
			Assert.That(tangent.North, Is.EqualTo(0.0).Within(1e-7));
			Assert.That(tangent.East, Is.EqualTo(0.5).Within(1e-7));
		}

		[Test]
		public void AlongTheShore_TheFlowKeepsItsStrengthToTheShoreline_AndFadesOverLand()
		{
			// Row 1 (20 m north) is the last water row, row 0 (0 m) the dry bank: the shoreline is halfway, at 10 m.
			var (basis, stations) = TwoStations();
			var field = new ChannelCurrentField(basis, stations);
			var time = 2000.0;
			var wet = field.Sample(20.0, 60.0, time);
			Assert.That(wet.Magnitude, Is.GreaterThan(0.1), "row 1 is water");
			Assert.That(field.Sample(10.0, 60.0, time), Is.EqualTo(wet), "at the shoreline the water cell counts in full");
			var land = field.Sample(5.0, 60.0, time);
			Assert.That(land.North, Is.EqualTo(0.5 * wet.North).Within(1e-12), "a quarter of the weight on water, over the floor of a half");
			Assert.That(land.East, Is.EqualTo(0.5 * wet.East).Within(1e-12));
			Assert.That(field.Sample(0.0, 60.0, time), Is.EqualTo(HorizontalVector.Zero), "nothing at the dry centre");
			Assert.That(field.Sample(0.001, 60.0, time).Magnitude, Is.LessThan(1e-3 * wet.Magnitude), "and continuous toward it");
		}

		[Test]
		public void OutsideEveryGrid_IsTheStationBlend_AndIsCounted()
		{
			var (basis, stations) = TwoStations();
			var field = new ChannelCurrentField(basis, stations);
			var blend = new StationBlendCurrentField(stations);
			foreach (var (north, east) in new[] { (-0.001, 50.0), (180.001, 50.0), (50.0, -5.0), (50.0, 181.0), (1000.0, -1000.0) })
			{
				Assert.That(field.Sample(north, east, 1234.0), Is.EqualTo(blend.Sample(north, east, 1234.0)), $"({north}, {east})");
			}

			Assert.That(field.OutsideSamples, Is.EqualTo(5));
			Assert.That(field.Sample(180.0, 180.0, 1234.0), Is.Not.EqualTo(blend.Sample(180.0, 180.0, 1234.0)), "the last centres are inside");
			Assert.That(field.OutsideSamples, Is.EqualTo(5));
		}

		[Test]
		public void ABasisStationTheDayDoesNotHave_IsRefused()
		{
			var (basis, stations) = TwoStations();
			Assert.That(() => new ChannelCurrentField(basis, new[] { stations[0] }), Throws.ArgumentException.With.Message.Contains("B"));
			var depth = PackFixtures.Depth(20.0, PackFixtures.FlatGrid("d", -100.0, -100.0, 10.0, 41, 41, -10.0f));
			var gauges = new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) };
			Assert.That(() => new EnvironmentPack(PackFixtures.Region, PackFixtures.Date, PackFixtures.Epoch, -3600.0, 90000.0, depth,
				new[] { stations[1] }, gauges, Array.Empty<WindStation>(), null, string.Empty, basis), Throws.ArgumentException.With.Message.Contains("A"));
			Assert.That(() => new EnvironmentPack("upper-bay", PackFixtures.Date, PackFixtures.Epoch, -3600.0, 90000.0, depth,
				stations, gauges, Array.Empty<WindStation>(), null, string.Empty, basis), Throws.ArgumentException.With.Message.Contains("upper-bay"));
		}

		private static IEnumerable<TestCaseData> MalformedCells()
		{
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 0.6), (1, 0.3))), "summing to 1").SetName("weights not summing to 1");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => { var c = FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 1.0)); c.AxisNorth = 0.9; return c; }), "unit axis").SetName("axis not unit length");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (2, 1.0))), "slot 2").SetName("slot naming no station");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => { var c = FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 1.0), (FieldFixtures.E, 0.1)); return c; }), "empty slot").SetName("weight in an empty slot");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => FieldFixtures.Cell.Of(0.0, 1.2, 1.0, (0, 1.0))), "outside [0, 1]").SetName("along factor above 1");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => FieldFixtures.Cell.Of(0.0, 1.0, -0.1, (0, 1.0))), "outside [0, 1]").SetName("negative cross factor");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => FieldFixtures.Cell.Of(0.0, double.NaN, 1.0, (0, 1.0))), "not finite").SetName("NaN factor");
			yield return new TestCaseData(new Func<FieldFixtures.Cell>(() => { var c = FieldFixtures.Cell.Dry; c.Along = 0.5; return c; }), "all zeros").SetName("dry cell with a factor");
		}

		[TestCase(0)]
		[TestCase(9)]
		public void SlotsOutsideOneToEight_AreRefused(int slots)
		{
			Assert.That(() => FieldFixtures.Grid("g", 0.0, 0.0, 10.0, 2, 2, 1, (r, c) => FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 1.0)), slots),
				Throws.ArgumentException.With.Message.Contains("slots must be 1 to 8"));
		}

		[TestCaseSource(nameof(MalformedCells))]
		public void AMalformedGrid_IsRefused(Func<FieldFixtures.Cell> bad, string expected)
		{
			Assert.That(() => FieldFixtures.Grid("g", 0.0, 0.0, 10.0, 2, 2, 2, (r, c) => r == 1 && c == 1 ? bad() : FieldFixtures.Cell.Of(0.0, 1.0, 1.0, (0, 1.0))),
				Throws.ArgumentException.With.Message.Contains(expected));
		}

		[Test]
		public void ReversingCurrent_GoesSlackBetweenSamples()
		{
			var grid = FieldFixtures.Grid("g", 0.0, 0.0, 10.0, 2, 2, 1, (r, c) => FieldFixtures.Cell.Of(45.0, 1.0, 1.0, (0, 1.0)));
			var basis = new CurrentFieldBasis(PackFixtures.Region, new[] { FieldFixtures.StationOn(grid, "S", 5.0, 5.0) }, new[] { grid }, null, string.Empty);
			var station = PackFixtures.Current("S", 5.0, 5.0, t => t < 3600.0 ? new HorizontalVector(1.0, 1.0) : new HorizontalVector(-1.0, -1.0));
			var field = new ChannelCurrentField(basis, new[] { station });
			Assert.That(field.Sample(2.0, 8.0, 3420.0).Magnitude, Is.LessThan(1e-12), "halfway between flood and ebb samples");
		}

		[Test]
		public void TheTimeCache_NeverServesAnotherTime()
		{
			var (basis, stations) = TwoStations();
			var cached = new ChannelCurrentField(basis, stations);
			var random = new Random(5);
			for (var i = 0; i < 300; i++)
			{
				var time = i % 3 == 0 ? 500.0 : 90000.0 * random.NextDouble();
				var north = 180.0 * random.NextDouble();
				var east = 180.0 * random.NextDouble();
				Assert.That(cached.Sample(north, east, time), Is.EqualTo(new ChannelCurrentField(basis, stations).Sample(north, east, time)), $"sample {i}");
			}
		}

		[Test]
		public void Sampling_DoesNotAllocate()
		{
			var (basis, stations) = TwoStations();
			var field = new ChannelCurrentField(basis, stations);
			var sink = field.Sample(1.0, 2.0, 3.0).North;
			var allocated = -1L;
			for (var round = 0; round < 3 && allocated != 0; round++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var i = 0; i < 10000; i++)
				{
					// Inside, outside (station blend) and a new time every few samples.
					sink += field.Sample(i % 190, (7 * i) % 190, 0.5 * (i / 3)).East;
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			}

			Assert.That(allocated, Is.EqualTo(0));
			Assert.That(double.IsNaN(sink), Is.False);
		}

		[Test]
		public void DataEnvironment_UsesTheChannelField_WhenThePackHasOne()
		{
			var (basis, stations) = TwoStations();
			var depth = PackFixtures.Depth(20.0, PackFixtures.FlatGrid("d", -100.0, -100.0, 10.0, 41, 41, -10.0f));
			var gauges = new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) };
			var withField = new EnvironmentPack(PackFixtures.Region, PackFixtures.Date, PackFixtures.Epoch, -3600.0, 90000.0, depth,
				stations, gauges, Array.Empty<WindStation>(), null, string.Empty, basis);
			var environment = new DataEnvironment(withField, PackFixtures.Epoch.AddHours(2.0));
			Assert.That(environment.CurrentField, Is.TypeOf<ChannelCurrentField>());
			Assert.That(environment.SampleCurrent(50.0, 120.0, 30.0), Is.EqualTo(new ChannelCurrentField(basis, stations).Sample(50.0, 120.0, 7230.0)));

			var without = PackFixtures.Pack(depth, stations, gauges, Array.Empty<WindStation>());
			Assert.That(without.CurrentField, Is.Null);
			Assert.That(new DataEnvironment(without, PackFixtures.Epoch).CurrentField, Is.TypeOf<StationBlendCurrentField>());
		}
	}
}
