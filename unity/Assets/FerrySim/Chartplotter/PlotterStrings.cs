using System;
using System.Globalization;
using FerrySim.Core.Navigation;
using FerrySim.UI;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// Bounded strings at the plotter's display resolution, built once so the CPA list and the readouts allocate
	/// nothing while values change: CPA and range in nautical miles to a hundredth and a tenth, TCPA in minutes and
	/// seconds, and the view's span. Values outside the tables fall back to the instrument formatter.
	/// </summary>
	public static class PlotterStrings
	{
		public const string NoValue = InstrumentFormat.NoValue;
		public const string Past = "PAST";

		/// <summary>A TCPA of an hour or more: beyond what the list resolves.</summary>
		public const string OverAnHour = "> 60:00";
		private static readonly string[] Hundredths = Build(1000, i => (i / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
		private static readonly string[] Tenths = Build(1000, i => (i / 10.0).ToString("0.0", CultureInfo.InvariantCulture));
		private static readonly string[] MinutesSeconds = Build(3600, i => string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", i / 60, i % 60));

		private static string[] Build(int count, Func<int, string> make)
		{
			var result = new string[count];
			for (var i = 0; i < count; i++)
			{
				result[i] = make(i);
			}

			return result;
		}

		/// <summary>Nautical miles to a hundredth, for a CPA: "0.07"; "---" when not finite.</summary>
		public static string MilesHundredths(double metres)
		{
			if (!InstrumentFormat.IsFinite(metres))
			{
				return NoValue;
			}

			var index = (int)Math.Round(metres / ChartView.MetresPerNauticalMile * 100.0, MidpointRounding.AwayFromZero);
			return index >= 0 && index < Hundredths.Length ? Hundredths[index] : InstrumentFormat.Fixed(metres / ChartView.MetresPerNauticalMile, 2);
		}

		/// <summary>Nautical miles to a tenth, for a range: "1.3".</summary>
		public static string MilesTenths(double metres)
		{
			if (!InstrumentFormat.IsFinite(metres))
			{
				return NoValue;
			}

			var index = (int)Math.Round(metres / ChartView.MetresPerNauticalMile * 10.0, MidpointRounding.AwayFromZero);
			return index >= 0 && index < Tenths.Length ? Tenths[index] : InstrumentFormat.Fixed(metres / ChartView.MetresPerNauticalMile, 1);
		}

		/// <summary>A TCPA as m:ss up to 59:59, "> 60:00" beyond; "PAST" once negative; "---" when not finite.</summary>
		public static string MinutesAndSeconds(double seconds)
		{
			if (!InstrumentFormat.IsFinite(seconds))
			{
				return NoValue;
			}

			if (seconds < 0.0)
			{
				return Past;
			}

			// Compare before the cast: a nearly parallel target's huge TCPA would overflow int to a negative index.
			if (seconds >= MinutesSeconds.Length - 0.5)
			{
				return OverAnHour;
			}

			return MinutesSeconds[(int)Math.Round(seconds, MidpointRounding.AwayFromZero)];
		}

		/// <summary>A view span: "0.25 NM", "0.5 NM", "2 NM".</summary>
		public static string Span(double nauticalMiles) =>
			(nauticalMiles < 1.0 ? nauticalMiles.ToString("0.##", CultureInfo.InvariantCulture) : nauticalMiles.ToString("0.#", CultureInfo.InvariantCulture)) + " NM";
	}
}
