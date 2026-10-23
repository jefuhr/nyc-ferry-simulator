using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	public class WorldGeometryTests
	{
		private static readonly ContactSurface Fender = new ContactSurface(2.0e5, 2.0e4, 0.3, 0.25);

		[Test]
		public void SegmentRunningNorth_IsSolidToTheEast()
		{
			var segment = new ContactSegment(new HorizontalVector(0.0, 0.0), new HorizontalVector(10.0, 0.0), Fender, 1);
			Assert.That(segment.WaterNormal.North, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(segment.WaterNormal.East, Is.EqualTo(-1.0).Within(1e-12), "water is on the west (left) side");
			Assert.That(segment.Length, Is.EqualTo(10.0).Within(1e-12));
		}

		[Test]
		public void SegmentRunningEast_IsSolidToTheSouth()
		{
			var segment = new ContactSegment(new HorizontalVector(0.0, 0.0), new HorizontalVector(0.0, 5.0), Fender, 1);
			Assert.That(segment.WaterNormal.North, Is.EqualTo(1.0).Within(1e-12), "water is on the north (left) side");
		}

		[TestCase(5.0, -3.0, 3.0)]
		[TestCase(-4.0, 0.0, 4.0)]
		[TestCase(13.0, 4.0, 5.0)]
		public void DistanceTo_UsesTheClosestPointOnTheSegment(double north, double east, double expected)
		{
			var segment = new ContactSegment(new HorizontalVector(0.0, 0.0), new HorizontalVector(10.0, 0.0), Fender, 1);
			Assert.That(segment.DistanceTo(north, east), Is.EqualTo(expected).Within(1e-12));
		}

		[Test]
		public void DegenerateSegment_IsRejected()
		{
			Assert.Throws<ArgumentException>(() => new ContactSegment(new HorizontalVector(1.0, 1.0), new HorizontalVector(1.0, 1.0), Fender, 1));
		}

		[Test]
		public void InvalidSurface_IsRejected()
		{
			Assert.Throws<ArgumentException>(() => new ContactSurface(0.0, 1.0, 0.3, 0.1));
			Assert.Throws<ArgumentException>(() => new ContactSurface(1.0, -1.0, 0.3, 0.1));
			Assert.Throws<ArgumentException>(() => new ContactSurface(1.0, 1.0, double.NaN, 0.1));
		}

		[Test]
		public void SegmentList_ReturnsNearbySegmentsInStoredOrderWithinTheBuffer()
		{
			var geometry = new SegmentListGeometry(new[]
			{
				new ContactSegment(new HorizontalVector(0.0, 0.0), new HorizontalVector(10.0, 0.0), Fender, 1),
				new ContactSegment(new HorizontalVector(0.0, 50.0), new HorizontalVector(10.0, 50.0), Fender, 2),
				new ContactSegment(new HorizontalVector(0.0, 5.0), new HorizontalVector(10.0, 5.0), Fender, 3),
			});

			var buffer = new ContactSegment[8];
			var count = geometry.QuerySegments(5.0, 2.0, 4.0, buffer);
			Assert.That(count, Is.EqualTo(2));
			Assert.That(buffer[0].Id, Is.EqualTo(1));
			Assert.That(buffer[1].Id, Is.EqualTo(3));

			var small = new ContactSegment[1];
			Assert.That(geometry.QuerySegments(5.0, 2.0, 4.0, small), Is.EqualTo(1), "never writes past the buffer");
		}

		[Test]
		public void EmptyGeometry_HasNoSegments()
		{
			Assert.That(EmptyWorldGeometry.Instance.QuerySegments(0.0, 0.0, 1.0e6, new ContactSegment[4]), Is.EqualTo(0));
		}

		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void FactoryWithEmptyGeometry_MatchesOpenWaterFactory(string vesselFile)
		{
			var parameters = VesselParametersJson.Load(RepoPaths.Vessel(vesselFile));
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.3);
			var factory = new VesselDynamicsFactory();
			var openWater = factory.Create(parameters, UniformEnvironment.Calm, initial);
			var withGeometry = factory.Create(parameters, UniformEnvironment.Calm, EmptyWorldGeometry.Instance, initial);
			for (var step = 0; step < 20 * SimulationClock.StepsPerSecond; step++)
			{
				var inputs = new ControlInputs(0.8, 0.6, 0.2);
				openWater.Step(inputs);
				withGeometry.Step(inputs);
			}

			Assert.That(withGeometry.State, Is.EqualTo(openWater.State));
			Assert.That(withGeometry.Telemetry.ContactForce, Is.EqualTo(0.0));
		}
	}
}
