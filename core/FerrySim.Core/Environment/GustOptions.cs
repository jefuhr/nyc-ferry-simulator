using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>Opt-in synthetic gust timing, not a reconstruction of measured turbulence. Defaults to mean wind.</summary>
	public sealed class GustOptions
	{
		public GustOptions(bool enabled = false, uint seed = 0, double intervalSeconds = 5)
		{
			if (!(intervalSeconds > 0) || double.IsInfinity(intervalSeconds)) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
			Enabled = enabled; Seed = seed; IntervalSeconds = intervalSeconds;
		}

		public bool Enabled { get; }
		public uint Seed { get; }
		public double IntervalSeconds { get; }

		/// <summary>
		/// Continuous, bounded, same-direction excursions between the mean and reported gust ceiling.
		/// Hashes absolute pack-time knots; independent of sample order, position and physics substeps.
		/// Calm wind has no known direction and stays calm. A missing/low ceiling leaves the mean unchanged.
		/// </summary>
		public HorizontalVector Apply(HorizontalVector mean, double ceiling, double packTime)
		{
			var speed = mean.Magnitude;
			if (!Enabled || speed == 0 || !(ceiling > speed) || double.IsInfinity(ceiling)) return mean;
			var phase = packTime / IntervalSeconds;
			var floor = Math.Floor(phase);
			var index = unchecked((long)floor);
			var fraction = phase - floor;
			var smooth = fraction * fraction * (3 - 2 * fraction);
			var a = Knot(index);
			var amount = a + (Knot(unchecked(index + 1)) - a) * smooth;
			return mean * ((speed + (ceiling - speed) * amount) / speed);
		}

		private double Knot(long index)
		{
			// Fixed integer mixing, never System.Random/GetHashCode (which may vary by runtime).
			unchecked
			{
				var value = (ulong)index + ((ulong)Seed << 32) + 0x9e3779b97f4a7c15UL;
				value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
				value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
				value ^= value >> 31;
				return (value >> 11) * (1.0 / 9007199254740992.0);
			}
		}
	}
}
