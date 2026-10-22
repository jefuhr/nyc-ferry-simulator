using System;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	public class ValueTypeTests
	{
		[Test]
		public void ControlInputs_ClampToUnitRange()
		{
			var inputs = new ControlInputs(3.0, -7.0, 1.5);
			Assert.That(inputs.PortLever, Is.EqualTo(1.0));
			Assert.That(inputs.StarboardLever, Is.EqualTo(-1.0));
			Assert.That(inputs.Helm, Is.EqualTo(1.0));
		}

		[Test]
		public void ControlInputs_TreatNaNAsNeutral()
		{
			var inputs = new ControlInputs(double.NaN, double.NaN, double.NaN);
			Assert.That(inputs, Is.EqualTo(ControlInputs.Neutral));
		}

		[Test]
		public void FlowToward_East_PointsEast()
		{
			var current = HorizontalVector.FromSpeedToward(2.0, Angles.ToRadians(90.0));
			Assert.That(current.North, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(current.East, Is.EqualTo(2.0).Within(1e-12));
			Assert.That(Angles.ToDegrees(current.Direction), Is.EqualTo(90.0).Within(1e-9));
		}

		[Test]
		public void WindFrom_North_PointsSouth()
		{
			var wind = HorizontalVector.FromSpeedFrom(10.0, 0.0);
			Assert.That(wind.North, Is.EqualTo(-10.0).Within(1e-12));
			Assert.That(wind.East, Is.EqualTo(0.0).Within(1e-9));
		}

		[Test]
		public void VesselState_WrapsHeadingAndReportsGroundVelocity()
		{
			// Heading 450° is 090°: moving ahead at 3 m/s means moving east.
			var state = VesselState.Planar(0.0, 0.0, 0.0, Angles.ToRadians(450.0), surge: 3.0);
			Assert.That(Angles.ToDegrees(state.Heading), Is.EqualTo(90.0).Within(1e-9));
			Assert.That(state.GroundVelocity.North, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(state.GroundVelocity.East, Is.EqualTo(3.0).Within(1e-12));
		}

		[Test]
		public void VesselState_SwayToStarboard_OnNorthHeading_MovesEast()
		{
			var state = VesselState.Planar(0.0, 0.0, 0.0, 0.0, sway: 1.0);
			Assert.That(state.GroundVelocity.East, Is.EqualTo(1.0).Within(1e-12));
		}

		[Test]
		public void VesselState_DetectsNonFiniteValues()
		{
			Assert.That(VesselState.Planar(0.0, 1.0, 2.0, 0.5).IsFinite(), Is.True);
			Assert.That(VesselState.Planar(0.0, double.NaN, 2.0, 0.5).IsFinite(), Is.False);
		}

		[Test]
		public void UniformEnvironment_RejectsNonFiniteValues()
		{
			Assert.Throws<ArgumentException>(() => new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, double.NaN));
		}

		[Test]
		public void SimulationClock_TimeIsDerivedFromStepCount()
		{
			Assert.That(SimulationClock.FixedTimeStep, Is.EqualTo(0.01));
			Assert.That(SimulationClock.TimeAtStep(360000), Is.EqualTo(3600.0));
		}
	}
}
