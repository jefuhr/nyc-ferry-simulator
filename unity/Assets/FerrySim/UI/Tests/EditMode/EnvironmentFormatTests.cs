using System;
using System.Globalization;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Text for the water and the air. Guards against: a set read as where the current comes from (it is where it
	/// flows toward) and a wind read as where it blows toward (it is where it comes from); heading wrap mistakes
	/// (359.6° printing "360"); a direction for slack water or calm air; wrong knot factors and "-0.0"; the
	/// distance-unit setting not reaching the tide; a trend from a rate too small to mean anything or from NaN;
	/// NaN and infinities leaking into text; a missing wind day reading as still air; culture-dependent decimal
	/// commas and month names; and a clock that ignores the pack's UTC offset or does not say it is UTC.
	/// </summary>
	public class EnvironmentFormatTests
	{
		private static HorizontalVector Toward(double knots, double degrees) =>
			HorizontalVector.FromSpeedToward(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		private static HorizontalVector From(double knots, double degrees) =>
			HorizontalVector.FromSpeedFrom(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		[Test]
		public void Set_IsTheDirectionTheCurrentFlowsToward()
		{
			Assert.That(EnvironmentFormat.Set(new HorizontalVector(1.0, 1.0)), Is.EqualTo("045"), "flowing north-east");
			Assert.That(EnvironmentFormat.Set(new HorizontalVector(-1.0, 0.0)), Is.EqualTo("180"), "flowing south");
			Assert.That(EnvironmentFormat.Set(new HorizontalVector(0.0, -2.0)), Is.EqualTo("270"), "flowing west");
		}

		[TestCase(47.4, "047")]
		[TestCase(47.6, "048")]
		[TestCase(359.4, "359")]
		[TestCase(359.6, "000")]
		[TestCase(360.0, "000")]
		[TestCase(-0.4, "000")]
		[TestCase(-10.0, "350")]
		[TestCase(725.0, "005")]
		public void Set_WrapsAndPadsToThreeDigits(double degrees, string expected)
		{
			Assert.That(EnvironmentFormat.Set(Toward(2.0, degrees)), Is.EqualTo(expected));
		}

		[Test]
		public void Set_IsBlankForSlackWater_AndDriftStillReadsZero()
		{
			Assert.That(EnvironmentFormat.Set(Toward(0.04, 90.0)), Is.EqualTo(InstrumentFormat.NoValue), "0.0 kn has no direction");
			Assert.That(EnvironmentFormat.Drift(Toward(0.04, 90.0)), Is.EqualTo("0.0"));
			Assert.That(EnvironmentFormat.Set(Toward(0.06, 90.0)), Is.EqualTo("090"), "0.1 kn does");
			Assert.That(EnvironmentFormat.Drift(Toward(0.06, 90.0)), Is.EqualTo("0.1"));
			Assert.That(EnvironmentFormat.Set(HorizontalVector.Zero), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.Drift(HorizontalVector.Zero), Is.EqualTo("0.0"));
		}

		[Test]
		public void Drift_IsKnotsWithOneDecimal()
		{
			Assert.That(EnvironmentFormat.Drift(new HorizontalVector(1852.0 / 3600.0, 0.0)), Is.EqualTo("1.0"));
			Assert.That(EnvironmentFormat.Drift(new HorizontalVector(0.0, 2.3)), Is.EqualTo("4.5"), "2.3 m/s is 4.47 kn");
			Assert.That(EnvironmentFormat.Drift(Toward(1.26, 200.0)), Is.EqualTo("1.3"));
			Assert.That(EnvironmentFormat.Drift(Toward(1.24, 200.0)), Is.EqualTo("1.2"));
			Assert.That(EnvironmentFormat.Drift(Toward(150.0, 10.0)), Is.EqualTo("150.0"), "outside the cache, still exact");
		}

		[Test]
		public void SetAndDrift_ShowNoValue_ForNonFiniteComponents_AndNeverNegativeZero()
		{
			foreach (var current in new[]
			{
				new HorizontalVector(double.NaN, 0.0), new HorizontalVector(0.0, double.NaN),
				new HorizontalVector(double.PositiveInfinity, 0.0), new HorizontalVector(0.0, double.NegativeInfinity),
			})
			{
				Assert.That(EnvironmentFormat.Set(current), Is.EqualTo(InstrumentFormat.NoValue), current.ToString());
				Assert.That(EnvironmentFormat.Drift(current), Is.EqualTo(InstrumentFormat.NoValue), current.ToString());
			}

			Assert.That(EnvironmentFormat.Drift(new HorizontalVector(-0.0, -0.0)), Is.EqualTo("0.0"));
			Assert.That(EnvironmentFormat.Drift(new HorizontalVector(-0.001, 0.0)), Is.EqualTo("0.0"));
		}

		[Test]
		public void Wind_IsTheDirectionItComesFrom_InWholeKnots()
		{
			Assert.That(EnvironmentFormat.WindFrom(new HorizontalVector(0.0, 5.0), false), Is.EqualTo("270"), "blowing toward the east comes from the west");
			Assert.That(EnvironmentFormat.WindSpeed(new HorizontalVector(0.0, 5.0), false), Is.EqualTo("10"), "5 m/s is 9.7 kn");
			Assert.That(EnvironmentFormat.WindFrom(From(12.0, 250.0), false), Is.EqualTo("250"));
			Assert.That(EnvironmentFormat.WindSpeed(From(12.0, 250.0), false), Is.EqualTo("12"));
			Assert.That(EnvironmentFormat.WindFrom(From(12.0, 359.6), false), Is.EqualTo("000"));
			Assert.That(EnvironmentFormat.WindFrom(From(12.0, 0.4), false), Is.EqualTo("000"));
			Assert.That(EnvironmentFormat.WindFrom(From(12.0, -10.0), false), Is.EqualTo("350"));
			Assert.That(EnvironmentFormat.WindSpeed(From(12.4, 90.0), false), Is.EqualTo("12"));
			Assert.That(EnvironmentFormat.WindSpeed(From(12.6, 90.0), false), Is.EqualTo("13"));
			Assert.That(EnvironmentFormat.WindSpeed(From(250.0, 90.0), false), Is.EqualTo("250"));
		}

		[Test]
		public void Wind_HasNoDirectionWhenCalm()
		{
			Assert.That(EnvironmentFormat.WindFrom(From(0.4, 90.0), false), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.WindSpeed(From(0.4, 90.0), false), Is.EqualTo("0"));
			Assert.That(EnvironmentFormat.WindFrom(From(0.6, 90.0), false), Is.EqualTo("090"));
			Assert.That(EnvironmentFormat.WindSpeed(From(0.6, 90.0), false), Is.EqualTo("1"));
			Assert.That(EnvironmentFormat.WindSpeed(new HorizontalVector(-0.0, -0.0), false), Is.EqualTo("0"));
		}

		[Test]
		public void Wind_SaysSoWhenTheDayHasNoWindData_InsteadOfShowingStillAir()
		{
			Assert.That(EnvironmentFormat.WindFrom(HorizontalVector.Zero, true), Is.EqualTo(EnvironmentFormat.NoWindData));
			Assert.That(EnvironmentFormat.WindSpeed(HorizontalVector.Zero, true), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.WindFrom(From(10.0, 90.0), true), Is.EqualTo(EnvironmentFormat.NoWindData), "a missing day never shows numbers");
			Assert.That(EnvironmentFormat.NoWindData, Is.EqualTo("NO WIND DATA"));
		}

		[Test]
		public void Wind_ShowsNoValue_ForNonFiniteComponents()
		{
			Assert.That(EnvironmentFormat.WindFrom(new HorizontalVector(double.NaN, 1.0), false), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.WindSpeed(new HorizontalVector(double.NaN, 1.0), false), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.WindSpeed(new HorizontalVector(double.PositiveInfinity, 1.0), false), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Tide_FollowsTheDistanceUnit_WithOneDecimal()
		{
			Assert.That(EnvironmentFormat.TideLevel(0.3048, DistanceUnit.Feet), Is.EqualTo("1.0"));
			Assert.That(EnvironmentFormat.TideLevel(0.3048, DistanceUnit.Metres), Is.EqualTo("0.3"));
			Assert.That(EnvironmentFormat.TideLevel(-0.4, DistanceUnit.Feet), Is.EqualTo("-1.3"), "below NAVD88 reads negative");
			Assert.That(EnvironmentFormat.TideLevel(-0.4, DistanceUnit.Metres), Is.EqualTo("-0.4"));
			Assert.That(EnvironmentFormat.TideLevel(-0.01, DistanceUnit.Feet), Is.EqualTo("0.0"), "no '-0.0'");
			Assert.That(EnvironmentFormat.TideLevel(-0.0, DistanceUnit.Metres), Is.EqualTo("0.0"));
			Assert.That(EnvironmentFormat.TideLevel(double.NaN, DistanceUnit.Feet), Is.EqualTo(InstrumentFormat.NoValue), "calm and uniform runs have no tide");
			Assert.That(EnvironmentFormat.TideLevel(double.PositiveInfinity, DistanceUnit.Feet), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(EnvironmentFormat.TideLevel(1000.0, DistanceUnit.Metres), Is.EqualTo("1000"), "out of range stays exact");
			Assert.That(EnvironmentFormat.Datum, Is.EqualTo("NAVD88"));
		}

		[Test]
		public void TideTrend_FollowsTheSignOfTheRate_WithASteadyBand()
		{
			const double perHour = 1.0 / 3600.0;
			Assert.That(EnvironmentFormat.Trend(0.3 * perHour), Is.EqualTo(TideTrend.Rising));
			Assert.That(EnvironmentFormat.Trend(-0.3 * perHour), Is.EqualTo(TideTrend.Falling));
			Assert.That(EnvironmentFormat.Trend(0.031 * perHour), Is.EqualTo(TideTrend.Rising));
			Assert.That(EnvironmentFormat.Trend(-0.031 * perHour), Is.EqualTo(TideTrend.Falling));
			Assert.That(EnvironmentFormat.Trend(0.029 * perHour), Is.EqualTo(TideTrend.Steady), "under 3 cm an hour is a stand");
			Assert.That(EnvironmentFormat.Trend(-0.029 * perHour), Is.EqualTo(TideTrend.Steady));
			Assert.That(EnvironmentFormat.Trend(0.0), Is.EqualTo(TideTrend.Steady));
			Assert.That(EnvironmentFormat.Trend(-0.0), Is.EqualTo(TideTrend.Steady));
			Assert.That(EnvironmentFormat.Trend(double.NaN), Is.EqualTo(TideTrend.Unknown), "no tide, no trend");
			Assert.That(EnvironmentFormat.Trend(double.PositiveInfinity), Is.EqualTo(TideTrend.Unknown));
			Assert.That(EnvironmentFormat.SteadyTideMetresPerHour, Is.EqualTo(0.03));
		}

		[Test]
		public void TideTrendText_IsAWordForEveryTrend()
		{
			Assert.That(EnvironmentFormat.TrendText(TideTrend.Rising), Is.EqualTo("RISING"));
			Assert.That(EnvironmentFormat.TrendText(TideTrend.Falling), Is.EqualTo("FALLING"));
			Assert.That(EnvironmentFormat.TrendText(TideTrend.Steady), Is.EqualTo("STEADY"));
			Assert.That(EnvironmentFormat.TrendText(TideTrend.Unknown), Is.EqualTo(string.Empty));
		}

		[Test]
		public void Zone_FollowsThePackOffset_AndSaysUtcOtherwise()
		{
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, TimeSpan.FromHours(-4)), Is.EqualTo("EDT"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, TimeSpan.FromHours(-5)), Is.EqualTo("EST"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Calm, TimeSpan.Zero), Is.EqualTo("UTC"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Uniform, TimeSpan.Zero), Is.EqualTo("UTC"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Calm, TimeSpan.FromHours(-4)), Is.EqualTo("UTC"), "only packs carry a local offset");
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, TimeSpan.Zero), Is.EqualTo("UTC"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, new TimeSpan(-4, -30, 0)), Is.EqualTo("UTC-04:30"));
			Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, new TimeSpan(5, 30, 0)), Is.EqualTo("UTC+05:30"));
		}

		[TestCase(2025, 7, 14, "MON 14 JUL 2025")]
		[TestCase(2025, 1, 5, "SUN 5 JAN 2025")]
		[TestCase(2024, 2, 29, "THU 29 FEB 2024")]
		[TestCase(2024, 10, 13, "SUN 13 OCT 2024")]
		public void Date_ReadsDayMonthYearWithTheWeekday(int year, int month, int day, string expected)
		{
			Assert.That(EnvironmentFormat.Date(new DateTime(year, month, day, 23, 59, 59)), Is.EqualTo(expected));
		}

		[Test]
		public void Clock_ForAPack_IsLocalTimeFromTheOffset()
		{
			var start = new DateTimeOffset(2025, 7, 14, 13, 0, 0, TimeSpan.Zero);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, TimeSpan.FromHours(-4), start, 45.9, out var local), Is.True);
			Assert.That(local, Is.EqualTo(new DateTime(2025, 7, 14, 9, 0, 45, 900)));
			Assert.That(InstrumentFormat.Clock(local), Is.EqualTo("09:00:45"));

			var january = new DateTimeOffset(2025, 1, 15, 13, 0, 0, TimeSpan.Zero);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, TimeSpan.FromHours(-5), january, 0.0, out local), Is.True);
			Assert.That(local, Is.EqualTo(new DateTime(2025, 1, 15, 8, 0, 0)), "January is EST, UTC-5");
		}

		[Test]
		public void Clock_ForAPack_ChangesDateAtLocalMidnight()
		{
			// 23:59 EDT on Sunday 13 July is 03:59 UTC on the 14th.
			var start = new DateTimeOffset(2025, 7, 14, 3, 59, 0, TimeSpan.Zero);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, TimeSpan.FromHours(-4), start, 30.0, out var before), Is.True);
			Assert.That(EnvironmentFormat.Date(before), Is.EqualTo("SUN 13 JUL 2025"));
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, TimeSpan.FromHours(-4), start, 90.0, out var after), Is.True);
			Assert.That(EnvironmentFormat.Date(after), Is.EqualTo("MON 14 JUL 2025"));
			Assert.That(InstrumentFormat.Clock(after), Is.EqualTo("00:00:30"));
		}

		[Test]
		public void Clock_ForCalmAndUniformRuns_IsUtc_WhateverTheOffset()
		{
			var start = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
			foreach (var kind in new[] { EnvironmentKind.Calm, EnvironmentKind.Uniform })
			{
				Assert.That(EnvironmentFormat.TryClock(kind, TimeSpan.FromHours(-4), start, 10.0, out var utc), Is.True);
				Assert.That(utc, Is.EqualTo(new DateTime(2026, 6, 15, 13, 0, 10)), kind.ToString());
			}
		}

		[Test]
		public void Clock_IsUnknown_ForAnUnsetStart_ANonFiniteTime_OrAnInstantOutsideTheCalendar()
		{
			var offset = TimeSpan.FromHours(-4);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, default, 10.0, out _), Is.False);
			var start = new DateTimeOffset(2025, 7, 14, 13, 0, 0, TimeSpan.Zero);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, start, double.NaN, out _), Is.False);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, start, double.PositiveInfinity, out _), Is.False);
			Assert.That(EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, start, 1e15, out _), Is.False);
			Assert.DoesNotThrow(() => EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, DateTimeOffset.MaxValue, 1e9, out _));
			Assert.DoesNotThrow(() => EnvironmentFormat.TryClock(EnvironmentKind.Pack, offset, new DateTimeOffset(1, 1, 1, 1, 0, 0, TimeSpan.Zero), 0.0, out _));
		}

		[Test]
		public void Readouts_IgnoreTheMachineCulture()
		{
			var saved = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
				Assert.That(EnvironmentFormat.Drift(Toward(1.26, 10.0)), Is.EqualTo("1.3"));
				Assert.That(EnvironmentFormat.Drift(Toward(150.0, 10.0)), Is.EqualTo("150.0"));
				Assert.That(EnvironmentFormat.TideLevel(-0.4, DistanceUnit.Feet), Is.EqualTo("-1.3"));
				Assert.That(EnvironmentFormat.Date(new DateTime(2025, 10, 7)), Is.EqualTo("TUE 7 OCT 2025"));
				Assert.That(EnvironmentFormat.Zone(EnvironmentKind.Pack, new TimeSpan(-4, -30, 0)), Is.EqualTo("UTC-04:30"));
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = saved;
			}
		}
	}
}
