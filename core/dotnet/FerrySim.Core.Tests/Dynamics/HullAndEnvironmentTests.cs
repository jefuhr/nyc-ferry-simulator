using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Hull, shallow-water and wind modules. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Resistance not growing with speed, or not opposing the motion astern.</item>
	/// <item>Sway and yaw forces that feed the motion instead of damping it (at speed and at rest).</item>
	/// <item>Shallow water lowering resistance, or blowing up as depth reaches the draft, zero or below.</item>
	/// <item>Wind from/toward confusion that pushes the boat upwind; yaw moment of the wrong sense.</item>
	/// <item>A force from still air on a boat drifting with the water (the conformance tests' assumption).</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class HullAndEnvironmentTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;
		private HullModel _hull;
		private ShallowWaterModel _shallow;
		private WindageModel _windage;

		public HullAndEnvironmentTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
			_hull = new HullModel(_parameters);
			_shallow = new ShallowWaterModel(_parameters);
			_windage = new WindageModel(_parameters);
		}

		[Test]
		public void Resistance_GrowsWithSpeed_AndOpposesMotionBothWays()
		{
			var deep = ShallowWaterFactors.Deep;
			var previous = 0.0;
			for (var knots = 0.5; knots <= 35.0; knots += 0.5)
			{
				var resistance = _hull.Resistance(Units.KnotsToMetresPerSecond(knots), deep);
				Assert.That(resistance, Is.GreaterThan(previous), $"{knots} kn");
				previous = resistance;
			}

			_hull.Forces(5.0, 0.0, 0.0, deep, out var ahead, out _, out _);
			_hull.Forces(-5.0, 0.0, 0.0, deep, out var astern, out _, out _);
			Assert.That(ahead, Is.LessThan(0.0));
			Assert.That(astern, Is.GreaterThan(-ahead), "going astern is harder than going ahead");
		}

		[TestCase(10.0)]
		[TestCase(0.0)]
		[TestCase(-6.0)]
		public void SwayAndYaw_AreDamped_AtSpeedAndAtRest(double surge)
		{
			var deep = ShallowWaterFactors.Deep;
			_hull.Forces(surge, 1.0, 0.0, deep, out _, out var swayForce, out _);
			_hull.Forces(surge, 0.0, 0.05, deep, out _, out _, out var yawMoment);
			Assert.That(swayForce, Is.LessThan(0.0), "drifting to starboard is resisted");
			Assert.That(yawMoment, Is.LessThan(0.0), "turning to starboard is resisted");
		}

		[Test]
		public void ShallowWater_IncreasesResistance()
		{
			// Not monotonic in depth at high speed: the depth-critical hump sits near depth Froude number 1,
			// so moderately deep water can cost more than very shallow water at the same speed. Every finite
			// depth costs at least as much as deep water, and water barely deeper than the draft costs much more.
			var draft = _parameters.Particulars.Draft.Value;
			foreach (var knots in new[] { 5.0, 12.0, 18.0, 25.0 })
			{
				var speed = Units.KnotsToMetresPerSecond(knots);
				var deep = _hull.Resistance(speed, ShallowWaterFactors.Deep);
				foreach (var depthRatio in new[] { 40.0, 20.0, 8.0, 4.0, 2.0, 1.5, 1.2 })
				{
					var shallow = _hull.Resistance(speed, _shallow.Evaluate(depthRatio * draft, speed));
					Assert.That(shallow, Is.GreaterThanOrEqualTo(deep), $"{knots} kn at h/T {depthRatio}");
				}

				var veryShallow = _hull.Resistance(speed, _shallow.Evaluate(1.2 * draft, speed));
				Assert.That(veryShallow, Is.GreaterThan(1.4 * deep), $"{knots} kn: h/T 1.2 must cost clearly more than deep water");
			}
		}

		[Test]
		public void ShallowWater_HasADepthCriticalHump()
		{
			var depth = 10.0;
			var critical = Math.Sqrt(PhysicalConstants.Gravity * depth);
			var atCritical = _shallow.Evaluate(depth, 0.95 * critical).ResiduaryResistance;
			var subcritical = _shallow.Evaluate(depth, 0.5 * critical).ResiduaryResistance;
			var supercritical = _shallow.Evaluate(depth, 1.6 * critical).ResiduaryResistance;
			Assert.That(atCritical, Is.GreaterThan(subcritical));
			Assert.That(atCritical, Is.GreaterThan(supercritical));
		}

		[Test]
		public void ShallowWater_StaysFiniteAsDepthReachesTheDraftAndBelow()
		{
			var draft = _parameters.Particulars.Draft.Value;
			foreach (var depth in new[] { 1.5 * draft, 1.06 * draft, 1.01 * draft, draft, 0.5 * draft, 0.0, -3.0, double.NaN })
			{
				var factors = _shallow.Evaluate(depth, 12.0);
				foreach (var factor in new[] { factors.ViscousResistance, factors.ResiduaryResistance, factors.LinearDerivatives, factors.CrossFlow, factors.AddedMass })
				{
					Assert.That(double.IsNaN(factor) || double.IsInfinity(factor), Is.False, $"depth {depth}");
					Assert.That(factor, Is.InRange(1.0, 50.0), $"depth {depth}");
				}
			}
		}

		[Test]
		public void ShallowWater_StiffensTheHullInSwayAndYaw()
		{
			var draft = _parameters.Particulars.Draft.Value;
			var deep = _shallow.Evaluate(100.0, 5.0);
			var shallow = _shallow.Evaluate(1.3 * draft, 5.0);
			Assert.That(shallow.LinearDerivatives, Is.GreaterThan(1.5 * deep.LinearDerivatives));
			Assert.That(shallow.CrossFlow, Is.GreaterThan(1.5 * deep.CrossFlow));
			Assert.That(shallow.AddedMass, Is.GreaterThan(1.2 * deep.AddedMass));
		}

		[Test]
		public void Wind_PushesDownwind_InBodyAxes()
		{
			// Air moving aft relative to the boat (head wind): pushed astern.
			_windage.Load(-10.0, 0.0, out var headX, out var headY, out _);
			Assert.That(headX, Is.LessThan(0.0));
			Assert.That(headY, Is.EqualTo(0.0).Within(1e-9));

			// Wind from starboard (air moving to port): pushed to port, heeled to port.
			_windage.Load(0.0, -10.0, out _, out var beamY, out _);
			Assert.That(beamY, Is.LessThan(0.0));

			// Wind on the starboard bow: the bow (windage forward) is pushed downwind, to port.
			_windage.Load(-7.0, -7.0, out _, out _, out var bowN);
			Assert.That(bowN, Is.LessThan(0.0));
		}

		[Test]
		public void StillAir_GivesNoForce_WhenDriftingWithTheWater()
		{
			var current = HorizontalVector.FromSpeedToward(1.5, Angles.ToRadians(45.0));
			_windage.Forces(HorizontalVector.Zero, current, 0.0, 0.0, 0.0, out var x, out var y, out var n);
			Assert.That(x, Is.EqualTo(0.0));
			Assert.That(y, Is.EqualTo(0.0));
			Assert.That(n, Is.EqualTo(0.0));
		}

		[Test]
		public void StillAir_WithoutCurrent_IsTheVesselsOwnAirDrag()
		{
			// No current: ground and water velocities match, so the load is exactly the apparent-wind load.
			var heading = Angles.ToRadians(30.0);
			var ground = new HorizontalVector(10.0 * Math.Cos(heading), 10.0 * Math.Sin(heading));
			_windage.Forces(HorizontalVector.Zero, ground, heading, 10.0, 0.0, out var x, out _, out _);
			_windage.Load(-10.0, 0.0, out var expected, out _, out _);
			Assert.That(x, Is.EqualTo(expected).Within(1e-9));
			Assert.That(x, Is.LessThan(0.0));
		}
	}
}
