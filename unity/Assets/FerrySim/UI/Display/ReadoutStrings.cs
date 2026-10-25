using System;
using System.Globalization;

namespace FerrySim.UI
{
	/// <summary>
	/// Shared, bounded strings at instrument resolution. Built once, using a reusable number buffer;
	/// never grows with the duration of a run. Values outside these normal display ranges retain exact
	/// formatting. The owning readout caches those fallback strings until its displayed value changes.
	/// </summary>
	public static class ReadoutStrings
	{
		public const int ElapsedSeconds = 4 * 3600;
		private const int RudderLimit = 35;
		private const int RudderCount = RudderLimit * 2 + 2; // Includes the missing reading.
		private static readonly char[] NumberBuffer = new char[64];
		private static readonly string[] Whole = Numbers(-1000, 4000, 1, "F0");
		private static readonly string[] Tenths = Numbers(-1000, 1000, 10, "F1");
		private static readonly string[] Headings = Numbers(0, 359, 1, "000");
		private static readonly string[] Levers = Affix(Numbers(0, 100, 1, "F0"), "", "%");
		private static readonly string[] Rudders = BuildRudders();
		private static readonly string[] RudderPairs = BuildRudderPairs();
		private static readonly string[] Forces = BuildForces(false);
		private static readonly string[] Peaks = BuildForces(true);
		private static readonly string[] ElapsedTimes = BuildElapsed();

		/// <summary>Conservative retained size including 64-bit array slots and aligned string/array headers.</summary>
		public static long RetainedBytesUpperBound { get; private set; }

		static ReadoutStrings()
		{
			// Count shared references repeatedly as well: the result remains an upper bound.
			foreach (var array in new[] { Whole, Tenths, Headings, Levers, Rudders, RudderPairs, Forces, Peaks, ElapsedTimes })
			{
				RetainedBytesUpperBound += 32 + array.Length * 8L;
				foreach (var text in array) RetainedBytesUpperBound += (32 + text.Length * 2L + 7) / 8 * 8;
			}
			RetainedBytesUpperBound += 32 + NumberBuffer.Length * 2;
		}

		public static string Fixed(double rounded, int decimals)
		{
			if (decimals == 0 && rounded >= -1000 && rounded <= 4000) return Whole[(int)rounded + 1000];
			if (decimals == 1 && rounded >= -100 && rounded <= 100) return Tenths[(int)Math.Round(rounded * 10) + 1000];
			return null;
		}

		public static string Compass(int degrees) => Headings[degrees];
		public static string Lever(double percent) => percent >= 0 && percent <= 100 ? Levers[(int)percent] : null;
		public static string Rudder(double degrees) => degrees >= -RudderLimit && degrees <= RudderLimit ? Rudders[(int)degrees + RudderLimit] : null;
		public static string RudderPair(double port, double starboard)
		{
			var p = RudderIndex(port);
			var s = RudderIndex(starboard);
			return p >= 0 && s >= 0 ? RudderPairs[p * RudderCount + s] : null;
		}

		public static string Contact(double kilonewtons, bool fractional, bool recent)
		{
			var index = fractional ? (int)Math.Round(kilonewtons * 10) : 100 + (int)kilonewtons;
			return kilonewtons >= 0 && (fractional ? kilonewtons <= 10 : kilonewtons <= 1000)
				? (recent ? Peaks : Forces)[index] : null;
		}

		public static string Elapsed(long seconds) => seconds >= 0 && seconds <= ElapsedSeconds ? ElapsedTimes[(int)seconds] : null;

		private static int RudderIndex(double radians)
		{
			if (!InstrumentFormat.IsFinite(radians)) return RudderCount - 1;
			var degrees = Math.Round(DisplayUnits.Degrees(radians), MidpointRounding.AwayFromZero);
			return degrees >= -RudderLimit && degrees <= RudderLimit ? (int)degrees + RudderLimit : -1;
		}

		private static string[] Numbers(int first, int last, double divisor, string format)
		{
			var result = new string[last - first + 1];
			for (var i = first; i <= last; i++)
			{
				(i / divisor).TryFormat(NumberBuffer.AsSpan(), out var length, format, CultureInfo.InvariantCulture);
				result[i - first] = new string(NumberBuffer, 0, length);
			}
			return result;
		}

		private static string[] Affix(string[] numbers, string prefix, string suffix)
		{
			var result = new string[numbers.Length];
			for (var i = 0; i < result.Length; i++) result[i] = prefix + numbers[i] + suffix;
			return result;
		}

		private static string[] BuildRudders()
		{
			var result = new string[RudderCount];
			for (var i = -RudderLimit; i <= RudderLimit; i++)
				result[i + RudderLimit] = i == 0 ? "0°" : (i > 0 ? "S " : "P ") + Math.Abs(i).ToString(CultureInfo.InvariantCulture) + "°";
			result[RudderCount - 1] = InstrumentFormat.NoValue;
			return result;
		}

		private static string[] BuildRudderPairs()
		{
			var result = new string[RudderCount * RudderCount];
			for (var p = 0; p < RudderCount; p++)
				for (var s = 0; s < RudderCount; s++)
					result[p * RudderCount + s] = p == s ? Rudders[p] : Rudders[p] + " / " + Rudders[s];
			return result;
		}

		private static string[] BuildForces(bool recent)
		{
			var result = new string[1101];
			for (var i = 0; i <= 100; i++) result[i] = (recent ? "PEAK " : "") + (i / 10.0).ToString("F1", CultureInfo.InvariantCulture) + " kN";
			for (var i = 1; i <= 1000; i++) result[100 + i] = (recent ? "PEAK " : "") + i.ToString(CultureInfo.InvariantCulture) + " kN";
			return result;
		}

		private static string[] BuildElapsed()
		{
			var result = new string[ElapsedSeconds + 1];
			for (var i = 0; i < result.Length; i++)
				result[i] = i >= 3600
					? string.Format(CultureInfo.InvariantCulture, "T+{0}:{1:00}:{2:00}", i / 3600, i / 60 % 60, i % 60)
					: string.Format(CultureInfo.InvariantCulture, "T+{0}:{1:00}", i / 60, i % 60);
			return result;
		}
	}

	/// <summary>One readout's last displayed value, including values outside the shared cache's bounds.</summary>
	public sealed class ReadoutText
	{
		private double _key = double.NaN;
		private int _kind = -1;
		private string _text;

		public string Fixed(double value, int decimals)
		{
			var key = InstrumentFormat.IsFinite(value) ? Math.Round(value, decimals, MidpointRounding.AwayFromZero) : double.NaN;
			if (_text != null && key.Equals(_key) && _kind == decimals) return _text;
			_key = key;
			_kind = decimals;
			return _text = InstrumentFormat.Fixed(value, decimals);
		}

		public string Rpm(double radians) => Fixed(Math.Abs(DisplayUnits.Rpm(radians)), 0);
		public string Speed(double metresPerSecond) => Fixed(DisplayUnits.Knots(metresPerSecond), 1);
		public string Distance(double metres, DistanceUnit unit)
		{
			var value = DisplayUnits.Distance(metres, unit);
			return Fixed(value, Math.Abs(value) < 99.95 ? 1 : 0);
		}

		public string Rudder(double radians)
		{
			var key = InstrumentFormat.IsFinite(radians) ? Math.Round(DisplayUnits.Degrees(radians), MidpointRounding.AwayFromZero) : double.NaN;
			if (_text != null && key.Equals(_key)) return _text;
			_key = key;
			return _text = InstrumentFormat.Rudder(radians);
		}

		public string Lever(double lever)
		{
			var key = InstrumentFormat.IsFinite(lever) ? Math.Round(Math.Abs(lever) * 100, MidpointRounding.AwayFromZero) : double.NaN;
			if (_text != null && key.Equals(_key)) return _text;
			_key = key;
			return _text = InstrumentFormat.LeverPercent(lever);
		}

		public string Elapsed(double seconds)
		{
			var key = InstrumentFormat.IsFinite(seconds) ? Math.Floor(Math.Abs(seconds)) * (seconds < 0 ? -1 : 1) : double.NaN;
			// Preserve T-0:00 separately from T+0:00.
			var sign = seconds < 0 ? -1 : 1;
			if (_text != null && key.Equals(_key) && sign == _kind) return _text;
			_key = key;
			_kind = sign;
			return _text = InstrumentFormat.Elapsed(seconds);
		}
	}
}
