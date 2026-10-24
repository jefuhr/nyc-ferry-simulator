using System;
using NUnit.Framework;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// The NOAA solar position algorithm against three independent kinds of reference:
	/// NOAA's own spreadsheet output (same formulas, so near-exact), NREL's Solar Position Algorithm
	/// example (a different, more precise algorithm) and USNO rise, transit and set times for the
	/// slice. Guards against degree/radian mix-ups, a flipped longitude sign, the wrong azimuth
	/// origin or direction, local time used as UTC, and a broken refraction branch.
	/// </summary>
	public class SolarPositionTests
	{
		// NOAA_Solar_Calculations_day.ods (https://gml.noaa.gov/grad/solcalc/calcdetails.html, retrieved
		// 2026-10-22): the cached cell values of its default sheet, latitude 40, longitude -105, time zone
		// -7, date 2010-06-21. Columns AG (refraction-corrected elevation) and AH (azimuth).
		private const double SpreadsheetLatitude = 40.0;
		private const double SpreadsheetLongitude = -105.0;
		private const double SpreadsheetTolerance = 1e-6;

		// USNO Astronomical Applications API v4.0.1 (https://aa.usno.navy.mil/api/rstt/oneday, coords
		// 40.7000,-74.0150, tz 0), retrieved 2026-10-23. Times are UT, rounded to the minute.
		private const double OriginLatitude = 40.7000;
		private const double OriginLongitude = -74.0150;

		[TestCase(7, 6, -26.5422164325893, 1.09867118388445, TestName = "Spreadsheet row 2, night, hour angle < 0")]
		[TestCase(11, 54, 3.14992917410899, 61.5001746421255, TestName = "Spreadsheet row 50, low sun, polynomial refraction")]
		[TestCase(19, 0, 73.4384793439627, 178.533230037881, TestName = "Spreadsheet row 121, near noon")]
		[TestCase(23, 0, 37.7562612257633, 270.411876295863, TestName = "Spreadsheet row 161, afternoon, tangent refraction")]
		public void MatchesNoaaSpreadsheet_SameDay(int hourUtc, int minuteUtc, double expectedElevation, double expectedAzimuth)
		{
			var utc = new DateTime(2010, 6, 21, hourUtc, minuteUtc, 0, DateTimeKind.Utc);

			var sun = SolarPosition.Compute(utc, SpreadsheetLatitude, SpreadsheetLongitude);

			Assert.That(sun.ApparentElevationDegrees, Is.EqualTo(expectedElevation).Within(SpreadsheetTolerance));
			Assert.That(sun.AzimuthDegrees, Is.EqualTo(expectedAzimuth).Within(SpreadsheetTolerance));
		}

		[TestCase(2, 30, -0.454693723488674, 0.0989819972923182, 301.72829080667, TestName = "Spreadsheet row 196, sunset, refraction just above -0.575")]
		[TestCase(2, 36, -1.42672612449374, -1.19505705429261, 302.70240363522, TestName = "Spreadsheet row 197, after sunset, refraction below -0.575")]
		[TestCase(7, 0, -26.5621921674993, -26.5506507277657, 359.504653217124, TestName = "Spreadsheet row 241, midnight, hour angle > 0")]
		public void MatchesNoaaSpreadsheet_NextUtcDay(int hourUtc, int minuteUtc, double expectedGeometric, double expectedApparent, double expectedAzimuth)
		{
			var utc = new DateTime(2010, 6, 22, hourUtc, minuteUtc, 0, DateTimeKind.Utc);

			var sun = SolarPosition.Compute(utc, SpreadsheetLatitude, SpreadsheetLongitude);

			Assert.That(sun.ElevationDegrees, Is.EqualTo(expectedGeometric).Within(SpreadsheetTolerance));
			Assert.That(sun.ApparentElevationDegrees, Is.EqualTo(expectedApparent).Within(SpreadsheetTolerance));
			Assert.That(sun.AzimuthDegrees, Is.EqualTo(expectedAzimuth).Within(SpreadsheetTolerance));
		}

		[Test]
		public void MatchesNoaaSpreadsheet_DeclinationAndEquationOfTime()
		{
			// Row 121 (12:00 local, 19:00 UTC): columns T (declination) and V (equation of time).
			var sun = SolarPosition.Compute(new DateTime(2010, 6, 21, 19, 0, 0, DateTimeKind.Utc), SpreadsheetLatitude, SpreadsheetLongitude);

			Assert.That(sun.DeclinationDegrees, Is.EqualTo(23.4381481561729).Within(SpreadsheetTolerance));
			Assert.That(sun.EquationOfTimeMinutes, Is.EqualTo(-1.82311100828435).Within(SpreadsheetTolerance));
		}

		[Test]
		public void AgreesWithNrelSpaExample()
		{
			// Reda and Andreas, Solar Position Algorithm for Solar Radiation Applications, NREL/TP-560-34302
			// (revised 2008), example: 2003-10-17 12:30:30 at UTC-7, 39.742476 N 105.1786 W, topocentric
			// zenith 50.11162 deg and azimuth 194.34024 deg. NOAA ignores parallax (0.002 deg) and uses a
			// sea-level refraction model where SPA used 820 mbar (0.004 deg), so allow 0.03 deg.
			var sun = SolarPosition.Compute(new DateTime(2003, 10, 17, 19, 30, 30, DateTimeKind.Utc), 39.742476, -105.1786);

			Assert.That(90.0 - sun.ApparentElevationDegrees, Is.EqualTo(50.11162).Within(0.03));
			Assert.That(sun.AzimuthDegrees, Is.EqualTo(194.34024).Within(0.03));
		}

		[TestCase(2026, 6, 15, 9, 24, 16, 57, 0, 29, TestName = "USNO 2026-06-15")]
		[TestCase(2026, 12, 15, 12, 13, 16, 51, 21, 30, TestName = "USNO 2026-12-15")]
		[TestCase(2026, 3, 20, 10, 59, 17, 3, 23, 8, TestName = "USNO 2026-03-20")]
		[TestCase(2026, 9, 23, 10, 45, 16, 48, 22, 51, TestName = "USNO 2026-09-23")]
		public void SunriseTransitAndSunset_MatchUsnoWithinAMinute(int year, int month, int day, int riseH, int riseM, int transitH, int transitM, int setH, int setM)
		{
			var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
			var events = SolarDay.Compute(date, OriginLatitude, OriginLongitude);

			// USNO lists the events that fall on the UT date. A set before 12:00 UT belongs to the previous
			// local evening, so compare it with the previous day's sunset.
			var expectedSet = date.AddHours(setH).AddMinutes(setM);
			var computedSet = setH < 12 ? SolarDay.Compute(date.AddDays(-1), OriginLatitude, OriginLongitude).SunsetUtc : events.SunsetUtc;

			// USNO rounds to the nearest minute; NOAA claims about a minute for rise and set.
			Assert.That(MinutesBetween(events.SunriseUtc, date.AddHours(riseH).AddMinutes(riseM)), Is.LessThanOrEqualTo(1.0), "sunrise " + events.SunriseUtc.ToString("O"));
			Assert.That(MinutesBetween(events.SolarNoonUtc, date.AddHours(transitH).AddMinutes(transitM)), Is.LessThanOrEqualTo(1.0), "transit " + events.SolarNoonUtc.ToString("O"));
			Assert.That(MinutesBetween(computedSet, expectedSet), Is.LessThanOrEqualTo(1.0), "sunset " + computedSet.ToString("O"));
		}

		[TestCase(2026, 6, 15, 8, 51, TestName = "USNO begin civil twilight 2026-06-15")]
		[TestCase(2026, 12, 15, 22, 0, TestName = "USNO end civil twilight 2026-12-15")]
		[TestCase(2026, 3, 20, 23, 36, TestName = "USNO end civil twilight 2026-03-20")]
		public void CivilTwilightBoundary_SunSixDegreesBelowHorizon(int year, int month, int day, int hourUtc, int minuteUtc)
		{
			var sun = SolarPosition.Compute(new DateTime(year, month, day, hourUtc, minuteUtc, 0, DateTimeKind.Utc), OriginLatitude, OriginLongitude);

			// Geometric elevation changes about 0.15 deg per minute here, and USNO rounds to the minute.
			Assert.That(sun.ElevationDegrees, Is.EqualTo(-6.0).Within(0.15));
		}

		[Test]
		public void AtTransit_SunIsDueSouthAtNinetyMinusLatitudePlusDeclination()
		{
			var events = SolarDay.Compute(new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc), OriginLatitude, OriginLongitude);
			var sun = SolarPosition.Compute(events.SolarNoonUtc, OriginLatitude, OriginLongitude);

			Assert.That(sun.AzimuthDegrees, Is.EqualTo(180.0).Within(0.01));
			Assert.That(sun.ElevationDegrees, Is.EqualTo(90.0 - OriginLatitude + sun.DeclinationDegrees).Within(0.01));
			Assert.That(sun.DeclinationDegrees, Is.EqualTo(23.44).Within(0.01), "June solstice declination");
		}

		[Test]
		public void LocalMidnight_SunWellBelowHorizon()
		{
			// 04:00 UTC is midnight EDT.
			var sun = SolarPosition.Compute(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), OriginLatitude, OriginLongitude);

			Assert.That(sun.ApparentElevationDegrees, Is.LessThan(-20.0));
		}

		[Test]
		public void LocalTimeKind_IsRejected()
		{
			// A local DateTime would silently shift the sun by the UTC offset.
			Assert.Throws<ArgumentException>(() => SolarPosition.Compute(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Local), OriginLatitude, OriginLongitude));
		}

		private static double MinutesBetween(DateTime a, DateTime b) => Math.Abs((a - b).TotalMinutes);
	}
}
