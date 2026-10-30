using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;

namespace FerrySim.Chartplotter.Tests
{
	/// <summary>
	/// A small harbor built in memory, in the real packs' types, so the plotter's tests never read the big processed
	/// outputs: water across a 4 km square with depth bands shoaling northward (10, 5, 2, 0 m), a land block with a lake
	/// in its north-west, three charted contours, a 300-sounding grid, three kinds of hazard, a fairway with a
	/// recommended track, lateral, special and lit aids (plus one outside coverage), two landings, a barge and a bridge span.
	/// </summary>
	public static class SyntheticHarbor
	{
		public const string PierA = "Pier A";
		public const string PierB = "Pier B";
		public static readonly HorizontalVector PierAPosition = new HorizontalVector(-100, -400);
		public static readonly HorizontalVector PierBPosition = new HorizontalVector(300, 1500);
		public static readonly HorizontalVector PortBuoy = new HorizontalVector(-1800, -250);
		public static readonly HorizontalVector StarboardBuoy = new HorizontalVector(-1800, 250);
		public static readonly HorizontalVector OutsideAid = new HorizontalVector(5000, 5000);
		/// <summary>Where the land block is split between two cells.</summary>
		public const double SplitNorth = 1250;
		public const int SoundingColumns = 20;
		public const int SoundingRows = 15;

		private static HorizontalVector P(double north, double east) => new HorizontalVector(north, east);

		private static HarborValue Text(string text) => HarborValue.FromText(text);

		private static HarborValue Number(double value) => HarborValue.FromNumber(value);

		private static HarborValue Fields(params (string, HarborValue)[] fields)
		{
			var dictionary = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
			foreach (var (name, value) in fields)
			{
				dictionary[name] = value;
			}

			return HarborValue.FromFields(dictionary);
		}

		private static HarborValue Items(params HarborValue[] items) => HarborValue.FromItems(items);

		private static HarborPolygon Box(double minNorth, double minEast, double maxNorth, double maxEast, HarborPolygon hole = null)
		{
			var rings = new List<IReadOnlyList<HorizontalVector>>
			{
				new[] { P(minNorth, minEast), P(minNorth, maxEast), P(maxNorth, maxEast), P(maxNorth, minEast), P(minNorth, minEast) },
			};
			if (hole != null)
			{
				rings.Add(hole.Rings[0]);
			}

			return new HarborPolygon(rings);
		}

		private static HarborGeometry Polygon(HarborPolygon polygon) =>
			new HarborGeometry("Polygon", new[] { polygon }, new IReadOnlyList<HorizontalVector>[0], new HorizontalVector[0]);

		private static HarborGeometry Line(params HorizontalVector[] points) =>
			new HarborGeometry("LineString", new HarborPolygon[0], new IReadOnlyList<HorizontalVector>[] { points }, new HorizontalVector[0]);

		private static HarborGeometry Point(HorizontalVector point) =>
			new HarborGeometry("Point", new HarborPolygon[0], new IReadOnlyList<HorizontalVector>[0], new[] { point });

		public static HarborRuntimePack Runtime()
		{
			var aids = new List<HarborAid>
			{
				Aid("aid:1", PortBuoy, "BOYLAT", "port-hand", new[] { "R" }, "Fl R 4s", "Harbor Entrance Buoy 1"),
				Aid("aid:2", StarboardBuoy, "BOYLAT", "starboard-hand", new[] { "G" }, "Fl G 4s", "Harbor Entrance Buoy 2"),
				Aid("aid:3", P(-1200, 900), "BOYSPP", null, new[] { "Y" }, null, "Special Mark"),
				Aid("aid:4", P(-200, -300), "BCNLAT", "port-hand", new[] { "R" }, "Q R 1s", "Pier A Beacon"),
				Aid("aid:5", P(300, 700), "LIGHTS", null, new[] { "W" }, "F W", "Bridge Light"),
				Aid("aid:6", P(-900, 1200), "BOYSPP", null, new string[0], null, null),
				Aid("aid:7", P(-1400, -100), "BOYLAT", "preferred channel to starboard", new[] { "G", "R", "G" }, "Fl(2+1) G 6s", "Junction Buoy"),
				Aid("aid:8", OutsideAid, "BOYLAT", "port-hand", new[] { "R" }, "Fl R 2.5s", "Boundary Buoy"),
			};
			var stops = new List<HarborStop>
			{
				new HarborStop("stop:A", PierA, PierAPosition, new[] { new HarborBerth("berth:A", "barge:A", Fields(("berth_name", Text("Main")))) }, Fields(("stop_name", Text(PierA)))),
				new HarborStop("stop:B", PierB, PierBPosition, new[] { new HarborBerth("berth:B", "barge:B", Fields(("berth_name", Text("Main")))) }, Fields(("stop_name", Text(PierB)))),
			};
			var barges = new List<HarborBarge>
			{
				new HarborBarge("barge:A", Polygon(Box(-130, -440, -100, -380)), new HarborVerticalBounds(null, null), Fields(("floating", HarborValue.FromBoolean(true)))),
			};
			var span = new HarborSpan("span:1", "span-1", Line(P(200, -600), P(200, 600)), new HarborVerticalBounds(null, null), Fields(("clearances", Items())));
			var bridges = new List<HarborBridge> { new HarborBridge("bridge:1", new[] { span }, new HarborPier[0], Fields(("names", Items(Text("Test Bridge"))))) };
			var geoid = new HarborGeoidLattice(-2500, -2500, 5000, 2, 2, new double[] { 0, 0, 0, 0 }, new byte[] { 1 });
			var coverage = Polygon(Box(-2500, -2500, 2500, 2500));
			var manifest = Fields(("origin_latlon", Items(Number(40.7), Number(-74.015))), ("coordinate_frame", Text("NED horizontal metres; north,east; heights NAVD88 unless explicitly stated")));
			return new HarborRuntimePack("synthetic", new string('a', 64), new string('b', 64), aids, bridges, stops, barges, geoid, coverage, new HarborFootprint[0], manifest, "[]");
		}

		private static HarborAid Aid(string id, HorizontalVector position, string featureClass, string lateral, string[] colours, string light, string name)
		{
			var colourItems = new HarborValue[colours.Length];
			for (var i = 0; i < colours.Length; i++)
			{
				colourItems[i] = Text(colours[i]);
			}

			var fields = new List<(string, HarborValue)>
			{
				("feature_class", Text(featureClass)),
				("lateral_category", lateral != null ? Text(lateral) : HarborValue.Null),
				("colours", HarborValue.FromItems(colourItems)),
				("lights", light != null ? Items(Fields(("notation", Text(light)), ("full_notation", Text(light)))) : Items()),
				("name", name != null ? Text(name) : HarborValue.Null),
			};
			return new HarborAid(id, id, position, Fields(("properties", Fields(fields.ToArray()))));
		}

		public static HarborChartPack Chart(HarborRuntimePack runtime)
		{
			var layers = new ChartFeatureLayer[8];
			var land = new List<ChartFeature>();
			var lake = Box(1000, -1400, 1200, -1200);
			var landBlock = Box(500, -2000, 2000, -500, lake);
			// The land block is split along north 1250 as two ENC cells would clip it; the coast is one charted COALNE line around the whole block.
			land.Add(Feature("land:1", "LNDARE", Polygon(Box(500, -2000, SplitNorth, -500, lake)), Fields()));
			land.Add(Feature("land:2", "LNDARE", Polygon(Box(SplitNorth, -2000, 2000, -500)), Fields()));
			land.Add(Feature("land:3", "COALNE", Line(landBlock.Rings[0][0], landBlock.Rings[0][1], landBlock.Rings[0][2], landBlock.Rings[0][3], landBlock.Rings[0][4]), Fields()));
			layers[(int)ChartLayer.Land] = Layer(land);

			var depth = new List<ChartFeature>
			{
				Feature("depth:1", "DEPARE", Polygon(Box(-2000, -2000, -1000, 2000)), Fields(("DRVAL1", Number(10)), ("DRVAL2", Number(20)), ("depth_datum", Text("MLLW")))),
				Feature("depth:2", "DEPARE", Polygon(Box(-1000, -2000, 0, 2000)), Fields(("DRVAL1", Number(5)), ("DRVAL2", Number(10)), ("depth_datum", Text("MLLW")))),
				Feature("depth:3", "DEPARE", Polygon(Box(0, -2000, 500, 2000)), Fields(("DRVAL1", Number(2)), ("DRVAL2", Number(5)), ("depth_datum", Text("MLLW")))),
				Feature("depth:4", "DEPARE", Polygon(Box(500, -500, 2000, 2000)), Fields(("DRVAL1", Number(0)), ("DRVAL2", Number(2)), ("depth_datum", Text("MLLW")))),
				Feature("depth:5", "DRGARE", Polygon(Box(-1500, 300, -1200, 600)), Fields(("DRVAL1", Number(8)), ("depth_datum", Text("MLLW")))),
			};
			layers[(int)ChartLayer.DepthAreas] = Layer(depth);

			var contours = new List<ChartFeature>
			{
				Feature("contour:1", "DEPCNT", Line(P(-1000, -2000), P(-1000, 2000)), Fields(("VALDCO", Number(10)), ("depth_datum", Text("MLLW")))),
				Feature("contour:2", "DEPCNT", Line(P(0, -2000), P(0, 2000)), Fields(("VALDCO", Number(5)), ("depth_datum", Text("MLLW")))),
				Feature("contour:3", "DEPCNT", Line(P(500, -500), P(500, 2000)), Fields(("VALDCO", Number(2)), ("depth_datum", Text("MLLW")))),
			};
			layers[(int)ChartLayer.Contours] = Layer(contours);

			var soundings = new List<ChartFeature>();
			for (var row = 0; row < SoundingRows; row++)
			{
				for (var column = 0; column < SoundingColumns; column++)
				{
					var north = -1900.0 + row * 150.0;
					var east = -1900.0 + column * 200.0;
					var value = Math.Round(12.0 - (north + 2000.0) / 250.0 + column * 0.1, 1);
					var id = "sounding:" + row.ToString("00") + column.ToString("00");
					soundings.Add(Feature(id, "SOUNDG", Point(P(north, east)), Fields(("depth_m", Number(value)), ("depth_datum", Text("MLLW")))));
				}
			}

			layers[(int)ChartLayer.Soundings] = Layer(soundings);

			var hazards = new List<ChartFeature>
			{
				Feature("hazard:1", "UWTROC", Point(P(-300, 300)), Fields(("VALSOU", Number(1.2)))),
				Feature("hazard:2", "WRECKS", Point(P(-600, -800)), Fields(("CATWRK", Number(2)))),
				Feature("hazard:3", "OBSTRN", Polygon(Box(-1520, 1480, -1480, 1520)), Fields(("CATOBS", Number(1)))),
			};
			layers[(int)ChartLayer.Hazards] = Layer(hazards);

			var channels = new List<ChartFeature>
			{
				// The fairway is clipped into two cells along north -1500: its outline must not show the split.
				Feature("channel:1", "FAIRWY", Polygon(Box(-2000, -200, -1500, 200)), Fields(("OBJNAM", Text("Main Channel")))),
				Feature("channel:3", "FAIRWY", Polygon(Box(-1500, -200, -1000, 200)), Fields(("OBJNAM", Text("Main Channel")))),
				Feature("channel:2", "RECTRC", Line(P(-2000, 0), P(400, 0)), Fields(("ORIENT", Number(0)))),
			};
			layers[(int)ChartLayer.Channels] = Layer(channels);
			layers[(int)ChartLayer.RadarObstructions] = Layer(new List<ChartFeature>());
			layers[(int)ChartLayer.References] = Layer(new List<ChartFeature>());
			var manifest = Fields(("format", Text("ferrysim-harbor-chart/1")), ("depth_datum", Text("MLLW")), ("notice", Text("Not for navigation")));
			return new HarborChartPack(new string('c', 64), runtime.Digest, layers, manifest);
		}

		private static ChartFeature Feature(string id, string featureClass, HarborGeometry geometry, HarborValue properties) =>
			new ChartFeature(id, id, featureClass, geometry, Fields(("id", Text(id)), ("source_id", Text(id)), ("feature_class", Text(featureClass)), ("properties", properties)),
				geometry.MinimumNorth, geometry.MinimumEast, geometry.MaximumNorth, geometry.MaximumEast);

		private static ChartFeatureLayer Layer(List<ChartFeature> features)
		{
			features.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
			var grid = new Dictionary<long, List<int>>();
			for (var i = 0; i < features.Count; i++)
			{
				var f = features[i];
				for (var n = (int)Math.Floor(f.MinimumNorth / 250); n <= Math.Floor(f.MaximumNorth / 250); n++)
				{
					for (var e = (int)Math.Floor(f.MinimumEast / 250); e <= Math.Floor(f.MaximumEast / 250); e++)
					{
						var key = ChartFeatureLayer.GridKey(n, e);
						if (!grid.TryGetValue(key, out var list))
						{
							list = new List<int>();
							grid.Add(key, list);
						}

						list.Add(i);
					}
				}
			}

			var arrays = new Dictionary<long, int[]>();
			foreach (var pair in grid)
			{
				arrays[pair.Key] = pair.Value.ToArray();
			}

			return new ChartFeatureLayer(features, arrays);
		}
	}
}
