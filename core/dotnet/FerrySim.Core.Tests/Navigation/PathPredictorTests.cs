using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: north/east swapped, port curvature sign wrong, cancellation near zero rate, endpoint
	// omitted, stationary boat moving, non-finite input silently leaking, or allocation on every display draw.
	public class PathPredictorTests
	{
		[TestCase(0)]
		[TestCase(1e-14)]
		public void StraightLineIncludesBothEndpoints(double rate)
		{
			var points = new HorizontalVector[3];
			PathPredictor.Sample(new HorizontalVector(10, 20), 2, Math.PI / 2, rate, 10, points);
			Assert.That(points[0], Is.EqualTo(new HorizontalVector(10, 20)));
			Assert.That(points[1].East, Is.EqualTo(30).Within(1e-10));
			Assert.That(points[2].East, Is.EqualTo(40).Within(1e-10));
			Assert.That(points[2].North, Is.EqualTo(10).Within(1e-10));
		}

		[TestCase(1)]
		[TestCase(-1)]
		public void QuarterCircleTurnsInTheRequestedDirection(int direction)
		{
			var points = new HorizontalVector[5];
			PathPredictor.Sample(default, 10, 0, direction * .1, Math.PI * 5, points);
			Assert.That(points[4].North, Is.EqualTo(100).Within(1e-9));
			Assert.That(points[4].East, Is.EqualTo(direction * 100).Within(1e-9));
		}

		[Test]
		public void StationaryAndFullCircle_AndNoAllocation()
		{
			var points = new HorizontalVector[10];
			PathPredictor.Sample(default, 0, 1, 1, 20, points);
			Assert.That(points[9], Is.EqualTo(HorizontalVector.Zero));
			var before = GC.GetAllocatedBytesForCurrentThread();
			PathPredictor.Sample(default, 10, 1, .1, 20 * Math.PI, points);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.That(allocated, Is.Zero);
			Assert.That(points[9].Magnitude, Is.LessThan(1e-10));
			Assert.Throws<ArgumentOutOfRangeException>(() => PathPredictor.Sample(default, 1, 0, 0, -1, points));
			Assert.Throws<ArgumentOutOfRangeException>(() => PathPredictor.Sample(default, double.NaN, 0, 0, 1, points));
			Assert.Throws<ArgumentException>(() => PathPredictor.Sample(default, 1, 0, 0, 1, new HorizontalVector[1]));
		}
	}
}
