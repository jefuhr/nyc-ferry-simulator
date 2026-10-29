using System;
using FerrySim.Core.World;

namespace FerrySim.World
{
	/// <summary>The harbor's canonical NAVD88-zero ellipsoidal height. It never extrapolates and never supplies depth.</summary>
	public sealed class HarborDatum
	{

		/// <summary>Uses the immutable canonical lattice from a verified harbor runtime pack.</summary>
		public HarborDatum(HarborGeoidLattice lattice)
		{
			Lattice = lattice ?? throw new ArgumentNullException(nameof(lattice));
		}

		/// <summary>The shared canonical datum lattice; no depth data is retained.</summary>
		public HarborGeoidLattice Lattice { get; }

		/// <summary>Samples the datum in world north/east metres, returning false outside active cells.</summary>
		public bool TrySample(double north, double east, out double height) => Lattice.TrySample(north, east, out height);

		/// <summary>Returns the NAVD88-zero ellipsoidal height in metres, refusing uncovered positions.</summary>
		public double Height(double north, double east)
		{
			if (!TrySample(north, east, out var height))
			{
				throw new InvalidOperationException($"Harbor datum is not covered at north {north:R}, east {east:R}; no extrapolation is permitted.");
			}

			return height;
		}
	}
}
