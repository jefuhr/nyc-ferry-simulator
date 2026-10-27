using System;
using System.Collections.Generic;

namespace FerrySim.Core.World
{
	/// <summary>Immutable north-sorted bounding-box index for chart/camera/radar broad-phase queries, never contacts.</summary>
	public sealed class HarborQueryIndex
	{
		private readonly HarborFootprint[] _sorted;

		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborQueryIndex(IReadOnlyList<HarborFootprint> footprints)
		{
			_sorted = new HarborFootprint[footprints.Count];
			for (var i = 0; i < _sorted.Length; i++)
			{
				_sorted[i] = footprints[i];
			}

			Array.Sort(_sorted, (a, b) =>
			{
				var order = a.Geometry.MinimumNorth.CompareTo(b.Geometry.MinimumNorth);
				return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id);
			});
		}

		/// <summary>Returns deterministic bounding-box candidates into caller storage; reports truncation explicitly.</summary>
		public int Query(double north, double east, double radius, HarborFootprint[] buffer, out bool saturated)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException(nameof(buffer));
			}

			if (double.IsNaN(north) || double.IsInfinity(north) || double.IsNaN(east) || double.IsInfinity(east) || double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0)
			{
				throw new ArgumentException("Finite query coordinates and a nonnegative radius are required.");
			}

			var count = 0;
			saturated = false;
			for (var i = 0; i < _sorted.Length; i++)
			{
				var item = _sorted[i];
				var g = item.Geometry;
				if (g.MinimumNorth > north + radius)
				{
					break;
				}

				if (g.MaximumNorth < north - radius || g.MinimumEast > east + radius || g.MaximumEast < east - radius)
				{
					continue;
				}

				if (count == buffer.Length)
				{
					saturated = true;
					break;
				}

				buffer[count++] = item;
			}

			return count;
		}
	}
}
