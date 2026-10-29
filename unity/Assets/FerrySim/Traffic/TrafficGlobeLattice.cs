using System;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.Traffic
{
	/// <summary>
	/// Bakes the same ellipsoid route as CesiumVesselPlacement into a 250 m lattice at connection time. Bilinear ECEF
	/// and local-axis interpolation avoids geodesy in Place; the worst cell-centre sag is about 2.5 mm. Height is always
	/// sampled separately from IWaterSurface at the actual hull position and render time, never from this lattice.
	/// </summary>
	public sealed class TrafficGlobeLattice
	{
		public const double CellSize = 250;
		private readonly double _north, _east;
		private readonly int _rows, _columns;
		private readonly Node[] _nodes;
		private readonly struct Node
		{
			public Node(double3 position, double3 up, double3 north) { Position = position; Up = up; North = north; }
			public readonly double3 Position, Up, North;
		}
		public TrafficGlobeLattice(TrafficBounds bounds)
		{
			if (!(bounds.MinNorth < bounds.MaxNorth) || !(bounds.MinEast < bounds.MaxEast)) throw new ArgumentException("Empty traffic extent.");
			_north = bounds.MinNorth; _east = bounds.MinEast;
			_rows = checked((int)Math.Ceiling((bounds.MaxNorth - _north) / CellSize) + 1);
			_columns = checked((int)Math.Ceiling((bounds.MaxEast - _east) / CellSize) + 1);
			if ((long)_rows * _columns > 1000000) throw new ArgumentException("Traffic extent exceeds the harbor lattice budget.");
			_nodes = new Node[_rows * _columns];
			for (var r = 0; r < _rows; r++)
			for (var c = 0; c < _columns; c++)
			{
				var ll = LocalTangentPlane.World.ToGeodetic(_north + r * CellSize, _east + c * CellSize);
				var lat = Angles.ToRadians(ll.LatitudeDegrees); var lon = Angles.ToRadians(ll.LongitudeDegrees);
				_nodes[r * _columns + c] = new Node(
					CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, 0)),
					new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)),
					new double3(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat)));
			}
		}
		public long StorageBytes => (long)_nodes.Length * 9 * sizeof(double);

		/// <summary>Returns double ECEF and a local globe basis. No trigonometry, geodesy or allocation.</summary>
		public void Sample(double north, double east, double height, out double3 position, out double3 up, out double3 forward)
		{
			var r = (north - _north) / CellSize; var c = (east - _east) / CellSize;
			if (!(r >= 0 && r <= _rows - 1 && c >= 0 && c <= _columns - 1)) throw new ArgumentOutOfRangeException(nameof(north));
			var ri = Math.Min((int)r, _rows - 2); var ci = Math.Min((int)c, _columns - 2);
			var a = _nodes[ri * _columns + ci]; var b = _nodes[ri * _columns + ci + 1];
			var d = _nodes[(ri + 1) * _columns + ci]; var e = _nodes[(ri + 1) * _columns + ci + 1];
			r -= ri; c -= ci;
			up = math.normalize(math.lerp(math.lerp(a.Up, b.Up, c), math.lerp(d.Up, e.Up, c), r));
			forward = math.normalize(math.lerp(math.lerp(a.North, b.North, c), math.lerp(d.North, e.North, c), r));
			position = math.lerp(math.lerp(a.Position, b.Position, c), math.lerp(d.Position, e.Position, c), r) + up * height;
		}

		public static void Place(CesiumGeoreference georeference, Transform target, double3 position, double3 up, double3 north, double heading)
		{
			// Cesium's matrix subtracts its local origin in double precision BEFORE this float conversion.
			target.localPosition = (Vector3)(float3)georeference.TransformEarthCenteredEarthFixedPositionToUnity(position);
			var localUp = (Vector3)(float3)georeference.TransformEarthCenteredEarthFixedDirectionToUnity(up);
			var localNorth = (Vector3)(float3)georeference.TransformEarthCenteredEarthFixedDirectionToUnity(north);
			target.localRotation = Quaternion.LookRotation(localNorth, localUp) * Quaternion.Euler(0, (float)Angles.ToDegrees(heading), 0);
		}
	}
}
