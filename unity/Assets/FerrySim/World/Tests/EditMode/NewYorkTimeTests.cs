using System;
using NUnit.Framework;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// America/New_York conversions under the US rule in force since 2007: daylight time from the second
	/// Sunday in March to the first Sunday in November, switching at 02:00 local. In 2026 that is
	/// 8 March and 1 November. Guards against an off-by-one Sunday, switching at UTC midnight, and
	/// treating a local time as UTC.
	/// </summary>
	public class NewYorkTimeTests
	{
		[TestCase(2026, 3, 8, 6, 59, -5, TestName = "Just before spring forward (01:59 EST)")]
		[TestCase(2026, 3, 8, 7, 0, -4, TestName = "Spring forward (03:00 EDT)")]
		[TestCase(2026, 11, 1, 5, 59, -4, TestName = "Just before fall back (01:59 EDT)")]
		[TestCase(2026, 11, 1, 6, 0, -5, TestName = "Fall back (01:00 EST)")]
		[TestCase(2026, 6, 15, 16, 0, -4, TestName = "Summer")]
		[TestCase(2026, 12, 15, 17, 0, -5, TestName = "Winter")]
		[TestCase(2027, 3, 14, 7, 0, -4, TestName = "2027 spring forward is the 14th")]
		public void UtcOffset_FollowsUsDaylightSavingRule(int year, int month, int day, int hour, int minute, int expectedOffsetHours)
		{
			var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

			Assert.That(NewYorkTime.UtcOffset(utc), Is.EqualTo(TimeSpan.FromHours(expectedOffsetHours)));
		}

		[Test]
		public void ToUtc_SummerNoon_IsSixteenHundred()
		{
			var utc = NewYorkTime.ToUtc(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Unspecified));

			Assert.That(utc, Is.EqualTo(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc)));
			Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc));
		}

		[Test]
		public void ToUtc_WinterNoon_IsSeventeenHundred()
		{
			var utc = NewYorkTime.ToUtc(new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Unspecified));

			Assert.That(utc, Is.EqualTo(new DateTime(2026, 12, 15, 17, 0, 0, DateTimeKind.Utc)));
		}

		[Test]
		public void ToUtc_InSpringForwardGap_ReadsAsStandardTime()
		{
			// 02:30 does not exist on 2026-03-08; it is read as 02:30 EST, the same instant as 03:30 EDT.
			var utc = NewYorkTime.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified));

			Assert.That(utc, Is.EqualTo(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc)));
		}

		[Test]
		public void ToUtc_InFallBackOverlap_TakesTheFirstOccurrence()
		{
			// 01:30 happens twice on 2026-11-01; the first one is daylight time.
			var utc = NewYorkTime.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified));

			Assert.That(utc, Is.EqualTo(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc)));
		}

		[Test]
		public void ToLocal_RoundTripsThroughToUtc()
		{
			var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			for (var hour = 0; hour < 366 * 24; hour += 7)
			{
				var utc = start.AddHours(hour);
				var local = NewYorkTime.ToLocal(utc);
				if (IsInFallBackOverlap(local))
				{
					continue;
				}

				Assert.That(NewYorkTime.ToUtc(local), Is.EqualTo(utc), utc.ToString("O"));
			}
		}

		[Test]
		public void ToLocal_RejectsLocalKind()
		{
			Assert.Throws<ArgumentException>(() => NewYorkTime.ToLocal(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local)));
		}

		[Test]
		public void ToUtc_RejectsUtcKind()
		{
			Assert.Throws<ArgumentException>(() => NewYorkTime.ToUtc(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
		}

		// The second 01:xx on the first Sunday in November maps back to the first one by design.
		private static bool IsInFallBackOverlap(DateTime local) =>
			local.Month == 11 && local.Day <= 7 && local.DayOfWeek == DayOfWeek.Sunday && local.Hour == 1;
	}
}
