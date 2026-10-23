using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Control model: lever bands and ZF-style shift sequencing for one engine. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Gear engaging inside the neutral band, or throttle opening at the idle detent.</item>
	/// <item>Throttle applied before the clutch is at full pressure and the throttle pause has passed.</item>
	/// <item>Ahead straight to astern with no release and no neutral dwell, by any lever path.</item>
	/// <item>Both clutches pressurised at once.</item>
	/// <item>A noisy lever near the detent chattering the clutch.</item>
	/// </list>
	/// </summary>
	public class ControlModelTests
	{
		private const double Dt = SimulationClock.FixedTimeStep;

		private ShiftSettings _settings;
		private double _idle;

		[OneTimeSetUp]
		public void Load()
		{
			var parameters = ManeuverRunner.Load("t-class.json");
			_settings = new ShiftSettings(parameters);
			_idle = parameters.Engines.IdleSpeed.Value;
		}

		[Test]
		public void InsideTheNeutralBand_StaysInNeutral()
		{
			var control = new EngineControl(_settings);
			Hold(control, 0.9 * _settings.ClutchInLever, 10.0);
			Assert.That(control.Gear, Is.EqualTo(GearState.Neutral));
			Assert.That(control.ClutchDirection, Is.EqualTo(0));
			Assert.That(control.Throttle, Is.EqualTo(0.0));
		}

		[Test]
		public void IdleDetent_EngagesAtIdle_AndThrottleOpensAboveIt()
		{
			var control = new EngineControl(_settings);
			Hold(control, 0.5 * (_settings.ClutchInLever + _settings.ThrottleStartLever), 5.0);
			Assert.That(control.Gear, Is.EqualTo(GearState.Ahead));
			Assert.That(control.Throttle, Is.EqualTo(0.0), "between the detent and the throttle start the engine idles in gear");

			Hold(control, 0.6, 1.0);
			Assert.That(control.Throttle, Is.EqualTo((0.6 - _settings.ThrottleStartLever) / (1.0 - _settings.ThrottleStartLever)).Within(1e-12));
			Hold(control, 1.0, 1.0);
			Assert.That(control.Throttle, Is.EqualTo(1.0));
		}

		[Test]
		public void Engagement_WaitsForFill_PressureRamp_AndThrottlePause()
		{
			var control = new EngineControl(_settings);
			var firstPressure = double.NaN;
			var firstThrottle = double.NaN;
			for (var step = 1; step <= 500; step++)
			{
				var t = step * Dt;
				control.Step(1.0, _idle, Dt);
				if (double.IsNaN(firstPressure) && control.ClutchPressure > 0.0)
				{
					firstPressure = t;
				}

				if (double.IsNaN(firstThrottle) && control.Throttle > 0.0)
				{
					firstThrottle = t;
				}
			}

			Assert.That(firstPressure, Is.GreaterThanOrEqualTo(_settings.ClutchFillTime), "no torque before the clutch has filled");
			Assert.That(firstThrottle, Is.GreaterThanOrEqualTo(_settings.ClutchFillTime + _settings.ClutchEngagementTime + _settings.ThrottlePause - 2 * Dt),
				"throttle only after the pressure ramp and the throttle pause");
		}

		[Test]
		public void FullAheadToFullAstern_NeverReversesInstantly()
		{
			var control = new EngineControl(_settings);
			Hold(control, 1.0, 10.0);
			Assert.That(control.Gear, Is.EqualTo(GearState.Ahead));

			var firstAsternPressure = double.NaN;
			var lastAheadPressure = 0.0;
			var sawShifting = false;
			for (var step = 1; step <= 1000; step++)
			{
				var t = step * Dt;
				control.Step(-1.0, _idle, Dt);
				sawShifting |= control.Gear == GearState.Shifting;
				if (control.ClutchDirection > 0 && control.ClutchPressure > 0.0)
				{
					lastAheadPressure = t;
				}

				if (double.IsNaN(firstAsternPressure) && control.ClutchDirection < 0 && control.ClutchPressure > 0.0)
				{
					firstAsternPressure = t;
				}

				if (control.ClutchDirection > 0)
				{
					Assert.That(control.Throttle, Is.EqualTo(0.0), "throttle drops to idle before anything else");
				}
			}

			Assert.That(sawShifting, Is.True, "the gear indicator shows the shift");
			Assert.That(control.Gear, Is.EqualTo(GearState.Astern));
			Assert.That(firstAsternPressure - lastAheadPressure,
				Is.GreaterThanOrEqualTo(_settings.NeutralDwell + _settings.ClutchFillTime - 2 * Dt),
				"ahead clutch released, then a neutral dwell and a clutch fill before astern carries torque");
		}

		[Test]
		public void AheadThroughNeutralToAstern_QuicklyStillWaitsTheDwell()
		{
			const double inNeutral = 0.1;
			var control = new EngineControl(_settings);
			Hold(control, 1.0, 10.0);
			Hold(control, 0.0, _settings.ClutchReleaseTime + inNeutral);
			Assert.That(control.Gear, Is.EqualTo(GearState.Neutral));
			var start = double.NaN;
			for (var step = 1; step <= 600; step++)
			{
				control.Step(-1.0, _idle, Dt);
				if (double.IsNaN(start) && control.ClutchPressure > 0.0)
				{
					start = step * Dt;
				}
			}

			Assert.That(start, Is.GreaterThanOrEqualTo(_settings.NeutralDwell - inNeutral + _settings.ClutchFillTime - 2 * Dt),
				"the dwell counts from the ahead clutch's release, not from the lever reaching astern");
		}

		[Test]
		public void ReturningToTheSameDirection_DuringTheDwell_ReengagesWithoutWaiting()
		{
			var control = new EngineControl(_settings);
			Hold(control, 1.0, 10.0);
			Hold(control, -1.0, _settings.ClutchReleaseTime + 0.2);
			Assert.That(control.Phase, Is.EqualTo(ShiftPhase.NeutralDwell));
			Hold(control, 1.0, _settings.ClutchFillTime + 2 * Dt);
			Assert.That(control.Phase, Is.EqualTo(ShiftPhase.Engaging), "no dwell is needed to go back ahead");
		}

		[Test]
		public void EngineSpeedInterlock_HoldsTheShiftUntilTheEngineSlows()
		{
			var control = new EngineControl(_settings);
			Hold(control, 1.0, 2.0, engineSpeed: 2.0 * _settings.ShiftInterlockSpeed);
			Assert.That(control.Phase, Is.EqualTo(ShiftPhase.AwaitingInterlock));
			Hold(control, 1.0, 0.1, engineSpeed: _idle);
			Assert.That(control.Phase, Is.EqualTo(ShiftPhase.Filling));
		}

		[Test]
		public void NoisyLeverAtTheDetent_DoesNotChatter()
		{
			var control = new EngineControl(_settings);
			Hold(control, 0.2, 5.0);
			var releases = 0;
			var previous = control.Phase;
			for (var step = 0; step < 2000; step++)
			{
				// Jitter just around the clutch-in position, never back past the hysteresis.
				var lever = _settings.ClutchInLever + 0.4 * _settings.DetentHysteresis * Math.Sin(step * 0.7);
				control.Step(lever, _idle, Dt);
				if (control.Phase == ShiftPhase.Releasing && previous != ShiftPhase.Releasing)
				{
					releases++;
				}

				previous = control.Phase;
			}

			Assert.That(releases, Is.EqualTo(0));
			Assert.That(control.Gear, Is.EqualTo(GearState.Ahead));
		}

		private static void Hold(EngineControl control, double lever, double seconds, double engineSpeed = double.NaN)
		{
			var steps = (int)Math.Round(seconds / Dt);
			for (var i = 0; i < steps; i++)
			{
				control.Step(lever, double.IsNaN(engineSpeed) ? 60.0 : engineSpeed, Dt);
			}
		}
	}

	/// <summary>
	/// Steering gear. Failure modes guarded: rudder jumping to the order, exceeding hard over, the
	/// wrong slew rate, non-follow-up steering drifting back when the helm is released.
	/// </summary>
	public class SteeringGearTests
	{
		private const double Dt = SimulationClock.FixedTimeStep;

		[Test]
		public void FollowUp_RudderSlewsAtTheSteeringRate_AndStopsAtHardOver()
		{
			var parameters = ManeuverRunner.Load("k-class.json");
			var gear = new SteeringGear(parameters);
			Hold(gear, 1.0, 1.0);
			Assert.That(gear.Angle, Is.EqualTo(gear.SteeringRate * 1.0).Within(1e-9), "rate-limited, not instant");
			Hold(gear, 1.0, 30.0);
			Assert.That(gear.Angle, Is.EqualTo(gear.HardOverAngle).Within(1e-12), "stops exactly at hard over");

			var steps = 0;
			while (gear.Angle > -gear.HardOverAngle + 1e-12 && steps < 10000)
			{
				gear.Step(-1.0, Dt);
				steps++;
			}

			Assert.That(steps * Dt, Is.EqualTo(2.0 * gear.HardOverAngle / gear.SteeringRate).Within(2 * Dt), "hard over to hard over");
			Hold(gear, 0.5, 30.0);
			Assert.That(gear.Angle, Is.EqualTo(0.5 * gear.HardOverAngle).Within(1e-12), "follow-up: helm position orders rudder angle");
		}

		[Test]
		public void NonFollowUp_RudderMovesWhileHeld_AndStaysWhenReleased()
		{
			var parameters = ManeuverRunner.Load("k-class.json");
			parameters.Controls.SteeringMode = new ParameterText { Value = "non-follow-up", Source = "test", Confidence = ParameterConfidence.Estimated };
			var gear = new SteeringGear(parameters);
			Hold(gear, 1.0, 2.0);
			var held = gear.Angle;
			Assert.That(held, Is.EqualTo(2.0 * gear.SteeringRate).Within(1e-9));
			Hold(gear, 0.0, 10.0);
			Assert.That(gear.Angle, Is.EqualTo(held));
			Hold(gear, 1.0, 60.0);
			Assert.That(gear.Angle, Is.EqualTo(gear.HardOverAngle).Within(1e-12));
		}

		private static void Hold(SteeringGear gear, double helm, double seconds)
		{
			var steps = (int)Math.Round(seconds / Dt);
			for (var i = 0; i < steps; i++)
			{
				gear.Step(helm, Dt);
			}
		}
	}
}
