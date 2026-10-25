using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// The provisional station-blend current field. Failure modes guarded:
	/// <list type="bullet">
	/// <item>North and east swapped or negated, or "set" (toward) confused with "from".</item>
	/// <item>The blend at a station not equal to that station (or NaN from a zero distance).</item>
	/// <item>A blend outside the stations' range, or weights that are not inverse distance squared.</item>
	/// <item>A single station that is not exactly uniform, so the hull would feel a spurious shear.</item>
	/// <item>Interpolating speed and direction instead of components, so a reversing current never goes slack.</item>
	/// <item>The per-time cache returning another time's values.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class StationBlendCurrentFieldTests
	{
		[Test]
		public void Components_PointTheWayTheWaterFlows()
		{
			var east = new StationBlendCurrentField(new[] { PackFixtures.SteadyCurrent("E", 0.0, 0.0, HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(90.0))) });
			var flowEast = east.Sample(100.0, -50.0, 0.0);
			Assert.That(flowEast.North, Is.EqualTo(0.0).Within(1e-15));
			Assert.That(flowEast.East, Is.EqualTo(1.0).Within(1e-15), "a current setting 090 has a positive east component");

			var southWest = new StationBlendCurrentField(new[] { PackFixtures.SteadyCurrent("SW", 0.0, 0.0, HorizontalVector.FromSpeedToward(2.0, Angles.ToRadians(225.0))) });
			var flowSouthWest = southWest.Sample(0.0, 0.0, 0.0);
			Assert.That(flowSouthWest.North, Is.EqualTo(-Math.Sqrt(2.0)).Within(1e-12), "setting 225 flows south");
			Assert.That(flowSouthWest.East, Is.EqualTo(-Math.Sqrt(2.0)).Within(1e-12), "and west");
			Assert.That(flowSouthWest.Direction, Is.EqualTo(Angles.ToRadians(225.0)).Within(1e-12));
		}

		[Test]
		public void AtAStation_IsThatStationExactly()
		{
			var stations = Three();
			var field = new StationBlendCurrentField(stations);
			foreach (var time in new[] { -3600.0, 0.0, 777.7, 45000.0, 90000.0 })
			{
				foreach (var station in stations)
				{
					Assert.That(field.Sample(station.North, station.East, time), Is.EqualTo(station.Velocity.Sample(time)), $"{station.Id} at {time} s");
				}
			}
		}

		[Test]
		public void Between_IsAnInverseDistanceSquaredBlend_InsideTheStationsRange()
		{
			var a = PackFixtures.SteadyCurrent("A", 0.0, 0.0, new HorizontalVector(1.0, 0.0));
			var b = PackFixtures.SteadyCurrent("B", 0.0, 1000.0, new HorizontalVector(0.0, -1.0));
			var field = new StationBlendCurrentField(new[] { a, b });
			var quarter = field.Sample(0.0, 250.0, 0.0);
			Assert.That(quarter.North, Is.EqualTo(0.9).Within(1e-12), "weights 9 to 1 a quarter of the way along");
			Assert.That(quarter.East, Is.EqualTo(-0.1).Within(1e-12));

			var stations = Three();
			var three = new StationBlendCurrentField(stations);
			var random = new Random(7);
			for (var i = 0; i < 500; i++)
			{
				var north = -3000.0 + 6000.0 * random.NextDouble();
				var east = -3000.0 + 6000.0 * random.NextDouble();
				var time = 90000.0 * random.NextDouble();
				var blend = three.Sample(north, east, time);
				var minNorth = double.MaxValue;
				var maxNorth = double.MinValue;
				var minEast = double.MaxValue;
				var maxEast = double.MinValue;
				foreach (var station in stations)
				{
					var v = station.Velocity.Sample(time);
					minNorth = Math.Min(minNorth, v.North);
					maxNorth = Math.Max(maxNorth, v.North);
					minEast = Math.Min(minEast, v.East);
					maxEast = Math.Max(maxEast, v.East);
				}

				Assert.That(blend.North, Is.InRange(minNorth - 1e-12, maxNorth + 1e-12));
				Assert.That(blend.East, Is.InRange(minEast - 1e-12, maxEast + 1e-12));
			}
		}

		[Test]
		public void OneStation_IsUniformInEveryBit()
		{
			var station = PackFixtures.Current("A", 300.0, -200.0, t => HorizontalVector.FromSpeedToward(0.9 + 0.3 * Math.Sin(t / 5000.0), 1.1));
			var field = new StationBlendCurrentField(new[] { station });
			var expected = station.Velocity.Sample(1234.5);
			for (var north = -5000.0; north <= 5000.0; north += 333.3)
			{
				Assert.That(field.Sample(north, 17.0 * north, 1234.5), Is.EqualTo(expected));
			}
		}

		[Test]
		public void ReversingCurrent_GoesSlackBetweenSamples()
		{
			// Flood north at 1 m/s, then ebb south at 1 m/s six minutes later: the components pass through zero.
			var times = PackFixtures.Times();
			var station = PackFixtures.Current("A", 0.0, 0.0, t => t < 3600.0 ? new HorizontalVector(1.0, 0.0) : new HorizontalVector(-1.0, 0.0), times);
			var field = new StationBlendCurrentField(new[] { station });
			var slack = field.Sample(0.0, 0.0, 3420.0);
			Assert.That(slack.Magnitude, Is.LessThan(1e-12), "halfway between flood and ebb samples");
			Assert.That(field.Sample(0.0, 0.0, 3330.0).North, Is.EqualTo(0.5).Within(1e-12));
		}

		[Test]
		public void TheTimeCache_NeverServesAnotherTime()
		{
			var stations = Three();
			var cached = new StationBlendCurrentField(stations);
			var random = new Random(11);
			for (var i = 0; i < 300; i++)
			{
				var time = i % 3 == 0 ? 500.0 : 90000.0 * random.NextDouble();
				var north = -2000.0 + 4000.0 * random.NextDouble();
				var east = -2000.0 + 4000.0 * random.NextDouble();
				var fresh = new StationBlendCurrentField(stations).Sample(north, east, time);
				Assert.That(cached.Sample(north, east, time), Is.EqualTo(fresh), $"sample {i}");
			}
		}

		[Test]
		public void Sampling_DoesNotAllocate()
		{
			var field = new StationBlendCurrentField(Three());
			var sink = 0.0;
			sink += field.Sample(1.0, 2.0, 3.0).North;
			// The runtime may allocate once while it recompiles a hot method; an allocation per sample shows in every round.
			var allocated = -1L;
			for (var round = 0; round < 3 && allocated != 0; round++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var i = 0; i < 10000; i++)
				{
					sink += field.Sample(i, -i, 0.5 * i).East;
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			}

			Assert.That(allocated, Is.EqualTo(0));
			Assert.That(double.IsNaN(sink), Is.False);
		}

		private static CurrentStation[] Three() => new[]
		{
			PackFixtures.Current("A", 0.0, 0.0, t => HorizontalVector.FromSpeedToward(1.5 * Math.Sin(t / 7116.0), Angles.ToRadians(40.0))),
			PackFixtures.Current("B", 1800.0, 400.0, t => HorizontalVector.FromSpeedToward(0.9 * Math.Cos(t / 7116.0), Angles.ToRadians(200.0))),
			PackFixtures.Current("C", -900.0, -1600.0, t => new HorizontalVector(0.2, -0.4 + 1e-5 * t)),
		};
	}
}
