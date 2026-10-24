using System;

namespace FerrySim.World
{
	/// <summary>
	/// Sun position by the NOAA Global Monitoring Laboratory solar calculator, which implements equations
	/// from Jean Meeus, Astronomical Algorithms (https://gml.noaa.gov/grad/solcalc/calcdetails.html). The
	/// formulas follow the "Calculations" sheet of NOAA_Solar_Calculations_day.xls/.ods column by column
	/// (column letters in comments), with the time zone fixed at UTC. NOAA calls the result approximate,
	/// valid 1901 to 2099; it ignores parallax and uses a standard-atmosphere refraction model.
	/// </summary>
	public readonly struct SolarPosition
	{
		private const double UnixEpochJulianDay = 2440587.5;
		private const double J2000JulianDay = 2451545.0;
		private const double DaysPerJulianCentury = 36525.0;

		private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private SolarPosition(double julianDay, double declination, double equationOfTime, double hourAngle, double zenith, double refraction, double azimuth)
		{
			JulianDay = julianDay;
			DeclinationDegrees = declination;
			EquationOfTimeMinutes = equationOfTime;
			HourAngleDegrees = hourAngle;
			ZenithDegrees = zenith;
			RefractionDegrees = refraction;
			AzimuthDegrees = azimuth;
		}

		public double JulianDay { get; }

		/// <summary>Apparent declination of the sun, degrees (column T).</summary>
		public double DeclinationDegrees { get; }

		/// <summary>Equation of time, minutes (column V): apparent minus mean solar time.</summary>
		public double EquationOfTimeMinutes { get; }

		/// <summary>Hour angle, degrees, negative before solar noon (column AC).</summary>
		public double HourAngleDegrees { get; }

		/// <summary>Geometric zenith angle without refraction, degrees (column AD).</summary>
		public double ZenithDegrees { get; }

		/// <summary>Geometric elevation without refraction, degrees (column AE).</summary>
		public double ElevationDegrees => 90.0 - ZenithDegrees;

		/// <summary>Approximate atmospheric refraction, degrees (column AF).</summary>
		public double RefractionDegrees { get; }

		/// <summary>Elevation corrected for refraction, the one to light the scene with, degrees (column AG).</summary>
		public double ApparentElevationDegrees => ElevationDegrees + RefractionDegrees;

		/// <summary>Azimuth clockwise from true north, degrees in [0, 360) (column AH).</summary>
		public double AzimuthDegrees { get; }

		/// <summary>Sun position seen from a WGS84 latitude and longitude (degrees, east positive) at a UTC instant.</summary>
		public static SolarPosition Compute(DateTime utc, double latitudeDegrees, double longitudeDegrees)
		{
			var julianDay = ToJulianDay(utc);
			var sun = SunAt(julianDay);
			var utcMinutes = utc.TimeOfDay.TotalMinutes;

			// Z: true solar time, minutes. AA: hour angle.
			var trueSolarTime = Modulo(utcMinutes + sun.EquationOfTimeMinutes + 4.0 * longitudeDegrees, 1440.0);
			var hourAngle = trueSolarTime / 4.0 < 0.0 ? trueSolarTime / 4.0 + 180.0 : trueSolarTime / 4.0 - 180.0;

			var latitude = Radians(latitudeDegrees);
			var declination = Radians(sun.DeclinationDegrees);

			// AD: solar zenith angle.
			var cosZenith = Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(Radians(hourAngle));
			var zenith = Degrees(Math.Acos(Clamp(cosZenith)));
			var refraction = Refraction(90.0 - zenith);

			// AH: azimuth, clockwise from north. Undefined with the sun at the zenith; call it due south.
			var sinZenith = Math.Sin(Radians(zenith));
			double azimuth;
			if (Math.Abs(Math.Cos(latitude) * sinZenith) < 1e-12)
			{
				azimuth = 180.0;
			}
			else
			{
				var cosAzimuth = (Math.Sin(latitude) * Math.Cos(Radians(zenith)) - Math.Sin(declination)) / (Math.Cos(latitude) * sinZenith);
				var angle = Degrees(Math.Acos(Clamp(cosAzimuth)));
				azimuth = hourAngle > 0.0 ? Modulo(angle + 180.0, 360.0) : Modulo(540.0 - angle, 360.0);
			}

			return new SolarPosition(julianDay, sun.DeclinationDegrees, sun.EquationOfTimeMinutes, hourAngle, zenith, refraction, azimuth);
		}

		internal static double ToJulianDay(DateTime utc)
		{
			if (utc.Kind != DateTimeKind.Utc)
			{
				throw new ArgumentException("Solar position needs a UTC DateTime (DateTimeKind.Utc).", nameof(utc));
			}

			return UnixEpochJulianDay + (utc - UnixEpoch).TotalDays;
		}

		/// <summary>Declination and equation of time at a Julian day (columns G to V).</summary>
		internal static SunTerms SunAt(double julianDay)
		{
			var century = (julianDay - J2000JulianDay) / DaysPerJulianCentury;

			// I: geometric mean longitude. J: geometric mean anomaly. K: eccentricity of Earth's orbit.
			var meanLongitude = Modulo(280.46646 + century * (36000.76983 + century * 0.0003032), 360.0);
			var meanAnomaly = 357.52911 + century * (35999.05029 - 0.0001537 * century);
			var eccentricity = 0.016708634 - century * (0.000042037 + 0.0000001267 * century);

			// L: equation of centre. M: true longitude. P: apparent longitude.
			var anomaly = Radians(meanAnomaly);
			var centre = Math.Sin(anomaly) * (1.914602 - century * (0.004817 + 0.000014 * century))
				+ Math.Sin(2.0 * anomaly) * (0.019993 - 0.000101 * century)
				+ Math.Sin(3.0 * anomaly) * 0.000289;
			var trueLongitude = meanLongitude + centre;
			var omega = Radians(125.04 - 1934.136 * century);
			var apparentLongitude = trueLongitude - 0.00569 - 0.00478 * Math.Sin(omega);

			// Q: mean obliquity of the ecliptic. R: corrected obliquity. T: declination.
			var meanObliquity = 23.0 + (26.0 + (21.448 - century * (46.815 + century * (0.00059 - century * 0.001813))) / 60.0) / 60.0;
			var obliquity = Radians(meanObliquity + 0.00256 * Math.Cos(omega));
			var declination = Degrees(Math.Asin(Math.Sin(obliquity) * Math.Sin(Radians(apparentLongitude))));

			// U: var y. V: equation of time, minutes.
			var y = Math.Tan(obliquity / 2.0) * Math.Tan(obliquity / 2.0);
			var longitude = Radians(meanLongitude);
			var equationOfTime = 4.0 * Degrees(
				y * Math.Sin(2.0 * longitude)
				- 2.0 * eccentricity * Math.Sin(anomaly)
				+ 4.0 * eccentricity * y * Math.Sin(anomaly) * Math.Cos(2.0 * longitude)
				- 0.5 * y * y * Math.Sin(4.0 * longitude)
				- 1.25 * eccentricity * eccentricity * Math.Sin(2.0 * anomaly));

			return new SunTerms(declination, equationOfTime);
		}

		/// <summary>NOAA's approximate refraction (column AF), degrees, for a geometric elevation in degrees.</summary>
		internal static double Refraction(double elevationDegrees)
		{
			double arcSeconds;
			if (elevationDegrees > 85.0)
			{
				arcSeconds = 0.0;
			}
			else if (elevationDegrees > 5.0)
			{
				var t = Math.Tan(Radians(elevationDegrees));
				arcSeconds = 58.1 / t - 0.07 / (t * t * t) + 0.000086 / Math.Pow(t, 5.0);
			}
			else if (elevationDegrees > -0.575)
			{
				var e = elevationDegrees;
				arcSeconds = 1735.0 + e * (-518.2 + e * (103.4 + e * (-12.79 + e * 0.711)));
			}
			else
			{
				arcSeconds = -20.772 / Math.Tan(Radians(elevationDegrees));
			}

			return arcSeconds / 3600.0;
		}

		internal static double Radians(double degrees) => degrees * Math.PI / 180.0;

		internal static double Degrees(double radians) => radians * 180.0 / Math.PI;

		// Excel's MOD: the result takes the sign of the divisor.
		internal static double Modulo(double value, double divisor) => value - divisor * Math.Floor(value / divisor);

		private static double Clamp(double cosine) => Math.Max(-1.0, Math.Min(1.0, cosine));

		internal readonly struct SunTerms
		{
			public SunTerms(double declinationDegrees, double equationOfTimeMinutes)
			{
				DeclinationDegrees = declinationDegrees;
				EquationOfTimeMinutes = equationOfTimeMinutes;
			}

			public double DeclinationDegrees { get; }

			public double EquationOfTimeMinutes { get; }
		}
	}

	/// <summary>
	/// Solar noon, sunrise and sunset for one day, from the same NOAA calculator (columns U to Y). Rise
	/// and set use a zenith of 90.833 deg (0.833 deg of refraction and the sun's semidiameter). The
	/// spreadsheet evaluates each event with the sun's declination and equation of time at the row's
	/// time; here each event is evaluated once more at its own time, which keeps it within NOAA's
	/// stated minute.
	/// </summary>
	public readonly struct SolarDay
	{
		private const double RiseSetZenithDegrees = 90.833;

		private SolarDay(DateTime solarNoonUtc, DateTime sunriseUtc, DateTime sunsetUtc, bool hasSunriseAndSunset)
		{
			SolarNoonUtc = solarNoonUtc;
			SunriseUtc = sunriseUtc;
			SunsetUtc = sunsetUtc;
			HasSunriseAndSunset = hasSunriseAndSunset;
		}

		public DateTime SolarNoonUtc { get; }

		/// <summary>Sunrise in UTC; equal to <see cref="SolarNoonUtc"/> during polar day or night.</summary>
		public DateTime SunriseUtc { get; }

		/// <summary>Sunset in UTC; equal to <see cref="SolarNoonUtc"/> during polar day or night.</summary>
		public DateTime SunsetUtc { get; }

		/// <summary>False during polar day or night, when the sun does not cross 90.833 deg zenith.</summary>
		public bool HasSunriseAndSunset { get; }

		/// <summary>
		/// Events for the solar day whose noon falls nearest 12:00 local mean time on <paramref name="dateUtc"/>
		/// (only the date part is used). At New York that noon is near 17:00 UTC, so sunset usually falls
		/// on the next UTC date.
		/// </summary>
		public static SolarDay Compute(DateTime dateUtc, double latitudeDegrees, double longitudeDegrees)
		{
			if (dateUtc.Kind != DateTimeKind.Utc)
			{
				throw new ArgumentException("Solar day needs a UTC DateTime (DateTimeKind.Utc).", nameof(dateUtc));
			}

			var midnight = dateUtc.Date;
			var meanNoonMinutes = 720.0 - 4.0 * longitudeDegrees;

			// X: solar noon, refined once with the equation of time at the first estimate.
			var noonMinutes = meanNoonMinutes - EquationOfTime(midnight, meanNoonMinutes);
			noonMinutes = meanNoonMinutes - EquationOfTime(midnight, noonMinutes);

			var sunriseMinutes = noonMinutes;
			var sunsetMinutes = noonMinutes;
			var hasEvents = TryHalfDayLength(midnight, noonMinutes, latitudeDegrees, out var halfDay);
			if (hasEvents)
			{
				// W, Y: each event refined with the declination and equation of time at its own estimate.
				var riseEstimate = noonMinutes - halfDay;
				var setEstimate = noonMinutes + halfDay;
				hasEvents = TryEvent(midnight, riseEstimate, meanNoonMinutes, latitudeDegrees, -1.0, out sunriseMinutes)
					&& TryEvent(midnight, setEstimate, meanNoonMinutes, latitudeDegrees, 1.0, out sunsetMinutes);
				if (!hasEvents)
				{
					sunriseMinutes = noonMinutes;
					sunsetMinutes = noonMinutes;
				}
			}

			return new SolarDay(
				midnight.AddMinutes(noonMinutes),
				midnight.AddMinutes(sunriseMinutes),
				midnight.AddMinutes(sunsetMinutes),
				hasEvents);
		}

		private static bool TryEvent(DateTime midnight, double estimateMinutes, double meanNoonMinutes, double latitudeDegrees, double sign, out double eventMinutes)
		{
			var sun = SolarPosition.SunAt(SolarPosition.ToJulianDay(midnight) + estimateMinutes / 1440.0);
			var noon = meanNoonMinutes - sun.EquationOfTimeMinutes;
			if (!TryHourAngle(latitudeDegrees, sun.DeclinationDegrees, out var hourAngleDegrees))
			{
				eventMinutes = noon;
				return false;
			}

			eventMinutes = noon + sign * 4.0 * hourAngleDegrees;
			return true;
		}

		private static bool TryHalfDayLength(DateTime midnight, double noonMinutes, double latitudeDegrees, out double halfDayMinutes)
		{
			var sun = SolarPosition.SunAt(SolarPosition.ToJulianDay(midnight) + noonMinutes / 1440.0);
			var ok = TryHourAngle(latitudeDegrees, sun.DeclinationDegrees, out var hourAngleDegrees);
			halfDayMinutes = 4.0 * hourAngleDegrees;
			return ok;
		}

		// W: hour angle of sunrise, degrees.
		private static bool TryHourAngle(double latitudeDegrees, double declinationDegrees, out double hourAngleDegrees)
		{
			var latitude = SolarPosition.Radians(latitudeDegrees);
			var declination = SolarPosition.Radians(declinationDegrees);
			var cosHourAngle = Math.Cos(SolarPosition.Radians(RiseSetZenithDegrees)) / (Math.Cos(latitude) * Math.Cos(declination))
				- Math.Tan(latitude) * Math.Tan(declination);
			if (cosHourAngle < -1.0 || cosHourAngle > 1.0)
			{
				hourAngleDegrees = 0.0;
				return false;
			}

			hourAngleDegrees = SolarPosition.Degrees(Math.Acos(cosHourAngle));
			return true;
		}

		private static double EquationOfTime(DateTime midnight, double minutes) =>
			SolarPosition.SunAt(SolarPosition.ToJulianDay(midnight) + minutes / 1440.0).EquationOfTimeMinutes;
	}
}
