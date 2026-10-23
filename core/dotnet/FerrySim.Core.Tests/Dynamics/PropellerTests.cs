using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Propeller module. Failure modes guarded:
	/// <list type="bullet">
	/// <item>B-series polynomial transcribed wrongly (known chart values, first quadrant match).</item>
	/// <item>Thrust or torque sign wrong for astern rotation or for flow from astern (four quadrants).</item>
	/// <item>A locked or windmilling propeller pushing instead of dragging.</item>
	/// <item>Astern thrust as strong as ahead, or implausibly weak.</item>
	/// <item>Jumps in the tabulated curves (the shaft dynamics are stiff and would chatter).</item>
	/// <item>A propeller that creates energy (thrust power exceeding shaft power outside windmilling).</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class PropellerTests
	{
		private readonly string _vesselFile;
		private PropellerModel _propeller;

		public PropellerTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_propeller = new PropellerModel(ManeuverRunner.Load(_vesselFile));
		}

		[Test]
		public void FirstQuadrant_IsTheBSeries()
		{
			var curves = _propeller.Curves;
			for (var j = 0.0; j <= curves.ZeroThrustAdvanceRatio * 0.95; j += 0.05)
			{
				curves.Evaluate(Math.Atan2(j, 0.7 * Math.PI), out var thrust, out var torque);
				var kt = WageningenBSeries.ThrustCoefficient(j, curves.PitchRatio, curves.AreaRatio, curves.Blades);
				var kq = WageningenBSeries.TorqueCoefficient(j, curves.PitchRatio, curves.AreaRatio, curves.Blades);
				Assert.That(thrust, Is.EqualTo(FourQuadrantPropellerCurves.OpenWaterToFourQuadrant(kt, j)).Within(0.004), $"C_T* at J = {j:F2}");
				Assert.That(torque, Is.EqualTo(FourQuadrantPropellerCurves.OpenWaterToFourQuadrant(kq, j)).Within(0.0008), $"C_Q* at J = {j:F2}");
			}
		}

		[Test]
		public void BollardThrust_FollowsShaftDirection_AndAsternIsWeaker()
		{
			var shaft = Units.RpmToRadiansPerSecond(600.0);
			_propeller.Evaluate(shaft, 0.0, out var ahead, out var aheadTorque);
			_propeller.Evaluate(-shaft, 0.0, out var astern, out var asternTorque);
			Assert.That(ahead, Is.GreaterThan(0.0), "ahead rotation pushes the boat ahead");
			Assert.That(aheadTorque, Is.GreaterThan(0.0), "an ahead-turning propeller loads the engine");
			Assert.That(astern, Is.LessThan(0.0), "astern rotation pushes the boat astern");
			Assert.That(asternTorque, Is.LessThan(0.0), "astern-turning torque has the sign of the rotation");
			Assert.That(-astern / ahead, Is.InRange(0.55, 0.8), "fixed-pitch astern bollard thrust is typically 0.6 to 0.75 of ahead");
		}

		[TestCase(1.0, 2.0, 1, 1)]
		[TestCase(-1.0, 2.0, -1, -1)]
		[TestCase(-1.0, -2.0, -1, -1)]
		[TestCase(1.0, -2.0, 1, 1)]
		public void FourQuadrants_HaveTheRightSigns(double rotation, double advanceSpeed, int thrustSign, int torqueSign)
		{
			// Q1 ahead, Q2 crash-back (turning astern while moving ahead), Q3 astern, Q4 crash-ahead.
			var shaft = rotation * Units.RpmToRadiansPerSecond(700.0);
			_propeller.Evaluate(shaft, advanceSpeed, out var thrust, out var torque);
			Assert.That(Math.Sign(thrust), Is.EqualTo(thrustSign), $"thrust {thrust:F0} N");
			Assert.That(Math.Sign(torque), Is.EqualTo(torqueSign), $"torque {torque:F0} N·m");
		}

		[Test]
		public void LockedPropeller_DragsWithTheFlow()
		{
			_propeller.Evaluate(0.0, 4.0, out var movingAhead, out _);
			_propeller.Evaluate(0.0, -4.0, out var movingAstern, out _);
			Assert.That(movingAhead, Is.LessThan(0.0), "a stopped propeller on a boat moving ahead holds it back");
			Assert.That(movingAstern, Is.GreaterThan(0.0), "and holds it back going astern too");
		}

		[Test]
		public void FreelyTurningPropeller_IsDrivenByTheWater()
		{
			// Well beyond zero thrust the water drives the propeller round: negative load torque.
			var curves = _propeller.Curves;
			var advance = 8.0;
			var slowShaft = advance / (1.6 * curves.ZeroThrustAdvanceRatio * _propeller.Diameter) * 2.0 * Math.PI;
			_propeller.Evaluate(slowShaft, advance, out var thrust, out var torque);
			Assert.That(thrust, Is.LessThan(0.0));
			Assert.That(torque, Is.LessThan(0.0));
		}

		[Test]
		public void Curves_AreContinuousAllTheWayRound()
		{
			var curves = _propeller.Curves;
			curves.Evaluate(-Math.PI, out var previousThrust, out var previousTorque);
			for (var deg = -179.9; deg <= 180.0001; deg += 0.1)
			{
				curves.Evaluate(Angles.ToRadians(deg), out var thrust, out var torque);
				Assert.That(Math.Abs(thrust - previousThrust), Is.LessThan(0.01), $"C_T* jump at {deg:F1}°");
				Assert.That(Math.Abs(torque - previousTorque), Is.LessThan(0.002), $"C_Q* jump at {deg:F1}°");
				previousThrust = thrust;
				previousTorque = torque;
			}
		}

		[Test]
		public void Propeller_NeverCreatesEnergy()
		{
			// Passivity (Häusler et al. 2013): thrust power T·V_A cannot exceed shaft power Q·ω, all the way round.
			const double relativeSpeed = 15.0;
			for (var deg = -180.0; deg < 180.0; deg += 1.0)
			{
				var beta = Angles.ToRadians(deg);
				var advance = relativeSpeed * Math.Sin(beta);
				var omega = relativeSpeed * Math.Cos(beta) / (0.35 * _propeller.Diameter);
				_propeller.Evaluate(omega, advance, out var thrust, out var torque);
				Assert.That(thrust * advance, Is.LessThanOrEqualTo(torque * omega + 1.0), $"β = {deg}°");
			}
		}

		[Test]
		public void SlipstreamSpeed_IsFasterThanTheInflowAhead_AndReversesAstern()
		{
			_propeller.Evaluate(Units.RpmToRadiansPerSecond(700.0), 3.0, out var ahead, out _);
			Assert.That(_propeller.SlipstreamSpeed(ahead, 3.0), Is.GreaterThan(3.0));
			_propeller.Evaluate(-Units.RpmToRadiansPerSecond(700.0), 0.0, out var astern, out _);
			Assert.That(_propeller.SlipstreamSpeed(astern, 0.0), Is.LessThan(0.0), "the astern jet runs forward, away from the rudder");
		}

		[Test]
		public void PropWalk_OutwardTurning_AddsToTheTwist()
		{
			// Starboard propeller (right-handed) ahead walks the stern to starboard (bow to port), which is the
			// same way its thrust twists the boat; port propeller astern walks the stern to starboard too.
			Assert.That(_propeller.PropWalkForce(1, 10000.0), Is.GreaterThan(0.0));
			Assert.That(_propeller.PropWalkForce(-1, -10000.0), Is.GreaterThan(0.0));
			Assert.That(_propeller.PropWalkForce(-1, 10000.0), Is.LessThan(0.0));
			Assert.That(Math.Abs(_propeller.PropWalkForce(1, -10000.0)), Is.GreaterThan(Math.Abs(_propeller.PropWalkForce(1, 10000.0))),
				"walk is stronger astern");
		}
	}

	public class WageningenBSeriesTests
	{
		[Test]
		public void B4_70_MatchesThePublishedChart()
		{
			// B4-70, P/D 1.0: K_T(0) about 0.45, 10K_Q(0) about 0.67, peak efficiency about 0.69 near J 0.8 and
			// zero thrust near J 1.06 (Oosterveld and van Oossanen 1975 charts).
			Assert.That(WageningenBSeries.ThrustCoefficient(0.0, 1.0, 0.7, 4.0), Is.EqualTo(0.45).Within(0.01));
			Assert.That(WageningenBSeries.TorqueCoefficient(0.0, 1.0, 0.7, 4.0), Is.EqualTo(0.067).Within(0.002));
			Assert.That(WageningenBSeries.ZeroThrustAdvanceRatio(1.0, 0.7, 4.0), Is.EqualTo(1.06).Within(0.02));
			var best = 0.0;
			for (var j = 0.5; j <= 1.0; j += 0.01)
			{
				var kt = WageningenBSeries.ThrustCoefficient(j, 1.0, 0.7, 4.0);
				var kq = WageningenBSeries.TorqueCoefficient(j, 1.0, 0.7, 4.0);
				best = Math.Max(best, j * kt / (2.0 * Math.PI * kq));
			}

			Assert.That(best, Is.EqualTo(0.69).Within(0.02));
		}
	}
}
