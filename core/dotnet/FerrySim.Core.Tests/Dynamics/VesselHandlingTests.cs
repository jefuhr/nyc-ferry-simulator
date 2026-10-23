using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The whole model through <see cref="VesselDynamicsSimulation"/>. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Yaw from single engines, split engines or helm going the wrong way, ahead or astern.</item>
	/// <item>No prop-wash steering at zero speed.</item>
	/// <item>Wind or shallow water acting with the wrong sign at the whole-boat level.</item>
	/// <item>NaN or blow-up near, at and below the draft; grounding not reported.</item>
	/// <item>Telemetry inconsistent with the machinery (shaft vs gear ratio, gear state, keel draft).</item>
	/// <item>Heap allocation in the per-step path.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class VesselHandlingTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public VesselHandlingTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
		}

		[TestCase(-0.5, 0.0, -1)]
		[TestCase(0.0, -0.5, 1)]
		[TestCase(0.5, 0.0, 1)]
		[TestCase(0.0, 0.5, -1)]
		public void SingleEngine_YawsTheWayItsThrustTwists(double port, double starboard, int expectedSign)
		{
			// Port ahead or starboard astern swing the bow to starboard; the opposite pair to port.
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(port, starboard, 0.0), 20.0);
			Assert.That(Math.Sign(simulation.State.YawRate), Is.EqualTo(expectedSign), $"yaw rate {simulation.State.YawRate}");
		}

		[TestCase(0.15, -0.15, 1)]
		[TestCase(-0.15, 0.15, -1)]
		[TestCase(0.3, -0.3, 1)]
		public void SplitEngines_PivotTowardTheAsternEngine(double port, double starboard, int expectedSign)
		{
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(port, starboard, 0.0), 40.0);
			Assert.That(Math.Sign(simulation.State.YawRate), Is.EqualTo(expectedSign));
			Assert.That(Math.Abs(simulation.State.YawRate), Is.GreaterThan(Angles.ToRadians(1.0)), "a split twists the boat briskly");
		}

		[TestCase(1.0, -1)]
		[TestCase(-1.0, 1)]
		public void Helm_WhileMakingSternway_SwingsTheSternTheWayTheRudderPoints(double helm, int expectedSign)
		{
			// Going astern the flow meets the rudder from aft: starboard rudder pushes the stern to starboard,
			// so the bow swings to port (negative yaw rate).
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(-0.5, -0.5, 0.0), 60.0);
			Assert.That(simulation.Telemetry.SpeedThroughWater, Is.LessThan(-2.0), "making sternway");
			ManeuverRunner.Run(simulation, new ControlInputs(-0.5, -0.5, helm), 30.0);
			Assert.That(Math.Sign(simulation.State.YawRate), Is.EqualTo(expectedSign), $"yaw rate {simulation.State.YawRate}");
		}

		[Test]
		public void PropWash_SteersTheBoatFromRest()
		{
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(0.15, 0.15, 1.0), 10.0);
			Assert.That(simulation.State.YawRate, Is.GreaterThan(0.0), "kick ahead with starboard helm turns to starboard");
		}

		[Test]
		public void Windage_PushesTheBoatDownwind()
		{
			var wind = HorizontalVector.FromSpeedFrom(15.0, Angles.ToRadians(270.0));
			var simulation = ManeuverRunner.Create(_parameters, new UniformEnvironment(HorizontalVector.Zero, wind, 50.0));
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, 120.0);
			var drift = simulation.State.Position;
			Assert.That(drift.East, Is.GreaterThan(30.0), "a west wind blows the boat east");
			Assert.That(Math.Abs(drift.North), Is.LessThan(0.3 * drift.East));
			Assert.That(simulation.State.Roll, Is.GreaterThan(0.0), "and heels it to leeward (starboard side down)");
		}

		[Test]
		public void ShallowWater_ReducesTurning()
		{
			var draft = _parameters.Particulars.Draft.Value;
			var deep = SteadyTurnRate(new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, 50.0));
			var shallow = SteadyTurnRate(new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, 1.3 * draft));
			Assert.That(shallow, Is.LessThan(0.9 * deep));
		}

		[TestCase(1.05)]
		[TestCase(1.0)]
		[TestCase(0.5)]
		[TestCase(0.0)]
		[TestCase(-2.0)]
		public void DepthAtOrBelowTheDraft_StaysFinite_AndReportsGrounding(double depthRatio)
		{
			var depth = depthRatio * _parameters.Particulars.Draft.Value;
			var simulation = ManeuverRunner.Create(_parameters, new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, depth),
				VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 12.0));
			for (var step = 0; step < 120 * SimulationClock.StepsPerSecond; step++)
			{
				var phase = step / (20 * SimulationClock.StepsPerSecond) % 3;
				simulation.Step(phase == 0 ? new ControlInputs(1.0, 1.0, 1.0) : phase == 1 ? new ControlInputs(-1.0, 1.0, -1.0) : new ControlInputs(-1.0, -1.0, 0.0));
				Assert.That(simulation.State.IsFinite(), Is.True, $"step {step}: {simulation.State}");
			}

			Assert.That(simulation.Telemetry.DepthUnderKeel, Is.EqualTo(depth - simulation.KeelDraft).Within(1e-12));
			Assert.That(simulation.IsAground, Is.True, "deeper than the water: the depth sounder shows a negative depth under keel");
		}

		[Test]
		public void VaryingCurrentAndDepth_StayFinite()
		{
			var simulation = ManeuverRunner.Create(_parameters, new ShoalingEnvironment(), VesselState.Planar(0.0, 0.0, 0.0, Angles.ToRadians(80.0), surge: 10.0));
			for (var step = 0; step < 300 * SimulationClock.StepsPerSecond; step++)
			{
				var t = step / (double)SimulationClock.StepsPerSecond;
				simulation.Step(new ControlInputs(Math.Sin(0.05 * t), Math.Cos(0.03 * t), Math.Sin(0.11 * t)));
				Assert.That(simulation.State.IsFinite(), Is.True, $"step {step}");
			}
		}

		[Test]
		public void Telemetry_MatchesTheMachinery()
		{
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(0.7, 0.15, 0.4), 60.0);
			var telemetry = simulation.Telemetry;
			var ratio = _parameters.Machinery.GearRatio.Value;
			var throttleStart = _parameters.Controls.ThrottleStartLever.Value;
			Assert.That(telemetry.Port.Gear, Is.EqualTo(GearState.Ahead));
			Assert.That(telemetry.Starboard.Gear, Is.EqualTo(GearState.Ahead));
			Assert.That(telemetry.Port.ShaftSpeed, Is.EqualTo(telemetry.Port.EngineSpeed / ratio).Within(1e-6), "locked ahead: shaft = engine / ratio");
			Assert.That(telemetry.Starboard.ShaftSpeed, Is.EqualTo(telemetry.Starboard.EngineSpeed / ratio).Within(1e-6));
			Assert.That(telemetry.Starboard.EngineSpeed, Is.EqualTo(_parameters.Engines.IdleSpeed.Value).Within(Units.RpmToRadiansPerSecond(10.0)), "ahead idle");
			Assert.That(telemetry.Port.Throttle, Is.EqualTo((0.7 - throttleStart) / (1.0 - throttleStart)).Within(1e-9));
			Assert.That(telemetry.Starboard.Throttle, Is.EqualTo(0.0));
			Assert.That(telemetry.PortRudderAngle, Is.EqualTo(0.4 * _parameters.Controls.HardOverAngle.Value).Within(1e-12));
			Assert.That(telemetry.PortRudderAngle, Is.EqualTo(telemetry.StarboardRudderAngle));
			Assert.That(telemetry.ContactForce, Is.EqualTo(0.0));
			Assert.That(simulation.KeelDraftIsHullDraft, Is.False, "both classes publish a maximum draft");
			Assert.That(simulation.KeelDraft, Is.EqualTo(_parameters.Particulars.MaxDraft.Value));

			var ground = simulation.State.GroundVelocity;
			Assert.That(telemetry.SpeedOverGround, Is.EqualTo(ground.Magnitude).Within(1e-12));
			Assert.That(telemetry.CourseOverGround, Is.EqualTo(ground.Direction).Within(1e-12));
		}

		[Test]
		public void Telemetry_ShowsShiftingThenAstern_AndSpeedLogNegativeAstern()
		{
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(0.6, 0.6, 0.0), 60.0);
			simulation.Step(new ControlInputs(-0.6, -0.6, 0.0));
			Assert.That(simulation.Telemetry.Port.Gear, Is.EqualTo(GearState.Shifting));
			ManeuverRunner.Run(simulation, new ControlInputs(-0.6, -0.6, 0.0), 120.0);
			var telemetry = simulation.Telemetry;
			Assert.That(telemetry.Port.Gear, Is.EqualTo(GearState.Astern));
			Assert.That(telemetry.Port.ShaftSpeed, Is.EqualTo(-telemetry.Port.EngineSpeed / _parameters.Machinery.GearRatio.Value).Within(1e-6),
				"locked astern: shaft turns the other way at engine / ratio");
			Assert.That(telemetry.SpeedThroughWater, Is.LessThan(0.0), "the speed log reads negative going astern");
		}

		[Test]
		public void Neutral_AtSpeed_LetsThePropellersWindmill()
		{
			var simulation = ManeuverRunner.Approach(_parameters, 0.8, 120.0);
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, 3.0);
			Assert.That(simulation.Telemetry.Port.Gear, Is.EqualTo(GearState.Neutral));
			Assert.That(simulation.Telemetry.Port.ShaftSpeed, Is.GreaterThan(Units.RpmToRadiansPerSecond(100.0)), "the water keeps the shaft turning ahead");
			Assert.That(simulation.Telemetry.Port.EngineSpeed, Is.EqualTo(_parameters.Engines.IdleSpeed.Value).Within(Units.RpmToRadiansPerSecond(20.0)),
				"the declutched engine falls back to idle");
		}

		[Test]
		public void EngineSpeed_LagsTheThrottle_AndHoldsRatedSpeed()
		{
			var simulation = ManeuverRunner.Create(_parameters);
			ManeuverRunner.Run(simulation, new ControlInputs(0.15, 0.15, 0.0), 10.0);
			var rated = _parameters.Machinery.RatedEngineSpeed.Value;
			var peak = 0.0;
			var timeTo90 = double.NaN;
			for (var step = 1; step <= 120 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(new ControlInputs(1.0, 1.0, 0.0));
				var speed = simulation.Telemetry.Port.EngineSpeed;
				peak = Math.Max(peak, speed);
				if (double.IsNaN(timeTo90) && speed >= 0.9 * rated)
				{
					timeTo90 = step / (double)SimulationClock.StepsPerSecond;
				}
			}

			Assert.That(timeTo90, Is.GreaterThan(2.0), "turbocharged diesels with heavy propellers take seconds to wind up");
			Assert.That(peak, Is.LessThan(1.03 * rated), "the governor does not overspeed the engine");
			Assert.That(simulation.Telemetry.Port.EngineSpeed, Is.EqualTo(rated).Within(0.03 * rated));
		}

		[Test]
		public void SteadyLever_EngineDoesNotHunt()
		{
			var simulation = ManeuverRunner.Approach(_parameters, 0.6, 120.0);
			var low = double.MaxValue;
			var high = double.MinValue;
			for (var step = 0; step < 20 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(new ControlInputs(0.6, 0.6, 0.0));
				low = Math.Min(low, simulation.Telemetry.Port.EngineSpeed);
				high = Math.Max(high, simulation.Telemetry.Port.EngineSpeed);
			}

			Assert.That(Units.RadiansPerSecondToRpm(high - low), Is.LessThan(1.0), "governor holds a steady speed without hunting");
		}

		[Test]
		public void HeadCurrent_ChangesSpeedOverGround_NotSpeedThroughWater()
		{
			// Hydrodynamics see the water, so the speed log reads the same in a current; GPS reads less.
			var calm = ManeuverRunner.Approach(_parameters, 0.8, 200.0);
			var current = HorizontalVector.FromSpeedToward(1.5, Math.PI);
			var stemming = ManeuverRunner.Create(_parameters, new UniformEnvironment(current, HorizontalVector.Zero, 50.0));
			ManeuverRunner.Run(stemming, new ControlInputs(0.8, 0.8, 0.0), 200.0);
			Assert.That(stemming.Telemetry.SpeedThroughWater, Is.EqualTo(calm.Telemetry.SpeedThroughWater).Within(0.02));
			Assert.That(stemming.Telemetry.SpeedOverGround, Is.EqualTo(calm.Telemetry.SpeedOverGround - 1.5).Within(0.02));
		}

		[Test]
		public void Turning_HeelsOutward_AndAcceleration_TrimsBowUp()
		{
			var simulation = ManeuverRunner.Create(_parameters);
			var pitchWhileAccelerating = 0.0;
			for (var step = 0; step < 8 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(new ControlInputs(1.0, 1.0, 0.0));
				pitchWhileAccelerating = Math.Max(pitchWhileAccelerating, simulation.State.Pitch);
			}

			Assert.That(pitchWhileAccelerating, Is.GreaterThan(0.0), "bow up as the boat gets going");
			ManeuverRunner.Run(simulation, new ControlInputs(1.0, 1.0, 0.0), 60.0);
			ManeuverRunner.Run(simulation, new ControlInputs(1.0, 1.0, 1.0), 30.0);
			Assert.That(simulation.State.YawRate, Is.GreaterThan(0.0));
			Assert.That(simulation.State.Roll, Is.LessThan(0.0), "a starboard turn heels the boat outward, port side down");
		}

		[Test]
		public void Step_DoesNotAllocate()
		{
			var simulation = ManeuverRunner.Create(_parameters, new UniformEnvironment(HorizontalVector.FromSpeedToward(1.0, 1.0), HorizontalVector.FromSpeedFrom(8.0, 2.0), 6.0));
			var inputs = new ControlInputs(0.8, -0.4, 0.5);
			ManeuverRunner.Run(simulation, inputs, 5.0);
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var step = 0; step < 10000; step++)
			{
				simulation.Step(inputs);
			}

			Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
		}

		[Test]
		public void MissingDynamicsSection_IsAClearError()
		{
			var parameters = ManeuverRunner.Load(_vesselFile);
			parameters.Rudders = null;
			var error = Assert.Throws<ArgumentException>(() => new VesselDynamicsFactory().Create(parameters, UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0)));
			Assert.That(error.Message, Does.Contain("rudders"));
		}

		private double SteadyTurnRate(IEnvironment environment)
		{
			var simulation = ManeuverRunner.Create(_parameters, environment);
			ManeuverRunner.Run(simulation, new ControlInputs(0.6, 0.6, 0.0), 120.0);
			ManeuverRunner.Run(simulation, new ControlInputs(0.6, 0.6, 1.0), 60.0);
			return simulation.State.YawRate;
		}

		/// <summary>Current shear and a bottom that shoals to dry land toward the east.</summary>
		private sealed class ShoalingEnvironment : IEnvironment
		{
			public HorizontalVector SampleCurrent(double north, double east, double time) =>
				HorizontalVector.FromSpeedToward(1.5 + Math.Tanh(east / 100.0), 0.3 + 0.2 * Math.Sin(north / 150.0 + 0.01 * time));

			public HorizontalVector SampleWind(double north, double east, double time) =>
				HorizontalVector.FromSpeedFrom(12.0 + 4.0 * Math.Sin(0.2 * time), Angles.ToRadians(250.0));

			public double SampleDepth(double north, double east, double time) => 12.0 - east / 40.0;
		}
	}
}
