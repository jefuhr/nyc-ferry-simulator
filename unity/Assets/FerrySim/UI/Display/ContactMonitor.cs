using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.UI
{
	public enum ContactIndication
	{
		None = 0,

		/// <summary>The latest physics step had contact.</summary>
		Live = 1,

		/// <summary>Contact ended within the hold time; the indicator still shows the peak so a brief touch is not missed.</summary>
		Recent = 2,

		/// <summary>The source cannot say whether the hull is touching (a non-finite contact reading). Not the same as no contact.</summary>
		Unknown = 3,
	}

	/// <summary>
	/// Drives the contact indicator from the source's <see cref="ContactLatch"/>, which counts every physics
	/// step with contact, so a touch that lasts one 10 ms step between two rendered frames is still seen.
	/// Landing on fenders is normal, so contact is an indication, not an alarm: it never flashes. It is Live
	/// while the latest step touches; afterwards it holds the episode's peak for a few seconds of simulated
	/// time after the last touching step. Touches inside the hold time belong to the same episode. A
	/// missing reading is Unknown, never None, but a touch the latch recorded is still shown.
	/// </summary>
	public sealed class ContactMonitor
	{
		public const double DefaultHoldSeconds = 3.0;

		private long _seenSteps;
		private long _seenLastStep = -1;
		private long _episodeAfterStep = -1;
		private bool _inEpisode;
		private double _lastContactTime = double.NaN;
		private double _lastUpdateTime = double.NaN;
		private ContactLatch _stepLatch = ContactLatch.None;

		public ContactMonitor(double holdSeconds = DefaultHoldSeconds)
		{
			HoldSeconds = Math.Max(0.0, holdSeconds);
		}

		public double HoldSeconds { get; }

		public ContactIndication Indication { get; private set; }

		/// <summary>Contact force at the latest step while Live, newtons; 0 otherwise.</summary>
		public double ForceNewtons { get; private set; }

		/// <summary>Largest force in the current or most recent episode, newtons.</summary>
		public double PeakNewtons { get; private set; }

		/// <summary>
		/// Index of the physics step whose end state a sample at <paramref name="time"/> holds; -1 for a
		/// non-finite time. Simulation time is <c>SimulationClock.TimeAtStep(stepCount)</c> and the latch numbers
		/// steps from 0, so it is round(100 t) - 1. The initial state at t = 0 counts as step 0.
		/// </summary>
		public static long StepEndingAt(double time) =>
			InstrumentFormat.IsFinite(time) ? Math.Max(0L, (long)Math.Round(time * SimulationClock.StepsPerSecond) - 1L) : -1L;

		/// <summary>Updates from the source's latch; call it once per refresh, however many steps have passed.</summary>
		/// <param name="latch">The source's contact latch for the current run.</param>
		/// <param name="time">Simulation time of the latest sample, seconds.</param>
		/// <param name="readingValid">False when the latest contact reading is missing (non-finite).</param>
		public void Update(ContactLatch latch, double time, bool readingValid)
		{
			if ((!double.IsNaN(_lastUpdateTime) && time < _lastUpdateTime) || latch.StepsWithContact < _seenSteps)
			{
				// A seek back or a restarted latch, not a contact: start over.
				Reset();
			}

			_lastUpdateTime = time;
			if (latch.StepsWithContact > _seenSteps && latch.LastContactStep >= 0)
			{
				var contactTime = SimulationClock.TimeAtStep(latch.LastContactStep + 1);
				if (!_inEpisode || !(contactTime - _lastContactTime < HoldSeconds))
				{
					_episodeAfterStep = _seenLastStep;
					PeakNewtons = 0.0;
				}

				_inEpisode = true;
				_lastContactTime = contactTime;
				PeakNewtons = MaxFinite(PeakNewtons, latch.LastContactForce);
				if (latch.PeakContactStep > _episodeAfterStep)
				{
					// The run's peak fell inside this episode, possibly on a step between two refreshes.
					PeakNewtons = MaxFinite(PeakNewtons, latch.PeakContactForce);
				}
			}

			_seenSteps = latch.StepsWithContact;
			_seenLastStep = latch.LastContactStep;
			if (_inEpisode && latch.LastContactStep >= StepEndingAt(time))
			{
				Indication = ContactIndication.Live;
				ForceNewtons = latch.LastContactForce;
				return;
			}

			ForceNewtons = 0.0;
			if (_inEpisode && time - _lastContactTime < HoldSeconds)
			{
				Indication = ContactIndication.Recent;
				return;
			}

			_inEpisode = false;
			Indication = readingValid ? ContactIndication.None : ContactIndication.Unknown;
		}

		/// <summary>
		/// Updates from one physics step's contact force, for callers that see every step. Per-frame callers
		/// must use the latch overload: a force sampled once per frame misses touches between frames. A
		/// non-finite force is an unknown reading; zero or negative is no contact.
		/// </summary>
		public void Update(double forceNewtons, double time)
		{
			if (!double.IsNaN(_lastUpdateTime) && time < _lastUpdateTime)
			{
				Reset();
			}

			if (forceNewtons > 0.0 && !double.IsInfinity(forceNewtons))
			{
				var step = StepEndingAt(time);
				var peak = forceNewtons > _stepLatch.PeakContactForce;
				_stepLatch = new ContactLatch(_stepLatch.StepsWithContact + 1, step, forceNewtons,
					peak ? forceNewtons : _stepLatch.PeakContactForce, peak ? step : _stepLatch.PeakContactStep);
			}

			Update(_stepLatch, time, InstrumentFormat.IsFinite(forceNewtons));
		}

		public void Reset()
		{
			Indication = ContactIndication.None;
			ForceNewtons = 0.0;
			PeakNewtons = 0.0;
			_seenSteps = 0;
			_seenLastStep = -1;
			_episodeAfterStep = -1;
			_inEpisode = false;
			_lastContactTime = double.NaN;
			_lastUpdateTime = double.NaN;
			_stepLatch = ContactLatch.None;
		}

		private static double MaxFinite(double current, double candidate) =>
			InstrumentFormat.IsFinite(candidate) && candidate > current ? candidate : current;
	}
}
