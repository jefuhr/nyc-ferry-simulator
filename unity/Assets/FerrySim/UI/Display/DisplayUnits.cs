using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	/// <summary>Length unit for depths, alarm thresholds and track distances. Speeds are always knots.</summary>
	public enum DistanceUnit
	{
		Feet = 0,
		Metres = 1,
	}

	/// <summary>
	/// SI to display-unit conversions. Telemetry stays SI (docs/conventions.md); these run only at the
	/// moment a value is shown, using the exact factors in <see cref="Units"/>.
	/// </summary>
	public static class DisplayUnits
	{
		public static double Knots(double metresPerSecond) => Units.MetresPerSecondToKnots(metresPerSecond);

		public static double Rpm(double radiansPerSecond) => Units.RadiansPerSecondToRpm(radiansPerSecond);

		public static double Degrees(double radians) => Angles.ToDegrees(radians);

		/// <summary>A heading or course in degrees, wrapped into [0, 360).</summary>
		public static double CompassDegrees(double radians) => Angles.ToDegrees(Angles.WrapTwoPi(radians));

		public static double Kilonewtons(double newtons) => newtons / 1000.0;

		public static double Distance(double metres, DistanceUnit unit) =>
			unit == DistanceUnit.Feet ? metres / Units.MetresPerFoot : metres;

		public static double DistanceToMetres(double value, DistanceUnit unit) =>
			unit == DistanceUnit.Feet ? Units.FeetToMetres(value) : value;

		public static string Symbol(DistanceUnit unit) => unit == DistanceUnit.Feet ? "ft" : "m";
	}
}
