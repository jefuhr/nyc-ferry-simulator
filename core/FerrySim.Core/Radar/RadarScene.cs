using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;

namespace FerrySim.Core.Radar
{
	public readonly struct RadarSegment
	{
		public RadarSegment(double n0, double e0, double n1, double e1) { N0 = n0; E0 = e0; N1 = n1; E1 = e1; }
		public double N0 { get; }
		public double E0 { get; }
		public double N1 { get; }
		public double E1 { get; }
	}
	public sealed class RadarFeature
	{
		public RadarFeature(RadarSegment[] edges, double? roofNavd88, bool land = false, bool bridge = false)
		{ Edges = (RadarSegment[])edges.Clone(); RoofNavd88 = roofNavd88; Land = land; Bridge = bridge; }
		internal RadarSegment[] Edges { get; }
		public double? RoofNavd88 { get; }
		public bool Land { get; }
		public bool Bridge { get; }
	}

	/// <summary>Resident semantic edges indexed at connection time. Never references streamed render content.</summary>
	public sealed class RadarScene
	{
		internal const double CellSize = 50;
		internal readonly FeatureInfo[] Features;
		internal readonly RadarSegment[] Segments;
		internal readonly int[] EdgeFeatures, CellEdges;
		internal readonly RadarTarget[] Aids;
		internal readonly Dictionary<long, CellRange> Cells;
		// Packed integer coordinates need mixing: Int64's default XOR hash clusters entire grid diagonals.
		private sealed class CellComparer : IEqualityComparer<long>
		{
			internal static readonly CellComparer Instance = new CellComparer();
			public bool Equals(long a, long b) => a == b;
			public int GetHashCode(long key)
			{
				unchecked { var h = (ulong)key; h ^= h >> 30; h *= 0xbf58476d1ce4e5b9UL; h ^= h >> 27; h *= 0x94d049bb133111ebUL; return (int)(h ^ (h >> 31)); }
			}
		}

		internal readonly struct FeatureInfo
		{
			internal FeatureInfo(RadarFeature feature) { RoofNavd88 = feature.RoofNavd88; Land = feature.Land; Bridge = feature.Bridge; }
			internal double? RoofNavd88 { get; }
			internal bool Land { get; }
			internal bool Bridge { get; }
		}
		internal readonly struct CellRange
		{
			internal CellRange(int offset, int count) { Offset = offset; Count = count; }
			internal int Offset { get; }
			internal int Count { get; }
		}
		public RadarScene(RadarFeature[] features, RadarTarget[] aids = null)
		{
			Features = new FeatureInfo[features.Length]; Aids = aids == null ? Array.Empty<RadarTarget>() : (RadarTarget[])aids.Clone();
			var edgeCount = 0; for (var f = 0; f < features.Length; f++) edgeCount = checked(edgeCount + features[f].Edges.Length);
			Segments = new RadarSegment[edgeCount]; EdgeFeatures = new int[edgeCount];
			var lists = new Dictionary<long, List<int>>(CellComparer.Instance); var id = 0;
			for (var f = 0; f < features.Length; f++)
			{
				Features[f] = new FeatureInfo(features[f]);
				foreach (var segment in features[f].Edges)
				{
					Segments[id] = segment; EdgeFeatures[id] = f;
					var n0 = Cell(Math.Min(segment.N0, segment.N1)); var n1 = Cell(Math.Max(segment.N0, segment.N1));
					var e0 = Cell(Math.Min(segment.E0, segment.E1)); var e1 = Cell(Math.Max(segment.E0, segment.E1));
					for (var n = n0; n <= n1; n++) for (var e = e0; e <= e1; e++)
					{
						var key = Key(n, e); if (!lists.TryGetValue(key, out var list)) lists.Add(key, list = new List<int>());
						list.Add(id);
					}
					id++;
				}
			}
			var references = 0; foreach (var pair in lists) references = checked(references + pair.Value.Count);
			CellEdges = new int[references]; Cells = new Dictionary<long, CellRange>(lists.Count, CellComparer.Instance); var cursor = 0;
			foreach (var pair in lists)
			{
				Cells.Add(pair.Key, new CellRange(cursor, pair.Value.Count)); pair.Value.CopyTo(CellEdges, cursor); cursor += pair.Value.Count;
			}
			// No feature object or source edge array is retained; doubles remain bit-for-bit unchanged.
		}
		/// <summary>The aids the radar answers for, in runtime order; evidence for the coverage rule.</summary>
		public IReadOnlyList<RadarTarget> AidTargets => Aids;
		public int FeatureCount => Features.Length;
		public int EdgeCount => Segments.Length;
		public int CellReferenceCount => CellEdges.Length;
		public int SpatialCellCount => Cells.Count;
		internal static int Cell(double coordinate) => (int)Math.Floor(coordinate / CellSize);
		internal static long Key(int n, int e) => ((long)n << 32) | (uint)e;

		public static RadarScene FromPacks(HarborChartPack chart, HarborRuntimePack runtime)
		{
			if (chart == null || runtime == null) throw new ArgumentNullException();
			if (chart.RuntimeDigest != runtime.Digest) throw new ArgumentException("Radar chart/runtime digests differ.");
			var features = new List<RadarFeature>();
			var layer = chart[ChartLayer.RadarObstructions];
			for (var i = 0; i < layer.Features.Count; i++)
			{
				var f = layer.Features[i];
				features.Add(new RadarFeature(Edges(f.Geometry), f.Properties["roof_navd88_m"].Number, f.FeatureClass != "building"));
			}
			for (var b = 0; b < runtime.Bridges.Count; b++)
			{
				var bridge = runtime.Bridges[b];
				for (var s = 0; s < bridge.Spans.Count; s++) features.Add(new RadarFeature(Edges(bridge.Spans[s].Geometry), bridge.Spans[s].VerticalBounds.Maximum, bridge: true));
				for (var p = 0; p < bridge.Piers.Count; p++) features.Add(new RadarFeature(Edges(bridge.Piers[p].Geometry), bridge.Piers[p].VerticalBounds.Maximum, bridge: true));
			}
			// Aids outside active coverage stay in the data but are never drawn (decision 0005): the same rule as the chartplotter's.
			var aids = new List<RadarTarget>(runtime.Aids.Count);
			for (var i = 0; i < runtime.Aids.Count; i++)
			{
				var position = runtime.Aids[i].Position;
				if (InCoverage(runtime.Coverage, position.North, position.East)) aids.Add(new RadarTarget(position.North, position.East, 2, 3, .55));
			}
			return new RadarScene(features.ToArray(), aids.ToArray());
		}
		/// <summary>Inside any coverage polygon; a runtime without coverage geometry covers everything.</summary>
		private static bool InCoverage(HarborGeometry coverage, double north, double east)
		{
			if (coverage == null || coverage.Polygons.Count == 0) return true;
			for (var p = 0; p < coverage.Polygons.Count; p++) if (coverage.Polygons[p].Contains(north, east)) return true;
			return false;
		}
		private static RadarSegment[] Edges(HarborGeometry geometry)
		{
			var result = new List<RadarSegment>();
			void Add(IReadOnlyList<HorizontalVector> line)
			{
				for (var i = 1; i < line.Count; i++) result.Add(new RadarSegment(line[i - 1].North, line[i - 1].East, line[i].North, line[i].East));
			}
			for (var p = 0; p < geometry.Polygons.Count; p++) for (var r = 0; r < geometry.Polygons[p].Rings.Count; r++) Add(geometry.Polygons[p].Rings[r]);
			for (var l = 0; l < geometry.Lines.Count; l++) Add(geometry.Lines[l]);
			return result.ToArray();
		}
	}
}
