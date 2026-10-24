using System;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Local New York time for the clock. Guards against: showing UTC or the machine's own time zone,
	/// daylight-saving switches on the wrong day or hour, and the pre-2007 US rules being ignored.
	/// </summary>
	public class NewYorkTimeTests
	{
		private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
			new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);

		[Test]
		public void Summer_IsEdtUtcMinusFour()
		{
			var local = NewYorkTime.ToLocal(Utc(2026, 6, 15, 13, 0), out var daylight);

			Assert.That(local, Is.EqualTo(new DateTime(2026, 6, 15, 9, 0, 0)));
			Assert.That(daylight, Is.True);
			Assert.That(NewYorkTime.Abbreviation(daylight), Is.EqualTo("EDT"));
		}

		[Test]
		public void Winter_IsEstUtcMinusFive()
		{
			var local = NewYorkTime.ToLocal(Utc(2026, 1, 10, 3, 30), out var daylight);

			Assert.That(local, Is.EqualTo(new DateTime(2026, 1, 9, 22, 30, 0)), "crosses midnight into the previous day");
			Assert.That(daylight, Is.False);
			Assert.That(NewYorkTime.Abbreviation(daylight), Is.EqualTo("EST"));
		}

		[Test]
		public void Spring2026_SwitchesAtTwoAmOnTheSecondSundayOfMarch()
		{
			// 2026-03-08 02:00 EST = 07:00 UTC; local clocks jump to 03:00 EDT.
			Assert.That(NewYorkTime.ToLocal(Utc(2026, 3, 8, 6, 59, 59), out var before), Is.EqualTo(new DateTime(2026, 3, 8, 1, 59, 59)));
			Assert.That(before, Is.False);
			Assert.That(NewYorkTime.ToLocal(Utc(2026, 3, 8, 7, 0, 0), out var after), Is.EqualTo(new DateTime(2026, 3, 8, 3, 0, 0)));
			Assert.That(after, Is.True);
		}

		[Test]
		public void Autumn2026_SwitchesAtTwoAmOnTheFirstSundayOfNovember()
		{
			// 2026-11-01 02:00 EDT = 06:00 UTC; local clocks fall back to 01:00 EST.
			Assert.That(NewYorkTime.ToLocal(Utc(2026, 11, 1, 5, 59, 59), out var before), Is.EqualTo(new DateTime(2026, 11, 1, 1, 59, 59)));
			Assert.That(before, Is.True);
			Assert.That(NewYorkTime.ToLocal(Utc(2026, 11, 1, 6, 0, 0), out var after), Is.EqualTo(new DateTime(2026, 11, 1, 1, 0, 0)));
			Assert.That(after, Is.False);
		}

		[Test]
		public void Before2007_UsesTheAprilToOctoberRule()
		{
			// 2005: DST from the first Sunday in April (Apr 3) to the last Sunday in October (Oct 30).
			NewYorkTime.ToLocal(Utc(2005, 3, 20, 12), out var march);
			NewYorkTime.ToLocal(Utc(2005, 4, 3, 7), out var aprilStart);
			NewYorkTime.ToLocal(Utc(2005, 10, 30, 5, 59), out var octoberLast);
			NewYorkTime.ToLocal(Utc(2005, 10, 30, 6), out var octoberEnd);

			Assert.That(march, Is.False, "mid-March 2005 was still standard time");
			Assert.That(aprilStart, Is.True);
			Assert.That(octoberLast, Is.True);
			Assert.That(octoberEnd, Is.False);
		}

		[Test]
		public void NonUtcOffsetsAreHonoured()
		{
			var instant = new DateTimeOffset(2026, 6, 15, 15, 0, 0, TimeSpan.FromHours(2));

			Assert.That(NewYorkTime.ToLocal(instant, out _), Is.EqualTo(new DateTime(2026, 6, 15, 9, 0, 0)));
		}
	}
}
