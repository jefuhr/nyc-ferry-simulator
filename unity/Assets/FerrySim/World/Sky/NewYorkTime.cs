using System;

namespace FerrySim.World
{
	/// <summary>
	/// America/New_York local time without the platform time-zone database, so it gives the same answer
	/// on every machine. Uses the US rule in force since 2007 (Energy Policy Act of 2005): Eastern Daylight
	/// Time (UTC-4) from the second Sunday in March, 02:00 local standard time, to the first Sunday in
	/// November, 02:00 local daylight time; Eastern Standard Time (UTC-5) otherwise. Earlier years get the
	/// same rule, which is wrong before 2007.
	/// </summary>
	public static class NewYorkTime
	{
		private static readonly TimeSpan Standard = TimeSpan.FromHours(-5);
		private static readonly TimeSpan Daylight = TimeSpan.FromHours(-4);

		/// <summary>Offset of New York local time from UTC at a UTC instant.</summary>
		public static TimeSpan UtcOffset(DateTime utc)
		{
			RequireKind(utc, DateTimeKind.Utc, nameof(utc));
			var year = utc.Year;
			return utc >= DaylightStartUtc(year) && utc < DaylightEndUtc(year) ? Daylight : Standard;
		}

		/// <summary>New York wall-clock time (DateTimeKind.Unspecified) for a UTC instant.</summary>
		public static DateTime ToLocal(DateTime utc)
		{
			var offset = UtcOffset(utc);
			return DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified);
		}

		/// <summary>
		/// UTC instant for a New York wall-clock time (DateTimeKind.Unspecified). A time in the spring-forward
		/// gap is read as standard time; a time in the fall-back overlap is the first occurrence (daylight).
		/// </summary>
		public static DateTime ToUtc(DateTime local)
		{
			RequireKind(local, DateTimeKind.Unspecified, nameof(local));
			var year = local.Year;
			var daylightStartLocal = DaylightStartUtc(year) + Standard;
			var daylightEndLocal = DaylightEndUtc(year) + Daylight;

			// Daylight from 03:00 on the start day (02:00-03:00 is the gap) to 02:00 on the end day; the
			// overlap hour 01:00-02:00 on the end day is read as daylight because it comes first.
			var isDaylight = local >= daylightStartLocal + TimeSpan.FromHours(1) && local < daylightEndLocal;
			return DateTime.SpecifyKind(local - (isDaylight ? Daylight : Standard), DateTimeKind.Utc);
		}

		/// <summary>Start of daylight time in UTC: second Sunday in March, 02:00 EST (07:00 UTC).</summary>
		public static DateTime DaylightStartUtc(int year) =>
			DateTime.SpecifyKind(NthSunday(year, 3, 2).AddHours(2) - Standard, DateTimeKind.Utc);

		/// <summary>End of daylight time in UTC: first Sunday in November, 02:00 EDT (06:00 UTC).</summary>
		public static DateTime DaylightEndUtc(int year) =>
			DateTime.SpecifyKind(NthSunday(year, 11, 1).AddHours(2) - Daylight, DateTimeKind.Utc);

		private static DateTime NthSunday(int year, int month, int n)
		{
			var first = new DateTime(year, month, 1);
			var daysToSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
			return first.AddDays(daysToSunday + 7 * (n - 1));
		}

		private static void RequireKind(DateTime value, DateTimeKind kind, string name)
		{
			if (value.Kind != kind)
			{
				throw new ArgumentException($"Expected DateTimeKind.{kind}, got {value.Kind}.", name);
			}
		}
	}
}
