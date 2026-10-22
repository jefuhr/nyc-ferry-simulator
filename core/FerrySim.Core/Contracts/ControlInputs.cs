using System;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// What the operator is doing with the wheelhouse controls, normalized and device-independent.
	/// Input devices only produce these values; detents, clutch engagement, shift timing and
	/// steering-gear behavior are modelled inside the core's control model.
	/// </summary>
	public readonly struct ControlInputs : IEquatable<ControlInputs>
	{
		public ControlInputs(double portLever, double starboardLever, double helm)
		{
			PortLever = Sanitize(portLever);
			StarboardLever = Sanitize(starboardLever);
			Helm = Sanitize(helm);
		}

		/// <summary>Port engine lever in [-1, 1]: -1 full astern, 0 neutral, +1 full ahead.</summary>
		public double PortLever { get; }

		/// <summary>Starboard engine lever in [-1, 1]: -1 full astern, 0 neutral, +1 full ahead.</summary>
		public double StarboardLever { get; }

		/// <summary>
		/// Helm order in [-1, 1]: +1 hard over to starboard (bow swings to starboard when going ahead),
		/// -1 hard over to port. How this maps to rudder angle (follow-up or non-follow-up steering)
		/// is a control-model detail to confirm with crew.
		/// </summary>
		public double Helm { get; }

		/// <summary>Both levers in neutral and the helm amidships.</summary>
		public static ControlInputs Neutral => default;

		public bool Equals(ControlInputs other) =>
			PortLever.Equals(other.PortLever) && StarboardLever.Equals(other.StarboardLever) && Helm.Equals(other.Helm);

		public override bool Equals(object obj) => obj is ControlInputs other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(PortLever, StarboardLever, Helm);

		public override string ToString() => $"port={PortLever:F2} stbd={StarboardLever:F2} helm={Helm:F2}";

		// Devices can glitch; a NaN axis must never reach the physics.
		private static double Sanitize(double value) => double.IsNaN(value) ? 0.0 : Math.Clamp(value, -1.0, 1.0);
	}
}
