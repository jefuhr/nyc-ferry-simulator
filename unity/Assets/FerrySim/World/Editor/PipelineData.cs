using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Reads the data pipeline's processed outputs for a slice (data/processed/&lt;slice&gt;/, gitignored;
	/// build them with `uv run --project data-pipeline python data-pipeline/ferrydata build --slice
	/// lower-east-river`). GeoJSON geometry is WGS84 [lon, lat]; local_coordinates are [north_m, east_m].
	/// The datum station's tidal datums come from the raw CO-OPS file in the cache, checked against the
	/// sha256 the pipeline recorded.
	/// </summary>
	public sealed class PipelineData
	{
		public const string DefaultSlice = "lower-east-river";

		private PipelineData()
		{
		}

		public string SliceDirectory { get; private set; }

		public List<GtfsLanding> Landings { get; } = new List<GtfsLanding>();

		public List<LandingFace> Faces { get; } = new List<LandingFace>();

		public List<BridgeFeature> Bridges { get; } = new List<BridgeFeature>();

		public string BridgeCells { get; private set; }

		public GeoidOffsets Geoid { get; private set; }

		public TidalDatums Datums { get; private set; }

		/// <summary>Repository root: Application.dataPath is &lt;repo&gt;/unity/Assets.</summary>
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string SliceDirectoryFor(string slice) => Path.Combine(RepoRoot, "data", "processed", slice);

		/// <summary>Raw-data cache root, as the pipeline resolves it (FERRYSIM_CACHE or ~/FerrySim-cache, then data/raw).</summary>
		public static string RawCacheRoot
		{
			get
			{
				var baseDirectory = Environment.GetEnvironmentVariable("FERRYSIM_CACHE");
				if (string.IsNullOrEmpty(baseDirectory))
				{
					baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FerrySim-cache");
				}

				return Path.Combine(baseDirectory, "data", "raw");
			}
		}

		/// <summary>True when the slice's processed outputs this tool needs are present.</summary>
		public static bool Exists(string slice = DefaultSlice)
		{
			var directory = SliceDirectoryFor(slice);
			return RequiredFiles.All(name => File.Exists(Path.Combine(directory, name)));
		}

		public static readonly string[] RequiredFiles =
		{
			"landings.geojson",
			"landing_faces.geojson",
			"bridges.geojson",
			"geoid_offset.json",
			"depth_merged_navd88.json",
		};

		/// <summary>Loads everything; throws with a clear message when a file is missing or malformed.</summary>
		public static PipelineData Load(string slice = DefaultSlice)
		{
			var directory = SliceDirectoryFor(slice);
			foreach (var name in RequiredFiles)
			{
				var path = Path.Combine(directory, name);
				if (!File.Exists(path))
				{
					throw new FileNotFoundException($"Pipeline output {path} is missing. Run: uv run --project data-pipeline python data-pipeline/ferrydata build --slice {slice}", path);
				}
			}

			var data = new PipelineData { SliceDirectory = directory };
			data.ReadLandings(ReadJson(Path.Combine(directory, "landings.geojson")));
			data.ReadFaces(ReadJson(Path.Combine(directory, "landing_faces.geojson")));
			data.ReadBridges(ReadJson(Path.Combine(directory, "bridges.geojson")));
			data.Geoid = GeoidOffsets.Read(ReadJson(Path.Combine(directory, "geoid_offset.json")));
			data.Datums = TidalDatums.Read(ReadJson(Path.Combine(directory, "depth_merged_navd88.json")), RawCacheRoot);
			return data;
		}

		private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path));

		private void ReadLandings(JObject collection)
		{
			foreach (var feature in Features(collection))
			{
				var properties = (JObject)feature["properties"];
				Landings.Add(new GtfsLanding
				{
					StopId = (string)properties["stop_id"],
					StopName = (string)properties["stop_name"],
					Latitude = (double)properties["lat"],
					Longitude = (double)properties["lon"],
					North = (double)properties["north_m"],
					East = (double)properties["east_m"],
					RouteIds = StringArray(properties["route_ids"]),
				});
			}
		}

		private void ReadFaces(JObject collection)
		{
			foreach (var feature in Features(collection))
			{
				var properties = (JObject)feature["properties"];
				var geometry = (JObject)feature["geometry"];
				if ((string)geometry["type"] != "Polygon")
				{
					throw new InvalidDataException($"landing_faces.geojson: stop {properties["stop_id"]} outline is {geometry["type"]}, expected Polygon.");
				}

				var normal = (JArray)properties["outward_normal_ne"];
				Faces.Add(new LandingFace
				{
					StopId = (string)properties["stop_id"],
					StopName = (string)properties["stop_name"],
					RouteIds = StringArray(properties["route_ids"]),
					Source = (string)properties["source"],
					Confidence = (string)properties["confidence"],
					Verification = (string)properties["verification"],
					EncId = (string)properties["enc_id"] ?? "",
					OutlineLonLat = LonLatRing((JArray)geometry["coordinates"][0]),
					OutlineNorthEast = PairRing((JArray)feature["local_coordinates"][0]),
					FaceLonLat = LonLatRing((JArray)properties["face_geometry"]["coordinates"]),
					FaceNorthEast = PairRing((JArray)properties["face_local_coordinates"]),
					FaceHeadingDegrees = (double)properties["face_heading_deg_true"],
					OutwardNormalNorth = (double)normal[0],
					OutwardNormalEast = (double)normal[1],
				});
			}
		}

		private void ReadBridges(JObject collection)
		{
			var cells = collection["cells"] as JObject;
			BridgeCells = cells == null ? "" : string.Join(", ", cells.Properties().Select(p => $"{p.Name} ed. {p.Value["edition"]} upd. {p.Value["update"]} ({p.Value["issue_date"]})"));
			foreach (var feature in Features(collection))
			{
				var properties = (JObject)feature["properties"];
				var geometry = (JObject)feature["geometry"];
				var rings = new List<List<double[]>>();
				switch ((string)geometry["type"])
				{
					case "Polygon":
						rings.Add(LonLatRing((JArray)geometry["coordinates"][0]));
						break;
					case "MultiPolygon":
						foreach (var polygon in (JArray)geometry["coordinates"])
						{
							rings.Add(LonLatRing((JArray)polygon[0]));
						}

						break;
					default:
						continue;
				}

				Bridges.Add(new BridgeFeature
				{
					Id = (string)properties["id"],
					Name = (string)properties["name"] ?? "",
					VerticalClearance = (double?)properties["vertical_clearance_m"],
					HorizontalClearance = (double?)properties["horizontal_clearance_m"],
					ClearanceDatum = (string)properties["clearance_datum"] ?? "",
					HasClearance = (bool?)properties["has_clearance"] ?? false,
					Information = (string)properties["information"] ?? "",
					RingsLonLat = rings,
				});
			}
		}

		private static IEnumerable<JObject> Features(JObject collection) => ((JArray)collection["features"]).Cast<JObject>();

		private static string[] StringArray(JToken token) => token is JArray array ? array.Select(t => (string)t).ToArray() : Array.Empty<string>();

		// GeoJSON positions are [lon, lat].
		private static List<double[]> LonLatRing(JArray positions) =>
			positions.Select(p => new[] { (double)p[0], (double)p[1] }).ToList();

		// local_coordinates pairs are [north_m, east_m].
		private static List<double[]> PairRing(JArray pairs) =>
			pairs.Select(p => new[] { (double)p[0], (double)p[1] }).ToList();
	}

	/// <summary>A GTFS stop from landings.geojson.</summary>
	public sealed class GtfsLanding
	{
		public string StopId;
		public string StopName;
		public double Latitude;
		public double Longitude;
		public double North;
		public double East;
		public string[] RouteIds;
	}

	/// <summary>One feature of landing_faces.geojson. Rings are [lon, lat] or [north, east] pairs.</summary>
	public sealed class LandingFace
	{
		public string StopId;
		public string StopName;
		public string[] RouteIds;
		public string Source;
		public string Confidence;
		public string Verification;
		public string EncId;
		public List<double[]> OutlineLonLat;
		public List<double[]> OutlineNorthEast;
		public List<double[]> FaceLonLat;
		public List<double[]> FaceNorthEast;
		public double FaceHeadingDegrees;
		public double OutwardNormalNorth;
		public double OutwardNormalEast;
	}

	/// <summary>One feature of bridges.geojson (NOAA ENC BRIDGE).</summary>
	public sealed class BridgeFeature
	{
		public string Id;
		public string Name;
		public double? VerticalClearance;
		public double? HorizontalClearance;
		public string ClearanceDatum;
		public bool HasClearance;
		public string Information;
		public List<List<double[]>> RingsLonLat;
	}

	/// <summary>geoid_offset.json: ellipsoidal height of NAVD88 zero at the slice corners and centre.</summary>
	public sealed class GeoidOffsets
	{
		public string Slice;
		public string Model;
		public string Transformation;
		public double South;
		public double North;
		public double West;
		public double East;
		public double SouthWest;
		public double SouthEast;
		public double NorthWest;
		public double NorthEast;
		public double Centre;
		public double CentreLatitude;
		public double CentreLongitude;

		public static GeoidOffsets Read(JObject json)
		{
			var points = (JObject)json["points"];
			double Height(string corner) => (double)points[corner]["navd88_zero_ellipsoidal_height_m"];
			double Coordinate(string corner, string axis) => (double)points[corner][axis];
			var result = new GeoidOffsets
			{
				Slice = (string)json["slice"],
				Model = (string)json["geoid_model"],
				Transformation = (string)json["transformation"],
				South = Coordinate("sw", "lat"),
				West = Coordinate("sw", "lon"),
				North = Coordinate("ne", "lat"),
				East = Coordinate("ne", "lon"),
				SouthWest = Height("sw"),
				SouthEast = Height("se"),
				NorthWest = Height("nw"),
				NorthEast = Height("ne"),
				Centre = Height("centre"),
				CentreLatitude = Coordinate("centre", "lat"),
				CentreLongitude = Coordinate("centre", "lon"),
			};

			// The corners must form the slice's bounding box.
			if (Coordinate("se", "lat") != result.South || Coordinate("nw", "lat") != result.North ||
				Coordinate("nw", "lon") != result.West || Coordinate("se", "lon") != result.East)
			{
				throw new InvalidDataException("geoid_offset.json corners do not form an axis-aligned bounding box.");
			}

			return result;
		}
	}

	/// <summary>Tidal datums at the slice's datum station, as NAVD88 heights.</summary>
	public sealed class TidalDatums
	{
		public string StationId;
		public string StationName;
		public string Epoch;
		public string SourceFile;
		public string SourceUrl;
		public string SourceSha256;
		public double MeanHighWaterNavd88;
		public double MeanLowerLowWaterNavd88;
		public double PipelineMllwNavd88;

		/// <summary>
		/// Reads the datum station named in depth_merged_navd88.json and its CO-OPS datums file from the
		/// raw cache. NOAA lists every datum relative to station datum (STND), so a datum's NAVD88 height
		/// is its value minus the NAVD88 value, as the pipeline computes MLLW.
		/// </summary>
		public static TidalDatums Read(JObject depthMetadata, string rawCacheRoot)
		{
			var station = (JObject)depthMetadata["datum_station"];
			var source = (JObject)station["datum_source"];
			var result = new TidalDatums
			{
				StationId = (string)station["id"],
				StationName = (string)station["name"],
				Epoch = (string)station["datum_epoch"],
				SourceFile = (string)source["name"],
				SourceUrl = (string)source["url"],
				SourceSha256 = (string)source["sha256"],
				PipelineMllwNavd88 = (double)station["mllw_navd88_offset_m"],
			};

			var path = Path.Combine(rawCacheRoot, "coops", result.SourceFile);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException($"CO-OPS datums file {path} is missing from the raw cache; run ferrydata fetch --only coops.", path);
			}

			var bytes = File.ReadAllBytes(path);
			string sha;
			using (var hash = SHA256.Create())
			{
				sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
			}

			if (sha != result.SourceSha256)
			{
				throw new InvalidDataException($"{path} has sha256 {sha}, but the pipeline recorded {result.SourceSha256}.");
			}

			var datums = (JArray)JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes))["datums"];
			double Value(string name)
			{
				var entry = datums.FirstOrDefault(d => (string)d["name"] == name);
				if (entry == null)
				{
					throw new InvalidDataException($"{path} has no {name} datum.");
				}

				return (double)entry["value"];
			}

			var navd88 = Value("NAVD88");
			result.MeanHighWaterNavd88 = Value("MHW") - navd88;
			result.MeanLowerLowWaterNavd88 = Value("MLLW") - navd88;
			if (Math.Abs(result.MeanLowerLowWaterNavd88 - result.PipelineMllwNavd88) > 1e-6)
			{
				throw new InvalidDataException($"MLLW from {path} ({result.MeanLowerLowWaterNavd88} m NAVD88) disagrees with the pipeline ({result.PipelineMllwNavd88} m).");
			}

			return result;
		}
	}
}
