using System;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.HarborWater
{
	/// <summary>A baked visual node relative to one double-precision local anchor, with caller-owned normalized gauge weights.</summary>
	public readonly struct WaterNode
	{
		internal WaterNode(double3 position, double3 up, int offset)
		{
			BasePosition = position;
			Up = up;
			WeightOffset = offset;
		}

		/// <summary>Datum position in the patch local east/up/north frame, in metres.</summary>
		public double3 BasePosition { get; }

		/// <summary>Unit ellipsoid-normal axis in this frame.</summary>
		public double3 Up { get; }

		/// <summary>Start of this node's normalized weights in the caller-owned array.</summary>
		public int WeightOffset { get; }
	}

	/// <summary>A local east/up/north mesh frame. ECEF is subtracted in doubles before any float conversion.</summary>
	public readonly struct WaterFrame
	{
		private WaterFrame(double3 origin, double3 east, double3 up, double3 north)
		{
			OriginEcef = origin;
			East = east;
			Up = up;
			North = north;
		}

		/// <summary>Anchor ECEF position, retained in doubles.</summary>
		public double3 OriginEcef { get; }

		/// <summary>Local east unit axis in ECEF coordinates.</summary>
		public double3 East { get; }

		/// <summary>Unit ellipsoid-normal axis in this frame.</summary>
		public double3 Up { get; }

		/// <summary>Local north unit axis in ECEF coordinates.</summary>
		public double3 North { get; }

		/// <summary>Builds a frame once at an ellipsoid-surface world position.</summary>
		public static WaterFrame AtNorthEast(double north, double east)
		{
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			var lat = math.radians(ll.LatitudeDegrees);
			var lon = math.radians(ll.LongitudeDegrees);
			return new WaterFrame(Ecef(ll, 0), new double3(-Math.Sin(lon), Math.Cos(lon), 0),
				new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)),
				new double3(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat)));
		}

		/// <summary>Subtracts the anchor in double precision before projecting into the local frame.</summary>
		public double3 ToLocal(double3 ecef) => DirectionToLocal(ecef - OriginEcef);

		/// <summary>Projects a direction onto the local axes without translation.</summary>
		public double3 DirectionToLocal(double3 direction) => new double3(math.dot(direction, East), math.dot(direction, Up), math.dot(direction, North));

		/// <summary>Reconstructs a local mesh position in double-precision ECEF for accuracy checks.</summary>
		public double3 ToEcef(Vector3 local) => OriginEcef + East * local.x + Up * local.y + North * local.z;
		/// <summary>One-time WGS84 conversion for baking and independent precision checks; never called in UpdateNodes.</summary>
		public static double3 Ecef(double north, double east, double height) => Ecef(LocalTangentPlane.World.ToGeodetic(north, east), height);
		internal static double3 Ecef(GeodeticPosition position, double height)
		{
			const double major = 6378137.0;
			const double flattening = 1.0 / 298.257223563;
			const double eccentricity = flattening * (2 - flattening);
			var latitude = math.radians(position.LatitudeDegrees);
			var longitude = math.radians(position.LongitudeDegrees);
			var sin = Math.Sin(latitude);
			var cos = Math.Cos(latitude);
			var radius = major / Math.Sqrt(1 - eccentricity * sin * sin);
			return new double3((radius + height) * cos * Math.Cos(longitude), (radius + height) * cos * Math.Sin(longitude), (radius * (1 - eccentricity) + height) * sin);
		}
	}

	/// <summary>Spatial harbor water: canonical datum plus the active run's local tide. This is a visual/height service, never depth.</summary>
	public sealed class HarborWaterService : IWaterSurface
	{
		private readonly double _timeOffset;

		/// <summary>Binds the canonical datum and one active day's observer environment.</summary>
		public HarborWaterService(HarborDatum datum, RunEnvironment environment)
		{
			Datum = datum ?? throw new ArgumentNullException(nameof(datum));
			Environment = environment ?? throw new ArgumentNullException(nameof(environment));
			if (!environment.HasTide)
			{
				throw new ArgumentException("Harbor water needs a verified environment day.", nameof(environment));
			}

			_timeOffset = (environment.ScenarioStartUtc - environment.Pack.EpochUtc).TotalSeconds;
		}

		/// <summary>Canonical datum shared by all floating visuals.</summary>
		public HarborDatum Datum { get; }

		/// <summary>The run's observer environment; sampling never changes physics counters.</summary>
		public RunEnvironment Environment { get; }

		/// <summary>Number of normalized weights per node and gauge levels per update.</summary>
		public int GaugeCount => Environment.Pack.WaterLevelStations.Count;

		/// <summary>Local datum plus local tide in metres above the WGS84 ellipsoid.</summary>
		public double EllipsoidalHeight(double north, double east, double time) => Datum.Height(north, east) + Environment.WaterLevel(north, east, time);
		/// <summary>Bakes position, local up and normalized gauge weights into caller-owned storage once.</summary>
		public WaterNode BakeNode(double north, double east, WaterFrame frame, double[] weights, int offset)
		{
			if (weights == null || offset < 0 || offset > weights.Length - GaugeCount)
			{
				throw new ArgumentException("Gauge weight storage is too small.", nameof(weights));
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			var height = Datum.Height(north, east);
			var latitude = math.radians(position.LatitudeDegrees);
			var longitude = math.radians(position.LongitudeDegrees);
			var up = new double3(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
			var gauges = Environment.Pack.WaterLevelStations;
			var total = 0.0;
			var atStation = -1;
			for (var i = 0; i < gauges.Count; i++)
			{
				var dn = north - gauges[i].North;
				var de = east - gauges[i].East;
				var squared = dn * dn + de * de;
				if (squared <= 1e-12)
				{
					atStation = i;
					break;
				}

				weights[offset + i] = 1.0 / squared;
				total += weights[offset + i];
			}

			for (var i = 0; i < gauges.Count; i++)
			{
				weights[offset + i] = atStation >= 0 ? (i == atStation ? 1.0 : 0.0) : weights[offset + i] / total;
			}

			return new WaterNode(frame.ToLocal(WaterFrame.Ecef(position, height)), frame.DirectionToLocal(up), offset);
		}

		/// <summary>Samples each gauge once at render time into caller storage, with the same coverage clamp as DataEnvironment.</summary>
		public void SampleGaugeLevels(double time, double[] levels)
		{
			if (levels == null || levels.Length < GaugeCount)
			{
				throw new ArgumentException("Gauge level storage is too small.", nameof(levels));
			}

			var pack = Environment.Pack;
			var packTime = Math.Max(pack.CoverageStart, Math.Min(pack.CoverageEnd, time + _timeOffset));
			for (var i = 0; i < GaugeCount; i++)
			{
				levels[i] = pack.WaterLevelStations[i].Level.Sample(packTime);
			}
		}

		/// <summary>Updates caller-owned mesh vertices using baked local values only: no allocation, geodesy or depth access.</summary>
		public void UpdateNodes(WaterNode[] nodes, double[] weights, double[] levels, Vector3[] vertices)
		{
			if (nodes == null || vertices == null || vertices.Length != nodes.Length || levels == null || levels.Length < GaugeCount || weights == null)
			{
				throw new ArgumentException("Water node buffers do not match.");
			}

			for (var i = 0; i < nodes.Length; i++)
			{
				var node = nodes[i];
				var tide = 0.0;
				for (var gauge = 0; gauge < GaugeCount; gauge++)
				{
					tide += weights[node.WeightOffset + gauge] * levels[gauge];
				}

				var local = node.BasePosition + node.Up * tide;
				vertices[i] = new Vector3((float)local.x, (float)local.y, (float)local.z);
			}
		}

		/// <summary>Whether every positive-area datum cell touched by a visual cell is active.</summary>
		public bool CellCovered(double north, double east, double size)
		{
			var grid = Datum.Lattice;
			if (!(size > 0) || double.IsNaN(north) || double.IsNaN(east) ||
				north < grid.OriginNorth || east < grid.OriginEast ||
				north + size > grid.OriginNorth + (grid.Rows - 1) * grid.CellSize ||
				east + size > grid.OriginEast + (grid.Columns - 1) * grid.CellSize)
			{
				return false;
			}

			var firstRow = (int)Math.Floor((north - grid.OriginNorth) / grid.CellSize);
			var lastRow = (int)Math.Ceiling((north + size - grid.OriginNorth) / grid.CellSize) - 1;
			var firstColumn = (int)Math.Floor((east - grid.OriginEast) / grid.CellSize);
			var lastColumn = (int)Math.Ceiling((east + size - grid.OriginEast) / grid.CellSize) - 1;
			for (var row = firstRow; row <= lastRow; row++)
			{
				for (var column = firstColumn; column <= lastColumn; column++)
				{
					if (!grid.CellActive(row, column))
					{
						return false;
					}
				}
			}

			return size > 0 && lastRow >= firstRow && lastColumn >= firstColumn;
		}
	}
}
