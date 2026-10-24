using System;

namespace FerrySim.UI
{
	/// <summary>
	/// America/New_York local time from a UTC instant, using the US daylight-saving rules directly
	/// instead of the machine's time-zone database, so the clock reads the same on every platform and
	/// test machine. From 2007: second Sunday in March to the first Sunday in November. Before 2007
	/// (the 1987 rule): first Sunday in April to the last Sunday in October. Changes happen at 02:00
	/// local time.
	/// </summary>
	public static class NewYorkTime
	{
		private static readonly TimeSpan Standard = TimeSpan.FromHours(-5);
		private static readonly TimeSpan Daylight = TimeSpan.FromHours(-4);

		// Simulation offsets beyond this (about 28,000 years) cannot be a scenario clock, and TimeSpan overflows near it.
		private const double MaxOffsetSeconds = 9e11;

		// Room for the shift to local time at either end of the calendar DateTime can hold.
		private static readonly long EarliestTicks = DateTime.MinValue.Ticks + TimeSpan.TicksPerHour * 6;
		private static readonly long LatestTicks = DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour * 6;

		/// <summary>
		/// Local time <paramref name="seconds"/> after <paramref name="scenarioStartUtc"/>, or false when there is
		/// nothing true to show: a start that was never set (<c>default(DateTimeOffset)</c>), a non-finite time,
		/// or an instant outside the calendar. Never throws, so a bad start cannot stop the panel.
		/// </summary>
		public static bool TryToLocal(DateTimeOffset scenarioStartUtc, double seconds, out DateTime local, out bool daylight)
		{
			local = default;
			daylight = false;
			if (scenarioStartUtc == default || !InstrumentFormat.IsFinite(seconds) || Math.Abs(seconds) > MaxOffsetSeconds)
			{
				return false;
			}

			var start = scenarioStartUtc.UtcTicks;
			var offset = TimeSpan.FromSeconds(seconds).Ticks;
			if (offset > LatestTicks - start || offset < EarliestTicks - start)
			{
				return false;
			}

			local = ToLocal(new DateTimeOffset(start + offset, TimeSpan.Zero), out daylight);
			return true;
		}

		public static DateTime ToLocal(DateTimeOffset instant, out bool daylight)
		{
			var utc = instant.UtcDateTime;
			daylight = IsDaylightTime(utc);
			return DateTime.SpecifyKind(utc + (daylight ? Daylight : Standard), DateTimeKind.Unspecified);
		}

		public static string Abbreviation(bool daylight) => daylight ? "EDT" : "EST";

		public static bool IsDaylightTime(DateTime utc)
		{
			var year = utc.Year;
			DateTime startLocal;
			DateTime endLocal;
			if (year >= 2007)
			{
				startLocal = NthSunday(year, 3, 2).AddHours(2);
				endLocal = NthSunday(year, 11, 1).AddHours(2);
			}
			else
			{
				startLocal = NthSunday(year, 4, 1).AddHours(2);
				endLocal = LastSunday(year, 10).AddHours(2);
			}

			// The start is 02:00 standard time and the end is 02:00 daylight time.
			var startUtc = startLocal - Standard;
			var endUtc = endLocal - Daylight;
			return utc >= startUtc && utc < endUtc;
		}

		private static DateTime NthSunday(int year, int month, int n)
		{
			var first = new DateTime(year, month, 1);
			var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
			return first.AddDays(offset + 7 * (n - 1));
		}

		private static DateTime LastSunday(int year, int month)
		{
			var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
			return last.AddDays(-(int)last.DayOfWeek);
		}
	}
}
