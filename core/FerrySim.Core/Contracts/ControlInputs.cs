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
			: this(portLever, starboardLever, helm, 0.0)
		{
		}

		public ControlInputs(double portLever, double starboardLever, double helm, double jog)
		{
			PortLever = Sanitize(portLever);
			StarboardLever = Sanitize(starboardLever);
			Helm = Sanitize(helm);
			Jog = Sanitize(jog);
		}

		/// <summary>Port engine lever in [-1, 1]: -1 full astern, 0 neutral, +1 full ahead.</summary>
		public double PortLever { get; }

		/// <summary>Starboard engine lever in [-1, 1]: -1 full astern, 0 neutral, +1 full ahead.</summary>
		public double StarboardLever { get; }

		/// <summary>
		/// Tiller order in [-1, 1]: +1 hard over to starboard (bow swings to starboard when going ahead),
		/// -1 hard over to port. How this maps to rudder angle is a control-model detail; with follow-up
		/// steering, which the shipped vessels use, the gear drives the rudder to helm × hard over.
		/// </summary>
		public double Helm { get; }

		/// <summary>
		/// Jog (non-follow-up) lever demand in [-1, 1]: positive drives the rudder to starboard at the
		/// steering rate, negative to port, and 0 leaves the steering to <see cref="Helm"/>. Both controls
		/// are live at once on the real boats (crew-reported); while the jog demand is non-zero it
		/// overrides the tiller. A run whose jog demand is zero at every step behaves exactly as it did
		/// before this input existed.
		/// </summary>
		public double Jog { get; }

		/// <summary>Both levers in neutral, the helm amidships and the jog lever released.</summary>
		public static ControlInputs Neutral => default;

		public bool Equals(ControlInputs other) =>
			PortLever.Equals(other.PortLever) && StarboardLever.Equals(other.StarboardLever) && Helm.Equals(other.Helm) && Jog.Equals(other.Jog);

		public override bool Equals(object obj) => obj is ControlInputs other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(PortLever, StarboardLever, Helm, Jog);

		public override string ToString() =>
			Jog == 0.0
				? $"port={PortLever:F2} stbd={StarboardLever:F2} helm={Helm:F2}"
				: $"port={PortLever:F2} stbd={StarboardLever:F2} helm={Helm:F2} jog={Jog:F2}";

		// Devices can glitch; a NaN axis must never reach the physics.
		private static double Sanitize(double value) => double.IsNaN(value) ? 0.0 : Math.Clamp(value, -1.0, 1.0);
	}
}
