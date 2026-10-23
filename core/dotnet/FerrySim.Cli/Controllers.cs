using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>Produces the operator inputs for each physics step and says when the run is over.</summary>
	public interface IController
	{
		/// <summary>Set by <see cref="Command"/> when the row it is called for is the last one to record.</summary>
		bool Finished { get; }

		/// <summary>Controls to apply from this instant; <paramref name="elapsed"/> is seconds since the controller started.</summary>
		ControlInputs Command(double elapsed, VesselState state, VesselTelemetry telemetry);
	}

	/// <summary>Accumulates heading change without wrapping, so a full circle reads 360 degrees.</summary>
	internal sealed class HeadingTracker
	{
		private bool _started;
		private double _last;

		/// <summary>Radians turned since the first call, positive to starboard.</summary>
		public double Change { get; private set; }

		public double Update(double heading)
		{
			if (_started)
			{
				Change += Angles.WrapPi(heading - _last);
			}

			_started = true;
			_last = heading;
			return Change;
		}
	}

	/// <summary>Holds the levers until the speed through water stops changing (or a time limit passes).</summary>
	internal sealed class SettleController : IController
	{
		public const double MaxSeconds = 900.0;
		public const double MinSeconds = 30.0;
		public const double WindowSeconds = 20.0;
		public const double ToleranceMetresPerSecond = 0.005;

		private readonly ControlInputs _controls;
		private readonly List<double> _speeds = new List<double>();

		public SettleController(double lever)
		{
			_controls = new ControlInputs(lever, lever, 0.0);
		}

		public bool Finished { get; private set; }

		public ControlInputs Command(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			// Judge only on whole seconds so the maneuver that follows starts on the sample grid.
			if (Math.Abs(elapsed - Math.Round(elapsed)) < 1e-9)
			{
				_speeds.Add(telemetry.SpeedThroughWater);
				var window = (int)WindowSeconds;
				if (elapsed >= MaxSeconds)
				{
					Finished = true;
				}
				else if (elapsed >= MinSeconds && _speeds.Count > window &&
					Math.Abs(_speeds[_speeds.Count - 1] - _speeds[_speeds.Count - 1 - window]) <= ToleranceMetresPerSecond)
				{
					Finished = true;
				}
			}

			return _controls;
		}
	}

	/// <summary>
	/// Base for the standard maneuvers. With a fixed duration the run is exactly that long; otherwise
	/// it ends when the maneuver is complete or <c>cap</c> seconds have passed.
	/// </summary>
	internal abstract class ManeuverController : IController
	{
		private readonly double? _fixedDuration;
		private readonly double _cap;

		protected ManeuverController(double? fixedDuration, double cap)
		{
			_fixedDuration = fixedDuration;
			_cap = cap;
		}

		public bool Finished { get; private set; }

		public ControlInputs Command(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			var controls = Decide(elapsed, state, telemetry);
			var complete = IsComplete(elapsed, state, telemetry);
			Finished = _fixedDuration.HasValue ? elapsed >= _fixedDuration.Value : complete || elapsed >= _cap;
			return controls;
		}

		protected abstract ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry);

		protected abstract bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry);
	}

	/// <summary>Hard-over helm at the test speed, levers held, until the heading has changed more than a full circle.</summary>
	internal sealed class TurningCircleController : ManeuverController
	{
		public const double CompletionDegrees = 370.0;
		public const double DefaultCap = 1800.0;

		private readonly double _hold;
		private readonly double _helm;
		private readonly HeadingTracker _heading = new HeadingTracker();

		public TurningCircleController(double hold, double helm, double? fixedDuration)
			: base(fixedDuration, DefaultCap)
		{
			_hold = hold;
			_helm = helm;
		}

		protected override ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			_heading.Update(state.Heading);
			return new ControlInputs(_hold, _hold, _helm);
		}

		protected override bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry) =>
			Math.Abs(Angles.ToDegrees(_heading.Change)) >= CompletionDegrees;
	}

	/// <summary>
	/// Reverses the helm each time the heading change reaches the check angle. It stops once the third
	/// reversal is ordered, which is when both overshoots are known.
	/// </summary>
	internal sealed class ZigzagController : ManeuverController
	{
		public const double DefaultCap = 1800.0;

		private readonly double _hold;
		private readonly double _helm;
		private readonly double _check;
		private readonly double _firstSign;
		private readonly HeadingTracker _heading = new HeadingTracker();
		private int _reversals;
		private bool _complete;

		public ZigzagController(double hold, double helm, double checkRadians, double firstSign, double? fixedDuration)
			: base(fixedDuration, DefaultCap)
		{
			_hold = hold;
			_helm = helm;
			_check = checkRadians;
			_firstSign = firstSign;
		}

		protected override ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			var change = _heading.Update(state.Heading);
			// Turning first one way, then the other: the heading target alternates sign each reversal.
			var target = (_reversals % 2 == 0 ? 1.0 : -1.0) * _firstSign;
			if (target * change >= _check)
			{
				_reversals++;
				_complete = _reversals >= 3;
			}

			var side = (_reversals % 2 == 0 ? 1.0 : -1.0) * _firstSign;
			return new ControlInputs(_hold, _hold, side * _helm);
		}

		protected override bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry) => _complete;
	}

	/// <summary>
	/// Constant levers and helm from the start of the maneuver (crash-stop, stopping). With
	/// <c>stopsRun</c> the run ends five seconds after the speed through water drops to the stop threshold.
	/// </summary>
	internal sealed class StopController : ManeuverController
	{
		public const double StopSpeedMetresPerSecond = 0.1;
		public const double MarginSeconds = 5.0;
		public const double DefaultCap = 1800.0;

		private readonly ControlInputs _controls;
		private double _stoppedAt = double.NaN;

		public StopController(double lever, double? fixedDuration)
			: base(fixedDuration, DefaultCap)
		{
			_controls = new ControlInputs(lever, lever, 0.0);
		}

		protected override ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			if (double.IsNaN(_stoppedAt) && telemetry.SpeedThroughWater <= StopSpeedMetresPerSecond)
			{
				_stoppedAt = elapsed;
			}

			return _controls;
		}

		protected override bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry) =>
			!double.IsNaN(_stoppedAt) && elapsed >= _stoppedAt + MarginSeconds;
	}

	/// <summary>Constant inputs for a fixed time, or until the speed through water has stopped rising (acceleration).</summary>
	internal sealed class HoldController : ManeuverController
	{
		public const double DefaultPivotSeconds = 90.0;
		public const double AccelerationCap = 900.0;
		public const double AccelerationMinSeconds = 120.0;
		public const double AccelerationWindowSeconds = 60.0;
		public const double AccelerationToleranceMetresPerSecond = 0.02;

		private readonly ControlInputs _controls;
		private readonly bool _untilSteadySpeed;
		private readonly List<double> _speeds = new List<double>();

		public HoldController(ControlInputs controls, double? fixedDuration, bool untilSteadySpeed, double cap)
			: base(fixedDuration, cap)
		{
			_controls = controls;
			_untilSteadySpeed = untilSteadySpeed;
		}

		protected override ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			if (Math.Abs(elapsed - Math.Round(elapsed)) < 1e-9)
			{
				_speeds.Add(telemetry.SpeedThroughWater);
			}

			return _controls;
		}

		protected override bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			if (!_untilSteadySpeed)
			{
				return false;
			}

			var window = (int)AccelerationWindowSeconds;
			return elapsed >= AccelerationMinSeconds && _speeds.Count > window &&
				Math.Abs(_speeds[_speeds.Count - 1] - _speeds[_speeds.Count - 1 - window]) <= AccelerationToleranceMetresPerSecond;
		}
	}
}
