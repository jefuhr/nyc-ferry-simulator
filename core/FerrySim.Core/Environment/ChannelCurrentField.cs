using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// The channel-constrained current field (method <c>channel-harmonic/1</c>) from a <see cref="CurrentFieldBasis"/> and
	/// a pack's current stations. The sampling rule is part of the format, and the pipeline implements the same:
	/// <list type="number">
	/// <item>Each station's velocity v, interpolated in time, is resolved on its frame f into an along part
	/// a = v·f / |f|² and an across part x = v·f⊥ / |f|², f⊥ = (−f_east, f_north) (f turned 90° clockwise).</item>
	/// <item>A water cell's velocity is along·A·n + cross·X·n⊥, with A and X the slot-weighted sums of the stations' a and
	/// x, n the cell's axis and along and cross its factors. A dry cell (every slot empty) has none.</item>
	/// <item>A point's value is the bilinear blend of the four cells around it over the water cells only, divided by their
	/// share of the bilinear weight or by <see cref="WetFloor"/>, whichever is larger: in full up to a shoreline halfway
	/// between a water and a land centre, fading to zero over land.</item>
	/// <item>Outside every grid it is the <see cref="StationBlendCurrentField"/> of all the pack's current stations.</item>
	/// </list>
	/// <para>
	/// The station parts are cached for the last pack time asked for, so the samples a simulation takes along its hull in
	/// one step share them. Deterministic and allocation-free per sample. Not thread-safe: one field per simulation thread.
	/// </para>
	/// </summary>
	public sealed class ChannelCurrentField : ICurrentField
	{
		/// <summary>The share of a point's bilinear weight its water cells must carry for their blend to count in full.</summary>
		public const double WetFloor = 0.5;

		private readonly CurrentFieldGrid[] _grids;
		private readonly VectorSeries[] _series;
		private readonly double[] _frameNorth;
		private readonly double[] _frameEast;
		private readonly double[] _frameSquared;
		private readonly double[] _along;
		private readonly double[] _across;
		private readonly StationBlendCurrentField _outside;
		private double _cachedTime = double.NaN;
		private long _outsideSamples;

		/// <param name="basis">The region's basis.</param>
		/// <param name="stations">The day's current stations; every station of the basis must be among them, by id.</param>
		public ChannelCurrentField(CurrentFieldBasis basis, IReadOnlyList<CurrentStation> stations)
		{
			Basis = basis ?? throw new ArgumentNullException(nameof(basis));
			if (stations == null || stations.Count == 0)
			{
				throw new ArgumentException("The channel field needs the day's current stations.");
			}

			var count = basis.Stations.Count;
			_series = new VectorSeries[count];
			_frameNorth = new double[count];
			_frameEast = new double[count];
			_frameSquared = new double[count];
			_along = new double[count];
			_across = new double[count];
			for (var k = 0; k < count; k++)
			{
				var station = basis.Stations[k];
				for (var i = 0; i < stations.Count && _series[k] == null; i++)
				{
					if (stations[i] != null && stations[i].Id == station.Id)
					{
						_series[k] = stations[i].Velocity;
					}
				}

				if (_series[k] == null)
				{
					throw new ArgumentException($"The current field needs station {station.Id}, which the day does not have.");
				}

				_frameNorth[k] = station.FrameNorth;
				_frameEast[k] = station.FrameEast;
				_frameSquared[k] = station.FrameNorth * station.FrameNorth + station.FrameEast * station.FrameEast;
			}

			_grids = new CurrentFieldGrid[basis.Grids.Count];
			for (var g = 0; g < _grids.Length; g++)
			{
				_grids[g] = basis.Grids[g];
			}

			_outside = new StationBlendCurrentField(stations);
		}

		public CurrentFieldBasis Basis { get; }

		/// <summary>Samples taken outside every grid, which used the station blend.</summary>
		public long OutsideSamples => _outsideSamples;

		/// <summary>Zeroes <see cref="OutsideSamples"/>, for example before the run a report is about.</summary>
		public void ResetCounters() => _outsideSamples = 0;

		public HorizontalVector Sample(double north, double east, double packTime)
		{
			for (var g = 0; g < _grids.Length; g++)
			{
				var grid = _grids[g];
				var row = (north - grid.OriginNorth) / grid.CellSize;
				var column = (east - grid.OriginEast) / grid.CellSize;
				if (row >= 0.0 && row <= grid.Rows - 1 && column >= 0.0 && column <= grid.Columns - 1)
				{
					Refresh(packTime);
					return Bilinear(grid, row, column);
				}
			}

			_outsideSamples++;
			return _outside.Sample(north, east, packTime);
		}

		private void Refresh(double packTime)
		{
			if (packTime == _cachedTime)
			{
				return;
			}

			for (var k = 0; k < _series.Length; k++)
			{
				var v = _series[k].Sample(packTime);
				_along[k] = (v.North * _frameNorth[k] + v.East * _frameEast[k]) / _frameSquared[k];
				_across[k] = (-v.North * _frameEast[k] + v.East * _frameNorth[k]) / _frameSquared[k];
			}

			_cachedTime = packTime;
		}

		private HorizontalVector Bilinear(CurrentFieldGrid grid, double row, double column)
		{
			var r = Math.Min((int)row, grid.Rows - 2);
			var c = Math.Min((int)column, grid.Columns - 2);
			var up = row - r;
			var across = column - c;
			var north = 0.0;
			var east = 0.0;
			var wet = 0.0;
			Corner(grid, r, c, (1.0 - up) * (1.0 - across), ref north, ref east, ref wet);
			Corner(grid, r, c + 1, (1.0 - up) * across, ref north, ref east, ref wet);
			Corner(grid, r + 1, c, up * (1.0 - across), ref north, ref east, ref wet);
			Corner(grid, r + 1, c + 1, up * across, ref north, ref east, ref wet);
			var share = Math.Max(wet, WetFloor);
			return new HorizontalVector(north / share, east / share);
		}

		private void Corner(CurrentFieldGrid grid, int row, int column, double weight, ref double north, ref double east, ref double wet)
		{
			var cell = row * grid.Columns + column;
			var values = grid.Values;
			var slots = grid.SlotArray;
			var count = grid.Slots;
			var v = cell * (CurrentFieldGrid.Leading + count);
			var s = cell * count;
			var along = 0.0;
			var across = 0.0;
			var water = false;
			for (var j = 0; j < count; j++)
			{
				var slot = slots[s + j];
				if (slot != CurrentFieldGrid.EmptySlot)
				{
					double w = values[v + CurrentFieldGrid.Leading + j];
					along += w * _along[slot];
					across += w * _across[slot];
					water = true;
				}
			}

			if (!water)
			{
				return;
			}

			double axisNorth = values[v];
			double axisEast = values[v + 1];
			along = values[v + 2] * along;
			across = values[v + 3] * across;
			north += weight * (along * axisNorth - across * axisEast);
			east += weight * (along * axisEast + across * axisNorth);
			wet += weight;
		}
	}
}
