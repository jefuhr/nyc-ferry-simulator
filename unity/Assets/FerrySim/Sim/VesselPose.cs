using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// Where to draw the vessel: world position (NED, metres, double) and attitude (radians), following
	/// docs/conventions.md. Rendering interpolates these between physics states; they never feed back.
	/// </summary>
	public readonly struct VesselPose
	{
		public VesselPose(double north, double east, double down, double heading, double pitch, double roll)
		{
			North = north;
			East = east;
			Down = down;
			Heading = heading;
			Pitch = pitch;
			Roll = roll;
		}

		public double North { get; }

		public double East { get; }

		/// <summary>Heave below the calm-water equilibrium, metres, positive down.</summary>
		public double Down { get; }

		/// <summary>Radians clockwise from true north, in [0, 2π).</summary>
		public double Heading { get; }

		/// <summary>Radians, positive bow up.</summary>
		public double Pitch { get; }

		/// <summary>Radians, positive starboard side down.</summary>
		public double Roll { get; }

		public static VesselPose FromState(in VesselState state) =>
			new VesselPose(state.North, state.East, state.Down, state.Heading, state.Pitch, state.Roll);

		/// <summary>
		/// The pose a fraction <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>.
		/// Heading takes the short way round, so 359° to 1° passes through 0°, not 180°.
		/// </summary>
		public static VesselPose Interpolate(in VesselState from, in VesselState to, double t)
		{
			t = Math.Min(1.0, Math.Max(0.0, t));
			var headingChange = Angles.WrapPi(to.Heading - from.Heading);
			return new VesselPose(
				Lerp(from.North, to.North, t),
				Lerp(from.East, to.East, t),
				Lerp(from.Down, to.Down, t),
				Angles.WrapTwoPi(from.Heading + headingChange * t),
				Lerp(from.Pitch, to.Pitch, t),
				Lerp(from.Roll, to.Roll, t));
		}

		private static double Lerp(double a, double b, double t) => a + (b - a) * t;
	}
}
