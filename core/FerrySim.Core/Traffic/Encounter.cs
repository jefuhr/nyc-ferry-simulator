using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Traffic
{
	/// <summary>
	/// The closest point of approach between the learner's vessel and another, from positions and velocities over the ground,
	/// both held constant from now, as an ARPA computes it for point targets (the vessels' reference points). SI throughout.
	/// <list type="bullet">
	/// <item><see cref="Tcpa"/> = −(r·w)/|w|², with r the other vessel's position less the learner's and w its velocity less the
	/// learner's: positive while the two close, negative once the closest point is past (opening).</item>
	/// <item><see cref="Cpa"/> = |r + w·TCPA|: the distance at that time, past or future, never negative.</item>
	/// <item>With no relative motion (|w| below <see cref="StillSpeed"/>) the range stays as it is: TCPA 0 and CPA the range.</item>
	/// <item><see cref="Bearing"/> is the true bearing of the other vessel from the learner, clockwise from north in [0, 2π).</item>
	/// </list>
	/// </summary>
	public readonly struct Encounter
	{
		/// <summary>Relative speeds below this, m/s, count as no relative motion.</summary>
		public const double StillSpeed = 1e-6;

		public Encounter(double range, double bearing, double cpa, double tcpa, double relativeSpeed)
		{
			Range = range;
			Bearing = bearing;
			Cpa = cpa;
			Tcpa = tcpa;
			RelativeSpeed = relativeSpeed;
		}

		/// <summary>Distance between the reference points now, m.</summary>
		public double Range { get; }

		/// <summary>True bearing of the other vessel from the learner, rad.</summary>
		public double Bearing { get; }

		/// <summary>Distance at the closest point of approach, m.</summary>
		public double Cpa { get; }

		/// <summary>Time to the closest point of approach, s: positive ahead, negative once past.</summary>
		public double Tcpa { get; }

		/// <summary>Speed of the other vessel relative to the learner, m/s.</summary>
		public double RelativeSpeed { get; }

		/// <summary>True while the range is shrinking: the closest point is still ahead.</summary>
		public bool Closing => Tcpa > 0.0;

		/// <summary>The encounter between the learner (position and velocity over the ground) and another vessel.</summary>
		public static Encounter Between(double ownNorth, double ownEast, double ownVelocityNorth, double ownVelocityEast,
			double otherNorth, double otherEast, double otherVelocityNorth, double otherVelocityEast)
		{
			var rn = otherNorth - ownNorth;
			var re = otherEast - ownEast;
			var wn = otherVelocityNorth - ownVelocityNorth;
			var we = otherVelocityEast - ownVelocityEast;
			var range = Math.Sqrt(rn * rn + re * re);
			var bearing = range > 0.0 ? Angles.WrapTwoPi(Math.Atan2(re, rn)) : 0.0;
			var w2 = wn * wn + we * we;
			var speed = Math.Sqrt(w2);
			if (!(speed >= StillSpeed))
			{
				return new Encounter(range, bearing, range, 0.0, speed);
			}

			var tcpa = -(rn * wn + re * we) / w2;
			var cn = rn + wn * tcpa;
			var ce = re + we * tcpa;
			return new Encounter(range, bearing, Math.Sqrt(cn * cn + ce * ce), tcpa, speed);
		}
	}
}
