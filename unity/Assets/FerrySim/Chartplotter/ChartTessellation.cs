using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Navigation;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;

namespace FerrySim.Chartplotter
{
	/// <summary>What a hazard symbol stands for.</summary>
	public enum HazardKind
	{
		Rock = 0,
		Wreck = 1,
		Obstruction = 2,
	}

	/// <summary>How an aid is drawn: its lateral sense and whether it is a buoy, a beacon or a light.</summary>
	public enum AidKind
	{
		Other = 0,
		PortBuoy = 1,
		StarboardBuoy = 2,
		PreferredChannelToStarboard = 3,
		PreferredChannelToPort = 4,
		SpecialBuoy = 5,
		PortBeacon = 6,
		StarboardBeacon = 7,
		OtherBeacon = 8,
		Light = 9,
	}

	/// <summary>
	/// The chart pack and the runtime pack turned into drawable arrays once, at connect: polygons triangulated
	/// (<see cref="PolygonTriangulator"/>), lines flattened, soundings formatted and sorted shallowest first, aids classified.
	/// Everything stays in world north/east metres as doubles; the canvas projects it to pixels when the view changes.
	/// Nothing here is collision or simulation geometry.
	/// </summary>
	public sealed class ChartTessellation
	{
		/// <summary>Filled polygons: every vertex, and every triangle as three global vertex indices.</summary>
		public sealed class PolygonSet
		{
			public readonly List<double> North = new List<double>();
			public readonly List<double> East = new List<double>();
			public readonly List<int> Triangles = new List<int>();
			/// <summary>Per item (one polygon): its triangle range, its bounds, its chart feature and its depth value (NaN when none).</summary>
			public readonly List<int> TriangleStart = new List<int>();
			public readonly List<int> TriangleCount = new List<int>();
			public readonly List<double> MinNorth = new List<double>();
			public readonly List<double> MinEast = new List<double>();
			public readonly List<double> MaxNorth = new List<double>();
			public readonly List<double> MaxEast = new List<double>();
			public readonly List<double> Value = new List<double>();
			/// <summary>Per chart feature: the item range, so a layer query maps to items.</summary>
			public int[] FeatureItemStart = Array.Empty<int>();
			public int[] FeatureItemCount = Array.Empty<int>();

			public int Count => TriangleStart.Count;
		}

		/// <summary>Polylines: every vertex, and per item the vertex range and bounds.</summary>
		public sealed class LineSet
		{
			public readonly List<double> North = new List<double>();
			public readonly List<double> East = new List<double>();
			public readonly List<int> Start = new List<int>();
			public readonly List<int> Length = new List<int>();
			public readonly List<double> MinNorth = new List<double>();
			public readonly List<double> MinEast = new List<double>();
			public readonly List<double> MaxNorth = new List<double>();
			public readonly List<double> MaxEast = new List<double>();
			public readonly List<double> Value = new List<double>();
			public int[] FeatureItemStart = Array.Empty<int>();
			public int[] FeatureItemCount = Array.Empty<int>();

			public int Count => Start.Count;
		}

		/// <summary>Soundings with their labels in both units, and the order shallowest first (then by id).</summary>
		public sealed class SoundingSet
		{
			public double[] North = Array.Empty<double>();
			public double[] East = Array.Empty<double>();
			public double[] Depth = Array.Empty<double>();
			public string[] TextMetres = Array.Empty<string>();
			public string[] TextFeet = Array.Empty<string>();
			public int[] ByDepth = Array.Empty<int>();

			public int Count => North.Length;
		}

		public sealed class HazardSet
		{
			public readonly List<double> North = new List<double>();
			public readonly List<double> East = new List<double>();
			public readonly List<HazardKind> Kind = new List<HazardKind>();

			public int Count => North.Count;
		}

		public sealed class AidSet
		{
			public readonly List<double> North = new List<double>();
			public readonly List<double> East = new List<double>();
			public readonly List<AidKind> Kind = new List<AidKind>();
			public readonly List<bool> Lit = new List<bool>();
			public readonly List<string> LightText = new List<string>();
			public readonly List<string> Name = new List<string>();

			public int Count => North.Count;
		}

		public sealed class StopSet
		{
			public readonly List<double> North = new List<double>();
			public readonly List<double> East = new List<double>();
			public readonly List<string> Name = new List<string>();
			public readonly List<string> Id = new List<string>();

			public int Count => North.Count;
		}

		public HarborChartPack Chart { get; private set; }

		/// <summary>
		/// The digest of the runtime pack the tessellation was built from. The pack itself is not retained: everything the
		/// rendering needs was copied into the sets at build, so a cached tessellation keeps no scene's runtime pack alive
		/// (the composition root loads a fresh one for every scene).
		/// </summary>
		public string RuntimeDigest { get; private set; }

		public PolygonSet Land { get; } = new PolygonSet();

		public PolygonSet DepthAreas { get; } = new PolygonSet();

		public PolygonSet Fairways { get; } = new PolygonSet();

		public LineSet Coastlines { get; } = new LineSet();

		public LineSet Contours { get; } = new LineSet();

		public LineSet ChannelLines { get; } = new LineSet();

		public LineSet FairwayOutlines { get; } = new LineSet();

		public LineSet HazardOutlines { get; } = new LineSet();

		public LineSet Bridges { get; } = new LineSet();

		public LineSet Barges { get; } = new LineSet();

		public SoundingSet Soundings { get; } = new SoundingSet();

		public HazardSet Hazards { get; } = new HazardSet();

		public AidSet Aids { get; } = new AidSet();

		public StopSet Stops { get; } = new StopSet();

		/// <summary>Distinct charted contour depths, ascending, m below chart datum.</summary>
		public double[] ChartedContours { get; private set; } = Array.Empty<double>();

		/// <summary>Time taken to build, ms, for the report.</summary>
		public double BuildMilliseconds { get; private set; }

		public int TriangleCount => (Land.Triangles.Count + DepthAreas.Triangles.Count + Fairways.Triangles.Count) / 3;

		public int VertexCount => Land.North.Count + DepthAreas.North.Count + Fairways.North.Count + Coastlines.North.Count + Contours.North.Count +
			ChannelLines.North.Count + FairwayOutlines.North.Count + HazardOutlines.North.Count + Bridges.North.Count + Barges.North.Count;

		/// <summary>Builds every drawable set from the two verified packs. Allocates freely: call it once.</summary>
		public static ChartTessellation Build(HarborChartPack chart, HarborRuntimePack runtime)
		{
			if (chart == null) throw new ArgumentNullException(nameof(chart));
			if (runtime == null) throw new ArgumentNullException(nameof(runtime));
			var watch = Stopwatch.StartNew();
			var result = new ChartTessellation { Chart = chart, RuntimeDigest = runtime.Digest };
			result.BuildLand(chart[ChartLayer.Land]);
			result.BuildDepthAreas(chart[ChartLayer.DepthAreas]);
			result.BuildContours(chart[ChartLayer.Contours]);
			result.BuildSoundings(chart[ChartLayer.Soundings]);
			result.BuildHazards(chart[ChartLayer.Hazards]);
			result.BuildChannels(chart[ChartLayer.Channels]);
			result.BuildRuntime(runtime);
			watch.Stop();
			result.BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
			return result;
		}

		private static double Number(HarborValue value) => value.Number ?? double.NaN;

		private void BuildLand(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			Land.FeatureItemStart = new int[features.Count];
			Land.FeatureItemCount = new int[features.Count];
			Coastlines.FeatureItemStart = new int[features.Count];
			Coastlines.FeatureItemCount = new int[features.Count];
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				Land.FeatureItemStart[i] = Land.Count;
				Coastlines.FeatureItemStart[i] = Coastlines.Count;
				if (feature.FeatureClass == "LNDARE")
				{
					AddPolygons(Land, feature.Geometry, double.NaN);
				}

				// Only charted coastline lines: a land polygon's ring includes the ENC cell's clip edges, which are not coast.
				if (feature.FeatureClass == "COALNE")
				{
					AddLines(Coastlines, feature.Geometry, double.NaN, true, false);
				}
				Land.FeatureItemCount[i] = Land.Count - Land.FeatureItemStart[i];
				Coastlines.FeatureItemCount[i] = Coastlines.Count - Coastlines.FeatureItemStart[i];
			}
		}

		private void BuildDepthAreas(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			DepthAreas.FeatureItemStart = new int[features.Count];
			DepthAreas.FeatureItemCount = new int[features.Count];
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				DepthAreas.FeatureItemStart[i] = DepthAreas.Count;
				AddPolygons(DepthAreas, feature.Geometry, Number(feature.Properties["DRVAL1"]));
				DepthAreas.FeatureItemCount[i] = DepthAreas.Count - DepthAreas.FeatureItemStart[i];
			}
		}

		private void BuildContours(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			Contours.FeatureItemStart = new int[features.Count];
			Contours.FeatureItemCount = new int[features.Count];
			var depths = new SortedSet<double>();
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				var depth = Number(feature.Properties["VALDCO"]);
				if (!double.IsNaN(depth))
				{
					depths.Add(depth);
				}

				Contours.FeatureItemStart[i] = Contours.Count;
				AddLines(Contours, feature.Geometry, depth, true);
				Contours.FeatureItemCount[i] = Contours.Count - Contours.FeatureItemStart[i];
			}

			ChartedContours = new double[depths.Count];
			depths.CopyTo(ChartedContours);
		}

		private void BuildSoundings(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			var north = new List<double>();
			var east = new List<double>();
			var depth = new List<double>();
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				var value = Number(feature.Properties["depth_m"]);
				var points = feature.Geometry?.Points;
				if (points == null || double.IsNaN(value))
				{
					continue;
				}

				for (var p = 0; p < points.Count; p++)
				{
					north.Add(points[p].North);
					east.Add(points[p].East);
					depth.Add(value);
				}
			}

			Soundings.North = north.ToArray();
			Soundings.East = east.ToArray();
			Soundings.Depth = depth.ToArray();
			Soundings.TextMetres = new string[depth.Count];
			Soundings.TextFeet = new string[depth.Count];
			Soundings.ByDepth = new int[depth.Count];
			for (var i = 0; i < depth.Count; i++)
			{
				Soundings.TextMetres[i] = SoundingText(depth[i], false);
				Soundings.TextFeet[i] = SoundingText(depth[i], true);
				Soundings.ByDepth[i] = i;
			}

			// Shallowest first; equal depths keep feature (id) order, so the declutter is deterministic.
			var depths = Soundings.Depth;
			Array.Sort(Soundings.ByDepth, (a, b) => depths[a] != depths[b] ? depths[a].CompareTo(depths[b]) : a.CompareTo(b));
		}

		/// <summary>A sounding label: metres to a tenth under 10 m and whole metres above; feet whole.</summary>
		public static string SoundingText(double metres, bool feet)
		{
			if (feet)
			{
				return Math.Round(metres / Units.MetresPerFoot).ToString("0", CultureInfo.InvariantCulture);
			}

			return metres < 9.95 ? metres.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(metres).ToString("0", CultureInfo.InvariantCulture);
		}

		private void BuildHazards(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			HazardOutlines.FeatureItemStart = new int[features.Count];
			HazardOutlines.FeatureItemCount = new int[features.Count];
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				var kind = feature.FeatureClass == "WRECKS" ? HazardKind.Wreck : feature.FeatureClass == "UWTROC" ? HazardKind.Rock : HazardKind.Obstruction;
				var geometry = feature.Geometry;
				HazardOutlines.FeatureItemStart[i] = HazardOutlines.Count;
				if (geometry != null)
				{
					for (var p = 0; p < geometry.Points.Count; p++)
					{
						Hazards.North.Add(geometry.Points[p].North);
						Hazards.East.Add(geometry.Points[p].East);
						Hazards.Kind.Add(kind);
					}

					if (geometry.Polygons.Count > 0 || geometry.Lines.Count > 0)
					{
						AddLines(HazardOutlines, geometry, double.NaN, true);
						// One symbol at the centre of an area or line hazard.
						Hazards.North.Add((geometry.MinimumNorth + geometry.MaximumNorth) * 0.5);
						Hazards.East.Add((geometry.MinimumEast + geometry.MaximumEast) * 0.5);
						Hazards.Kind.Add(kind);
					}
				}

				HazardOutlines.FeatureItemCount[i] = HazardOutlines.Count - HazardOutlines.FeatureItemStart[i];
			}
		}

		private void BuildChannels(ChartFeatureLayer layer)
		{
			var features = layer.Features;
			Fairways.FeatureItemStart = new int[features.Count];
			Fairways.FeatureItemCount = new int[features.Count];
			FairwayOutlines.FeatureItemStart = new int[features.Count];
			FairwayOutlines.FeatureItemCount = new int[features.Count];
			ChannelLines.FeatureItemStart = new int[features.Count];
			ChannelLines.FeatureItemCount = new int[features.Count];
			for (var i = 0; i < features.Count; i++)
			{
				var feature = features[i];
				Fairways.FeatureItemStart[i] = Fairways.Count;
				FairwayOutlines.FeatureItemStart[i] = FairwayOutlines.Count;
				ChannelLines.FeatureItemStart[i] = ChannelLines.Count;
				if (feature.Geometry != null)
				{
					if (feature.Geometry.Polygons.Count > 0)
					{
						AddPolygons(Fairways, feature.Geometry, double.NaN);
						AddLines(FairwayOutlines, feature.Geometry, double.NaN, false);
					}

					AddLines(ChannelLines, feature.Geometry, double.NaN, true, false);
				}

				Fairways.FeatureItemCount[i] = Fairways.Count - Fairways.FeatureItemStart[i];
				FairwayOutlines.FeatureItemCount[i] = FairwayOutlines.Count - FairwayOutlines.FeatureItemStart[i];
				ChannelLines.FeatureItemCount[i] = ChannelLines.Count - ChannelLines.FeatureItemStart[i];
			}

			DropSharedEdges(FairwayOutlines);
		}

		/// <summary>
		/// Splits a line set's rings into runs that skip edges another ring of the set traces in reverse: the clip edges
		/// where one charted area meets its continuation in the next ENC cell. Edges are matched on centimetre-quantised endpoints.
		/// </summary>
		private static void DropSharedEdges(LineSet set)
		{
			var edges = new HashSet<EdgeKey>();
			for (var item = 0; item < set.Count; item++)
			{
				var first = set.Start[item];
				for (var i = 1; i < set.Length[item]; i++)
				{
					edges.Add(new EdgeKey(set.North[first + i - 1], set.East[first + i - 1], set.North[first + i], set.East[first + i]));
				}
			}

			var rebuilt = new LineSet();
			var featureStart = new int[set.FeatureItemStart.Length];
			var featureCount = new int[set.FeatureItemStart.Length];
			for (var feature = 0; feature < set.FeatureItemStart.Length; feature++)
			{
				featureStart[feature] = rebuilt.Count;
				var end = set.FeatureItemStart[feature] + set.FeatureItemCount[feature];
				for (var item = set.FeatureItemStart[feature]; item < end; item++)
				{
					var first = set.Start[item];
					var run = new List<HorizontalVector>();
					for (var i = 1; i < set.Length[item]; i++)
					{
						var shared = edges.Contains(new EdgeKey(set.North[first + i], set.East[first + i], set.North[first + i - 1], set.East[first + i - 1]));
						if (shared)
						{
							if (run.Count > 1) AddLine(rebuilt, run, set.Value[item], false);
							run.Clear();
							continue;
						}

						if (run.Count == 0) run.Add(new HorizontalVector(set.North[first + i - 1], set.East[first + i - 1]));
						run.Add(new HorizontalVector(set.North[first + i], set.East[first + i]));
					}

					if (run.Count > 1) AddLine(rebuilt, run, set.Value[item], false);
				}

				featureCount[feature] = rebuilt.Count - featureStart[feature];
			}

			set.North.Clear(); set.North.AddRange(rebuilt.North);
			set.East.Clear(); set.East.AddRange(rebuilt.East);
			set.Start.Clear(); set.Start.AddRange(rebuilt.Start);
			set.Length.Clear(); set.Length.AddRange(rebuilt.Length);
			set.MinNorth.Clear(); set.MinNorth.AddRange(rebuilt.MinNorth);
			set.MinEast.Clear(); set.MinEast.AddRange(rebuilt.MinEast);
			set.MaxNorth.Clear(); set.MaxNorth.AddRange(rebuilt.MaxNorth);
			set.MaxEast.Clear(); set.MaxEast.AddRange(rebuilt.MaxEast);
			set.Value.Clear(); set.Value.AddRange(rebuilt.Value);
			set.FeatureItemStart = featureStart;
			set.FeatureItemCount = featureCount;
		}

		/// <summary>
		/// A directed edge's identity: all four centimetre-quantised coordinates, compared in full. The hash only buckets;
		/// a collision between two different edges can never make one count as the other's reversed seam.
		/// </summary>
		private readonly struct EdgeKey : IEquatable<EdgeKey>
		{
			private readonly long _n0, _e0, _n1, _e1;

			public EdgeKey(double n0, double e0, double n1, double e1)
			{
				_n0 = (long)Math.Round(n0 * 100.0);
				_e0 = (long)Math.Round(e0 * 100.0);
				_n1 = (long)Math.Round(n1 * 100.0);
				_e1 = (long)Math.Round(e1 * 100.0);
			}

			public bool Equals(EdgeKey other) => _n0 == other._n0 && _e0 == other._e0 && _n1 == other._n1 && _e1 == other._e1;

			public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);

			public override int GetHashCode()
			{
				unchecked
				{
					var hash = 17L;
					hash = hash * 31 + _n0;
					hash = hash * 31 + _e0;
					hash = hash * 31 + _n1;
					hash = hash * 31 + _e1;
					return (int)(hash ^ (hash >> 32));
				}
			}
		}

		private void BuildRuntime(HarborRuntimePack runtime)
		{
			for (var i = 0; i < runtime.Stops.Count; i++)
			{
				var stop = runtime.Stops[i];
				Stops.North.Add(stop.Position.North);
				Stops.East.Add(stop.Position.East);
				Stops.Name.Add(stop.Name ?? string.Empty);
				Stops.Id.Add(stop.Id);
			}

			Barges.FeatureItemStart = new int[runtime.Barges.Count];
			Barges.FeatureItemCount = new int[runtime.Barges.Count];
			for (var i = 0; i < runtime.Barges.Count; i++)
			{
				Barges.FeatureItemStart[i] = Barges.Count;
				AddLines(Barges, runtime.Barges[i].Geometry, double.NaN, true);
				Barges.FeatureItemCount[i] = Barges.Count - Barges.FeatureItemStart[i];
			}

			Bridges.FeatureItemStart = new int[runtime.Bridges.Count];
			Bridges.FeatureItemCount = new int[runtime.Bridges.Count];
			for (var i = 0; i < runtime.Bridges.Count; i++)
			{
				Bridges.FeatureItemStart[i] = Bridges.Count;
				var bridge = runtime.Bridges[i];
				for (var s = 0; s < bridge.Spans.Count; s++)
				{
					AddLines(Bridges, bridge.Spans[s].Geometry, double.NaN, true);
				}

				Bridges.FeatureItemCount[i] = Bridges.Count - Bridges.FeatureItemStart[i];
			}

			for (var i = 0; i < runtime.Aids.Count; i++)
			{
				var aid = runtime.Aids[i];
				if (!InCoverage(runtime.Coverage, aid.Position.North, aid.Position.East))
				{
					// Boundary aids outside active coverage are neither drawn nor given a datum (decision 0005).
					continue;
				}

				var properties = aid.Properties;
				var lights = properties["lights"].Items;
				var lit = lights != null && lights.Count > 0;
				Aids.North.Add(aid.Position.North);
				Aids.East.Add(aid.Position.East);
				Aids.Kind.Add(ClassifyAid(properties["feature_class"].Text, properties["lateral_category"].Text, properties["colours"].Items));
				Aids.Lit.Add(lit);
				Aids.LightText.Add(lit ? lights[0]["notation"].Text ?? lights[0]["full_notation"].Text ?? string.Empty : string.Empty);
				Aids.Name.Add(properties["name"].Text ?? string.Empty);
			}
		}

		private static bool InCoverage(HarborGeometry coverage, double north, double east)
		{
			if (coverage == null || coverage.Polygons.Count == 0)
			{
				return true;
			}

			for (var p = 0; p < coverage.Polygons.Count; p++)
			{
				if (coverage.Polygons[p].Contains(north, east))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The symbol an ENC aid gets from its class, lateral category and colours.</summary>
		public static AidKind ClassifyAid(string featureClass, string lateral, IReadOnlyList<HarborValue> colours)
		{
			var beacon = featureClass == "BCNLAT" || featureClass == "BCNSPP" || featureClass == "DAYMAR";
			if (featureClass == "LIGHTS")
			{
				return AidKind.Light;
			}

			if (lateral == "port-hand")
			{
				return beacon ? AidKind.PortBeacon : AidKind.PortBuoy;
			}

			if (lateral == "starboard-hand")
			{
				return beacon ? AidKind.StarboardBeacon : AidKind.StarboardBuoy;
			}

			if (lateral == "preferred channel to starboard")
			{
				return AidKind.PreferredChannelToStarboard;
			}

			if (lateral == "preferred channel to port")
			{
				return AidKind.PreferredChannelToPort;
			}

			if (featureClass == "BOYSPP" || (colours != null && colours.Count == 1 && colours[0].Text == "Y"))
			{
				return beacon ? AidKind.OtherBeacon : AidKind.SpecialBuoy;
			}

			return beacon ? AidKind.OtherBeacon : AidKind.Other;
		}

		private static void AddPolygons(PolygonSet set, HarborGeometry geometry, double value)
		{
			if (geometry == null)
			{
				return;
			}

			for (var p = 0; p < geometry.Polygons.Count; p++)
			{
				var polygon = geometry.Polygons[p];
				var triangles = PolygonTriangulator.Triangulate(polygon.Rings, out var vertices);
				if (triangles.Length == 0)
				{
					continue;
				}

				var baseIndex = set.North.Count;
				double minN = double.PositiveInfinity, minE = double.PositiveInfinity, maxN = double.NegativeInfinity, maxE = double.NegativeInfinity;
				for (var v = 0; v + 1 < vertices.Length; v += 2)
				{
					var east = vertices[v];
					var north = vertices[v + 1];
					set.East.Add(east);
					set.North.Add(north);
					minN = Math.Min(minN, north);
					maxN = Math.Max(maxN, north);
					minE = Math.Min(minE, east);
					maxE = Math.Max(maxE, east);
				}

				set.TriangleStart.Add(set.Triangles.Count);
				set.TriangleCount.Add(triangles.Length);
				for (var t = 0; t < triangles.Length; t++)
				{
					set.Triangles.Add(baseIndex + triangles[t]);
				}

				set.MinNorth.Add(minN);
				set.MinEast.Add(minE);
				set.MaxNorth.Add(maxN);
				set.MaxEast.Add(maxE);
				set.Value.Add(value);
			}
		}

		private static void AddLines(LineSet set, HarborGeometry geometry, double value, bool includeLines, bool includeRings = true)
		{
			if (geometry == null)
			{
				return;
			}

			if (includeLines)
			{
				for (var l = 0; l < geometry.Lines.Count; l++)
				{
					AddLine(set, geometry.Lines[l], value, false);
				}
			}

			if (includeRings)
			{
				for (var p = 0; p < geometry.Polygons.Count; p++)
				{
					var rings = geometry.Polygons[p].Rings;
					for (var r = 0; r < rings.Count; r++)
					{
						AddLine(set, rings[r], value, true);
					}
				}
			}
		}

		private static void AddLine(LineSet set, IReadOnlyList<HorizontalVector> points, double value, bool close)
		{
			if (points.Count < 2)
			{
				return;
			}

			var start = set.North.Count;
			double minN = double.PositiveInfinity, minE = double.PositiveInfinity, maxN = double.NegativeInfinity, maxE = double.NegativeInfinity;
			for (var i = 0; i < points.Count; i++)
			{
				set.North.Add(points[i].North);
				set.East.Add(points[i].East);
				minN = Math.Min(minN, points[i].North);
				maxN = Math.Max(maxN, points[i].North);
				minE = Math.Min(minE, points[i].East);
				maxE = Math.Max(maxE, points[i].East);
			}

			if (close && (points[0].North != points[points.Count - 1].North || points[0].East != points[points.Count - 1].East))
			{
				set.North.Add(points[0].North);
				set.East.Add(points[0].East);
			}

			set.Start.Add(start);
			set.Length.Add(set.North.Count - start);
			set.MinNorth.Add(minN);
			set.MinEast.Add(minE);
			set.MaxNorth.Add(maxN);
			set.MaxEast.Add(maxE);
			set.Value.Add(value);
		}
	}
}
