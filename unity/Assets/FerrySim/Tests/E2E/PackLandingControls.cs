using System;
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
	/// It reads the runner's state before each step, so it is deterministic. Speeds and gains are authoring estimates, not
	/// operating guidance. The back-off is short because Pier 11's charted berth, 2.0 m below MLLW, ends 35 m off the face;
	/// past it the depth grid shoals to 1.2 m below NAVD88, where a T-class at mid tide would ground.
	/// </summary>
	internal sealed class PackLandingControls : IControlInputSource
	{
		/// <summary>How far to back clear before coming back in, m.</summary>
		public const double BackOff = 15.0;

		/// <summary>Speed over the ground the approach slows to near the face, m/s.</summary>
		public const double ContactSpeed = 0.3;

		private readonly VesselSimulationRunner _runner;
		private readonly HorizontalVector _middle;
		private readonly HorizontalVector _normal;
		private VesselState _initial;
		private ControlInputs _controls = ControlInputs.Neutral;

		public PackLandingControls(VesselSimulationRunner runner, LandingFace face)
		{
			_runner = runner ?? throw new ArgumentNullException(nameof(runner));
			_middle = (face.Start + face.End) * 0.5;
			var along = face.End - face.Start;
			along = along * (1.0 / along.Magnitude);
			_normal = new HorizontalVector(along.East, -along.North);
		}

		/// <summary>-1 backing clear, 0 stopping, 1 approaching, 2 within 20 m, 3 holding on the face.</summary>
		public int Phase { get; private set; } = -1;

		/// <summary>The first step the hold applied, or -1.</summary>
		public long HoldFromStep { get; private set; } = -1;

		/// <summary>Contact before the approach began: a departure that touched the face.</summary>
		public bool DepartureContact { get; private set; }

		public ControlInputs ControlsForStep(long step)
		{
			var simulation = (VesselDynamicsSimulation)_runner.Driver.Simulation;
			var state = _runner.State;
			if (step == 0)
			{
				Phase = -1;
				HoldFromStep = -1;
				DepartureContact = false;
				_initial = state;
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
				}
			}
			else
			{
				var heading = inward + Math.Atan2(cross, Math.Max(25.0, distance));
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
