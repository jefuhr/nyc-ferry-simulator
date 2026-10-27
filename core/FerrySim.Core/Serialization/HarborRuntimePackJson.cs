using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Core.Serialization
{
	/// <summary>A missing, malformed or mismatched runtime pack file, identified by its exact path.</summary>
	public sealed class HarborRuntimePackException : Exception
	{
		/// <summary>Names the failing file and preserves the original exception when available.</summary>
		public HarborRuntimePackException(string file, string message, Exception inner = null) : base(file + ": " + message, inner)
		{
			File = file;
		}

		/// <summary>The exact path of the failing file.</summary>
		public string File { get; }
	}

	/// <summary>Verifies the exact raw-byte inventory before decoding ferrysim-harbor-runtime/1.</summary>
	public static class HarborRuntimePackJson
	{
		private static readonly string[] Payloads =
		{
			"aids.json",
			"bridges.json",
			"contacts.json",
			"landings.json",
			"geoid.json",
			"geoid.f64",
			"geoid_cells.u8",
			"footprints.json"
		};

		/// <summary>Payload names in their fixed aggregate-digest order.</summary>
		public static IReadOnlyList<string> Files { get; } = System.Array.AsReadOnly(Payloads);

		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

		/// <summary>Loads and verifies one offline runtime folder, preserving nullable source evidence and double geometry.</summary>
		public static HarborRuntimePack Load(string folder)
		{
			if (string.IsNullOrEmpty(folder))
			{
				throw new ArgumentException("A runtime pack folder is required.", nameof(folder));
			}

			try
			{
				var expected = new HashSet<string>(Payloads, StringComparer.Ordinal)
				{
					"manifest.json",
					"DIGEST"
				};
				foreach (var path in Directory.GetFileSystemEntries(folder))
				{
					if (!expected.Remove(Path.GetFileName(path)))
					{
						throw Error(path, "unexpected entry in runtime inventory.");
					}

					if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
					{
						throw Error(path, "runtime files must be ordinary files, not links or directories.");
					}
				}

				foreach (var missing in expected)
				{
					throw Error(Path.Combine(folder, missing), "missing runtime file.");
				}

				var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
				bytes.Add("manifest.json", Read(folder, "manifest.json"));
				foreach (var name in Payloads)
				{
					bytes.Add(name, Read(folder, name));
				}

				var digestPath = Path.Combine(folder, "DIGEST");
				var digest = Utf8.GetString(Read(folder, "DIGEST"));
				if (digest.Length != 65 || digest[64] != '\n' || !IsHex(digest.Substring(0, 64)))
				{
					throw Error(digestPath, "must be 64 lowercase hex characters plus LF.");
				}

				digest = digest.Substring(0, 64);
				// Manifest uses the same filename/NUL/raw bytes/NUL framing as every payload, not parsed JSON.
				using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
				{
					Append(hash, "manifest.json", bytes["manifest.json"]);
					foreach (var name in Payloads)
					{
						Append(hash, name, bytes[name]);
					}

					var computed = Hex(hash.GetHashAndReset());
					if (computed != digest)
					{
						throw Error(digestPath, $"runtime digest mismatch: computed {computed}, recorded {digest}.");
					}
				}

				var manifestPath = Path.Combine(folder, "manifest.json");
				var manifest = Parse(manifestPath, bytes["manifest.json"]);
				Format(manifest, "ferrysim-harbor-runtime/1", manifestPath);
				var files = Object(manifest["files"], manifestPath);
				if (files.Count != Payloads.Length)
				{
					throw Error(manifestPath, "file inventory mismatch.");
				}

				foreach (var name in Payloads)
				{
					var recorded = Text(files[name], manifestPath);
					using var sha = SHA256.Create();
					if (!IsHex(recorded) || Hex(sha.ComputeHash(bytes[name])) != recorded)
					{
						throw Error(Path.Combine(folder, name), "SHA-256 does not match manifest.json.");
					}
				}

				var origin = Array(manifest["origin_latlon"], manifestPath);
				if (origin.Count != 2 || Number(origin[0], manifestPath) != 40.7 || Number(origin[1], manifestPath) != -74.015)
				{
					throw Error(manifestPath, "unsupported world origin.");
				}

				if (Text(manifest["region"], manifestPath) != "harbor")
				{
					throw Error(manifestPath, "runtime region must be harbor.");
				}

				var depthDigest = Text(manifest["environment_depth_digest"], manifestPath);
				if (!IsHex(depthDigest))
				{
					throw Error(manifestPath, "invalid environment_depth_digest.");
				}

				var aidsPath = Path.Combine(folder, "aids.json");
				var aidsRoot = Parse(aidsPath, bytes["aids.json"]);
				Format(aidsRoot, "ferrysim-harbor-aids/1", aidsPath);
				var aids = new List<HarborAid>();
				var aidIds = new HashSet<string>(StringComparer.Ordinal);
				foreach (var item in Array(aidsRoot["aids"], aidsPath))
				{
					var id = Id(item, aidIds, aidsPath);
					var position = Point(item["position_ne_m"], aidsPath);
					Point(item["position_lonlat"], aidsPath);
					Object(item["properties"], aidsPath);
					aids.Add(new HarborAid(id, Text(item["source_id"], aidsPath), position, Value(item, aidsPath)));
				}

				var bridgesPath = Path.Combine(folder, "bridges.json");
				var bridgesRoot = Parse(bridgesPath, bytes["bridges.json"]);
				Format(bridgesRoot, "ferrysim-harbor-bridges/1", bridgesPath);
				var bridges = new List<HarborBridge>();
				var bridgeIds = new HashSet<string>(StringComparer.Ordinal);
				var allSpans = new HashSet<string>(StringComparer.Ordinal);
				var spanCount = 0;
				foreach (var item in Array(bridgesRoot["bridges"], bridgesPath))
				{
					var id = Id(item, bridgeIds, bridgesPath);
					var spans = new List<HarborSpan>();
					var piers = new List<HarborPier>();
					var pierIds = new HashSet<string>(StringComparer.Ordinal);
					Strings(item["names"], bridgesPath);
					Strings(item["source_ids"], bridgesPath);
					Object(item["audit_evidence"], bridgesPath);
					foreach (var pier in Array(item["piers"], bridgesPath))
					{
						Array(pier["membership_evidence"], bridgesPath);
						piers.Add(new HarborPier(Id(pier, pierIds, bridgesPath), Text(pier["source_id"], bridgesPath), Geometry(pier["geometry_ne_m"], bridgesPath), Bounds(pier, bridgesPath), Value(pier, bridgesPath)));
					}

					foreach (var span in Array(item["spans"], bridgesPath))
					{
						Array(span["clearances"], bridgesPath);
						Boolean(span["pier_inventory_complete"], bridgesPath);
						foreach (var pier in Strings(span["pier_ids"], bridgesPath))
						{
							if (!pierIds.Contains(pier))
							{
								throw Error(bridgesPath, "span references a missing pier: " + pier);
							}
						}

						spans.Add(new HarborSpan(Id(span, allSpans, bridgesPath), Text(span["source_id"], bridgesPath), Geometry(span["geometry_ne_m"], bridgesPath), Bounds(span, bridgesPath), Value(span, bridgesPath)));
					}

					spanCount += spans.Count;
					bridges.Add(new HarborBridge(id, spans, piers, Value(item, bridgesPath)));
				}

				var landingsPath = Path.Combine(folder, "landings.json");
				var landingsRoot = Parse(landingsPath, bytes["landings.json"]);
				Format(landingsRoot, "ferrysim-harbor-landings/1", landingsPath);
				var barges = new List<HarborBarge>();
				var bargeIds = new HashSet<string>(StringComparer.Ordinal);
				foreach (var item in Array(landingsRoot["barges"], landingsPath))
				{
					var geometry = Geometry(item["geometry_ne_m"], landingsPath);
					BargeOrientation(geometry, landingsPath);
					if (geometry.Type != "Polygon")
					{
						throw Error(landingsPath, "barge must be a closed Polygon.");
					}

					Boolean(item["floating"], landingsPath);
					Strings(item["stop_ids"], landingsPath);
					Array(item["provenance"], landingsPath);
					barges.Add(new HarborBarge(Id(item, bargeIds, landingsPath), geometry, Bounds(item, landingsPath), Value(item, landingsPath)));
				}

				var stops = new List<HarborStop>();
				var stopIds = new HashSet<string>(StringComparer.Ordinal);
				var berthIds = new HashSet<string>(StringComparer.Ordinal);
				var berthCount = 0;
				foreach (var item in Array(landingsRoot["stops"], landingsPath))
				{
					var id = Unique(Text(item["stop_id"], landingsPath), stopIds, landingsPath);
					var berths = new List<HarborBerth>();
					foreach (var berth in Array(item["berths"], landingsPath))
					{
						var structureId = Text(berth["structure_id"], landingsPath);
						if (!bargeIds.Contains(structureId))
						{
							throw Error(landingsPath, "berth references a missing barge: " + structureId);
						}

						if (berth["primary_face"] != null)
						{
							Boolean(berth["primary_face"], landingsPath);
						}

						berths.Add(new HarborBerth(Id(berth, berthIds, landingsPath), structureId, Value(berth, landingsPath)));
					}

					berthCount += berths.Count;
					stops.Add(new HarborStop(id, Text(item["stop_name"], landingsPath), new HorizontalVector(Number(item["north_m"], landingsPath), Number(item["east_m"], landingsPath)), berths, Value(item, landingsPath)));
				}

				var footprintsPath = Path.Combine(folder, "footprints.json");
				var footprintsRoot = Parse(footprintsPath, bytes["footprints.json"]);
				Format(footprintsRoot, "ferrysim-harbor-queries/1", footprintsPath);
				var coverage = Geometry(footprintsRoot["coverage_ne_m"], footprintsPath);
				var footprints = new List<HarborFootprint>();
				var footprintIds = new HashSet<string>(StringComparer.Ordinal);
				foreach (var item in Array(footprintsRoot["footprints"], footprintsPath))
				{
					if (Boolean(item["collision_authority"], footprintsPath))
					{
						throw Error(footprintsPath, "collision_authority must be false.");
					}

					var semantic = Text(item["semantic"], footprintsPath);
					if (semantic != "shoreline" && semantic != "structure" && semantic != "bridge" && semantic != "barge")
					{
						throw Error(footprintsPath, "unknown semantic: " + semantic);
					}

					Strings(item["source_ids"], footprintsPath);
					var geometry = Geometry(item["geometry_ne_m"], footprintsPath);
					if (semantic == "barge")
					{
						BargeOrientation(geometry, footprintsPath);
					}

					footprints.Add(new HarborFootprint(Id(item, footprintIds, footprintsPath), semantic, geometry, Bounds(item, footprintsPath), Value(item, footprintsPath)));
				}

				var counts = Object(manifest["counts"], manifestPath);
				var actual = new Dictionary<string, int>
				{
					["stops"] = stops.Count,
					["berths"] = berthCount,
					["barges"] = barges.Count,
					["aids"] = aids.Count,
					["bridges"] = bridges.Count,
					["spans"] = spanCount,
					["footprints"] = footprints.Count
				};
				if (counts.Count != actual.Count)
				{
					throw Error(manifestPath, "count inventory mismatch.");
				}

				foreach (var pair in actual)
				{
					if (Integer(counts[pair.Key], manifestPath) != pair.Value)
					{
						throw Error(manifestPath, pair.Key + " count mismatch.");
					}
				}

				var geoid = Geoid(folder, bytes);
				Array(ParseAny(Path.Combine(folder, "contacts.json"), bytes["contacts.json"]), Path.Combine(folder, "contacts.json")); // finite JSON; contacts retain the existing parser/gate.
				return new HarborRuntimePack("harbor", digest, depthDigest, aids, bridges, stops, barges, geoid, coverage, footprints, Value(manifest, manifestPath), Utf8.GetString(bytes["contacts.json"]));
			}
			catch (HarborRuntimePackException)
			{
				throw;
			}
			catch (Exception e)when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is OverflowException)
			{
				throw Error(folder, e.Message, e);
			}
		}

		private static HarborGeoidLattice Geoid(string folder, Dictionary<string, byte[]> bytes)
		{
			var path = Path.Combine(folder, "geoid.json");
			var root = Parse(path, bytes["geoid.json"]);
			Format(root, "ferrysim-harbor-geoid/1", path);
			var rows = Integer(root["rows"], path);
			var columns = Integer(root["cols"], path);
			var maskShape = Array(root["mask_shape"], path);
			if (rows < 2 || columns < 2 || maskShape.Count != 2 || Integer(maskShape[0], path) != rows - 1 || Integer(maskShape[1], path) != columns - 1 || Text(root["values_type"], path) != "little-endian float64" || Text(root["layout"], path) != "row-major south-to-north; columns west-to-east" || Text(root["values_file"], path) != "geoid.f64" || Text(root["mask_file"], path) != "geoid_cells.u8")
			{
				throw Error(path, "invalid geoid schema or mask shape.");
			}

			var cell = Number(root["cell_m"], path);
			if (cell <= 0)
			{
				throw Error(path, "cell_m must be positive.");
			}

			var valuesBytes = bytes["geoid.f64"];
			var mask = bytes["geoid_cells.u8"];
			if ((long)rows * columns * 8 != valuesBytes.Length)
			{
				throw Error(Path.Combine(folder, "geoid.f64"), "grid byte length does not match shape.");
			}

			if ((long)(rows - 1) * (columns - 1) != mask.Length)
			{
				throw Error(Path.Combine(folder, "geoid_cells.u8"), "mask byte length does not match shape.");
			}

			var values = new double[valuesBytes.Length / 8];
			for (var i = 0; i < values.Length; i++)
			{
				long bits = 0;
				for (var j = 0; j < 8; j++)
				{
					bits |= (long)valuesBytes[i * 8 + j] << (8 * j);
				}

				values[i] = BitConverter.Int64BitsToDouble(bits);
				if (!Finite(values[i]))
				{
					throw Error(Path.Combine(folder, "geoid.f64"), "nonfinite geoid node at " + i);
				}
			}

			foreach (var value in mask)
			{
				if (value > 1)
				{
					throw Error(Path.Combine(folder, "geoid_cells.u8"), "mask must contain only 0 or 1.");
				}
			}

			return new HarborGeoidLattice(Number(root["origin_north_m"], path), Number(root["origin_east_m"], path), cell, rows, columns, values, mask);
		}

		private static HarborGeometry Geometry(JToken token, string path)
		{
			var polygons = new List<HarborPolygon>();
			var lines = new List<IReadOnlyList<HorizontalVector>>();
			var points = new List<HorizontalVector>();
			var type = Text(token?["type"], path);
			void Polygon(JToken coordinates)
			{
				var rings = new List<IReadOnlyList<HorizontalVector>>();
				foreach (var ring in Array(coordinates, path))
				{
					var vertices = Line(ring, path);
					if (vertices.Count < 4 || !Equal(vertices[0], vertices[vertices.Count - 1]) || Area(vertices) == 0)
					{
						throw Error(path, "polygon rings must be closed and have nonzero area.");
					}

					rings.Add(vertices);
				}

				if (rings.Count == 0)
				{
					throw Error(path, "empty polygon.");
				}

				polygons.Add(new HarborPolygon(rings));
			}

			switch (type)
			{
				case "Polygon":
					Polygon(token["coordinates"]);
					break;
				case "MultiPolygon":
					foreach (var polygon in Array(token["coordinates"], path))
					{
						Polygon(polygon);
					}

					break;
				case "LineString":
					lines.Add(Line(token["coordinates"], path));
					break;
				case "MultiLineString":
					foreach (var line in Array(token["coordinates"], path))
					{
						lines.Add(Line(line, path));
					}

					break;
				case "Point":
					points.Add(Point(token["coordinates"], path));
					break;
				case "MultiPoint":
					foreach (var point in Array(token["coordinates"], path))
					{
						points.Add(Point(point, path));
					}

					break;
				case "GeometryCollection":
					foreach (var child in Array(token["geometries"], path))
					{
						var g = Geometry(child, path);
						polygons.AddRange(g.Polygons);
						lines.AddRange(g.Lines);
						points.AddRange(g.Points);
					}

					break;
				default:
					throw Error(path, "unsupported geometry type: " + type);
			}

			if (polygons.Count + lines.Count + points.Count == 0)
			{
				throw Error(path, "empty geometry.");
			}

			return new HarborGeometry(type, polygons, lines, points);
		}

		private static IReadOnlyList<HorizontalVector> Line(JToken token, string path)
		{
			var array = Array(token, path);
			if (array.Count < 2)
			{
				throw Error(path, "line needs at least two points.");
			}

			var vertices = new HorizontalVector[array.Count];
			for (var i = 0; i < vertices.Length; i++)
			{
				vertices[i] = Point(array[i], path);
			}

			return vertices;
		}

		private static HorizontalVector Point(JToken token, string path)
		{
			var pair = Array(token, path);
			if (pair.Count != 2)
			{
				throw Error(path, "expected two coordinates.");
			}

			return new HorizontalVector(Number(pair[0], path), Number(pair[1], path));
		}

		private static bool Equal(HorizontalVector a, HorizontalVector b) => a.North == b.North && a.East == b.East;
		private static double Area(IReadOnlyList<HorizontalVector> ring)
		{
			// Subtract a local origin before products to retain precision on short chart edges far from the origin.
			var origin = ring[0];
			var sum = 0.0;
			for (var i = 1; i < ring.Count; i++)
			{
				var a = ring[i - 1] - origin;
				var b = ring[i] - origin;
				sum += a.North * b.East - b.North * a.East;
			}

			return sum * .5;
		}

		private static void BargeOrientation(HarborGeometry geometry, string path)
		{
			if (geometry.Polygons.Count == 0 || geometry.Lines.Count > 0 || geometry.Points.Count > 0)
			{
				throw Error(path, "barge geometry must be polygonal.");
			}

			foreach (var polygon in geometry.Polygons)
			{
				for (var i = 0; i < polygon.Rings.Count; i++)
				{
					if ((Area(polygon.Rings[i]) > 0) != (i == 0))
					{
						throw Error(path, "barge ring orientation must keep solid on the right.");
					}
				}
			}
		}

		private static HarborVerticalBounds Bounds(JToken item, string path)
		{
			var array = Array(item["vertical_bounds_navd88_m"], path);
			if (array.Count != 2)
			{
				throw Error(path, "vertical bounds must have two entries.");
			}

			double? minimum = array[0].Type == JTokenType.Null ? (double? )null : Number(array[0], path);
			double? maximum = array[1].Type == JTokenType.Null ? (double? )null : Number(array[1], path);
			if (minimum > maximum)
			{
				throw Error(path, "vertical bounds are reversed.");
			}

			return new HarborVerticalBounds(minimum, maximum);
		}

		private static HarborValue Value(JToken token, string path)
		{
			switch (token.Type)
			{
				case JTokenType.Null:
					return HarborValue.Null;
				case JTokenType.String:
					return HarborValue.FromText((string)token);
				case JTokenType.Integer:
				case JTokenType.Float:
					return HarborValue.FromNumber(Number(token, path));
				case JTokenType.Boolean:
					return HarborValue.FromBoolean((bool)token);
				case JTokenType.Array:
					var items = new List<HarborValue>();
					foreach (var item in (JArray)token)
					{
						items.Add(Value(item, path));
					}

					return HarborValue.FromItems(items);
				case JTokenType.Object:
					var fields = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
					foreach (var field in (JObject)token)
					{
						fields.Add(field.Key, Value(field.Value, path));
					}

					return HarborValue.FromFields(fields);
				default:
					throw Error(path, "unsupported JSON token.");
			}
		}

		private static JObject Parse(string path, byte[] bytes) => Object(ParseAny(path, bytes), path);
		private static JToken ParseAny(string path, byte[] bytes)
		{
			try
			{
				using var text = new StringReader(Utf8.GetString(bytes));
				using var reader = new JsonTextReader(text)
				{
					DateParseHandling = DateParseHandling.None,
					FloatParseHandling = FloatParseHandling.Double,
					MaxDepth = 100
				};
				var result = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
				while (reader.Read())
				{
					throw Error(path, "trailing JSON content.");
				}

				ValidateFinite(result, path);
				return result;
			}
			catch (Exception e)when (e is JsonException || e is DecoderFallbackException)
			{
				throw Error(path, "invalid JSON: " + e.Message, e);
			}
		}

		private static void ValidateFinite(JToken token, string path)
		{
			if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
			{
				Number(token, path);
			}

			if (token is JContainer container)
			{
				foreach (var child in container.Children())
				{
					ValidateFinite(child, path);
				}
			}
		}

		private static byte[] Read(string folder, string name)
		{
			var path = Path.Combine(folder, name);
			try
			{
				return File.ReadAllBytes(path);
			}
			catch (Exception e)when (e is IOException || e is UnauthorizedAccessException)
			{
				throw Error(path, e.Message, e);
			}
		}

		private static string Unique(string value, HashSet<string> ids, string path)
		{
			if (value.Length == 0 || !ids.Add(value))
			{
				throw Error(path, "empty or duplicate id: " + value);
			}

			return value;
		}

		private static string Id(JToken item, HashSet<string> ids, string path) => Unique(Text(item["id"], path), ids, path);
		private static List<string> Strings(JToken token, string path)
		{
			var list = new List<string>();
			foreach (var item in Array(token, path))
			{
				list.Add(Text(item, path));
			}

			return list;
		}

		private static JObject Object(JToken token, string path) => token as JObject ?? throw Error(path, "expected JSON object.");
		private static JArray Array(JToken token, string path) => token as JArray ?? throw Error(path, "expected JSON array.");
		private static string Text(JToken token, string path) => token?.Type == JTokenType.String ? (string)token : throw Error(path, "expected JSON string.");
		private static bool Boolean(JToken token, string path) => token?.Type == JTokenType.Boolean ? (bool)token : throw Error(path, "expected JSON boolean.");
		private static double Number(JToken token, string path)
		{
			if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) || !Finite((double)token))
			{
				throw Error(path, "expected a finite JSON number.");
			}

			return (double)token;
		}

		private static int Integer(JToken token, string path)
		{
			if (token?.Type != JTokenType.Integer || (double)token < 0 || (double)token > int.MaxValue)
			{
				throw Error(path, "expected a nonnegative int32 JSON integer.");
			}

			return (int)token;
		}

		private static void Format(JObject root, string expected, string path)
		{
			if (Text(root["format"], path) != expected)
			{
				throw Error(path, "unsupported format; expected " + expected);
			}
		}

		private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
		private static bool IsHex(string text)
		{
			if (text.Length != 64)
			{
				return false;
			}

			foreach (var c in text)
			{
				if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
				{
					return false;
				}
			}

			return true;
		}

		private static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
		private static void Append(IncrementalHash hash, string name, byte[] bytes)
		{
			hash.AppendData(Utf8.GetBytes(name + "\0"));
			hash.AppendData(bytes);
			hash.AppendData(new byte[] { 0 });
		}

		private static HarborRuntimePackException Error(string path, string message, Exception inner = null) => new HarborRuntimePackException(path, message, inner);
	}
}
