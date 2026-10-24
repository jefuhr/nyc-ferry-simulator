using FerrySim.Core.Contracts;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The conventions' world-to-Unity mapping and render interpolation. Failure modes guarded: swapped axes
	/// (x = north), down drawn as up, heading turning counter-clockwise, pitch or roll signs flipped by Unity's
	/// left-handedness, precision lost by casting kilometre-scale positions to float before subtracting the
	/// render origin, and heading interpolation taking the long way round through 180°.
	/// </summary>
	public class WorldToUnityTests
	{
		private const float Tight = 1e-5f;

		[Test]
		public void Position_IsEastUpNorth()
		{
			var position = WorldToUnity.Position(north: 10.0, east: 20.0, down: 0.5, originNorth: 0.0, originEast: 0.0);

			Assert.That(position.x, Is.EqualTo(20f));
			Assert.That(position.y, Is.EqualTo(-0.5f));
			Assert.That(position.z, Is.EqualTo(10f));
		}

		[Test]
		public void Position_SubtractsTheRenderOriginInDoublePrecision()
		{
			// 20 km out a float has about 2 mm resolution; the offset from a nearby origin keeps micrometres.
			var position = WorldToUnity.Position(20000.123456, -15000.654321, 0.0, 20000.0, -15000.0);

			Assert.That(position.z, Is.EqualTo(0.123456f).Within(1e-6f));
			Assert.That(position.x, Is.EqualTo(-0.654321f).Within(1e-6f));
		}

		[TestCase(0.0, 0f, 0f, 1f)]
		[TestCase(90.0, 1f, 0f, 0f)]
		[TestCase(180.0, 0f, 0f, -1f)]
		[TestCase(270.0, -1f, 0f, 0f)]
		[TestCase(45.0, 0.70710678f, 0f, 0.70710678f)]
		public void Heading_IsAClockwiseYawFromNorth(double headingDegrees, float x, float y, float z)
		{
			var forward = WorldToUnity.Rotation(Angles.ToRadians(headingDegrees), 0.0, 0.0) * Vector3.forward;

			Assert.That(forward.x, Is.EqualTo(x).Within(Tight));
			Assert.That(forward.y, Is.EqualTo(y).Within(Tight));
			Assert.That(forward.z, Is.EqualTo(z).Within(Tight));
		}

		[Test]
		public void PositivePitch_RaisesTheBow()
		{
			var forward = WorldToUnity.Rotation(0.0, Angles.ToRadians(10.0), 0.0) * Vector3.forward;

			Assert.That(forward.y, Is.EqualTo(Mathf.Sin(10f * Mathf.Deg2Rad)).Within(Tight));
			Assert.That(forward.z, Is.GreaterThan(0f));
		}

		[Test]
		public void PositiveRoll_PutsTheStarboardSideDown()
		{
			var starboard = WorldToUnity.Rotation(0.0, 0.0, Angles.ToRadians(10.0)) * Vector3.right;

			Assert.That(starboard.y, Is.EqualTo(-Mathf.Sin(10f * Mathf.Deg2Rad)).Within(Tight));
		}

		[Test]
		public void Attitude_IsYawThenPitchThenRollInTheBodyFrame()
		{
			// Heading east with the bow up: forward is east and up, whatever the roll.
			var rotation = WorldToUnity.Rotation(Angles.ToRadians(90.0), Angles.ToRadians(10.0), Angles.ToRadians(5.0));
			var forward = rotation * Vector3.forward;
			var starboard = rotation * Vector3.right;

			Assert.That(forward.x, Is.EqualTo(Mathf.Cos(10f * Mathf.Deg2Rad)).Within(Tight));
			Assert.That(forward.y, Is.EqualTo(Mathf.Sin(10f * Mathf.Deg2Rad)).Within(Tight));
			Assert.That(forward.z, Is.EqualTo(0f).Within(Tight));
			Assert.That(starboard.y, Is.LessThan(0f), "starboard down");
			Assert.That(starboard.z, Is.LessThan(0f), "heading east, starboard points south");
		}

		[Test]
		public void Interpolate_HeadingTakesTheShortWayRound()
		{
			var from = VesselState.Planar(0.0, 0.0, 0.0, Angles.ToRadians(359.0));
			var to = VesselState.Planar(0.01, 2.0, 4.0, Angles.ToRadians(1.0));

			var middle = VesselPose.Interpolate(from, to, 0.5);

			Assert.That(Angles.WrapPi(middle.Heading), Is.EqualTo(0.0).Within(1e-12));
			Assert.That(middle.North, Is.EqualTo(1.0).Within(1e-12));
			Assert.That(middle.East, Is.EqualTo(2.0).Within(1e-12));
		}

		[Test]
		public void Interpolate_EndsAreTheStatesThemselves()
		{
			var from = new VesselState(0.0, 1.0, 2.0, 0.1, 0.01, -0.02, 3.0, 5.0, 0.2, 0.0, 0.0, 0.0, 0.01);
			var to = new VesselState(0.01, 1.05, 2.01, 0.11, 0.012, -0.021, 3.001, 5.0, 0.2, 0.0, 0.0, 0.0, 0.01);

			var start = VesselPose.Interpolate(from, to, 0.0);
			var end = VesselPose.Interpolate(from, to, 1.0);
			var clamped = VesselPose.Interpolate(from, to, 7.0);

			Assert.That(start.North, Is.EqualTo(from.North));
			Assert.That(start.Heading, Is.EqualTo(from.Heading).Within(1e-15));
			Assert.That(end.East, Is.EqualTo(to.East));
			Assert.That(end.Roll, Is.EqualTo(to.Roll));
			Assert.That(clamped.North, Is.EqualTo(to.North), "factors beyond 1 never extrapolate");
		}

		[Test]
		public void TangentPlanePlacement_PlacesAgainstTheRenderOriginAndWaterSurface()
		{
			var target = new GameObject("placement-target").transform;
			try
			{
				var placement = new TangentPlanePlacement { RenderOriginNorth = 1000.0, RenderOriginEast = -500.0, WaterSurfaceHeight = 2.0 };

				placement.Place(target, new VesselPose(1010.0, -480.0, 0.25, Angles.ToRadians(90.0), 0.0, 0.0), 0.0);

				Assert.That(target.localPosition.x, Is.EqualTo(20f).Within(Tight));
				Assert.That(target.localPosition.y, Is.EqualTo(1.75f).Within(Tight));
				Assert.That(target.localPosition.z, Is.EqualTo(10f).Within(Tight));
				Assert.That((target.localRotation * Vector3.forward).x, Is.EqualTo(1f).Within(Tight));
				Assert.That(placement.ToUnity(1010.0, -480.0), Is.EqualTo(new Vector3(20f, 2f, 10f)));
			}
			finally
			{
				Object.DestroyImmediate(target.gameObject);
			}
		}
	}
}
