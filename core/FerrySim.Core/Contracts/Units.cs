using System;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// Exact conversion factors. The core works in SI units; non-SI units appear only at data
	/// and UI boundaries (see docs/conventions.md).
	/// </summary>
	public static class Units
	{
		/// <summary>One international knot in metres per second (1852 m per hour).</summary>
		public const double MetresPerSecondPerKnot = 1852.0 / 3600.0;

		/// <summary>One international foot in metres.</summary>
		public const double MetresPerFoot = 0.3048;

		/// <summary>One inch in metres.</summary>
		public const double MetresPerInch = 0.0254;

		/// <summary>One mechanical horsepower (550 ft·lbf/s) in watts.</summary>
		public const double WattsPerHorsepower = 745.69987158227022;

		/// <summary>One revolution per minute in radians per second.</summary>
		public const double RadiansPerSecondPerRpm = 2.0 * Math.PI / 60.0;

		public static double KnotsToMetresPerSecond(double knots) => knots * MetresPerSecondPerKnot;

		public static double MetresPerSecondToKnots(double metresPerSecond) => metresPerSecond / MetresPerSecondPerKnot;

		public static double FeetToMetres(double feet) => feet * MetresPerFoot;

		public static double FeetAndInchesToMetres(double feet, double inches) => feet * MetresPerFoot + inches * MetresPerInch;

		public static double HorsepowerToWatts(double horsepower) => horsepower * WattsPerHorsepower;

		public static double RpmToRadiansPerSecond(double rpm) => rpm * RadiansPerSecondPerRpm;

		public static double RadiansPerSecondToRpm(double radiansPerSecond) => radiansPerSecond / RadiansPerSecondPerRpm;
	}

	/// <summary>Angle helpers. Angles are radians everywhere in the core.</summary>
	public static class Angles
	{
		public const double TwoPi = 2.0 * Math.PI;

		public static double ToRadians(double degrees) => degrees * (Math.PI / 180.0);

		public static double ToDegrees(double radians) => radians * (180.0 / Math.PI);

		/// <summary>Wraps an angle into [0, 2π). Used for headings and courses.</summary>
		public static double WrapTwoPi(double angle)
		{
			var wrapped = angle % TwoPi;
			if (wrapped < 0.0)
			{
				wrapped += TwoPi;
			}

			// A tiny negative input can round up to exactly 2π after the addition above.
			return wrapped >= TwoPi ? 0.0 : wrapped;
		}

		/// <summary>Wraps an angle into [-π, π). Used for heading errors and relative bearings.</summary>
		public static double WrapPi(double angle) => WrapTwoPi(angle + Math.PI) - Math.PI;
	}
}
