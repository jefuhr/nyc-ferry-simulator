using System;
using FerrySim.Core.Traffic;

namespace FerrySim.Traffic
{
	/// <summary>Render coverage only. Never filters the core replay, CPA or collision monitor.</summary>
	public interface ITrafficArea
	{
		TrafficBounds Extent { get; }
		bool Contains(double north, double east);
		bool MayEnter(TrafficReplay replay, int vessel);
	}

	/// <summary>A closed rectangle in the shared simulation north/east frame, metres.</summary>
	public readonly struct TrafficBounds
	{
		public TrafficBounds(double minNorth, double maxNorth, double minEast, double maxEast)
		{
			if (!(minNorth < maxNorth) || !(minEast < maxEast) ||
				double.IsInfinity(minNorth) || double.IsInfinity(maxNorth) || double.IsInfinity(minEast) || double.IsInfinity(maxEast))
				throw new ArgumentException("Traffic bounds must be finite, ordered rectangles.");
			MinNorth = minNorth;
			MaxNorth = maxNorth;
			MinEast = minEast;
			MaxEast = maxEast;
		}
		public double MinNorth { get; }
		public double MaxNorth { get; }
		public double MinEast { get; }
		public double MaxEast { get; }
		public bool Contains(double north, double east) => north >= MinNorth && north <= MaxNorth && east >= MinEast && east <= MaxEast;
	}

	/// <summary>
	/// Immutable union of the active tiles' bounds_local_m rectangles from harbor/index.json, supplied by the composition
	/// root. Holes remain holes. Supply grown bounds only if the water service covers them too; no implicit slice margin.
	/// </summary>
	public sealed class TrafficTileArea : ITrafficArea
	{
		private readonly TrafficBounds[] _tiles;
		public TrafficTileArea(TrafficBounds[] activeTiles)
		{
			if (activeTiles == null || activeTiles.Length == 0) throw new ArgumentException("Traffic needs active tiles.", nameof(activeTiles));
			_tiles = (TrafficBounds[])activeTiles.Clone();
			var n0 = double.PositiveInfinity;
			var n1 = double.NegativeInfinity;
			var e0 = double.PositiveInfinity;
			var e1 = double.NegativeInfinity;
			foreach (var tile in _tiles)
			{
				if (!(tile.MinNorth < tile.MaxNorth) || !(tile.MinEast < tile.MaxEast)) throw new ArgumentException("Empty traffic tile.");
				n0 = Math.Min(n0, tile.MinNorth); n1 = Math.Max(n1, tile.MaxNorth);
				e0 = Math.Min(e0, tile.MinEast); e1 = Math.Max(e1, tile.MaxEast);
			}
			Extent = new TrafficBounds(n0, n1, e0, e1);
		}
		public TrafficBounds Extent { get; }
		public bool Contains(double north, double east)
		{
			for (var i = 0; i < _tiles.Length; i++) if (_tiles[i].Contains(north, east)) return true;
			return false;
		}
		public bool MayEnter(TrafficReplay replay, int vessel)
		{
			for (var i = 0; i < _tiles.Length; i++)
			{
				var t = _tiles[i];
				if (replay.MayEnter(vessel, t.MinNorth, t.MaxNorth, t.MinEast, t.MaxEast)) return true;
			}
			return false;
		}
	}
}
