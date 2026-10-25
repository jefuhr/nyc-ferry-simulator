using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>Hand-made current-field bases for tests: grids cell by cell, and station frames that make a station exact.</summary>
	public static class FieldFixtures
	{
		public const byte E = CurrentFieldGrid.EmptySlot;

		/// <summary>A cell of a hand-made grid; slots beyond those given are empty.</summary>
		public struct Cell
		{
			public double AxisNorth;
			public double AxisEast;
			public double Along;
			public double Cross;
			public byte[] Slots;
			public double[] Weights;

			public static Cell Dry => new Cell { Slots = new byte[0], Weights = new double[0] };

			public static Cell Of(double axisDegrees, double along, double cross, params (byte Slot, double Weight)[] stations)
			{
				var cell = new Cell
				{
					AxisNorth = Math.Cos(Angles.ToRadians(axisDegrees)),
					AxisEast = Math.Sin(Angles.ToRadians(axisDegrees)),
					Along = along,
					Cross = cross,
					Slots = new byte[stations.Length],
					Weights = new double[stations.Length],
				};
				for (var j = 0; j < stations.Length; j++)
				{
					cell.Slots[j] = stations[j].Slot;
					cell.Weights[j] = stations[j].Weight;
				}

				return cell;
			}
		}

		public static CurrentFieldGrid Grid(string id, double originNorth, double originEast, double cell, int rows, int columns, int stationCount,
			Func<int, int, Cell> make, int slots = 4)
		{
			var perCell = CurrentFieldGrid.Leading + slots;
			var values = new float[rows * columns * perCell];
			var stationIndices = new byte[rows * columns * slots];
			for (var r = 0; r < rows; r++)
			{
				for (var c = 0; c < columns; c++)
				{
					var x = make(r, c);
					var v = (r * columns + c) * perCell;
					values[v] = (float)x.AxisNorth;
					values[v + 1] = (float)x.AxisEast;
					values[v + 2] = (float)x.Along;
					values[v + 3] = (float)x.Cross;
					for (var j = 0; j < slots; j++)
					{
						values[v + CurrentFieldGrid.Leading + j] = j < x.Weights.Length ? (float)x.Weights[j] : 0.0f;
						stationIndices[(r * columns + c) * slots + j] = j < x.Slots.Length ? x.Slots[j] : E;
					}
				}
			}

			return new CurrentFieldGrid(id, originNorth, originEast, cell, rows, columns, slots, values, stationIndices, stationCount);
		}

		/// <summary>
		/// The frame a station needs to be exact on a grid: the cell axes around it blended by the sampling rule (bilinear
		/// over the water cells, divided by their share or one half).
		/// </summary>
		public static CurrentFieldStation StationOn(CurrentFieldGrid grid, string id, double north, double east)
		{
			var row = (north - grid.OriginNorth) / grid.CellSize;
			var column = (east - grid.OriginEast) / grid.CellSize;
			var r = Math.Min((int)row, grid.Rows - 2);
			var c = Math.Min((int)column, grid.Columns - 2);
			double up = row - r, across = column - c;
			double fn = 0.0, fe = 0.0, wet = 0.0;
			foreach (var (rr, cc, w) in new[] { (r, c, (1 - up) * (1 - across)), (r, c + 1, (1 - up) * across), (r + 1, c, up * (1 - across)), (r + 1, c + 1, up * across) })
			{
				if (grid.IsWet(rr, cc))
				{
					fn += w * grid.Value(rr, cc, 0);
					fe += w * grid.Value(rr, cc, 1);
					wet += w;
				}
			}

			var share = Math.Max(wet, ChannelCurrentField.WetFloor);
			return new CurrentFieldStation(id, north, east, fn / share, fe / share);
		}
	}
}
