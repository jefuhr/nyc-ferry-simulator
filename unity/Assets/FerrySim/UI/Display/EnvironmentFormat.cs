using System;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.UI
{
	public enum TideTrend
	{
		/// <summary>No tide in this run (calm or uniform), or no data.</summary>
		Unknown = 0,

		Rising = 1,

		Falling = 2,

		/// <summary>Changing by less than <see cref="EnvironmentFormat.SteadyTideMetresPerHour"/>: the stand at high or low water.</summary>
		Steady = 3,
	}

	/// <summary>
	/// Text for the water and the air at the vessel. Values arrive in SI from <see cref="IEnvironmentReadoutSource"/>
	/// (docs/conventions.md: the current and the wind as the velocity of the medium, north and east, toward). Each
	/// method converts, rounds to the resolution the readout claims and shows <see cref="InstrumentFormat.NoValue"/>
	/// for anything missing or meaningless. Everything in range comes from the shared caches in
	/// <see cref="ReadoutStrings"/>, so formatting a moving readout allocates nothing.
	/// </summary>
	public static class EnvironmentFormat
	{
		/// <summary>Shown for the wind on a day whose pack has no wind station, instead of the still air the simulation uses.</summary>
		public const string NoWindData = "NO WIND DATA";

		/// <summary>The vertical datum of <see cref="EnvironmentReadout.WaterLevelNavd88"/>, shown beside the tide.</summary>
		public const string Datum = "NAVD88";

		/// <summary>
		/// The tide reads STEADY while the water changes by less than this (3 cm, about 0.1 ft, an hour): roughly ten
		/// minutes either side of high and low water on a New York tide. A display choice, pending crew input.
		/// </summary>
		public const double SteadyTideMetresPerHour = 0.03;

		public const string UtcZone = "UTC";

		private static readonly string[] Weekdays = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
		private static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

		// Simulation offsets beyond this (about 28,000 years) cannot be a scenario clock, and TimeSpan overflows near it.
		private const double MaxOffsetSeconds = 9e11;

		/// <summary>
		/// The set: where the current flows toward, three digits true ("047"). <see cref="InstrumentFormat.NoValue"/>
		/// for slack water (a drift that reads 0.0 kn has no direction worth showing) and non-finite components.
		/// </summary>
		public static string Set(HorizontalVector current) =>
			RoundedKnots(current, 1) > 0.0 ? InstrumentFormat.Compass(current.Direction) : InstrumentFormat.NoValue;

		/// <summary>The drift: the current's speed in knots, one decimal ("1.8").</summary>
		public static string Drift(HorizontalVector current) =>
			IsFinite(current) ? InstrumentFormat.Speed(current.Magnitude) : InstrumentFormat.NoValue;

		/// <summary>
		/// Where the wind comes from, three digits true ("270"): the meteorological convention, the opposite of the
		/// vector's direction. <see cref="NoWindData"/> on a day without wind data; <see cref="InstrumentFormat.NoValue"/>
		/// for a calm (under 0.5 kn, which reads 0 kn) and non-finite components.
		/// </summary>
		public static string WindFrom(HorizontalVector wind, bool windMissing)
		{
			if (windMissing)
			{
				return NoWindData;
			}

			return RoundedKnots(wind, 0) > 0.0 ? InstrumentFormat.Compass(wind.Direction + Math.PI) : InstrumentFormat.NoValue;
		}

		/// <summary>The wind's mean speed in whole knots ("12"): the data does not support tenths. "---" on a day without wind data.</summary>
		public static string WindSpeed(HorizontalVector wind, bool windMissing) =>
			!windMissing && IsFinite(wind) ? InstrumentFormat.Fixed(DisplayUnits.Knots(wind.Magnitude), 0) : InstrumentFormat.NoValue;

		/// <summary>The water level above NAVD88 in the distance unit, one decimal ("1.2", "-0.4"); "---" when the run has no tide.</summary>
		public static string TideLevel(double metresAboveNavd88, DistanceUnit unit) => InstrumentFormat.Distance(metresAboveNavd88, unit);

		/// <summary>Rising or falling from the rate (m/s), STEADY inside <see cref="SteadyTideMetresPerHour"/>, Unknown without a finite rate.</summary>
		public static TideTrend Trend(double metresPerSecond)
		{
			if (!InstrumentFormat.IsFinite(metresPerSecond))
			{
				return TideTrend.Unknown;
			}

			var metresPerHour = metresPerSecond * 3600.0;
			return metresPerHour >= SteadyTideMetresPerHour ? TideTrend.Rising :
				metresPerHour <= -SteadyTideMetresPerHour ? TideTrend.Falling :
				TideTrend.Steady;
		}

		public static string TrendText(TideTrend trend) =>
			trend == TideTrend.Rising ? "RISING" :
			trend == TideTrend.Falling ? "FALLING" :
			trend == TideTrend.Steady ? "STEADY" :
			string.Empty;

		/// <summary>
		/// The clock's zone label: EDT or EST from a pack's offset (America/New_York on that day), UTC for calm and
		/// uniform runs, which carry no local offset. Any other offset is spelled out ("UTC-04:30").
		/// </summary>
		public static string Zone(EnvironmentKind kind, TimeSpan localUtcOffset)
		{
			if (kind != EnvironmentKind.Pack || localUtcOffset == TimeSpan.Zero)
			{
				return UtcZone;
			}

			if (localUtcOffset == TimeSpan.FromHours(-4))
			{
				return "EDT";
			}

			if (localUtcOffset == TimeSpan.FromHours(-5))
			{
				return "EST";
			}

			var magnitude = localUtcOffset.Duration();
			return string.Format(CultureInfo.InvariantCulture, "UTC{0}{1:00}:{2:00}", localUtcOffset < TimeSpan.Zero ? "-" : "+",
				(int)magnitude.TotalHours, magnitude.Minutes);
		}

		/// <summary>"MON 14 JUL 2025": the weekday matters to a ferry (weekday or weekend traffic). Built only when the date changes.</summary>
		public static string Date(DateTime date) =>
			Weekdays[(int)date.DayOfWeek] + " " + date.Day.ToString(CultureInfo.InvariantCulture) + " " + Months[date.Month - 1] + " " +
			date.Year.ToString(CultureInfo.InvariantCulture);

		/// <summary>
		/// The time the clock shows <paramref name="seconds"/> into a run: local time from the pack's offset for a pack
		/// run, UTC for calm and uniform runs. False, never an exception, when there is nothing true to show: an unset
		/// start (<c>default</c>), a non-finite time or an instant outside the calendar.
		/// </summary>
		public static bool TryClock(EnvironmentKind kind, TimeSpan localUtcOffset, DateTimeOffset scenarioStartUtc, double seconds, out DateTime clock)
		{
			clock = default;
			var offset = kind == EnvironmentKind.Pack ? localUtcOffset : TimeSpan.Zero;
			if (!TryInstant(scenarioStartUtc, seconds, offset, out var ticks))
			{
				return false;
			}

			clock = new DateTime(ticks, DateTimeKind.Unspecified);
			return true;
		}

		/// <summary>
		/// Ticks of <paramref name="start"/> plus <paramref name="seconds"/> plus <paramref name="shift"/>, or false when
		/// the start is unset, the time is not finite or the result leaves the calendar (summed in decimal, which cannot overflow).
		/// </summary>
		private static bool TryInstant(DateTimeOffset start, double seconds, TimeSpan shift, out long ticks)
		{
			ticks = 0;
			if (start == default || !InstrumentFormat.IsFinite(seconds) || Math.Abs(seconds) > MaxOffsetSeconds)
			{
				return false;
			}

			var instant = (decimal)start.UtcTicks + TimeSpan.FromSeconds(seconds).Ticks + shift.Ticks;
			if (instant < DateTime.MinValue.Ticks || instant > DateTime.MaxValue.Ticks)
			{
				return false;
			}

			ticks = (long)instant;
			return true;
		}

		private static bool IsFinite(HorizontalVector vector) => InstrumentFormat.IsFinite(vector.North) && InstrumentFormat.IsFinite(vector.East);

		/// <summary>The speed in knots rounded as its readout rounds it; NaN for non-finite components.</summary>
		private static double RoundedKnots(HorizontalVector vector, int decimals) =>
			IsFinite(vector) ? Math.Round(DisplayUnits.Knots(vector.Magnitude), decimals, MidpointRounding.AwayFromZero) : double.NaN;
	}
}
