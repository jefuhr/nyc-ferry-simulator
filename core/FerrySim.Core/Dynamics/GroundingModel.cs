using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>The bed under the hull at the end of the last step. Forces in N; the moment is about the reference point, N·m.</summary>
	public readonly struct GroundingState
	{
		public GroundingState(double penetration, double bedLoad, in ContactLoad load)
		{
			Penetration = penetration;
			BedLoad = bedLoad;
			Load = load;
		}

		/// <summary>True when the bed is above the keel: depth under keel below zero (<see cref="VesselDynamicsSimulation.IsAground"/>).</summary>
		public bool Grounded => Penetration > 0.0;

		/// <summary>How far the bed is above the keel, m; zero when afloat.</summary>
		public double Penetration { get; }

		/// <summary>Weight the bed carries, N (vertical).</summary>
		public double BedLoad { get; }

		/// <summary>The bed's horizontal friction on the hull: body and world components and the yaw moment.</summary>
		public ContactLoad Load { get; }

		/// <summary>Magnitude of the bed's horizontal force, N.</summary>
		public double Force => Load.Magnitude;
	}

	/// <summary>
	/// The bed's hold on a grounded hull. When the bed is above the keel by a penetration p, it lifts the boat and carries
	/// the buoyancy it loses: for wall-sided demihulls of hull draft T, W = m g min(p / T, 1). That load rests evenly on
	/// both keel lines, sampled at the hull's cross-flow strips, and each point resists its own sliding over the bed with
	/// Coulomb friction μ W_i, regularised through zero slip as f = −μ W_i v/√(|v|² + s₀²). Friction only ever resists
	/// motion, so the bed stops a boat and holds it but never throws it back.
	/// <para>
	/// The friction is stiff near zero slip: it acts like a damper μW/s₀. <see cref="SlipSpeed"/> raises s₀ so that an
	/// explicit Runge-Kutta step stays well inside its stability limit, as the fender friction does.
	/// </para>
	/// </summary>
	public sealed class GroundingModel
	{
		/// <summary>Share of the explicit stability limit the regularised friction may use (as for fender friction).</summary>
		private const double FrictionStability = 0.5;

		private readonly double[] _positions;
		private readonly double _keelOffset;
		private readonly double _weight;
		private readonly double _hullDraft;
		private readonly double _friction;
		private readonly double _effectiveMass;

		/// <param name="parameters">The vessel.</param>
		/// <param name="hull">Its hull model, for the strips and the demihull separation.</param>
		/// <param name="effectiveMass">A conservative mass for a push at the farthest keel point, kg.</param>
		public GroundingModel(VesselParameters parameters, HullModel hull, double effectiveMass)
		{
			var particulars = Required.Section(parameters.Particulars, "particulars");
			var mass = Required.Positive(particulars.Displacement, "particulars.displacement");
			_weight = mass * PhysicalConstants.Gravity;
			_hullDraft = hull.Draft;
			_keelOffset = 0.5 * hull.DemihullSeparation;
			_friction = PhysicalConstants.BedFrictionCoefficient;
			_effectiveMass = effectiveMass;
			_positions = new double[hull.StripCount];
			for (var i = 0; i < _positions.Length; i++)
			{
				_positions[i] = hull.StripPosition(i);
			}
		}

		/// <summary>Distance of each keel line from the centreline, m.</summary>
		public double KeelOffset => _keelOffset;

		/// <summary>Weight the bed carries at a penetration (m of bed above the keel), N; zero when afloat.</summary>
		public double BedLoad(double penetration) => penetration > 0.0 ? _weight * Math.Min(penetration / _hullDraft, 1.0) : 0.0;

		/// <summary>
		/// Slip speed regularising the friction through zero slip in an explicit step of length <paramref name="h"/>: the
		/// fender friction's floor, raised so that the friction's damping μW/s₀ stays within half the step's limit.
		/// </summary>
		public double SlipSpeed(double bedLoad, double h) =>
			Math.Max(ContactModel.MinimumSlipSpeed, _friction * bedLoad * h / (FrictionStability * _effectiveMass));

		/// <summary>
		/// Bed friction on the hull at the reference point, body axes (N, N·m), for its velocity over the ground (surge and
		/// sway, m/s) and yaw rate (rad/s).
		/// </summary>
		public void Forces(double surge, double sway, double yawRate, double bedLoad, double slipSpeed, out double x, out double y, out double n)
		{
			x = 0.0;
			y = 0.0;
			n = 0.0;
			var share = _friction * bedLoad / (2 * _positions.Length);
			var slipSquared = slipSpeed * slipSpeed;
			for (var side = -1; side <= 1; side += 2)
			{
				var offset = side * _keelOffset;
				var along = surge - yawRate * offset;
				for (var i = 0; i < _positions.Length; i++)
				{
					var position = _positions[i];
					var across = sway + yawRate * position;
					var scale = -share / Math.Sqrt(along * along + across * across + slipSquared);
					var forceX = scale * along;
					var forceY = scale * across;
					x += forceX;
					y += forceY;
					n += position * forceY - offset * forceX;
				}
			}
		}
	}
}
