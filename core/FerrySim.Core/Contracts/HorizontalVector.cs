using System;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// A horizontal vector in world NED components: north and east. Used for horizontal positions
	/// (metres) and for current and wind velocities (metres per second, direction of travel).
	/// </summary>
	public readonly struct HorizontalVector : IEquatable<HorizontalVector>
	{
		public HorizontalVector(double north, double east)
		{
			North = north;
			East = east;
		}

		public double North { get; }

		public double East { get; }

		public double Magnitude => Math.Sqrt(North * North + East * East);

		/// <summary>Direction the vector points toward, clockwise from true north, in [0, 2π).</summary>
		public double Direction => Angles.WrapTwoPi(Math.Atan2(East, North));

		public static HorizontalVector Zero => default;

		/// <summary>
		/// Builds a flow vector from a speed and the direction the flow is going toward, clockwise
		/// from true north. Tidal current set is reported this way.
		/// </summary>
		public static HorizontalVector FromSpeedToward(double speed, double directionToward) =>
			new HorizontalVector(speed * Math.Cos(directionToward), speed * Math.Sin(directionToward));

		/// <summary>
		/// Builds a wind vector from a meteorological report: speed and the direction the wind
		/// blows from, clockwise from true north. A north wind (from 0) points south.
		/// </summary>
		public static HorizontalVector FromSpeedFrom(double speed, double directionFrom) =>
			FromSpeedToward(speed, directionFrom + Math.PI);

		public static HorizontalVector operator +(HorizontalVector a, HorizontalVector b) =>
			new HorizontalVector(a.North + b.North, a.East + b.East);

		public static HorizontalVector operator -(HorizontalVector a, HorizontalVector b) =>
			new HorizontalVector(a.North - b.North, a.East - b.East);

		public static HorizontalVector operator *(HorizontalVector a, double scale) =>
			new HorizontalVector(a.North * scale, a.East * scale);

		public bool Equals(HorizontalVector other) => North.Equals(other.North) && East.Equals(other.East);

		public override bool Equals(object obj) => obj is HorizontalVector other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(North, East);

		public override string ToString() => $"(N {North:F3}, E {East:F3})";
	}
}
