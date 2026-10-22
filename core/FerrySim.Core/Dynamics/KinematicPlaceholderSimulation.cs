using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// PLACEHOLDER, not a physical model. First-order lags from levers and helm to speed and turn
	/// rate, plus drift with the current, so integration plumbing can be exercised end to end. It
	/// honours the sign conventions in docs/conventions.md and passes the contract conformance
	/// tests; nothing about its handling should be trusted.
	/// </summary>
	public sealed class KinematicPlaceholderSimulation : ISimulation
	{
		private const double FallbackServiceSpeed = 12.0;
		private const double SpeedTimeConstant = 12.0;
		private const double TurnTimeConstant = 3.0;
		private const double HelmTurnRateAtServiceSpeed = 0.08;
		private const double DifferentialTurnRate = 0.06;
		private const double AsternSpeedFraction = 0.4;

		private readonly IEnvironment _environment;
		private readonly double _serviceSpeed;
		private long _stepCount;
		private double _waterSurge;
		private double _yawRate;
		private double _north;
		private double _east;
		private double _heading;

		public KinematicPlaceholderSimulation(VesselParameters parameters, IEnvironment environment, in VesselState initialState)
		{
			Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
			_environment = environment ?? throw new ArgumentNullException(nameof(environment));
			var published = parameters.Performance?.ServiceSpeed?.Value ?? 0.0;
			_serviceSpeed = published > 0.0 ? published : FallbackServiceSpeed;
			Reset(initialState);
		}

		public VesselParameters Parameters { get; }

		public VesselState State { get; private set; }

		public VesselTelemetry Telemetry { get; private set; }

		public void Reset(in VesselState initialState)
		{
			_stepCount = 0;
			_north = initialState.North;
			_east = initialState.East;
			_heading = initialState.Heading;
			_yawRate = initialState.YawRate;
			var current = _environment.SampleCurrent(_north, _east, 0.0);
			var groundAlongHeading = initialState.GroundVelocity.North * Math.Cos(_heading) + initialState.GroundVelocity.East * Math.Sin(_heading);
			var currentAlongHeading = current.North * Math.Cos(_heading) + current.East * Math.Sin(_heading);
			_waterSurge = groundAlongHeading - currentAlongHeading;
			Publish(0.0, ControlInputs.Neutral);
		}

		public void Step(in ControlInputs controls)
		{
			const double dt = SimulationClock.FixedTimeStep;
			var time = SimulationClock.TimeAtStep(_stepCount);

			var meanLever = 0.5 * (controls.PortLever + controls.StarboardLever);
			var targetSurge = meanLever >= 0.0 ? meanLever * _serviceSpeed : meanLever * _serviceSpeed * AsternSpeedFraction;
			_waterSurge += (targetSurge - _waterSurge) * (dt / SpeedTimeConstant);

			// Port engine ahead alone turns the bow to starboard (positive yaw rate).
			var differential = 0.5 * (controls.PortLever - controls.StarboardLever);
			var helmAuthority = _waterSurge / _serviceSpeed;
			var targetYawRate = controls.Helm * HelmTurnRateAtServiceSpeed * helmAuthority + differential * DifferentialTurnRate;
			_yawRate += (targetYawRate - _yawRate) * (dt / TurnTimeConstant);

			var current = _environment.SampleCurrent(_north, _east, time);
			_heading = Angles.WrapTwoPi(_heading + _yawRate * dt);
			_north += (_waterSurge * Math.Cos(_heading) + current.North) * dt;
			_east += (_waterSurge * Math.Sin(_heading) + current.East) * dt;

			_stepCount++;
			Publish(SimulationClock.TimeAtStep(_stepCount), controls);
		}

		private void Publish(double time, in ControlInputs controls)
		{
			var current = _environment.SampleCurrent(_north, _east, time);
			var groundNorth = _waterSurge * Math.Cos(_heading) + current.North;
			var groundEast = _waterSurge * Math.Sin(_heading) + current.East;
			var cos = Math.Cos(_heading);
			var sin = Math.Sin(_heading);
			var surge = groundNorth * cos + groundEast * sin;
			var sway = -groundNorth * sin + groundEast * cos;
			State = new VesselState(time, _north, _east, 0.0, 0.0, 0.0, _heading, surge, sway, 0.0, 0.0, 0.0, _yawRate);

			var ground = new HorizontalVector(groundNorth, groundEast);
			var draft = Parameters.Particulars?.Draft?.Value ?? 0.0;
			Telemetry = new VesselTelemetry(
				Engine(controls.PortLever),
				Engine(controls.StarboardLever),
				controls.Helm * 0.6,
				controls.Helm * 0.6,
				_waterSurge,
				ground.Magnitude,
				ground.Magnitude > 0.0 ? ground.Direction : _heading,
				_environment.SampleDepth(_north, _east, time) - draft,
				0.0);
		}

		private static EngineTelemetry Engine(double lever)
		{
			var gear = lever > 0.05 ? GearState.Ahead : lever < -0.05 ? GearState.Astern : GearState.Neutral;
			var throttle = Math.Abs(lever);
			var engineSpeed = Units.RpmToRadiansPerSecond(600.0 + 1500.0 * throttle);
			var shaftSpeed = gear == GearState.Neutral ? 0.0 : Math.Sign(lever) * engineSpeed / 2.5;
			return new EngineTelemetry(engineSpeed, shaftSpeed, gear, throttle);
		}
	}
}
