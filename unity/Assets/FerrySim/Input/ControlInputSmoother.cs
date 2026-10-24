using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Controls
{
	/// <summary>
	/// Device-independent smoothing of <see cref="ControlInputs"/>, only enough to calm device noise (a jittery
	/// potentiometer, a stick that never quite rests). Each channel follows its input with a first-order lag of
	/// <see cref="TimeConstant"/>, advanced once per physics step so the result does not depend on the frame
	/// rate, and lands exactly on the input once within <see cref="SettleTolerance"/>. A steady input therefore
	/// comes out unchanged, bit for bit, from the first step. Detents, shift timing and steering behaviour are
	/// the core's control model, never this.
	/// </summary>
	public sealed class ControlInputSmoother
	{
		public const double DefaultTimeConstant = 0.05;

		/// <summary>Closer than this to the input, the output snaps onto it.</summary>
		public const double SettleTolerance = 1e-6;

		private double _timeConstant;
		private double _blend;
		private bool _hasOutput;
		private double _port;
		private double _starboard;
		private double _helm;

		public ControlInputSmoother(double timeConstant = DefaultTimeConstant)
		{
			TimeConstant = timeConstant;
		}

		/// <summary>Seconds for the output to cover 63% of a step change; zero passes inputs straight through.</summary>
		public double TimeConstant
		{
			get => _timeConstant;
			set
			{
				if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
				{
					throw new ArgumentOutOfRangeException(nameof(value), "The smoothing time constant must be zero or a positive number of seconds.");
				}

				_timeConstant = value;
				_blend = value > 0.0 ? 1.0 - Math.Exp(-SimulationClock.FixedTimeStep / value) : 1.0;
			}
		}

		/// <summary>The most recent output; neutral before the first step.</summary>
		public ControlInputs Output => _hasOutput ? new ControlInputs(_port, _starboard, _helm) : ControlInputs.Neutral;

		/// <summary>Forgets the history: the next step's output equals its input.</summary>
		public void Reset() => _hasOutput = false;

		/// <summary>Advances one physics step toward <paramref name="input"/> and returns the smoothed controls.</summary>
		public ControlInputs Step(in ControlInputs input)
		{
			if (!_hasOutput || _timeConstant <= 0.0)
			{
				_port = input.PortLever;
				_starboard = input.StarboardLever;
				_helm = input.Helm;
				_hasOutput = true;
				return input;
			}

			_port = Approach(_port, input.PortLever, _blend);
			_starboard = Approach(_starboard, input.StarboardLever, _blend);
			_helm = Approach(_helm, input.Helm, _blend);
			return new ControlInputs(_port, _starboard, _helm);
		}

		private static double Approach(double current, double target, double blend)
		{
			var next = current + (target - current) * blend;
			return Math.Abs(target - next) <= SettleTolerance ? target : next;
		}
	}
}
