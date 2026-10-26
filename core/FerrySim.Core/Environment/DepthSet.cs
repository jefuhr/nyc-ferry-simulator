using System;
using System.Collections.Generic;
using System.Globalization;

namespace FerrySim.Core.Env
{
	/// <summary>What a depth grid says about a point.</summary>
	public enum BedSample
	{
		/// <summary>The point is outside the grid's cell-centre lattice.</summary>
		Outside = 0,

		/// <summary>The point is inside, but one of the four surrounding cells has no bed elevation (NaN).</summary>
		Unknown = 1,

		/// <summary>The bed elevation is the bilinear blend of the four surrounding cells.</summary>
		Known = 2,
	}

	/// <summary>
	/// One bed-elevation grid in the world frame: NAVD88 metres, positive up, so water is negative. Cell (r, c) has its
	/// centre at north = <see cref="OriginNorth"/> + r·<see cref="CellSize"/> and east = <see cref="OriginEast"/> +
	/// c·<see cref="CellSize"/>; rows grow northward and columns eastward. NaN marks an unknown bed.
	/// </summary>
	public sealed class DepthGrid
	{
		private readonly float[] _bed;

		public DepthGrid(string id, double originNorth, double originEast, double cellSize, int rows, int columns, IReadOnlyList<float> bedElevation)
		{
			if (string.IsNullOrEmpty(id))
			{
				throw new ArgumentException("A depth grid needs an id.");
			}

			if (!SeriesTime.IsFinite(originNorth) || !SeriesTime.IsFinite(originEast) || !SeriesTime.IsFinite(cellSize) || !(cellSize > 0.0))
			{
				throw new ArgumentException($"Depth grid {id}: the origin must be finite and the cell size positive.");
			}

			if (rows < 2 || columns < 2)
			{
				throw new ArgumentException($"Depth grid {id}: needs at least two rows and two columns to interpolate, found {rows} by {columns}.");
			}

			if (bedElevation == null || bedElevation.Count != (long)rows * columns)
			{
				throw new ArgumentException($"Depth grid {id}: needs rows × columns = {(long)rows * columns} values.");
			}

			_bed = new float[bedElevation.Count];
			for (var i = 0; i < _bed.Length; i++)
			{
				var value = bedElevation[i];
				if (float.IsInfinity(value))
				{
					throw new ArgumentException($"Depth grid {id}: value {i} is infinite; unknown bed is NaN.");
				}

				_bed[i] = value;
			}

			Id = id;
			OriginNorth = originNorth;
			OriginEast = originEast;
			CellSize = cellSize;
			Rows = rows;
			Columns = columns;
		}

		public string Id { get; }

		/// <summary>North of the centre of cell (0, 0), m.</summary>
		public double OriginNorth { get; }

		/// <summary>East of the centre of cell (0, 0), m.</summary>
		public double OriginEast { get; }

		/// <summary>Spacing of the cell centres, m.</summary>
		public double CellSize { get; }

		public int Rows { get; }

		public int Columns { get; }

		/// <summary>North of the last row of centres, m.</summary>
		public double EndNorth => OriginNorth + (Rows - 1) * CellSize;

		/// <summary>East of the last column of centres, m.</summary>
		public double EndEast => OriginEast + (Columns - 1) * CellSize;

		/// <summary>Bed elevation of cell (row, column), NAVD88 m, positive up; NaN when unknown.</summary>
		public float BedElevation(int row, int column) => _bed[row * Columns + column];

		/// <summary>True when the point lies between the first and last centres, inclusive: the grid samples it.</summary>
		public bool Contains(double north, double east) => Inside((north - OriginNorth) / CellSize, (east - OriginEast) / CellSize);

		/// <summary>
		/// The bed elevation at a point, bilinear between the four surrounding cell centres. Points between the first and
		/// last centres, inclusive, are inside the grid.
		/// </summary>
		public BedSample Sample(double north, double east, out double bedElevation)
		{
			var row = (north - OriginNorth) / CellSize;
			var column = (east - OriginEast) / CellSize;
			if (!Inside(row, column))
			{
				bedElevation = 0.0;
				return BedSample.Outside;
			}

			var r = Math.Min((int)row, Rows - 2);
			var c = Math.Min((int)column, Columns - 2);
			var index = r * Columns + c;
			double southWest = _bed[index];
			double southEast = _bed[index + 1];
			double northWest = _bed[index + Columns];
			double northEast = _bed[index + Columns + 1];
			if (double.IsNaN(southWest) || double.IsNaN(southEast) || double.IsNaN(northWest) || double.IsNaN(northEast))
			{
				bedElevation = 0.0;
				return BedSample.Unknown;
			}

			var across = column - c;
			var up = row - r;
			var lower = southWest + across * (southEast - southWest);
			var upper = northWest + across * (northEast - northWest);
			bedElevation = lower + up * (upper - lower);
			return BedSample.Known;
		}

		internal float[] Values => _bed;

		private bool Inside(double row, double column) => row >= 0.0 && row <= Rows - 1 && column >= 0.0 && column <= Columns - 1;
	}

	/// <summary>One input the depth set was built from, as its manifest records it.</summary>
	public sealed class DepthSource
	{
		public DepthSource(string name, string sha256, string note)
		{
			Name = name ?? string.Empty;
			Sha256 = sha256 ?? string.Empty;
			Note = note ?? string.Empty;
		}

		public string Name { get; }

		public string Sha256 { get; }

		public string Note { get; }
	}

	/// <summary>
	/// The bed-elevation grids of a region (an environment pack's depth set), independent of the day. A point belongs to
	/// the first grid whose cell-centre lattice contains it. Outside every grid, or where the bed is unknown, the depth is
	/// <see cref="OutsideDepth"/>.
	/// <para>
	/// Grids may meet (overlap, or share an edge or a corner of their lattices) only on one lattice and only where every
	/// shared sample is identical (ferrysim-env-depth/1 as revised 2026-10-25). Two grids meet when their lattices, first
	/// to last centre inclusive, have a point in common. Grids that meet must have the same cell size and origins a whole
	/// number of cells apart, so every centre either holds inside both is a centre of both: a shared sample. Each shared
	/// sample must be the same float32 value in both grids, or unknown (NaN) in both. So the bed is continuous across a
	/// seam, and the first grid decides only the arithmetic and, where its cell has an unknown corner of zero weight,
	/// that the bed is unknown. Grids that do not meet are independent.
	/// </para>
	/// </summary>
	public sealed class DepthSet
	{
		private readonly DepthGrid[] _grids;

		public DepthSet(string region, string datum, double outsideDepth, IReadOnlyList<DepthGrid> grids, IReadOnlyList<DepthSource> sources, string digest)
		{
			if (!SeriesTime.IsFinite(outsideDepth) || !(outsideDepth > 0.0))
			{
				throw new ArgumentException("The depth set's outside depth must be a positive number.");
			}

			if (grids == null || grids.Count == 0)
			{
				throw new ArgumentException("A depth set needs at least one grid.");
			}

			var copy = new DepthGrid[grids.Count];
			for (var i = 0; i < copy.Length; i++)
			{
				copy[i] = grids[i] ?? throw new ArgumentException($"Depth grid {i} is missing.");
				for (var j = 0; j < i; j++)
				{
					if (copy[j].Id == copy[i].Id)
					{
						throw new ArgumentException($"Depth grid id {copy[i].Id} appears twice.");
					}

					CheckShared(copy[j], copy[i]);
				}
			}

			Region = region ?? string.Empty;
			Datum = datum ?? string.Empty;
			OutsideDepth = outsideDepth;
			_grids = copy;
			Sources = sources == null ? Array.Empty<DepthSource>() : new List<DepthSource>(sources).ToArray();
			Digest = digest ?? string.Empty;
		}

		public string Region { get; }

		/// <summary>Vertical datum of the bed elevations; NAVD88 in v1.</summary>
		public string Datum { get; }

		/// <summary>Depth returned outside every grid and where the bed is unknown, m: the documented conservative value.</summary>
		public double OutsideDepth { get; }

		public IReadOnlyList<DepthGrid> Grids => _grids;

		public IReadOnlyList<DepthSource> Sources { get; }

		/// <summary>The depth set's digest (ferrysim-env/1), hex; empty for a set built in memory.</summary>
		public string Digest { get; }

		/// <summary>The index in <see cref="Grids"/> of the first grid that contains the point, which <see cref="Sample"/> reads; −1 outside every grid.</summary>
		public int GridAt(double north, double east)
		{
			for (var i = 0; i < _grids.Length; i++)
			{
				if (_grids[i].Contains(north, east))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>The bed elevation at a point from the first grid that contains it.</summary>
		public BedSample Sample(double north, double east, out double bedElevation)
		{
			for (var i = 0; i < _grids.Length; i++)
			{
				var sample = _grids[i].Sample(north, east, out bedElevation);
				if (sample != BedSample.Outside)
				{
					return sample;
				}
			}

			bedElevation = 0.0;
			return BedSample.Outside;
		}

		// Grids that meet must lie on one lattice and agree on every centre both hold.
		private static void CheckShared(DepthGrid a, DepthGrid b)
		{
			if (a.CellSize == b.CellSize)
			{
				// b's centre (r, c) is a's centre (r + rows, c + columns) when the offsets are whole numbers of cells.
				var rows = (b.OriginNorth - a.OriginNorth) / a.CellSize;
				var columns = (b.OriginEast - a.OriginEast) / a.CellSize;
				if (rows == Math.Floor(rows) && columns == Math.Floor(columns))
				{
					var firstRow = Math.Max(0.0, rows);
					var lastRow = Math.Min(a.Rows - 1.0, rows + b.Rows - 1.0);
					var firstColumn = Math.Max(0.0, columns);
					var lastColumn = Math.Min(a.Columns - 1.0, columns + b.Columns - 1.0);
					if (firstRow > lastRow || firstColumn > lastColumn)
					{
						return;
					}

					// They meet, so both offsets are within the grids' sizes.
					var dr = (int)rows;
					var dc = (int)columns;
					for (var r = (int)firstRow; r <= (int)lastRow; r++)
					{
						for (var c = (int)firstColumn; c <= (int)lastColumn; c++)
						{
							var first = a.Values[r * a.Columns + c];
							var second = b.Values[(r - dr) * b.Columns + c - dc];
							if (!(first == second || (float.IsNaN(first) && float.IsNaN(second))))
							{
								throw new ArgumentException(
									$"Depth grids {a.Id} and {b.Id} share the sample at north {Text(a.OriginNorth + r * a.CellSize)}, east {Text(a.OriginEast + c * a.CellSize)}, " +
									$"but hold {Text(first)} and {Text(second)}: grids may meet only where every shared sample is identical.");
							}
						}
					}

					return;
				}
			}

			if (Meet(a, b))
			{
				throw new ArgumentException(a.CellSize != b.CellSize
					? $"Depth grids {a.Id} and {b.Id} meet with different cell sizes ({Text(a.CellSize)} and {Text(b.CellSize)} m): grids that meet must lie on one lattice."
					: $"Depth grids {a.Id} and {b.Id} meet with origins that are not a whole number of {Text(a.CellSize)} m cells apart: grids that meet must lie on one lattice.");
			}
		}

		// Lattices, first to last centre inclusive, with a point in common: an overlap, a shared edge or a shared corner.
		private static bool Meet(DepthGrid a, DepthGrid b) =>
			a.OriginNorth <= b.EndNorth && b.OriginNorth <= a.EndNorth && a.OriginEast <= b.EndEast && b.OriginEast <= a.EndEast;

		private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

		private static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
	}
}
