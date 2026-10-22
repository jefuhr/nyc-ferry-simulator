using System;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// Rigid-body state of one vessel's reference point: centreline, midships, design waterline.
	/// World frame is local NED; body frame is x forward, y starboard, z down; heading is clockwise
	/// from true north. All values are SI (metres, radians, seconds). See docs/conventions.md.
	/// </summary>
	public readonly struct VesselState : IEquatable<VesselState>
	{
		public VesselState(
			double time,
			double north,
			double east,
			double down,
			double roll,
			double pitch,
			double heading,
			double surge,
			double sway,
			double heave,
			double rollRate,
			double pitchRate,
			double yawRate)
		{
			Time = time;
			North = north;
			East = east;
			Down = down;
			Roll = roll;
			Pitch = pitch;
			Heading = Angles.WrapTwoPi(heading);
			Surge = surge;
			Sway = sway;
			Heave = heave;
			RollRate = rollRate;
			PitchRate = pitchRate;
			YawRate = yawRate;
		}

		/// <summary>Simulation time in seconds since scenario start.</summary>
		public double Time { get; }

		/// <summary>World position north of the origin, metres.</summary>
		public double North { get; }

		/// <summary>World position east of the origin, metres.</summary>
		public double East { get; }

		/// <summary>Vertical displacement from the calm-water equilibrium, metres, positive down.</summary>
		public double Down { get; }

		/// <summary>Roll angle, radians, positive starboard side down.</summary>
		public double Roll { get; }

		/// <summary>Pitch angle, radians, positive bow up.</summary>
		public double Pitch { get; }

		/// <summary>True heading, radians clockwise from true north, in [0, 2π).</summary>
		public double Heading { get; }

		/// <summary>Body-frame velocity forward over the ground, m/s.</summary>
		public double Surge { get; }

		/// <summary>Body-frame velocity to starboard over the ground, m/s.</summary>
		public double Sway { get; }

		/// <summary>Body-frame velocity downward, m/s.</summary>
		public double Heave { get; }

		/// <summary>Roll rate, rad/s, positive rolling to starboard.</summary>
		public double RollRate { get; }

		/// <summary>Pitch rate, rad/s, positive bow rising.</summary>
		public double PitchRate { get; }

		/// <summary>Yaw rate, rad/s, positive turning to starboard.</summary>
		public double YawRate { get; }

		/// <summary>World position as a horizontal vector.</summary>
		public HorizontalVector Position => new HorizontalVector(North, East);

		/// <summary>Velocity over the ground in world north and east components, m/s.</summary>
		public HorizontalVector GroundVelocity
		{
			get
			{
				var cos = Math.Cos(Heading);
				var sin = Math.Sin(Heading);
				return new HorizontalVector(Surge * cos - Sway * sin, Surge * sin + Sway * cos);
			}
		}

		/// <summary>Creates a state with only the planar (surge, sway, yaw) degrees of freedom set.</summary>
		public static VesselState Planar(
			double time,
			double north,
			double east,
			double heading,
			double surge = 0.0,
			double sway = 0.0,
			double yawRate = 0.0) =>
			new VesselState(time, north, east, 0.0, 0.0, 0.0, heading, surge, sway, 0.0, 0.0, 0.0, yawRate);

		/// <summary>True when every component is a finite number.</summary>
		public bool IsFinite() =>
			IsFinite(Time) && IsFinite(North) && IsFinite(East) && IsFinite(Down) &&
			IsFinite(Roll) && IsFinite(Pitch) && IsFinite(Heading) &&
			IsFinite(Surge) && IsFinite(Sway) && IsFinite(Heave) &&
			IsFinite(RollRate) && IsFinite(PitchRate) && IsFinite(YawRate);

		public bool Equals(VesselState other) =>
			Time.Equals(other.Time) && North.Equals(other.North) && East.Equals(other.East) &&
			Down.Equals(other.Down) && Roll.Equals(other.Roll) && Pitch.Equals(other.Pitch) &&
			Heading.Equals(other.Heading) && Surge.Equals(other.Surge) && Sway.Equals(other.Sway) &&
			Heave.Equals(other.Heave) && RollRate.Equals(other.RollRate) &&
			PitchRate.Equals(other.PitchRate) && YawRate.Equals(other.YawRate);

		public override bool Equals(object obj) => obj is VesselState other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(Time);
			hash.Add(North);
			hash.Add(East);
			hash.Add(Down);
			hash.Add(Roll);
			hash.Add(Pitch);
			hash.Add(Heading);
			hash.Add(Surge);
			hash.Add(Sway);
			hash.Add(Heave);
			hash.Add(RollRate);
			hash.Add(PitchRate);
			hash.Add(YawRate);
			return hash.ToHashCode();
		}

		public override string ToString() =>
			$"t={Time:F2}s N={North:F2} E={East:F2} hdg={Angles.ToDegrees(Heading):F1}° u={Surge:F2} v={Sway:F2} r={YawRate:F4}";

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
