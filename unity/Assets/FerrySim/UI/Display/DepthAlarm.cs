using System;

namespace FerrySim.UI
{
	public enum AlarmState
	{
		Normal = 0,

		/// <summary>Raised and not yet acknowledged: the indicator flashes.</summary>
		Active = 1,

		/// <summary>Acknowledged while the condition persists: the indicator is steady.</summary>
		Acknowledged = 2,
	}

	/// <summary>
	/// The only thing on the panel that flashes. Unacknowledged alarms flash at 1 Hz (well under the
	/// 3 flashes per second photosensitivity limit) and start in the lit phase so they are visible the
	/// moment they are raised.
	/// </summary>
	public static class AlarmFlash
	{
		public const double PeriodSeconds = 1.0;
		public const double OnFraction = 0.6;

		public static bool IsOn(double secondsSinceRaised)
		{
			if (double.IsNaN(secondsSinceRaised) || double.IsInfinity(secondsSinceRaised) || secondsSinceRaised < 0.0)
			{
				return true;
			}

			return secondsSinceRaised % PeriodSeconds < PeriodSeconds * OnFraction;
		}
	}

	/// <summary>
	/// Shallow-water alarm on depth under keel. It raises when the depth drops below the threshold and
	/// clears only once the depth is back above the threshold plus a hysteresis band, so a reading that
	/// hovers at the threshold does not chatter. A lost reading (NaN) changes nothing: it neither raises
	/// nor clears. The panel resets it for every new run, so an acknowledgement never carries over. The
	/// debrief runs the same logic over a recording, with the recorded threshold history when the
	/// recording has one, so its events match what the panel showed.
	/// </summary>
	public sealed class DepthAlarm
	{
		/// <summary>Default threshold, 5 ft under the keel. A placeholder until crew say what they set (see the UI README).</summary>
		public const double DefaultThresholdMetres = 1.524;

		/// <summary>
		/// How the default is qualified wherever the threshold is shown in words (settings and debrief) until
		/// crew confirm the value and its unit; the panel marks it PLACEHOLDER.
		/// </summary>
		public const string PlaceholderNote = "placeholder default and unit, pending crew input";

		private double _threshold;

		public DepthAlarm(double thresholdMetres = DefaultThresholdMetres)
		{
			_threshold = DefaultThresholdMetres;
			ThresholdMetres = thresholdMetres;
		}

		/// <summary>Alarm when depth under keel is below this, metres. NaN is ignored; negative values clamp to 0.</summary>
		public double ThresholdMetres
		{
			get => _threshold;
			set
			{
				if (double.IsNaN(value) || double.IsInfinity(value))
				{
					return;
				}

				_threshold = Math.Max(0.0, value);
			}
		}

		/// <summary>How far above the threshold the depth must recover before the alarm clears: 10% of the threshold, at least 0.1 m.</summary>
		public double HysteresisMetres => Math.Max(0.1, 0.1 * _threshold);

		/// <summary>Whether <paramref name="thresholdMetres"/> is the unconfirmed default, within a micrometre so 5 ft reached by stepping counts.</summary>
		public static bool IsDefaultThreshold(double thresholdMetres) => Math.Abs(thresholdMetres - DefaultThresholdMetres) < 1e-6;

		public AlarmState State { get; private set; }

		/// <summary>Time the current alarm was raised, in the caller's clock; NaN when normal.</summary>
		public double RaisedAt { get; private set; } = double.NaN;

		public bool IsRaised => State != AlarmState.Normal;

		public void Update(double depthUnderKeelMetres, double time)
		{
			if (double.IsNaN(depthUnderKeelMetres) || double.IsInfinity(depthUnderKeelMetres))
			{
				return;
			}

			if (State == AlarmState.Normal)
			{
				if (depthUnderKeelMetres < _threshold)
				{
					State = AlarmState.Active;
					RaisedAt = time;
				}
			}
			else if (depthUnderKeelMetres >= _threshold + HysteresisMetres)
			{
				State = AlarmState.Normal;
				RaisedAt = double.NaN;
			}
		}

		public void Acknowledge()
		{
			if (State == AlarmState.Active)
			{
				State = AlarmState.Acknowledged;
			}
		}

		public void Reset()
		{
			State = AlarmState.Normal;
			RaisedAt = double.NaN;
		}

		/// <summary>Whether the alarm indicator is lit at <paramref name="time"/>: flashing while active, steady once acknowledged.</summary>
		public bool IsLit(double time) =>
			State == AlarmState.Acknowledged || (State == AlarmState.Active && AlarmFlash.IsOn(time - RaisedAt));
	}
}
