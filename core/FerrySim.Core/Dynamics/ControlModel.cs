using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>Where one engine's shift sequence is.</summary>
	public enum ShiftPhase
	{
		/// <summary>Clutches open, lever in the neutral band.</summary>
		Neutral = 0,

		/// <summary>Gear requested; waiting for the engine to come down to the shift interlock speed.</summary>
		AwaitingInterlock = 1,

		/// <summary>Clutch filling with oil; no torque yet.</summary>
		Filling = 2,

		/// <summary>Clutch pressure ramping up; slipping until the speeds meet.</summary>
		Engaging = 3,

		/// <summary>Clutch at full pressure in the requested direction (it may still slip under overload).</summary>
		Engaged = 4,

		/// <summary>Clutch releasing.</summary>
		Releasing = 5,

		/// <summary>Held in neutral before engaging the opposite direction.</summary>
		NeutralDwell = 6,
	}

	/// <summary>Lever-band and shift-timing settings shared by both engine controls.</summary>
	public sealed class ShiftSettings
	{
		public ShiftSettings(VesselParameters parameters)
		{
			var controls = Required.Section(parameters.Controls, "controls");
			var transmission = Required.Section(parameters.Transmission, "transmission");
			ClutchInLever = Required.InRange(controls.ClutchInLever, "controls.clutchInLever", 0.01, 0.5);
			ThrottleStartLever = Required.InRange(controls.ThrottleStartLever, "controls.throttleStartLever", ClutchInLever, 0.9);
			DetentHysteresis = Required.InRange(controls.DetentHysteresis, "controls.detentHysteresis", 0.0, ClutchInLever);
			NeutralDwell = Required.NonNegative(controls.NeutralDwell, "controls.neutralDwell");
			ThrottlePause = Required.NonNegative(controls.ThrottlePause, "controls.throttlePause");
			ShiftInterlockSpeed = Required.Positive(controls.ShiftInterlockSpeed, "controls.shiftInterlockSpeed");
			ClutchFillTime = Required.NonNegative(transmission.ClutchFillTime, "transmission.clutchFillTime");
			ClutchEngagementTime = Required.Positive(transmission.ClutchEngagementTime, "transmission.clutchEngagementTime");
			ClutchReleaseTime = Required.Positive(transmission.ClutchReleaseTime, "transmission.clutchReleaseTime");
		}

		public double ClutchInLever { get; }

		public double ThrottleStartLever { get; }

		public double DetentHysteresis { get; }

		public double NeutralDwell { get; }

		public double ThrottlePause { get; }

		public double ShiftInterlockSpeed { get; }

		public double ClutchFillTime { get; }

		public double ClutchEngagementTime { get; }

		public double ClutchReleaseTime { get; }

		/// <summary>Throttle demand (0 idle to 1 rated) for a lever position already in gear.</summary>
		public double Throttle(double leverMagnitude) =>
			leverMagnitude <= ThrottleStartLever ? 0.0 : Math.Min(1.0, (leverMagnitude - ThrottleStartLever) / (1.0 - ThrottleStartLever));
	}

	/// <summary>
	/// The control head and electronic shift control for one engine, modelled on ZF-style single-lever
	/// controls: a neutral band, ahead and astern idle detents where the clutch engages, throttle above
	/// the detent, a shift interlock on engine speed, clutch fill and pressure ramp, a throttle pause after
	/// engagement, and a neutral dwell whenever the direction reverses. Ahead never goes straight to astern.
	/// </summary>
	public sealed class EngineControl
	{
		private readonly ShiftSettings _settings;
		private int _lastTransmitted;
		private double _sinceRelease;
		private double _releaseFrom;

		public EngineControl(ShiftSettings settings)
		{
			_settings = settings ?? throw new ArgumentNullException(nameof(settings));
			Reset();
		}

		public ShiftPhase Phase { get; private set; }

		/// <summary>The direction being engaged, engaged, released or waited for (+1 ahead, −1 astern), 0 in neutral.</summary>
		public int Direction { get; private set; }

		/// <summary>Time spent in the current phase, s.</summary>
		public double PhaseTime { get; private set; }

		/// <summary>Governor demand, 0 (idle) to 1 (rated).</summary>
		public double Throttle { get; private set; }

		/// <summary>Clutch pressure as a fraction of full, for the clutch in <see cref="ClutchDirection"/>.</summary>
		public double ClutchPressure { get; private set; }

		/// <summary>Which clutch is pressurised: +1 ahead, −1 astern, 0 none.</summary>
		public int ClutchDirection { get; private set; }

		/// <summary>Gear state as the control head would indicate it.</summary>
		public GearState Gear =>
			Phase == ShiftPhase.Neutral ? GearState.Neutral :
			Phase == ShiftPhase.Engaged ? (Direction > 0 ? GearState.Ahead : GearState.Astern) :
			GearState.Shifting;

		public void Reset()
		{
			Phase = ShiftPhase.Neutral;
			Direction = 0;
			PhaseTime = 0.0;
			Throttle = 0.0;
			ClutchPressure = 0.0;
			ClutchDirection = 0;
			_lastTransmitted = 0;
			_sinceRelease = double.PositiveInfinity;
			_releaseFrom = 0.0;
		}

		/// <summary>
		/// Advances the shift logic by <paramref name="dt"/> for a lever position in [−1, 1] and the current
		/// engine speed (for the shift interlock).
		/// </summary>
		public void Step(double lever, double engineSpeed, double dt)
		{
			var requested = RequestedDirection(lever);
			PhaseTime += dt;
			_sinceRelease += dt;
			switch (Phase)
			{
				case ShiftPhase.Neutral:
					if (requested != 0)
					{
						RequestEngagement(requested);
					}

					break;
				case ShiftPhase.NeutralDwell:
					if (requested == 0)
					{
						Enter(ShiftPhase.Neutral, 0);
					}
					else if (requested != Direction || _sinceRelease >= _settings.NeutralDwell)
					{
						RequestEngagement(requested);
					}

					break;
				case ShiftPhase.AwaitingInterlock:
					if (requested != Direction)
					{
						if (requested == 0)
						{
							Enter(ShiftPhase.Neutral, 0);
						}
						else
						{
							RequestEngagement(requested);
						}
					}
					else if (engineSpeed <= _settings.ShiftInterlockSpeed)
					{
						Enter(ShiftPhase.Filling, Direction);
					}

					break;
				case ShiftPhase.Filling:
					if (requested != Direction)
					{
						// Oil is in the clutch but no torque has been carried yet.
						BeginRelease(transmitted: false);
					}
					else if (PhaseTime >= _settings.ClutchFillTime)
					{
						Enter(ShiftPhase.Engaging, Direction);
					}

					break;
				case ShiftPhase.Engaging:
					if (requested != Direction)
					{
						BeginRelease(transmitted: true);
					}
					else if (PhaseTime >= _settings.ClutchEngagementTime)
					{
						// Engaged at full pressure, as the control system sees it; the clutch may still be
						// slipping (a windmilling propeller being brought round), which shows in shaft speed.
						Enter(ShiftPhase.Engaged, Direction);
					}

					break;
				case ShiftPhase.Engaged:
					if (requested != Direction)
					{
						BeginRelease(transmitted: true);
					}

					break;
				case ShiftPhase.Releasing:
					if (PhaseTime >= _settings.ClutchReleaseTime)
					{
						_sinceRelease = 0.0;
						if (requested == 0)
						{
							Enter(ShiftPhase.Neutral, 0);
						}
						else
						{
							RequestEngagement(requested);
						}
					}

					break;
			}

			UpdateOutputs(lever);
		}

		// Engaging against the direction that last carried torque waits out the neutral dwell first,
		// however the lever got there.
		private void RequestEngagement(int direction)
		{
			if (direction == -_lastTransmitted && _sinceRelease < _settings.NeutralDwell)
			{
				if (Phase != ShiftPhase.NeutralDwell || Direction != direction)
				{
					Enter(ShiftPhase.NeutralDwell, direction);
				}
			}
			else
			{
				Enter(ShiftPhase.AwaitingInterlock, direction);
			}
		}

		private int RequestedDirection(double lever)
		{
			var holding = Phase == ShiftPhase.AwaitingInterlock || Phase == ShiftPhase.Filling ||
				Phase == ShiftPhase.Engaging || Phase == ShiftPhase.Engaged;
			if (holding && lever * Direction >= _settings.ClutchInLever - _settings.DetentHysteresis)
			{
				return Direction;
			}

			return lever >= _settings.ClutchInLever ? 1 : lever <= -_settings.ClutchInLever ? -1 : 0;
		}

		private void BeginRelease(bool transmitted)
		{
			if (transmitted)
			{
				_lastTransmitted = Direction;
			}

			_releaseFrom = ClutchPressure;
			Phase = ShiftPhase.Releasing;
			PhaseTime = 0.0;
		}

		private void Enter(ShiftPhase phase, int direction)
		{
			Phase = phase;
			Direction = direction;
			PhaseTime = 0.0;
		}

		private void UpdateOutputs(double lever)
		{
			switch (Phase)
			{
				case ShiftPhase.Engaging:
					ClutchDirection = Direction;
					ClutchPressure = Math.Min(1.0, PhaseTime / _settings.ClutchEngagementTime);
					break;
				case ShiftPhase.Engaged:
					ClutchDirection = Direction;
					ClutchPressure = 1.0;
					break;
				case ShiftPhase.Releasing:
					ClutchPressure = _releaseFrom * Math.Max(0.0, 1.0 - PhaseTime / _settings.ClutchReleaseTime);
					if (ClutchPressure <= 0.0)
					{
						ClutchDirection = 0;
					}

					break;
				default:
					ClutchDirection = 0;
					ClutchPressure = 0.0;
					break;
			}

			var throttleAllowed = Phase == ShiftPhase.Engaged && PhaseTime >= _settings.ThrottlePause && lever * Direction > 0.0;
			Throttle = throttleAllowed ? _settings.Throttle(Math.Abs(lever)) : 0.0;
		}
	}

	/// <summary>
	/// Helm to rudder angle through a rate-limited steering gear. Follow-up: the helm position orders a
	/// rudder angle (helm × hard over). Non-follow-up: the helm orders the rudder to move at the steering
	/// rate in that direction and it stays put when the helm is centred. Both rudders move together.
	/// </summary>
	public sealed class SteeringGear
	{
		private readonly bool _followUp;

		public SteeringGear(VesselParameters parameters)
		{
			var controls = Required.Section(parameters.Controls, "controls");
			_followUp = Required.Text(controls.SteeringMode, "controls.steeringMode", "follow-up", "non-follow-up") == "follow-up";
			HardOverAngle = Required.InRange(controls.HardOverAngle, "controls.hardOverAngle", 0.1, 1.2);
			SteeringRate = Required.Positive(controls.SteeringRate, "controls.steeringRate");
		}

		public double HardOverAngle { get; }

		public double SteeringRate { get; }

		/// <summary>Rudder angle, rad, positive is starboard rudder.</summary>
		public double Angle { get; private set; }

		/// <summary>The rudder angle currently ordered, rad.</summary>
		public double Order { get; private set; }

		public void Reset()
		{
			Angle = 0.0;
			Order = 0.0;
		}

		public void Step(double helm, double dt)
		{
			var maximumMove = SteeringRate * dt;
			if (_followUp)
			{
				Order = Math.Clamp(helm, -1.0, 1.0) * HardOverAngle;
				Angle += Math.Clamp(Order - Angle, -maximumMove, maximumMove);
			}
			else
			{
				Angle = Math.Clamp(Angle + Math.Clamp(helm, -1.0, 1.0) * maximumMove, -HardOverAngle, HardOverAngle);
				Order = Angle;
			}
		}
	}
}
