using System;
using System.Collections.Generic;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// One grid of a current-field basis (ferrysim-env-field/1), on the depth grids' conventions: cell (r, c) has its
	/// centre at north = <see cref="OriginNorth"/> + r·<see cref="CellSize"/> and east = <see cref="OriginEast"/> +
	/// c·<see cref="CellSize"/>, rows grow northward. Each cell holds 4 + <see cref="Slots"/> values: the flood-oriented
	/// channel axis (a unit vector, north and east), the along and cross factors in [0, 1], then the weight of each of its
	/// station slots; and one station index per slot. A dry cell has every slot empty and all values zero.
	/// </summary>
	public sealed class CurrentFieldGrid
	{
		/// <summary>Values per cell before the weights: axis north, axis east, along factor, cross factor.</summary>
		public const int Leading = 4;

		/// <summary>Most station slots a cell may have.</summary>
		public const int MaximumSlots = 8;

		/// <summary>The slot value that names no station.</summary>
		public const byte EmptySlot = 255;

		/// <summary>How far a water cell's axis may be from unit length (float32 storage); the pipeline checks the same.</summary>
		public const double UnitAxisTolerance = 1e-5;

		/// <summary>How far a water cell's weights may be from summing to 1 (float32 storage); the pipeline checks the same.</summary>
		public const double WeightSumTolerance = 1e-5;

		private readonly float[] _values;
		private readonly byte[] _slots;

		/// <param name="slots">Station slots per cell, 1 to <see cref="MaximumSlots"/>.</param>
		/// <param name="values">rows × columns × (4 + slots) values, row 0 south, row-major.</param>
		/// <param name="stationIndices">rows × columns × slots station indices.</param>
		/// <param name="stationCount">Stations of the basis; a slot is a station index below it, or <see cref="EmptySlot"/>.</param>
		public CurrentFieldGrid(string id, double originNorth, double originEast, double cellSize, int rows, int columns, int slots,
			IReadOnlyList<float> values, IReadOnlyList<byte> stationIndices, int stationCount)
		{
			if (string.IsNullOrEmpty(id))
			{
				throw new ArgumentException("A current-field grid needs an id.");
			}

			if (!SeriesTime.IsFinite(originNorth) || !SeriesTime.IsFinite(originEast) || !SeriesTime.IsFinite(cellSize) || !(cellSize > 0.0))
			{
				throw new ArgumentException($"Current-field grid {id}: the origin must be finite and the cell size positive.");
			}

			if (rows < 2 || columns < 2)
			{
				throw new ArgumentException($"Current-field grid {id}: needs at least two rows and two columns to interpolate, found {rows} by {columns}.");
			}

			if (slots < 1 || slots > MaximumSlots)
			{
				throw new ArgumentException($"Current-field grid {id}: slots must be 1 to {MaximumSlots}, found {slots}.");
			}

			var cells = (long)rows * columns;
			var perCell = Leading + slots;
			if (values == null || values.Count != cells * perCell || stationIndices == null || stationIndices.Count != cells * slots)
			{
				throw new ArgumentException($"Current-field grid {id}: needs {cells * perCell} values and {cells * slots} slots for {rows} by {columns} cells of {slots} slots.");
			}

			_values = new float[values.Count];
			_slots = new byte[stationIndices.Count];
			for (var cell = 0; cell < cells; cell++)
			{
				var v = cell * perCell;
				var s = cell * slots;
				var wet = false;
				var sum = 0.0;
				for (var j = 0; j < perCell; j++)
				{
					var value = values[v + j];
					if (float.IsNaN(value) || float.IsInfinity(value))
					{
						throw new ArgumentException($"Current-field grid {id}: value {j} of cell {cell} is not finite.");
					}

					_values[v + j] = value;
				}

				for (var j = 0; j < slots; j++)
				{
					var slot = stationIndices[s + j];
					var weight = _values[v + Leading + j];
					_slots[s + j] = slot;
					if (slot == EmptySlot)
					{
						if (weight != 0.0f)
						{
							throw new ArgumentException($"Current-field grid {id}: cell {cell} has weight {weight} in an empty slot.");
						}

						continue;
					}

					if (slot >= stationCount)
					{
						throw new ArgumentException($"Current-field grid {id}: cell {cell} names station slot {slot}; the basis has {stationCount} stations.");
					}

					if (!(weight >= 0.0f && weight <= 1.0f))
					{
						throw new ArgumentException($"Current-field grid {id}: cell {cell} has weight {weight} outside [0, 1].");
					}

					wet = true;
					sum += weight;
				}

				var along = _values[v + 2];
				var cross = _values[v + 3];
				if (!(along >= 0.0f && along <= 1.0f && cross >= 0.0f && cross <= 1.0f))
				{
					throw new ArgumentException($"Current-field grid {id}: cell {cell} has factors {along}, {cross} outside [0, 1].");
				}

				if (wet)
				{
					var length = Math.Sqrt((double)_values[v] * _values[v] + (double)_values[v + 1] * _values[v + 1]);
					if (Math.Abs(length - 1.0) > UnitAxisTolerance || Math.Abs(sum - 1.0) > WeightSumTolerance)
					{
						throw new ArgumentException($"Current-field grid {id}: water cell {cell} needs a unit axis (within {UnitAxisTolerance}) and weights summing to 1 (within {WeightSumTolerance}), found {length} and {sum}.");
					}
				}
				else
				{
					for (var j = 0; j < Leading; j++)
					{
						if (_values[v + j] != 0.0f)
						{
							throw new ArgumentException($"Current-field grid {id}: dry cell {cell} (no station slot) must be all zeros.");
						}
					}
				}
			}

			Id = id;
			OriginNorth = originNorth;
			OriginEast = originEast;
			CellSize = cellSize;
			Rows = rows;
			Columns = columns;
			Slots = slots;
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

		/// <summary>Station slots per cell.</summary>
		public int Slots { get; }

		/// <summary>Values per cell: 4 + <see cref="Slots"/>.</summary>
		public int ValuesPerCell => Leading + Slots;

		/// <summary>North of the last row of centres, m.</summary>
		public double EndNorth => OriginNorth + (Rows - 1) * CellSize;

		/// <summary>East of the last column of centres, m.</summary>
		public double EndEast => OriginEast + (Columns - 1) * CellSize;

		/// <summary>Value <paramref name="index"/> (0 to <see cref="ValuesPerCell"/> − 1) of cell (row, column).</summary>
		public float Value(int row, int column, int index) => _values[(row * Columns + column) * ValuesPerCell + index];

		/// <summary>The station index in slot <paramref name="slot"/> of cell (row, column), or <see cref="EmptySlot"/>.</summary>
		public byte Slot(int row, int column, int slot) => _slots[(row * Columns + column) * Slots + slot];

		/// <summary>True when cell (row, column) is water: at least one of its slots names a station.</summary>
		public bool IsWet(int row, int column)
		{
			var s = (row * Columns + column) * Slots;
			for (var j = 0; j < Slots; j++)
			{
				if (_slots[s + j] != EmptySlot)
				{
					return true;
				}
			}

			return false;
		}

		internal float[] Values => _values;

		internal byte[] SlotArray => _slots;
	}

	/// <summary>
	/// A station of a current-field basis: its world position and its frame, the flood-oriented axis the field resolves
	/// its velocity on (not necessarily of unit length).
	/// </summary>
	public sealed class CurrentFieldStation
	{
		public CurrentFieldStation(string id, double north, double east, double frameNorth, double frameEast)
		{
			if (string.IsNullOrEmpty(id))
			{
				throw new ArgumentException("A current-field station needs an id.");
			}

			if (!SeriesTime.IsFinite(north) || !SeriesTime.IsFinite(east) || !SeriesTime.IsFinite(frameNorth) || !SeriesTime.IsFinite(frameEast)
				|| !(frameNorth * frameNorth + frameEast * frameEast > 1e-6))
			{
				throw new ArgumentException($"Current-field station {id}: position and frame must be finite, the frame not near zero.");
			}

			Id = id;
			North = north;
			East = east;
			FrameNorth = frameNorth;
			FrameEast = frameEast;
		}

		public string Id { get; }

		/// <summary>World position north of the origin, m.</summary>
		public double North { get; }

		/// <summary>World position east of the origin, m.</summary>
		public double East { get; }

		public double FrameNorth { get; }

		public double FrameEast { get; }
	}

	/// <summary>
	/// The static part of a region's channel-constrained current field (format <c>ferrysim-env-field/1</c>): grids of
	/// per-cell axes, factors and station weights, and the stations they refer to. Independent of the day; a pack
	/// supplies the station series. <see cref="ChannelCurrentField"/> samples it.
	/// </summary>
	public sealed class CurrentFieldBasis
	{
		/// <summary>The field manifest's format string.</summary>
		public const string Format = "ferrysim-env-field/1";

		/// <summary>The method this reader implements.</summary>
		public const string Method = "channel-harmonic/1";

		private readonly CurrentFieldStation[] _stations;
		private readonly CurrentFieldGrid[] _grids;

		/// <param name="digest">The field set's digest, hex; empty for a basis built in memory.</param>
		public CurrentFieldBasis(string region, IReadOnlyList<CurrentFieldStation> stations, IReadOnlyList<CurrentFieldGrid> grids,
			IReadOnlyList<string> notes, string digest)
			: this(region, stations, grids, null, null, notes, digest)
		{
		}

		/// <param name="parameters">How the pipeline built it, as its manifest records; null for a basis built in memory.</param>
		/// <param name="sources">The raw files and sets it was built from.</param>
		/// <param name="digest">The field set's digest, hex; empty for a basis built in memory.</param>
		public CurrentFieldBasis(string region, IReadOnlyList<CurrentFieldStation> stations, IReadOnlyList<CurrentFieldGrid> grids,
			CurrentFieldParameters parameters, IReadOnlyList<DepthSource> sources, IReadOnlyList<string> notes, string digest)
		{
			if (string.IsNullOrEmpty(region))
			{
				throw new ArgumentException("A current-field basis needs a region.");
			}

			if (stations == null || stations.Count == 0 || stations.Count >= CurrentFieldGrid.EmptySlot)
			{
				throw new ArgumentException($"A current-field basis needs between 1 and {CurrentFieldGrid.EmptySlot - 1} stations.");
			}

			if (grids == null || grids.Count == 0)
			{
				throw new ArgumentException("A current-field basis needs at least one grid.");
			}

			_stations = new CurrentFieldStation[stations.Count];
			for (var i = 0; i < _stations.Length; i++)
			{
				_stations[i] = stations[i] ?? throw new ArgumentException($"Current-field station {i} is missing.");
				for (var j = 0; j < i; j++)
				{
					if (_stations[j].Id == _stations[i].Id)
					{
						throw new ArgumentException($"Current-field station {_stations[i].Id} appears twice.");
					}
				}
			}

			_grids = new CurrentFieldGrid[grids.Count];
			for (var i = 0; i < _grids.Length; i++)
			{
				_grids[i] = grids[i] ?? throw new ArgumentException($"Current-field grid {i} is missing.");
				for (var j = 0; j < i; j++)
				{
					if (_grids[j].Id == _grids[i].Id)
					{
						throw new ArgumentException($"Current-field grid id {_grids[i].Id} appears twice.");
					}

					if (Overlap(_grids[j], _grids[i]))
					{
						throw new ArgumentException($"Current-field grids {_grids[j].Id} and {_grids[i].Id} overlap.");
					}
				}
			}

			Region = region;
			Parameters = parameters;
			Sources = sources == null ? Array.Empty<DepthSource>() : new List<DepthSource>(sources).ToArray();
			Notes = notes == null ? Array.Empty<string>() : new List<string>(notes).ToArray();
			Digest = digest ?? string.Empty;
		}

		public string Region { get; }

		/// <summary>The stations the grids' slots refer to, by index.</summary>
		public IReadOnlyList<CurrentFieldStation> Stations => _stations;

		public IReadOnlyList<CurrentFieldGrid> Grids => _grids;

		/// <summary>How the pipeline built the basis; null for one built in memory. Documentation only: sampling never reads it.</summary>
		public CurrentFieldParameters Parameters { get; }

		/// <summary>The raw files and sets the basis was built from (name, SHA-256, note).</summary>
		public IReadOnlyList<DepthSource> Sources { get; }

		/// <summary>The field manifest's notes.</summary>
		public IReadOnlyList<string> Notes { get; }

		/// <summary>The field set's digest (DIGEST), hex; empty for a basis built in memory.</summary>
		public string Digest { get; }

		// Lattices that share more than an edge.
		private static bool Overlap(CurrentFieldGrid a, CurrentFieldGrid b) =>
			a.OriginNorth < b.EndNorth && b.OriginNorth < a.EndNorth && a.OriginEast < b.EndEast && b.OriginEast < a.EndEast;
	}

	/// <summary>
	/// How a <c>channel-harmonic/1</c> basis was built, as its manifest's <c>parameters</c> record them (metres unless
	/// named otherwise). The pipeline's README says how each value was chosen.
	/// </summary>
	public sealed class CurrentFieldParameters
	{
		public CurrentFieldParameters(double cellSize, double southNorth, double northNorth, double westEast, double eastEast, string inflow, string outflow,
			double conveyanceExponent, double minimumDepth, double smoothing, double shelterThreshold, double depthExponent, double depthSmoothing,
			double crossTaper, double stationDisk, double stationReach, double acrossReach, double tolerance)
		{
			CellSize = cellSize;
			SouthNorth = southNorth;
			NorthNorth = northNorth;
			WestEast = westEast;
			EastEast = eastEast;
			Inflow = inflow ?? string.Empty;
			Outflow = outflow ?? string.Empty;
			ConveyanceExponent = conveyanceExponent;
			MinimumDepth = minimumDepth;
			Smoothing = smoothing;
			ShelterThreshold = shelterThreshold;
			DepthExponent = depthExponent;
			DepthSmoothing = depthSmoothing;
			CrossTaper = crossTaper;
			StationDisk = stationDisk;
			StationReach = stationReach;
			AcrossReach = acrossReach;
			Tolerance = tolerance;
		}

		/// <summary>Cell size of the lattice the basis was computed on.</summary>
		public double CellSize { get; }

		/// <summary>North of the computation lattice's southern row of centres.</summary>
		public double SouthNorth { get; }

		/// <summary>North of its northern row of centres.</summary>
		public double NorthNorth { get; }

		/// <summary>East of its western column of centres.</summary>
		public double WestEast { get; }

		/// <summary>East of its eastern column of centres.</summary>
		public double EastEast { get; }

		/// <summary>The lattice edge the flood pattern enters by.</summary>
		public string Inflow { get; }

		/// <summary>The lattice edge it leaves by.</summary>
		public string Outflow { get; }

		/// <summary>Exponent of depth in the pattern's face conductance (0: plain potential flow).</summary>
		public double ConveyanceExponent { get; }

		/// <summary>Smallest water depth used.</summary>
		public double MinimumDepth { get; }

		/// <summary>Length over which the pattern speed is smoothed for the sheltering factor.</summary>
		public double Smoothing { get; }

		/// <summary>Pattern speed over its smoothed value below which the flow is taken as sheltered.</summary>
		public double ShelterThreshold { get; }

		/// <summary>Exponent of depth over its smoothed value in the depth factor.</summary>
		public double DepthExponent { get; }

		/// <summary>Length over which the depth is smoothed for the depth factor.</summary>
		public double DepthSmoothing { get; }

		/// <summary>Distance from the shoreline over which the part of the flow across the axis returns.</summary>
		public double CrossTaper { get; }

		/// <summary>Radius of the disk each station's weight is held on.</summary>
		public double StationDisk { get; }

		/// <summary>Distance from a station within which the factors are raised toward 1.</summary>
		public double StationReach { get; }

		/// <summary>Distance from a station within which its direction off the channel axis is kept.</summary>
		public double AcrossReach { get; }

		/// <summary>Relative residual the solves converged to.</summary>
		public double Tolerance { get; }
	}
}
