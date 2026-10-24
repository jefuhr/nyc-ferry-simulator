using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>Rates of the planar rigid-body state at one Runge-Kutta stage.</summary>
	public interface IPlanarRates
	{
		/// <summary>
		/// Rates at a stage pose (north, east, heading) and velocity (surge, sway through the water, yaw rate):
		/// position rates over the ground (world north and east, m/s), surge and sway accelerations (m/s²) and
		/// yaw acceleration (rad/s²). <paramref name="firstStage"/> is true for the stage at the start of the step.
		/// </summary>
		void Rates(double north, double east, double heading, double surge, double sway, double yawRate, bool firstStage,
			out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration);
	}

	/// <summary>
	/// One classical fourth-order Runge-Kutta step of the planar rigid body (north, east, heading; surge,
	/// sway, yaw rate). The heading's rate is the yaw rate, so each stage's heading advances with that stage's
	/// yaw rate. Generic over a struct so the call is direct and allocation-free.
	/// </summary>
	public static class PlanarRungeKutta
	{
		/// <summary>Advances the state by <paramref name="h"/> seconds. Returns the first stage's surge acceleration.</summary>
		public static double Step<TRates>(TRates rates, double h, ref double north, ref double east, ref double heading,
			ref double surge, ref double sway, ref double yawRate) where TRates : IPlanarRates
		{
			var n0 = north;
			var e0 = east;
			var psi = heading;
			var u = surge;
			var v = sway;
			var r = yawRate;

			rates.Rates(n0, e0, psi, u, v, r, true, out var n1, out var e1, out var u1, out var v1, out var a1);
			var r2 = r + 0.5 * h * a1;
			rates.Rates(n0 + 0.5 * h * n1, e0 + 0.5 * h * e1, psi + 0.5 * h * r, u + 0.5 * h * u1, v + 0.5 * h * v1, r2, false,
				out var n2, out var e2, out var u2, out var v2, out var a2);
			var r3 = r + 0.5 * h * a2;
			rates.Rates(n0 + 0.5 * h * n2, e0 + 0.5 * h * e2, psi + 0.5 * h * r2, u + 0.5 * h * u2, v + 0.5 * h * v2, r3, false,
				out var n3, out var e3, out var u3, out var v3, out var a3);
			var r4 = r + h * a3;
			rates.Rates(n0 + h * n3, e0 + h * e3, psi + h * r3, u + h * u3, v + h * v3, r4, false,
				out var n4, out var e4, out var u4, out var v4, out var a4);

			var sixth = h / 6.0;
			north = n0 + sixth * (n1 + 2.0 * n2 + 2.0 * n3 + n4);
			east = e0 + sixth * (e1 + 2.0 * e2 + 2.0 * e3 + e4);
			heading = Angles.WrapTwoPi(psi + sixth * (r + 2.0 * r2 + 2.0 * r3 + r4));
			surge = u + sixth * (u1 + 2.0 * u2 + 2.0 * u3 + u4);
			sway = v + sixth * (v1 + 2.0 * v2 + 2.0 * v3 + v4);
			yawRate = r + sixth * (a1 + 2.0 * a2 + 2.0 * a3 + a4);
			return u1;
		}
	}
}
