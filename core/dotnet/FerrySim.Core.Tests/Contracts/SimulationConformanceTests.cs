using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	/// <summary>
	/// Behavior every <see cref="ISimulation"/> from <see cref="VesselDynamicsFactory"/> must show,
	/// whatever model is behind it. These guard the sign conventions and the classic failure modes
	/// (energy creep, wrong-frame current, nondeterminism, blow-ups). Keep them passing.
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class SimulationConformanceTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public SimulationConformanceTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void LoadVessel()
		{
			_parameters = VesselParametersJson.Load(RepoPaths.Vessel(_vesselFile));
		}

		[Test]
		public void Neutral_InStillWater_StaysAtRest()
		{
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, ControlInputs.Neutral, seconds: 300);
			Assert.That(simulation.State.Position.Magnitude, Is.LessThan(1e-6));
			Assert.That(Math.Abs(simulation.State.YawRate), Is.LessThan(1e-9));
		}

		[Test]
		public void Neutral_MovingWithUniformCurrent_StaysWithTheWater()
		{
			// Starting at the water's own velocity there is no relative flow, so no hydrodynamic force.
			// A model that applies current in the wrong frame (or twice) fails this.
			var current = HorizontalVector.FromSpeedToward(1.5, Angles.ToRadians(45.0));
			var heading = 0.0;
			var surge = current.North * Math.Cos(heading) + current.East * Math.Sin(heading);
			var sway = -current.North * Math.Sin(heading) + current.East * Math.Cos(heading);
			var initial = VesselState.Planar(0.0, 0.0, 0.0, heading, surge, sway);
			var simulation = Create(new UniformEnvironment(current, HorizontalVector.Zero, 50.0), initial);
			Run(simulation, ControlInputs.Neutral, seconds: 300);
			Assert.That((simulation.State.GroundVelocity - current).Magnitude, Is.LessThan(0.01));
			Assert.That((simulation.State.Position - current * 300.0).Magnitude, Is.LessThan(3.0));
			Assert.That(simulation.Telemetry.SpeedThroughWater, Is.EqualTo(0.0).Within(0.01));
		}

		[Test]
		public void Neutral_AtRestInCurrent_IsSetDownstream()
		{
			var current = HorizontalVector.FromSpeedToward(1.5, Angles.ToRadians(200.0));
			var simulation = Create(new UniformEnvironment(current, HorizontalVector.Zero, 50.0), VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, ControlInputs.Neutral, seconds: 120);
			var position = simulation.State.Position;
			var alongCurrent = (position.North * current.North + position.East * current.East) / current.Magnitude;
			Assert.That(alongCurrent, Is.GreaterThan(10.0), "the boat must be carried the way the water is going");
		}

		[Test]
		public void Time_IsDerivedFromStepCount()
		{
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, new ControlInputs(0.5, 0.5, 0.0), seconds: 60);
			Assert.That(simulation.State.Time, Is.EqualTo(SimulationClock.TimeAtStep(60 * SimulationClock.StepsPerSecond)));
		}

		[Test]
		public void BothAhead_MovesAlongTheHeading()
		{
			var heading = Angles.ToRadians(90.0);
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, heading));
			Run(simulation, new ControlInputs(1.0, 1.0, 0.0), seconds: 120);
			Assert.That(simulation.State.East, Is.GreaterThan(100.0), "heading 090 ahead should move east");
			Assert.That(Math.Abs(simulation.State.North), Is.LessThan(0.05 * simulation.State.East));
			Assert.That(simulation.Telemetry.SpeedThroughWater, Is.GreaterThan(0.0));
		}

		[Test]
		public void PortEngineAheadAlone_TurnsBowToStarboard()
		{
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, new ControlInputs(1.0, 0.0, 0.0), seconds: 30);
			Assert.That(simulation.State.YawRate, Is.GreaterThan(0.0));
		}

		[Test]
		public void StarboardEngineAheadAlone_TurnsBowToPort()
		{
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, new ControlInputs(0.0, 1.0, 0.0), seconds: 30);
			Assert.That(simulation.State.YawRate, Is.LessThan(0.0));
		}

		[Test]
		public void StarboardHelm_WhileAhead_TurnsToStarboard()
		{
			var simulation = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Run(simulation, new ControlInputs(0.8, 0.8, 0.0), seconds: 90);
			Run(simulation, new ControlInputs(0.8, 0.8, 1.0), seconds: 20);
			Assert.That(simulation.State.YawRate, Is.GreaterThan(0.0));
		}

		[Test]
		public void SameInputs_GiveBitIdenticalStates()
		{
			var first = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 10.0, -5.0, 1.0));
			var second = Create(UniformEnvironment.Calm, VesselState.Planar(0.0, 10.0, -5.0, 1.0));
			for (var step = 0; step < 30 * SimulationClock.StepsPerSecond; step++)
			{
				var inputs = ScriptedInputs(step);
				first.Step(inputs);
				second.Step(inputs);
			}

			Assert.That(second.State, Is.EqualTo(first.State));
		}

		[Test]
		public void Reset_RestoresTheInitialState()
		{
			var initial = VesselState.Planar(0.0, 100.0, 200.0, 2.0);
			var simulation = Create(UniformEnvironment.Calm, initial);
			Run(simulation, new ControlInputs(1.0, -1.0, 1.0), seconds: 60);
			simulation.Reset(initial);
			Assert.That(simulation.State.Position, Is.EqualTo(initial.Position));
			Assert.That(simulation.State.Heading, Is.EqualTo(initial.Heading));
			Assert.That(simulation.State.Time, Is.EqualTo(0.0));
		}

		[Test]
		public void HarshInputs_ForTenMinutes_StayFiniteAndInRange()
		{
			var current = HorizontalVector.FromSpeedToward(2.5, Angles.ToRadians(200.0));
			var wind = HorizontalVector.FromSpeedFrom(15.0, Angles.ToRadians(300.0));
			var simulation = Create(new UniformEnvironment(current, wind, 8.0), VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 12.0));
			for (var step = 0; step < 600 * SimulationClock.StepsPerSecond; step++)
			{
				// Slam between extremes every 15 s: full ahead/astern splits and hard-over helm.
				var phase = (step / (15 * SimulationClock.StepsPerSecond)) % 4;
				var inputs = phase switch
				{
					0 => new ControlInputs(1.0, -1.0, 1.0),
					1 => new ControlInputs(-1.0, 1.0, -1.0),
					2 => new ControlInputs(1.0, 1.0, -1.0),
					_ => new ControlInputs(-1.0, -1.0, 1.0),
				};
				simulation.Step(inputs);
				Assert.That(simulation.State.IsFinite(), Is.True, $"non-finite state at step {step}: {simulation.State}");
			}

			Assert.That(simulation.State.Heading, Is.GreaterThanOrEqualTo(0.0).And.LessThan(Angles.TwoPi));
			Assert.That(simulation.Telemetry.SpeedOverGround, Is.LessThan(30.0), "no runaway speed");
		}

		private ISimulation Create(IEnvironment environment, VesselState initial) =>
			new VesselDynamicsFactory().Create(_parameters, environment, initial);

		private static void Run(ISimulation simulation, ControlInputs inputs, int seconds)
		{
			for (var step = 0; step < seconds * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(inputs);
			}
		}

		private static ControlInputs ScriptedInputs(int step)
		{
			var t = step / (double)SimulationClock.StepsPerSecond;
			return new ControlInputs(Math.Sin(0.11 * t), Math.Cos(0.07 * t), Math.Sin(0.05 * t));
		}
	}
}
