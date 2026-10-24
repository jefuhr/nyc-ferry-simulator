using System;
using System.Globalization;

namespace FerrySim.UI
{
	/// <summary>
	/// Text for every numeric readout. Values arrive in SI; each method converts, rounds to the resolution
	/// the readout claims and never prints more precision than that. Missing or meaningless values show
	/// <see cref="NoValue"/> rather than a number that looks real.
	/// </summary>
	public static class InstrumentFormat
	{
		/// <summary>Shown in place of a value that is missing, non-finite or not meaningful right now.</summary>
		public const string NoValue = "---";

		public const double SpeedResolutionKnots = 0.1;
		public const double AngleResolutionDegrees = 1.0;
		public const double RpmResolution = 1.0;
		public const double LeverResolutionPercent = 1.0;

		/// <summary>
		/// Below this speed over ground a course is not meaningful (a GPS course wanders when the boat is
		/// nearly stopped), so the course readout shows <see cref="NoValue"/>.
		/// </summary>
		public const double MinimumSpeedForCourseKnots = 0.2;

		/// <summary>A heading or course as three digits, for example "005". 359.6 degrees reads "000", never "360".</summary>
		public static string Compass(double radians)
		{
			if (!IsFinite(radians))
			{
				return NoValue;
			}

			var degrees = Math.Round(DisplayUnits.CompassDegrees(radians), MidpointRounding.AwayFromZero);
			if (degrees >= 360.0)
			{
				degrees -= 360.0;
			}

			return degrees.ToString("000", CultureInfo.InvariantCulture);
		}

		/// <summary>Course over ground, or <see cref="NoValue"/> when the speed over ground is too low for it to mean anything.</summary>
		public static string Course(double courseRadians, double speedOverGroundMetresPerSecond) =>
			IsFinite(speedOverGroundMetresPerSecond) &&
			Math.Abs(DisplayUnits.Knots(speedOverGroundMetresPerSecond)) >= MinimumSpeedForCourseKnots
				? Compass(courseRadians)
				: NoValue;

		/// <summary>Speed in knots with one decimal. The sign is kept, so a speed log going astern reads "-1.2".</summary>
		public static string Speed(double metresPerSecond) => Fixed(DisplayUnits.Knots(metresPerSecond), 1);

		/// <summary>Rotational speed in whole rpm, without sign (direction is shown separately).</summary>
		public static string Rpm(double radiansPerSecond) =>
			IsFinite(radiansPerSecond) ? Fixed(Math.Abs(DisplayUnits.Rpm(radiansPerSecond)), 0) : NoValue;

		/// <summary>Rudder angle as side and whole degrees: "S 12°", "P 3°", or "0°" when it rounds to zero. Positive is starboard.</summary>
		public static string Rudder(double radians)
		{
			if (!IsFinite(radians))
			{
				return NoValue;
			}

			var degrees = Math.Round(DisplayUnits.Degrees(radians), MidpointRounding.AwayFromZero);
			if (degrees == 0.0)
			{
				return "0°";
			}

			var magnitude = Math.Abs(degrees).ToString("0", CultureInfo.InvariantCulture);
			return (degrees > 0.0 ? "S " : "P ") + magnitude + "°";
		}

		/// <summary>Lever travel from neutral as a whole percentage, "96%"; the direction is <see cref="LeverDirection"/>.</summary>
		public static string LeverPercent(double lever) =>
			IsFinite(lever) ? Fixed(Math.Abs(lever) * 100.0, 0) + "%" : NoValue;

		/// <summary>"AHEAD", "ASTERN", or "NEUTRAL" when the lever reads 0%.</summary>
		public static string LeverDirection(double lever)
		{
			if (!IsFinite(lever))
			{
				return NoValue;
			}

			var percent = Math.Round(lever * 100.0, MidpointRounding.AwayFromZero);
			return percent > 0.0 ? "AHEAD" : percent < 0.0 ? "ASTERN" : "NEUTRAL";
		}

		/// <summary>A depth or distance in the chosen unit: one decimal below 100, whole numbers from 100 up.</summary>
		public static string Distance(double metres, DistanceUnit unit)
		{
			if (!IsFinite(metres))
			{
				return NoValue;
			}

			var value = DisplayUnits.Distance(metres, unit);
			return Fixed(value, Math.Abs(value) < 99.95 ? 1 : 0);
		}

		/// <summary>A force in kilonewtons: one decimal below 10 kN, whole kilonewtons from 10 up.</summary>
		public static string Force(double newtons)
		{
			if (!IsFinite(newtons))
			{
				return NoValue;
			}

			var kilonewtons = DisplayUnits.Kilonewtons(Math.Abs(newtons));
			return Fixed(kilonewtons, kilonewtons < 9.95 ? 1 : 0);
		}

		/// <summary>Wall-clock time of day, "14:05:09".</summary>
		public static string Clock(DateTime local) => local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

		/// <summary>Scenario elapsed time: "T+0:45", "T+12:05", "T+1:02:03".</summary>
		public static string Elapsed(double seconds)
		{
			if (!IsFinite(seconds))
			{
				return NoValue;
			}

			var sign = seconds < 0.0 ? "T-" : "T+";
			var total = (long)Math.Floor(Math.Abs(seconds));
			var hours = total / 3600;
			var minutes = total / 60 % 60;
			var secs = total % 60;
			return hours > 0
				? string.Format(CultureInfo.InvariantCulture, "{0}{1}:{2:00}:{3:00}", sign, hours, minutes, secs)
				: string.Format(CultureInfo.InvariantCulture, "{0}{1}:{2:00}", sign, minutes, secs);
		}

		/// <summary>Fixed-point text in the invariant culture; NaN and infinities show <see cref="NoValue"/> and -0 prints as 0.</summary>
		public static string Fixed(double value, int decimals)
		{
			if (!IsFinite(value))
			{
				return NoValue;
			}

			var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
			if (rounded == 0.0)
			{
				// Normalizes -0.0, which some runtimes print as "-0.0".
				rounded = 0.0;
			}

			return rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
		}

		public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
