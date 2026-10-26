using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Tests.Dynamics;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// The learner's outline against a traffic hull's rectangle. Failure modes guarded: a collision missed when the shapes cross
	/// without a corner inside the other (a cross), when one holds the other wholly, or at a rotated pose; a false collision at
	/// the bounding circles' overlap only; the outline built from the wrong polylines or in the wrong order; the body axes wrong
	/// (starboard to port, or the bow aft).
	/// </summary>
	[TestFixture]
	public class HullOutlineTests
	{
		[Test]
		public void TheTClassOutline_IsItsContactRing()
		{
			var outline = HullOutline.FromContact(ManeuverRunner.Load("t-class.json"));
			Assert.That(outline.Count, Is.EqualTo(16), "8 starboard points and 8 port, the fender lines joining them");
			Assert.That((outline.X(0), outline.Y(0)), Is.EqualTo((-12.25, 3.7)), "starts at the starboard transom");
			Assert.That((outline.X(7), outline.Y(7)), Is.EqualTo((13.76, 2.0)), "the starboard end of the bow fender");
			Assert.That((outline.X(8), outline.Y(8)), Is.EqualTo((13.76, -2.0)), "then across the bow to port");
			Assert.That((outline.X(15), outline.Y(15)), Is.EqualTo((-12.25, -3.7)), "and back down the port side");
			Assert.That(outline.Radius, Is.EqualTo(Math.Sqrt(13.76 * 13.76 + 2.0 * 2.0)).Within(1e-12), "the bow fender's ends are the farthest points");
			Assert.Throws<ArgumentException>(() => HullOutline.FromContact(new VesselParameters()));
		}

		[Test]
		public void BoxesSideBySide_TouchOnlyWhenTheirSidesMeet()
		{
			var outline = TrafficFixtures.Box(20.0, 6.0);
			// Both heading north; the other 10 m east is beam to beam with 3 + 5 = 8 m of half-beams: 2 m apart.
			Assert.That(outline.Overlaps(0, 0, 0, 0, 10.0, 0, 30.0, 10.0), Is.False);
			Assert.That(outline.Overlaps(0, 0, 0, 0, 8.0, 0, 30.0, 10.0), Is.True, "touching counts");
			Assert.That(outline.Overlaps(0, 0, 0, 0, 7.9, 0, 30.0, 10.0), Is.True);
			// Bow to bow: 10 + 15 = 25 m of half-lengths.
			Assert.That(outline.Overlaps(0, 0, 0, 25.1, 0, 0, 30.0, 10.0), Is.False);
			Assert.That(outline.Overlaps(0, 0, 0, 24.9, 0, 0, 30.0, 10.0), Is.True);
		}

		[Test]
		public void ACross_WithNoCornerInsideEither_Overlaps()
		{
			// A long thin hull north-south and a long thin rectangle east-west over it: no vertex of either is inside the other.
			var outline = TrafficFixtures.Box(40.0, 2.0);
			Assert.That(outline.Overlaps(0, 0, 0, 0, 0, Math.PI / 2, 40.0, 2.0), Is.True);
		}

		[Test]
		public void OneWhollyInsideTheOther_Overlaps()
		{
			var small = TrafficFixtures.Box(4.0, 2.0);
			Assert.That(small.Overlaps(0, 0, 0.3, 1.0, 1.0, 0, 200.0, 30.0), Is.True, "the hull inside a ship's rectangle");
			var big = TrafficFixtures.Box(200.0, 30.0);
			Assert.That(big.Overlaps(0, 0, 0, 3.0, -2.0, 1.0, 4.0, 2.0), Is.True, "a small boat's rectangle inside the hull");
		}

		[Test]
		public void BoundingCirclesOverlapping_IsNotACollision()
		{
			var outline = TrafficFixtures.Box(20.0, 2.0);
			// Diagonal neighbours 15.3 m apart, inside the 20.1 m of their bounding circles, but with 1 m of water between their sides.
			Assert.That(outline.Overlaps(0, 0, 0, 15.0, 3.0, 0, 20.0, 2.0), Is.False);
			Assert.That(outline.Overlaps(0, 0, 0, 15.0, 1.9, 0, 20.0, 2.0), Is.True, "and 0.1 m of overlap once they close");
		}

		[Test]
		public void RotatedPoses_UseTheBodyAxes()
		{
			// The learner heading east: its bow is east. A small rectangle 11 m east (just past the 10 m half-length) is clear,
			// 9 m east is hit; 11 m north (abeam to port) is far clear of the 1 m half-beam.
			var outline = TrafficFixtures.Box(20.0, 2.0);
			var east = Math.PI / 2;
			Assert.That(outline.Overlaps(0, 0, east, 0, 11.5, 0, 1.0, 1.0), Is.False);
			Assert.That(outline.Overlaps(0, 0, east, 0, 9.0, 0, 1.0, 1.0), Is.True);
			Assert.That(outline.Overlaps(0, 0, east, 11.0, 0, 0, 1.0, 1.0), Is.False);
			// A T-class's bow fender is 13.76 m forward: a buoy-sized box 14 m ahead is clear, 13.5 m ahead is hit, at any heading.
			var t = HullOutline.FromContact(ManeuverRunner.Load("t-class.json"));
			for (var degrees = 0; degrees < 360; degrees += 15)
			{
				var heading = Angles.ToRadians(degrees);
				Assert.That(t.Overlaps(0, 0, heading, 14.1 * Math.Cos(heading), 14.1 * Math.Sin(heading), 0, 0.4, 0.4), Is.False, $"{degrees}°: clear ahead");
				Assert.That(t.Overlaps(0, 0, heading, 13.5 * Math.Cos(heading), 13.5 * Math.Sin(heading), 0, 0.4, 0.4), Is.True, $"{degrees}°: hit ahead");
				// 4.0 m to starboard is the sheer; 4.3 m is clear, 3.8 m is inside.
				var starboard = heading + Math.PI / 2;
				Assert.That(t.Overlaps(0, 0, heading, 4.3 * Math.Cos(starboard), 4.3 * Math.Sin(starboard), 0, 0.2, 0.2), Is.False, $"{degrees}°: clear to starboard");
				Assert.That(t.Overlaps(0, 0, heading, 3.8 * Math.Cos(starboard), 3.8 * Math.Sin(starboard), 0, 0.2, 0.2), Is.True, $"{degrees}°: hit to starboard");
			}
		}

		[Test]
		public void Overlaps_AllocatesNothing()
		{
			var t = HullOutline.FromContact(ManeuverRunner.Load("t-class.json"));
			var hits = 0;
			for (var k = 0; k < 100; k++)
			{
				hits += t.Overlaps(0, 0, 0.1 * k, 10.0, 0, 0.2 * k, 30.0, 9.0) ? 1 : 0;
			}

			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var k = 0; k < 100000; k++)
			{
				hits += t.Overlaps(0, 0, 0.001 * k, 20.0 + 0.0001 * k, 5.0, 0.002 * k, 30.0, 9.0) ? 1 : 0;
			}

			Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
			Assert.That(hits, Is.GreaterThan(0));
		}
	}
}
