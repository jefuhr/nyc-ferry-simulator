using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using FerrySim.Slice;

namespace FerrySim.E2E
{
	/// <summary>
	/// The scripted landing for real days, authored like the slice trips (TraceAuthoring.Navigate's helm, twist and speed
	/// laws, commands every 0.1 s rounded to hundredths) but run in the loop, so it lands whatever the day's current does
	/// and its applied controls are recorded and replayed bit for bit. From the catalog's start, bow 4 m off the face, it
	/// backs clear astern <see cref="BackOff"/> m, stops, comes back in bow-first slowing to <see cref="ContactSpeed"/> over
	/// the ground, and once the hull touches holds it on the face: ahead idle, more when the current pulls the hull off, and
	/// a twist that keeps the bow square to the face (a following current turns a bow pinned on a face away from square).
	/// Given waypoints, from a start away from the face, it instead runs them first: it steers for a point
	/// <see cref="Lookahead"/> m along each leg, lays off the set it measures (the angle between its track over the ground
	/// and its heading) and slows to <see cref="ArrivalSpeed"/> at the last one. There it turns square to the face and comes
	/// in as above, laying off the set until the bow is <see cref="SetAllowanceEnds"/> m off, so that it arrives square and
	/// holds. It reads the runner's state before each step, so it is deterministic. Speeds and gains are authoring
	/// estimates, not operating guidance. The back-off is short because Pier 11's charted berth, 2.0 m below MLLW, ends
	/// 35 m off the face; past it the depth grid shoals to 1.2 m below NAVD88, where a T-class at mid tide would ground.
	/// </summary>
	internal sealed class PackLandingControls : IControlInputSource
	{
		/// <summary>How far to back clear before coming back in, m.</summary>
		public const double BackOff = 15.0;

		/// <summary>Speed over the ground the approach slows to near the face, m/s.</summary>
		public const double ContactSpeed = 0.3;

		/// <summary>Speed over the ground between waypoints, m/s (about 3.9 kn).</summary>
		public const double TransitSpeed = 2.0;

		/// <summary>Speed over the ground at the last waypoint, m/s.</summary>
		public const double ArrivalSpeed = 0.5;

		/// <summary>How far along a leg the waypoint steering aims, m.</summary>
		public const double Lookahead = 15.0;

		/// <summary>Bow distance from the face at which the approach stops laying off the set, m; the allowance tapers out over the 10 m before.</summary>
		public const double SetAllowanceEnds = 3.0;

		private readonly VesselSimulationRunner _runner;
		private readonly HorizontalVector _middle;
		private readonly HorizontalVector _normal;
		private readonly HorizontalVector[] _path;
		private VesselState _initial;
		private ControlInputs _controls = ControlInputs.Neutral;
		private int _leg;
		private double _set;

		public PackLandingControls(VesselSimulationRunner runner, LandingFace face)
			: this(runner, face, Array.Empty<HorizontalVector>())
		{
		}

		/// <param name="waypoints">Points to run through before turning in, in order; none for the catalog's start.</param>
		public PackLandingControls(VesselSimulationRunner runner, LandingFace face, IReadOnlyList<HorizontalVector> waypoints)
		{
			_runner = runner ?? throw new ArgumentNullException(nameof(runner));
			_middle = (face.Start + face.End) * 0.5;
			var along = face.End - face.Start;
			along = along * (1.0 / along.Magnitude);
			_normal = new HorizontalVector(along.East, -along.North);
			_path = new HorizontalVector[(waypoints?.Count ?? 0) + 1];
			for (var i = 1; i < _path.Length; i++)
			{
				_path[i] = waypoints[i - 1];
			}
		}

		/// <summary>True when the landing runs waypoints from a start away from the face.</summary>
		public bool FromAway => _path.Length > 1;

		/// <summary>-3 running the waypoints, -2 turning square to the face, -1 backing clear, 0 stopping, 1 approaching, 2 within 20 m, 3 holding on the face.</summary>
		public int Phase { get; private set; } = -1;

		/// <summary>The first step the hold applied, or -1.</summary>
		public long HoldFromStep { get; private set; } = -1;

		/// <summary>The step the approach to the face began, or -1.</summary>
		public long ApproachFromStep { get; private set; } = -1;

		/// <summary>Contact before the approach began: a departure that touched the face, or a run in that touched anything.</summary>
		public bool DepartureContact { get; private set; }

		public ControlInputs ControlsForStep(long step)
		{
			var simulation = (VesselDynamicsSimulation)_runner.Driver.Simulation;
			var state = _runner.State;
			if (step == 0)
			{
				Phase = FromAway ? -3 : -1;
				HoldFromStep = -1;
				ApproachFromStep = -1;
				DepartureContact = false;
				_initial = state;
				_path[0] = new HorizontalVector(state.North, state.East);
				_leg = 1;
				_set = 0.0;
			}

			var touching = simulation.ContactCount > 0;
			if (touching && Phase < 1 && step > 0)
			{
				DepartureContact = true;
			}

			if (touching && Phase >= 1 && HoldFromStep < 0)
			{
				HoldFromStep = step;
				Phase = 3;
			}

			if (step % 10 != 0)
			{
				return _controls;
			}

			// The set: how far the track over the ground lies off the heading, smoothed over about 3 s.
			var ground = state.GroundVelocity;
			var set = ground.Magnitude > 0.3 ? Angles.WrapPi(Math.Atan2(ground.East, ground.North) - state.Heading) : 0.0;
			_set += (Clamp(set, -0.7, 0.7) - _set) * (0.1 / 3.0);

			var inward = Math.Atan2(-_normal.East, -_normal.North);
			var fromFace = new HorizontalVector(state.North, state.East) - _middle;
			var distance = fromFace.North * _normal.North + fromFace.East * _normal.East - 0.5 * _runner.Parameters.Particulars.LengthOverall.Value;
			var cross = fromFace.North * -_normal.East + fromFace.East * _normal.North;
			if (Phase == 3)
			{
				var push = Clamp(0.15 + 0.6 * Math.Max(0.0, -state.Surge), 0.15, 0.45);
				var error = Angles.WrapPi(inward - state.Heading);
				var twist = Clamp(3.0 * error - 8.0 * state.YawRate, -0.5, 0.5);
				var helm = Clamp(2.0 * error - 5.0 * state.YawRate, -1.0, 1.0);
				_controls = new ControlInputs(Round(push + twist), Round(push - twist), Round(helm));
			}
			else if (Phase == -3)
			{
				var from = _path[_leg - 1];
				var leg = _path[_leg] - from;
				var length = leg.Magnitude;
				var direction = leg * (1.0 / length);
				var relative = new HorizontalVector(state.North, state.East) - from;
				var along = relative.North * direction.North + relative.East * direction.East;
				var right = -relative.North * direction.East + relative.East * direction.North;
				var remaining = length - along;
				for (var k = _leg + 1; k < _path.Length; k++)
				{
					remaining += (_path[k] - _path[k - 1]).Magnitude;
				}

				var course = Math.Atan2(direction.East, direction.North) - Math.Atan2(right, Lookahead);
				_controls = Navigate(state, course - _set, Math.Min(TransitSpeed, ArrivalSpeed + 0.04 * Math.Max(0.0, remaining)), -1.0);
				if (along >= length - 1.0)
				{
					_leg++;
					if (_leg >= _path.Length)
					{
						Phase = -2;
					}
				}
			}
			else if (Phase == -2)
			{
				var error = Angles.WrapPi(inward - state.Heading);
				_controls = Navigate(state, inward, 0.0, -1.0);
				if (Math.Abs(error) < 0.1 && Math.Abs(state.YawRate) < 0.02)
				{
					Phase = 1;
					ApproachFromStep = step;
				}
			}
			else if (Phase < 0)
			{
				var backed = -(state.North - _initial.North) * Math.Cos(_initial.Heading) - (state.East - _initial.East) * Math.Sin(_initial.Heading);
				_controls = Navigate(state, inward, -1.5, -1.0);
				if (backed >= BackOff)
				{
					Phase = 0;
				}
			}
			else if (Phase == 0)
			{
				_controls = Navigate(state, inward, 0.0, -1.0);
				if (Math.Abs(state.Surge) < 0.15)
				{
					Phase = 1;
					ApproachFromStep = step;
				}
			}
			else
			{
				var heading = inward + Math.Atan2(cross, Math.Max(25.0, distance));
				if (FromAway)
				{
					heading -= Clamp((distance - SetAllowanceEnds) / 10.0, 0.0, 1.0) * Clamp(_set, -0.5, 0.5);
				}

				_controls = Navigate(state, heading, Clamp(0.06 * distance, ContactSpeed, 1.2), -1.0);
				Phase = Math.Max(Phase, distance < 20.0 ? 2 : 1);
			}

			_controls = FixedStepDriver.Canonical(_controls);
			return _controls;
		}

		private static ControlInputs Navigate(in VesselState state, double heading, double targetSpeed, double astern)
		{
			var error = Angles.WrapPi(heading - state.Heading);
			var common = Clamp(0.4 * (targetSpeed - state.Surge) + 0.02 * targetSpeed, astern, 0.6);
			var twist = Clamp(1.3 * error - 7.0 * state.YawRate, -0.35, 0.35);
			var helm = Clamp(2.0 * error - 5.0 * state.YawRate, -1.0, 1.0);
			return new ControlInputs(Round(common + twist), Round(common - twist), Round(helm));
		}

		private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));

		private static double Round(double value) => Math.Round(Clamp(value, -1.0, 1.0), 2, MidpointRounding.AwayFromZero);
	}
}
