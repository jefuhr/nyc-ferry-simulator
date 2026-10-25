using System;
using FerrySim.Sim;

namespace FerrySim.UI
{
	public enum CoverageIndication
	{
		None = 0,

		/// <summary>The count went up within the hold time: the environment is making this value up now.</summary>
		Live = 1,

		/// <summary>The count is above zero but has stopped: it happened earlier in this run.</summary>
		Earlier = 2,
	}

	/// <summary>
	/// Turns one of the environment's per-run counts (<see cref="EnvironmentReadout.TimeHolds"/> or
	/// <see cref="EnvironmentReadout.DepthMisses"/>) into a warning. Any count above zero is shown, as the contract
	/// asks. It is Live while the count keeps rising, and for <see cref="HoldSeconds"/> of simulated time after it last
	/// rose, so misses that come every other step do not flicker and a paused run keeps its warning; after that it is
	/// Earlier, which the panel draws outlined, like a finished contact. A count that drops, or time that goes back,
	/// is a counter that started over, never more misses.
	/// </summary>
	public sealed class CoverageMonitor
	{
		public const double DefaultHoldSeconds = 1.0;

		private bool _live;
		private double _lastRiseTime = double.NaN;
		private double _lastUpdateTime = double.NaN;

		public CoverageMonitor(double holdSeconds = DefaultHoldSeconds)
		{
			HoldSeconds = Math.Max(0.0, holdSeconds);
		}

		public double HoldSeconds { get; }

		public CoverageIndication Indication { get; private set; }

		/// <summary>The count at the latest update.</summary>
		public long Count { get; private set; }

		/// <param name="count">The source's count for the current run.</param>
		/// <param name="time">Simulation time of the latest sample, seconds; NaN when unknown.</param>
		public void Update(long count, double time)
		{
			if (count < Count || (InstrumentFormat.IsFinite(_lastUpdateTime) && time < _lastUpdateTime))
			{
				Reset();
			}

			if (InstrumentFormat.IsFinite(time))
			{
				_lastUpdateTime = time;
			}

			if (count > Count)
			{
				_live = true;
				_lastRiseTime = time;
			}
			else if (_live && InstrumentFormat.IsFinite(time))
			{
				if (!InstrumentFormat.IsFinite(_lastRiseTime))
				{
					// It rose while time was unknown: the hold starts now.
					_lastRiseTime = time;
				}
				else if (time - _lastRiseTime >= HoldSeconds)
				{
					_live = false;
				}
			}

			Count = Math.Max(0L, count);
			Indication = Count == 0 ? CoverageIndication.None : _live ? CoverageIndication.Live : CoverageIndication.Earlier;
		}

		/// <summary>Forgets the run, for a new one.</summary>
		public void Reset()
		{
			Count = 0;
			_live = false;
			_lastRiseTime = double.NaN;
			_lastUpdateTime = double.NaN;
			Indication = CoverageIndication.None;
		}
	}
}
