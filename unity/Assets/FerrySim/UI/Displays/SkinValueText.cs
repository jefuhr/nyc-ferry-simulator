using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// The text of one readout, without garbage in steady state. Each value is rounded to its decimals (half away from
	/// zero, as the panel rounds), and each rounded value's string is made once and kept, up to a bound, so a needle
	/// or number that moves over its normal range stops allocating once it has shown each value. A missing reading
	/// shows <see cref="NoValue"/>, never a number.
	/// </summary>
	public sealed class SkinValueText
	{
		/// <summary>Dashes for a reading that is not available: the same as the panel's.</summary>
		public const string NoValue = InstrumentFormat.NoValue;

		public const int MaximumDecimals = 4;

		/// <summary>How many distinct strings one readout keeps before it stops caching new ones.</summary>
		public const int CacheLimit = 8192;

		private static readonly double[] Scales = { 1.0, 10.0, 100.0, 1000.0, 10000.0 };
		private static readonly string[] Formats = { "F0", "F1", "F2", "F3", "F4" };

		private readonly int _decimals;
		private readonly Dictionary<long, string> _cache = new Dictionary<long, string>();
		private readonly char[] _buffer = new char[48];
		private long _lastKey = long.MinValue;
		private string _last;

		public SkinValueText(int decimals)
		{
			_decimals = Math.Max(0, Math.Min(MaximumDecimals, decimals));
		}

		public int Decimals => _decimals;

		/// <summary>The text for a value already in its display unit.</summary>
		public string Format(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				return NoValue;
			}

			var rounded = Math.Round(value * Scales[_decimals], MidpointRounding.AwayFromZero);
			if (Math.Abs(rounded) > 1e15)
			{
				return NoValue;
			}

			var key = (long)rounded;
			if (key == _lastKey && _last != null)
			{
				return _last;
			}

			if (!_cache.TryGetValue(key, out var text))
			{
				var shown = key == 0 ? 0.0 : key / Scales[_decimals];
				shown.TryFormat(_buffer, out var length, Formats[_decimals], CultureInfo.InvariantCulture);
				text = new string(_buffer, 0, length);
				if (_cache.Count < CacheLimit)
				{
					_cache.Add(key, text);
				}
			}

			_lastKey = key;
			_last = text;
			return text;
		}
	}

	/// <summary>
	/// The gear as a word, from a shift-phase channel, the way the control model itself reports the gear
	/// (<c>ControlModel.Gear</c>): neutral only when the clutches are open in the neutral band, ahead or astern only when
	/// engaged, and shifting for every phase between. The channel carries no direction, so an engaged gear takes it
	/// from the lever as applied, or from the shaft when the lever reading is lost.
	/// </summary>
	public static class GearWords
	{
		public const string Neutral = "NEUTRAL";
		public const string Ahead = "AHEAD";
		public const string Astern = "ASTERN";
		public const string Shifting = "SHIFTING";

		public static bool IsShiftPhase(Channel channel) =>
			channel == Channel.EnginePortShiftPhase || channel == Channel.EngineStarboardShiftPhase;

		/// <summary>The lever and shaft channels of the engine whose shift phase is <paramref name="phase"/>.</summary>
		public static void Companions(Channel phase, out Channel lever, out Channel shaft)
		{
			var port = phase == Channel.EnginePortShiftPhase;
			lever = port ? Channel.LeverPort : Channel.LeverStarboard;
			shaft = port ? Channel.EnginePortShaftSpeed : Channel.EngineStarboardShaftSpeed;
		}

		public static string Word(double phase, double lever, double shaft)
		{
			if (double.IsNaN(phase) || phase != Math.Floor(phase) || phase < 0 || phase > (double)ShiftPhase.NeutralDwell)
			{
				return SkinValueText.NoValue;
			}

			switch ((ShiftPhase)(int)phase)
			{
				case ShiftPhase.Neutral:
					return Neutral;
				case ShiftPhase.Engaged:
					var direction = !double.IsNaN(lever) && lever != 0.0 ? Math.Sign(lever) : !double.IsNaN(shaft) && shaft != 0.0 ? Math.Sign(shaft) : 0;
					return direction > 0 ? Ahead : direction < 0 ? Astern : SkinValueText.NoValue;
				default:
					return Shifting;
			}
		}
	}

	/// <summary>How a lamp shows its alarm (contract C1's alarm sequence).</summary>
	public enum LampState
	{
		Off,

		/// <summary>Unacknowledged: 1 Hz.</summary>
		Flashing,

		/// <summary>Acknowledged while the condition persists.</summary>
		Steady,

		/// <summary>Ring-back, the condition gone before anyone acknowledged it: 0.5 Hz.</summary>
		SlowFlash,

		/// <summary>Lamp test held: lit whatever the alarm is doing.</summary>
		Test,
	}

	/// <summary>
	/// The lamp rhythm of contract C1, taken from <c>AlarmAnnunciator.LampLit</c> in core so every viewer flashes in step: unacknowledged flashes at 1 Hz
	/// and ring-back at 0.5 Hz, both lit for the first half of each period, acknowledged is steady, and a lamp test
	/// lights everything. Both flash rates are well below the 3 per second photosensitivity limit.
	/// </summary>
	public static class AlarmLamp
	{
		public static LampState State(AlarmPhase phase, bool lampTest)
		{
			if (lampTest)
			{
				return LampState.Test;
			}

			switch (phase)
			{
				case AlarmPhase.Unacknowledged: return LampState.Flashing;
				case AlarmPhase.Acknowledged: return LampState.Steady;
				case AlarmPhase.RingBack: return LampState.SlowFlash;
				default: return LampState.Off;
			}
		}

		public static bool IsLit(LampState state, double time)
		{
			switch (state)
			{
				case LampState.Test:
				case LampState.Steady:
					return true;
				case LampState.Flashing:
					return double.IsNaN(time) || AlarmAnnunciator.LampLit(AlarmPhase.Unacknowledged, false, time);
				case LampState.SlowFlash:
					return double.IsNaN(time) || AlarmAnnunciator.LampLit(AlarmPhase.RingBack, false, time);
				default:
					return false;
			}
		}

		/// <summary>The state in words, so a lamp never relies on colour or flashing alone.</summary>
		public static string Word(LampState state)
		{
			switch (state)
			{
				case LampState.Flashing: return "ALARM";
				case LampState.Steady: return "ACK";
				case LampState.SlowFlash: return "CLEARED";
				case LampState.Test: return "TEST";
				default: return "";
			}
		}
	}

	/// <summary>The alarm ids a skin may name, from the vessel's instrument data or a running readout.</summary>
	public static class DisplaySkinAlarms
	{
		public static IReadOnlyCollection<string> From(InstrumentParameters parameters)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			if (parameters?.Alarms != null)
			{
				foreach (var alarm in parameters.Alarms)
				{
					ids.Add(alarm.Id);
				}
			}

			return ids;
		}

		public static IReadOnlyCollection<string> From(IInstrumentReadout readout)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			if (readout != null)
			{
				for (var i = 0; i < readout.AlarmCount; i++)
				{
					ids.Add(readout.AlarmId(i));
				}
			}

			return ids;
		}
	}
}
