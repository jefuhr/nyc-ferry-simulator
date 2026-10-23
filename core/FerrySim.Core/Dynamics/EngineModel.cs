using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// A governed marine diesel: full-load torque against speed, friction, a PI speed governor and a
	/// first-order turbocharger boost lag that limits how fast torque can build. Immutable; the
	/// per-engine state lives in <see cref="Driveline"/>.
	/// </summary>
	public sealed class EngineModel
	{
		private readonly double[] _curveSpeed;
		private readonly double[] _curveTorque;
		private readonly double _frictionAtIdle;
		private readonly double _frictionSlope;

		public EngineModel(VesselParameters parameters, double shaftSideInertia, double gearRatio)
		{
			var machinery = Required.Section(parameters.Machinery, "machinery");
			var section = Required.Section(parameters.Engines, "engines");
			RatedPower = Required.Positive(machinery.RatedPowerPerEngine, "machinery.ratedPowerPerEngine");
			RatedSpeed = Required.Positive(machinery.RatedEngineSpeed, "machinery.ratedEngineSpeed");
			IdleSpeed = Required.Positive(section.IdleSpeed, "engines.idleSpeed");
			if (!(IdleSpeed < RatedSpeed))
			{
				throw new ArgumentException("engines.idleSpeed must be below machinery.ratedEngineSpeed.");
			}

			Inertia = Required.Positive(section.Inertia, "engines.inertia");
			_frictionAtIdle = Required.NonNegative(section.FrictionTorqueAtIdle, "engines.frictionTorqueAtIdle");
			var frictionAtRated = Required.NonNegative(section.FrictionTorqueAtRatedSpeed, "engines.frictionTorqueAtRatedSpeed");
			_frictionSlope = (frictionAtRated - _frictionAtIdle) / (RatedSpeed - IdleSpeed);
			UnboostedTorqueFraction = Required.InRange(section.UnboostedTorqueFraction, "engines.unboostedTorqueFraction", 0.05, 1.0);
			TurbochargerTimeConstant = Required.Positive(section.TurbochargerTimeConstant, "engines.turbochargerTimeConstant");

			var points = Required.Section(section.TorqueCurve, "engines.torqueCurve");
			if (points.Count < 2)
			{
				throw new ArgumentException("engines.torqueCurve needs at least two points.");
			}

			_curveSpeed = new double[points.Count];
			_curveTorque = new double[points.Count];
			for (var i = 0; i < points.Count; i++)
			{
				var point = Required.Section(points[i], $"engines.torqueCurve[{i}]");
				_curveSpeed[i] = Required.Positive(point.Speed, $"engines.torqueCurve[{i}].speed");
				_curveTorque[i] = Required.Positive(point.Torque, $"engines.torqueCurve[{i}].torque");
				if (i > 0 && !(_curveSpeed[i] > _curveSpeed[i - 1]))
				{
					throw new ArgumentException("engines.torqueCurve must be in increasing speed order.");
				}
			}

			// Tune the PI governor on the inertia it drives with the gear engaged.
			var timeConstant = Required.Positive(section.GovernorTimeConstant, "engines.governorTimeConstant");
			var integralTime = Required.Positive(section.GovernorIntegralTime, "engines.governorIntegralTime");
			var engagedInertia = Inertia + shaftSideInertia / (gearRatio * gearRatio);
			ProportionalGain = engagedInertia / timeConstant;
			IntegralGain = ProportionalGain / integralTime;
		}

		public double RatedPower { get; }

		public double RatedSpeed { get; }

		public double IdleSpeed { get; }

		public double Inertia { get; }

		public double UnboostedTorqueFraction { get; }

		public double TurbochargerTimeConstant { get; }

		/// <summary>Governor proportional gain, N·m per rad/s.</summary>
		public double ProportionalGain { get; }

		/// <summary>Governor integral gain, N·m per rad.</summary>
		public double IntegralGain { get; }

		/// <summary>Rated torque, N·m (rated power over rated speed).</summary>
		public double RatedTorque => RatedPower / RatedSpeed;

		/// <summary>Governor speed set point for a throttle demand between 0 (idle) and 1 (rated).</summary>
		public double SpeedSetPoint(double throttle) => IdleSpeed + Math.Clamp(throttle, 0.0, 1.0) * (RatedSpeed - IdleSpeed);

		/// <summary>Full-load brake torque at an engine speed, N·m (held flat outside the curve).</summary>
		public double MaximumBrakeTorque(double speed)
		{
			var last = _curveSpeed.Length - 1;
			if (speed <= _curveSpeed[0])
			{
				return _curveTorque[0];
			}

			if (speed >= _curveSpeed[last])
			{
				return _curveTorque[last];
			}

			var i = 1;
			while (_curveSpeed[i] < speed)
			{
				i++;
			}

			var t = (speed - _curveSpeed[i - 1]) / (_curveSpeed[i] - _curveSpeed[i - 1]);
			return _curveTorque[i - 1] + t * (_curveTorque[i] - _curveTorque[i - 1]);
		}

		/// <summary>Friction and pumping torque at an engine speed, N·m (zero when stopped).</summary>
		public double FrictionTorque(double speed)
		{
			if (speed <= 0.0)
			{
				return 0.0;
			}

			var friction = _frictionAtIdle + _frictionSlope * (speed - IdleSpeed);
			var stopped = speed < IdleSpeed ? speed / IdleSpeed : 1.0;
			return Math.Max(0.0, friction) * stopped;
		}
	}
}
