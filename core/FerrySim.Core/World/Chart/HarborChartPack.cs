using System;
using System.Collections.Generic;

namespace FerrySim.Core.World.Chart
{
	/// <summary>The eight fixed resident layers. Depths retain MLLW; roofs explicitly use NAVD88.</summary>
	public enum ChartLayer { Land, DepthAreas, Contours, Soundings, Hazards, Channels, RadarObstructions, References }

	/// <summary>Immutable chart evidence, never collision geometry. References carry bounds and runtime identity only.</summary>
	public sealed class ChartFeature
	{
		/// <summary>Copies semantic identity, geometry, evidence and verified bounds into resident storage.</summary>
		public ChartFeature(string id, string sourceId, string featureClass, HarborGeometry geometry, HarborValue record, double minNorth, double minEast, double maxNorth, double maxEast)
		{
			Id = id; SourceId = sourceId; FeatureClass = featureClass; Geometry = geometry; Record = record;
			MinimumNorth = minNorth; MinimumEast = minEast; MaximumNorth = maxNorth; MaximumEast = maxEast;
		}
		/// <summary>Stable identity, in ordinal order within its layer.</summary>
		public string Id { get; }
		/// <summary>Original source identity or null for a runtime reference.</summary>
		public string SourceId { get; }
		/// <summary>ENC object class, building, or null for a runtime reference.</summary>
		public string FeatureClass { get; }
		/// <summary>Double north/east geometry; null only for runtime references.</summary>
		public HarborGeometry Geometry { get; }
		/// <summary>Immutable semantic metadata and shared attributes, excluding duplicate geometry and bounds.</summary>
		public HarborValue Record { get; }
		/// <summary>Source attributes; missing evidence stays null.</summary>
		public HarborValue Properties => Record["properties"];
		/// <summary>Southern extent in world metres.</summary>
		public double MinimumNorth { get; }
		/// <summary>Western extent in world metres.</summary>
		public double MinimumEast { get; }
		/// <summary>Northern extent in world metres.</summary>
		public double MaximumNorth { get; }
		/// <summary>Eastern extent in world metres.</summary>
		public double MaximumEast { get; }
	}

	/// <summary>Verified resident chart pack. Load with Serialization.HarborChartPackJson.Load(folder, runtime).</summary>
	public sealed class HarborChartPack
	{
		private readonly ChartFeatureLayer[] _layers;
		/// <summary>Creates resident immutable layers from a validating loader.</summary>
		public HarborChartPack(string digest, string runtimeDigest, IReadOnlyList<ChartFeatureLayer> layers, HarborValue manifest)
		{
			if (layers.Count != 8) throw new ArgumentException("Exactly eight chart layers are required.");
			_layers = new ChartFeatureLayer[8]; for (var i = 0; i < 8; i++) _layers[i] = layers[i];
			Digest = digest; RuntimeDigest = runtimeDigest; Manifest = manifest;
		}
		/// <summary>Verified raw-byte chart digest.</summary>
		public string Digest { get; }
		/// <summary>The exact runtime digest referenced by the chart.</summary>
		public string RuntimeDigest { get; }
		/// <summary>Immutable source, datum, licence and frame evidence.</summary>
		public HarborValue Manifest { get; }
		/// <summary>Gets one resident layer and its fixed grid.</summary>
		public ChartFeatureLayer this[ChartLayer layer] => _layers[(int)layer];
	}

	/// <summary>Caller-owned reusable query storage. Use a separate buffer per concurrent caller.</summary>
	public sealed class ChartQueryBuffer
	{
		internal readonly ChartFeatureLayer Owner;
		internal readonly int[] Seen;
		internal readonly int[] Candidates;
		internal readonly int[] Results;
		internal int Generation;
		internal ChartQueryBuffer(ChartFeatureLayer owner, int features, int capacity)
		{
			Owner = owner; Seen = new int[features]; Candidates = new int[features]; Results = new int[capacity];
		}
		/// <summary>Number of stored result indices.</summary>
		public int Count { get; internal set; }
		/// <summary>Total matching features, including results beyond capacity.</summary>
		public int TotalMatches { get; internal set; }
		/// <summary>True when result capacity omitted matches. Increase capacity before the next query.</summary>
		public bool Truncated => TotalMatches > Count;
		/// <summary>Ordinal feature index, ascending and stable for this verified pack.</summary>
		public int this[int index] => index >= 0 && index < Count ? Results[index] : throw new ArgumentOutOfRangeException(nameof(index));
	}

	/// <summary>Immutable fixed 250 m grid. Queries test exact semantic geometry; reference queries use runtime bounds.</summary>
	public sealed class ChartFeatureLayer
	{
		private readonly Dictionary<long, int[]> _grid;
		private readonly int _minN = int.MaxValue, _minE = int.MaxValue, _maxN = int.MinValue, _maxE = int.MinValue;
		/// <summary>Copies verified feature/grid data at load time.</summary>
		public ChartFeatureLayer(IReadOnlyList<ChartFeature> features, IDictionary<long, int[]> grid)
		{
			var copy = new ChartFeature[features.Count]; for (var i = 0; i < copy.Length; i++) copy[i] = features[i];
			Features = Array.AsReadOnly(copy); _grid = new Dictionary<long, int[]>();
			foreach (var pair in grid)
			{
				_grid.Add(pair.Key, (int[])pair.Value.Clone()); var n = (int)(pair.Key >> 32); var e = (int)pair.Key;
				_minN = Math.Min(_minN, n); _minE = Math.Min(_minE, e); _maxN = Math.Max(_maxN, n); _maxE = Math.Max(_maxE, e);
			}
		}
		/// <summary>Resident features in ordinal ID order.</summary>
		public IReadOnlyList<ChartFeature> Features { get; }
		/// <summary>Allocates reusable candidate, deduplication and result storage once, outside the query path.</summary>
		public ChartQueryBuffer CreateQueryBuffer(int capacity)
		{
			if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
			return new ChartQueryBuffer(this, Features.Count, capacity);
		}
		/// <summary>Fixed-grid key: signed north cell in the high word, signed east cell in the low word.</summary>
		public static long GridKey(int northCell, int eastCell) => ((long)northCell << 32) | (uint)eastCell;
		private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
		/// <summary>Queries a closed north/east box into reusable storage. No allocations; ascending feature indices.</summary>
		public void QueryBox(double minNorth, double minEast, double maxNorth, double maxEast, ChartQueryBuffer buffer)
		{
			if (!Finite(minNorth) || !Finite(minEast) || !Finite(maxNorth) || !Finite(maxEast) || minNorth > maxNorth || minEast > maxEast) throw new ArgumentException("Finite ordered box bounds required.");
			Query(minNorth, minEast, maxNorth, maxEast, 0, 0, -1, buffer);
		}
		/// <summary>Queries a closed circle, including tangencies and radius zero. No allocations.</summary>
		public void QueryCircle(double north, double east, double radius, ChartQueryBuffer buffer)
		{
			if (!Finite(north) || !Finite(east) || !Finite(radius) || radius < 0 || !Finite(north + radius) || !Finite(north - radius) || !Finite(east + radius) || !Finite(east - radius)) throw new ArgumentException("Finite centre and nonnegative bounded radius required.");
			Query(north - radius, east - radius, north + radius, east + radius, north, east, radius, buffer);
		}
		private void Query(double a, double b, double c, double d, double north, double east, double radius, ChartQueryBuffer buffer)
		{
			if (buffer == null || buffer.Owner != this) throw new ArgumentException("Buffer must belong to this layer.");
			buffer.Count = 0; buffer.TotalMatches = 0;
			if (buffer.Generation == int.MaxValue) { Array.Clear(buffer.Seen, 0, buffer.Seen.Length); buffer.Generation = 0; }
			var generation = ++buffer.Generation; var length = 0;
			if (_grid.Count == 0 || c < _minN * 250.0 || d < _minE * 250.0 || a >= (_maxN + 1) * 250.0 || b >= (_maxE + 1) * 250.0) return;
			var n0 = (int)Math.Max(_minN, Math.Floor(a / 250)); var n1 = (int)Math.Min(_maxN, Math.Floor(c / 250));
			var e0 = (int)Math.Max(_minE, Math.Floor(b / 250)); var e1 = (int)Math.Min(_maxE, Math.Floor(d / 250));
			for (var n = n0; n <= n1; n++) for (var e = e0; e <= e1; e++)
			{
				if (!_grid.TryGetValue(GridKey(n, e), out var entries)) continue;
				for (var j = 0; j < entries.Length; j++) { var i = entries[j]; if (buffer.Seen[i] == generation) continue; buffer.Seen[i] = generation; buffer.Candidates[length++] = i; }
			}
			// Heap sort avoids comparer initialization or delegates even on the first query.
			for (var i = length / 2 - 1; i >= 0; i--) Sift(buffer.Candidates, i, length);
			for (var end = length - 1; end > 0; end--) { var t = buffer.Candidates[0]; buffer.Candidates[0] = buffer.Candidates[end]; buffer.Candidates[end] = t; Sift(buffer.Candidates, 0, end); }
			for (var j = 0; j < length; j++)
			{
				var i = buffer.Candidates[j]; var f = Features[i];
				if (f.MaximumNorth < a || f.MinimumNorth > c || f.MaximumEast < b || f.MinimumEast > d) continue;
				if (!ChartIntersection.Intersects(f, a, b, c, d, north, east, radius)) continue;
				buffer.TotalMatches++; if (buffer.Count < buffer.Results.Length) buffer.Results[buffer.Count++] = i;
			}
		}
		private static void Sift(int[] data, int root, int length)
		{
			while (root * 2 + 1 < length) { var child = root * 2 + 1; if (child + 1 < length && data[child] < data[child + 1]) child++; if (data[root] >= data[child]) return; var t = data[root]; data[root] = data[child]; data[child] = t; root = child; }
		}
	}
}
