using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.Harbor.Tests
{
	/// <summary>
	/// Small fixtures for the harbor catalog's tests: a contacts.json with a few harbor stops in the shapes the real file has
	/// (a chart-checked face, a crew-reported face, a derived barge, a landing with three berths, and the three faces whose
	/// landing side is still a crew question), day manifests on disk, and a synthetic flat-bed environment for the depth
	/// checks. Nothing here reads the big processed outputs; <see cref="HarborCatalogRealDataTests"/> does that.
	/// </summary>
	internal static class HarborTestData
	{
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		/// <summary>The harbor contacts file as the pipeline writes it (gitignored), for the real-data tests.</summary>
		public static string RealContactsPath => Path.Combine(RepoRoot, "data", "processed", "harbor", "unity", "contacts.json");

		public static string SurfacesPath => Path.Combine(ProjectRoot, LandingSurfaceTable.DefaultAssetPath);

		public static string HarborDaysFolder => Path.Combine(RepoRoot, EnvironmentPacks.RegionFolder(HarborScenarioCatalog.Region), "days");

		/// <summary>An empty folder under artifacts/unity/harbor/&lt;name&gt;.</summary>
		public static string ArtifactFolder(string name)
		{
			var path = Path.Combine(RepoRoot, "artifacts", "unity", "harbor", name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		public static IReadOnlyList<KeyValuePair<string, VesselParameters>> Vessels()
		{
			var vessels = new List<KeyValuePair<string, VesselParameters>>();
			foreach (var file in new[] { "vessels/t-class.json", "vessels/k-class.json" })
			{
				vessels.Add(new KeyValuePair<string, VesselParameters>(file, VesselParametersJson.Load(Path.Combine(RepoRoot, file))));
			}

			return vessels;
		}

		/// <summary>
		/// One contacts.json segment with its left-hand water normal, in the harbor file's version 2 shape. Solid-side
		/// evidence is verified unless told otherwise, as it is for every charted or derived outline in the real file.
		/// </summary>
		public static JObject Segment(int id, string stopId, string stopName, double startNorth, double startEast, double endNorth, double endEast,
			string kind = "berth-face", bool waterSideVerified = true, bool primary = false, string berthId = null, string source = "enc-feature",
			string confidence = "medium", bool solidSideVerified = true, bool crewReported = false)
		{
			var dn = endNorth - startNorth;
			var de = endEast - startEast;
			var length = Math.Sqrt(dn * dn + de * de);
			var segment = new JObject
			{
				["id"] = id,
				["stop_id"] = stopId,
				["stop_name"] = stopName,
				["start_ne_m"] = new JArray(startNorth, startEast),
				["end_ne_m"] = new JArray(endNorth, endEast),
				["water_normal_ne"] = new JArray(de / length, -dn / length),
				["source"] = source,
				["confidence"] = confidence,
				["verification"] = crewReported ? "crew-reported landing side" : "needs-crew-verification",
				["water_side_verified_by_pipeline"] = waterSideVerified,
				["kind"] = kind,
				["solid_side_evidence"] = new JObject { ["verified"] = solidSideVerified, ["method"] = "enc-closed-outline" },
			};
			if (primary)
			{
				segment["primary_face"] = true;
			}

			if (crewReported)
			{
				segment["water_side_crew_reported"] = true;
			}

			if (berthId != null)
			{
				segment["berth_id"] = berthId;
			}

			return segment;
		}

		/// <summary>Marks a segment as a selectable berth face, as the exporter does.</summary>
		public static JObject Berth(JObject segment, string berthId, string name, int order, params int[] faceIds)
		{
			segment["berth_id"] = berthId;
			segment["berth_face"] = true;
			segment["berth_name"] = name;
			segment["berth_order"] = order;
			segment["berth_face_segment_ids"] = new JArray(faceIds.Length == 0 ? new[] { (int)segment["id"] } : faceIds);
			if (order == 0)
			{
				segment["primary_face"] = true;
			}

			return segment;
		}

		/// <summary>
		/// A harbor in miniature, every landing 300 m from the next so no start can be near another's structure:
		/// 4 Hunters Point South (chart-checked face), 19 North Williamsburg (crew-reported face), 20 Dumbo (a barge derived
		/// from the stop position, chart-checked), 87 Wall St/Pier 11 (east and north pontoons startable, the south one kept
		/// for contact on its solid side only), and the three crew questions: 113 East 90th St (derived face, water side
		/// unverified, with a kept barge side), 118 Sunset Park/BAT (every segment left out) and 137 St. George (a two-segment
		/// charted face, unverified).
		/// </summary>
		public static JArray HarborContacts()
		{
			return new JArray(
				Berth(Segment(41, "4", "Hunters Point South", 0, 0, 0, 30), "derived-barge:4:A", "Main berth", 0),
				Berth(Segment(191, "19", "North Williamsburg", 300, 0, 300, 30, waterSideVerified: false, crewReported: true), "enc:19A", "Main berth", 0),
				Berth(Segment(201, "20", "Dumbo/Fulton Ferry", 600, 0, 600, 30, source: "derived-from-gtfs", confidence: "low"), "derived-barge:20:B", "Main berth", 0),
				Berth(Segment(871, "87", "Wall St/Pier 11", 900, 0, 900, 30), "enc:EAST", "Outer pontoon (east)", 0),
				Berth(Segment(872, "87", "Wall St/Pier 11", 1200, 0, 1200, 30), "enc:NORTH", "North pontoon", 1),
				Berth(Segment(873, "87", "Wall St/Pier 11", 1500, 0, 1500, 30, waterSideVerified: false), "enc:SOUTH", "South pontoon", 2),
				Berth(Segment(1131, "113", "East 90th St", 1800, 0, 1800, 30, waterSideVerified: false, source: "derived-from-gtfs", confidence: "low"), "derived-barge:113:C", "Main berth", 0),
				Segment(1132, "113", "East 90th St", 1800, 30, 1790, 30, kind: "barge-end", waterSideVerified: false, source: "derived-from-gtfs", confidence: "low"),
				Berth(Segment(1181, "118", "Sunset Park/BAT", 2100, 0, 2100, 30, waterSideVerified: false, source: "derived-from-gtfs", confidence: "low", solidSideVerified: false), "derived-barge:118:D", "Main berth", 0),
				Berth(Segment(1371, "137", "St. George", 2400, 0, 2400, 15, waterSideVerified: false, confidence: "low"), "enc:137", "Main berth", 0, 1371, 1372),
				Berth(Segment(1372, "137", "St. George", 2400, 15, 2400, 30, waterSideVerified: false, confidence: "low"), "enc:137", "Main berth", 0, 1371, 1372));
		}

		public static LandingGeometry Geometry(JArray contacts = null) =>
			LandingGeometry.Parse((contacts ?? HarborContacts()).ToString(), "harbor-contacts.json", LandingSurfaceTable.Load(SurfacesPath));

		/// <summary>
		/// A temporary <c>data/processed</c> root with day manifests for the harbor (no series files: listing and start checks
		/// read only the manifest) and, when asked, a traffic manifest for 2025-07-14. The caller deletes it.
		/// </summary>
		public static string PacksRoot(bool withTraffic = true)
		{
			var root = Path.Combine(Path.GetTempPath(), "ferrysim-harbor-picker-" + Guid.NewGuid().ToString("N"));
			WriteDay(root, "2025-07-14", "2025-07-14T04:00:00Z", withWind: true);
			WriteDay(root, "2025-01-13", "2025-01-13T05:00:00Z", withWind: false);
			if (withTraffic)
			{
				var traffic = Path.Combine(root, "harbor", "traffic", "2025-07-14");
				Directory.CreateDirectory(traffic);
				File.WriteAllText(Path.Combine(traffic, "manifest.json"), "{}");
			}

			return root;
		}

		private static void WriteDay(string root, string date, string epoch, bool withWind)
		{
			var folder = Path.Combine(root, HarborScenarioCatalog.Region, "env", "days", date);
			Directory.CreateDirectory(folder);
			var manifest = new JObject
			{
				["format"] = "ferrysim-env/1",
				["region"] = HarborScenarioCatalog.Region,
				["date"] = date,
				["epoch_utc"] = epoch,
				["time_coverage_s"] = new JArray(-3600, 90000),
				["wind_stations"] = withWind ? new JArray(new JObject { ["id"] = "8516945" }) : new JArray(),
			};
			File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToString());
		}

		/// <summary>A flat bed at <paramref name="bed"/> m NAVD88 under the whole miniature harbor, with the water at <paramref name="level"/> m all day.</summary>
		public static DataEnvironment FlatEnvironment(float bed, double level = 0.0, string date = "2025-07-14")
		{
			var rows = 60;
			var columns = 30;
			var values = Enumerable.Repeat(bed, rows * columns).ToArray();
			var grid = new DepthGrid("flat", -200, -200, 50, rows, columns, values);
			var depth = new DepthSet("flat", "NAVD88", 20, new[] { grid }, Array.Empty<DepthSource>(), string.Empty);
			var times = new[] { -3600.0, 90000.0 };
			var current = new CurrentStation("current", "Synthetic", "synthetic", 40.7, -74.015, 0, 0, null, "synthetic", "",
				new VectorSeries(times, new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }));
			var station = new WaterLevelStation("level", "Synthetic", 40.7, -74.015, 0, 0, "synthetic", "",
				new ScalarSeries(times, new[] { level, level }), new[] { false, false });
			var epoch = new DateTimeOffset(DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddHours(4), TimeSpan.Zero);
			var pack = new EnvironmentPack(HarborScenarioCatalog.Region + "/" + date, date, epoch, -3600, 90000, depth,
				new[] { current }, new[] { station }, Array.Empty<WindStation>(), Array.Empty<string>(), "");
			return new DataEnvironment(pack, epoch);
		}

		/// <summary>Depth environments for the tests: one flat bed for every day, and a count of how often it was asked for.</summary>
		public sealed class FlatDepths : IStartDepthEnvironments
		{
			private readonly float _bed;
			private readonly double _level;

			public FlatDepths(float bed, double level = 0.0)
			{
				_bed = bed;
				_level = level;
			}

			public int Requests { get; private set; }

			public DataEnvironment Get(EnvironmentPackDay day, out string problem)
			{
				Requests++;
				problem = null;
				return FlatEnvironment(_bed, _level, day.Date);
			}
		}

		/// <summary>A launcher that records what it was asked to start and can be told to refuse.</summary>
		public sealed class RecordingLauncher : IScenarioLauncher
		{
			public List<ScenarioSettings> Started { get; } = new List<ScenarioSettings>();

			public string Problem { get; set; }

			public bool TryStart(ScenarioSettings settings, out string problem)
			{
				problem = Problem;
				if (problem != null)
				{
					return false;
				}

				Started.Add(settings);
				return true;
			}
		}
	}
}
