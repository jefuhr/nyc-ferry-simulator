using System;
using System.Collections.Generic;

namespace FerrySim.Core.World
{
	/// <summary>Shared-node NAVD88-zero ellipsoidal height lattice. Masked cells and outside points are never extrapolated.</summary>
	public sealed class HarborGeoidLattice
	{
		private readonly double[] _values;
		private readonly byte[] _mask;

		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborGeoidLattice(double north, double east, double cellSize, int rows, int columns, IReadOnlyList<double> values, IReadOnlyList<byte> activeCells)
		{
			if (!Finite(north) || !Finite(east) || !Finite(cellSize) || cellSize <= 0 || rows < 2 || columns < 2 || values.Count != (long)rows * columns || activeCells.Count != (long)(rows - 1) * (columns - 1))
			{
				throw new ArgumentException("Invalid geoid lattice shape or origin.");
			}

			_values = new double[values.Count];
			_mask = new byte[activeCells.Count];
			for (var i = 0; i < values.Count; i++)
			{
				if (!Finite(values[i]))
				{
					throw new ArgumentException("Nonfinite geoid node.");
				}

				_values[i] = values[i];
			}

			for (var i = 0; i < activeCells.Count; i++)
			{
				if (activeCells[i] > 1)
				{
					throw new ArgumentException("Geoid mask must contain only 0 or 1.");
				}

				_mask[i] = activeCells[i];
			}

			OriginNorth = north;
			OriginEast = east;
			CellSize = cellSize;
			Rows = rows;
			Columns = columns;
		}

		/// <summary>North coordinate of node (0, 0), metres.</summary>
		public double OriginNorth { get; }

		/// <summary>East coordinate of node (0, 0), metres.</summary>
		public double OriginEast { get; }

		/// <summary>Node spacing in metres.</summary>
		public double CellSize { get; }

		/// <summary>Number of shared node rows, increasing northward.</summary>
		public int Rows { get; }

		/// <summary>Number of shared node columns, increasing eastward.</summary>
		public int Columns { get; }

		/// <summary>Whether a cell is in the active mask; false outside its bounds.</summary>
		public bool CellActive(int row, int column) => row >= 0 && row < Rows - 1 && column >= 0 && column < Columns - 1 && _mask[row * (Columns - 1) + column] == 1;

		/// <summary>Ellipsoidal height of NAVD88 zero at a shared node, metres.</summary>
		public double Node(int row, int column) => _values[row * Columns + column];

		/// <summary>Samples the shared lattice without allocation or extrapolation; false through masked cells or outside coverage.</summary>
		public bool TrySample(double north, double east, out double value)
		{
			value = 0;
			var r = (north - OriginNorth) / CellSize;
			var c = (east - OriginEast) / CellSize;
			if (!Finite(r + c) || r < 0 || c < 0 || r > Rows - 1 || c > Columns - 1)
			{
				return false;
			}

			var ri = Math.Min((int)Math.Floor(r), Rows - 2);
			var ci = Math.Min((int)Math.Floor(c), Columns - 2);
			// Same owner order as ferrydata.harbor_runtime.geometry.sample_geoid, including exact edges/corners.
			for (var a = ri; a >= ri - 1; a--)
			{
				for (var b = ci; b >= ci - 1; b--)
				{
					if (!CellActive(a, b) || r < a || r > a + 1 || c < b || c > b + 1)
					{
						continue;
					}

					var u = r - a;
					var v = c - b;
					var index = a * Columns + b;
					value = (_values[index] * (1 - v) + _values[index + 1] * v) * (1 - u) + (_values[index + Columns] * (1 - v) + _values[index + Columns + 1] * v) * u;
					return true;
				}
			}

			return false;
		}

		private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
