using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Core.Serialization
{
	/// <summary>A missing, corrupt or incompatible chart pack file, with its exact path.</summary>
	public sealed class HarborChartPackException : Exception
	{
		/// <summary>Preserves the failing path and original cause.</summary>
		public HarborChartPackException(string file, string message, Exception inner = null) : base(file + ": " + message, inner) { File = file; }
		/// <summary>Exact failing file path.</summary>
		public string File { get; }
	}
	/// <summary>Strict raw-byte validating loader for the offline ferrysim-harbor-chart/1 pack.</summary>
	public static class HarborChartPackJson
	{
		private static readonly string[] Names = { "land", "depth_areas", "contours", "soundings", "hazards", "channels", "radar_obstructions", "references" };
		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
		/// <summary>Loads the exact inventory and frame against an already verified runtime pack. Fails closed.</summary>
		public static HarborChartPack Load(string folder, HarborRuntimePack runtime)
		{
			if (runtime == null) throw new ArgumentNullException(nameof(runtime));
			if (string.IsNullOrEmpty(folder)) throw new ArgumentException("A chart folder is required.", nameof(folder));
			try
			{
				var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json", "DIGEST" };
				foreach (var name in Names) expected.Add(name + ".json");
				foreach (var path in Directory.GetFileSystemEntries(folder)) if (!expected.Remove(Path.GetFileName(path))) throw Error(path, "unexpected chart file.");
				if (expected.Count != 0) { foreach (var name in expected) throw Error(Path.Combine(folder, name), "missing chart file."); }
				var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
				bytes.Add("manifest.json", Read(folder, "manifest.json"));
				foreach (var name in Names) bytes.Add(name + ".json", Read(folder, name + ".json"));
				var digestBytes = Read(folder, "DIGEST"); var digestText = Utf8.GetString(digestBytes);
				if (digestBytes.Length != 65 || digestText[64] != '\n' || !IsHex(digestText.Substring(0, 64))) throw Error(Path.Combine(folder, "DIGEST"), "invalid DIGEST framing.");
				string digest;
				using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
				{
					Append(hash, "manifest.json", bytes["manifest.json"]);
					foreach (var name in Names) Append(hash, name + ".json", bytes[name + ".json"]);
					digest = Hex(hash.GetHashAndReset());
				}
				if (digest != digestText.Substring(0, 64)) throw Error(Path.Combine(folder, "DIGEST"), "aggregate digest mismatch.");
				var pathManifest = Path.Combine(folder, "manifest.json"); var manifest = Parse(pathManifest, bytes["manifest.json"]);
				Format(manifest, "ferrysim-harbor-chart/1", pathManifest);
				if (Text(manifest["depth_datum"], pathManifest) != "MLLW") throw Error(pathManifest, "unsupported chart depth datum.");
				if (Integer(manifest["schema_version"], pathManifest) != 1 || Integer(manifest["grid_cell_m"], pathManifest) != 250) throw Error(pathManifest, "unsupported chart schema or grid.");
				if (Text(manifest["coordinate_frame"], pathManifest) != runtime.Manifest["coordinate_frame"].Text) throw Error(pathManifest, "runtime coordinate frame mismatch.");
				var origin = Array(manifest["origin_latlon"], pathManifest); var runtimeOrigin = runtime.Manifest["origin_latlon"].Items;
				if (origin.Count != 2 || runtimeOrigin == null || runtimeOrigin.Count != 2 || Number(origin[0], pathManifest) != runtimeOrigin[0].Number || Number(origin[1], pathManifest) != runtimeOrigin[1].Number) throw Error(pathManifest, "runtime origin mismatch.");
				if (Text(manifest["runtime_digest"], pathManifest) != runtime.Digest) throw Error(pathManifest, "referenced runtime digest mismatch.");
				var files = Object(manifest["files"], pathManifest); var counts = Object(manifest["counts"], pathManifest);
				if (files.Count != 8 || counts.Count != 8) throw Error(pathManifest, "file/count inventory mismatch.");
				var layers = new List<ChartFeatureLayer>();
				foreach (var name in Names)
				{
					var file = name + ".json"; var path = Path.Combine(folder, file); string actual;
					using (var hash = SHA256.Create()) actual = Hex(hash.ComputeHash(bytes[file]));
					if (actual != Text(files[file], pathManifest)) throw Error(path, "file hash mismatch.");
					var root = Parse(path, bytes[file]); Format(root, "ferrysim-chart-layer/1", path);
					var attributeTokens = Array(root["attributes"], path); var attributes = new HarborValue[attributeTokens.Count];
					for (var j = 0; j < attributes.Length; j++) { Object(attributeTokens[j], path); attributes[j] = Value(attributeTokens[j], path); }
					var fields = Array(root["fields"], path);
					var expectedFields = name == "references" ? new[] { "id", "runtime_layer", "runtime_id", "bounds_ne_m" } : new[] { "id", "source_id", "feature_class", "attribute_index", "geometry_ne_m", "cells" };
					if (fields.Count != expectedFields.Length) throw Error(path, "chart tuple fields mismatch.");
					for (var j = 0; j < fields.Count; j++) if (Text(fields[j], path) != expectedFields[j]) throw Error(path, "chart tuple field mismatch.");
					var features = new List<ChartFeature>(); string previous = null;
					var expectedGrid = new Dictionary<long, List<int>>();
					foreach (var tuple in Array(root["features"], path))
					{
						var values = Array(tuple, path); if (values.Count != fields.Count) throw Error(path, "chart tuple length mismatch.");
						var item = new JObject(); for (var j = 0; j < fields.Count; j++) item[expectedFields[j]] = values[j].DeepClone();
						HarborGeometry g = null;
						if (name != "references")
						{
							var encoded = Array(item["geometry_ne_m"], path); if (encoded.Count != 2) throw Error(path, "invalid encoded geometry."); var type = Text(encoded[0], path);
							item["geometry_ne_m"] = new JObject { ["type"] = type, [type == "GeometryCollection" ? "geometries" : "coordinates"] = encoded[1].DeepClone() };
							g = Geometry(item["geometry_ne_m"], path);
							item["bounds_ne_m"] = new JArray(g.MinimumNorth, g.MinimumEast, g.MaximumNorth, g.MaximumEast);
						}
						var id = Text(item["id"], path);
						if (previous != null && string.CompareOrdinal(previous, id) >= 0) throw Error(path, "feature IDs must be unique and sorted."); previous = id;
						var b = Array(item["bounds_ne_m"], path); if (b.Count != 4) throw Error(path, "four bounds required.");
						var a = Number(b[0], path); var e = Number(b[1], path); var c = Number(b[2], path); var d = Number(b[3], path);
						if (a > c || e > d || Math.Max(Math.Max(Math.Abs(a), Math.Abs(e)), Math.Max(Math.Abs(c), Math.Abs(d))) > 1000000) throw Error(path, "invalid harbor bounds.");
						if (name != "references")
						{
							if (g.MinimumNorth != a || g.MinimumEast != e || g.MaximumNorth != c || g.MaximumEast != d) throw Error(path, "geometry bounds mismatch.");
							Text(item["source_id"], path); Text(item["feature_class"], path);
						}
						else { if (item["geometry_ne_m"] != null) throw Error(path, "reference duplicates runtime geometry."); ValidateReference(item, runtime, path); }
						for (var n = (int)Math.Floor(a / 250); n <= Math.Floor(c / 250); n++) for (var col = (int)Math.Floor(e / 250); col <= Math.Floor(d / 250); col++)
						{
							var key = ChartFeatureLayer.GridKey(n, col); if (!expectedGrid.TryGetValue(key, out var list)) { list = new List<int>(); expectedGrid.Add(key, list); }
							list.Add(features.Count);
						}
						var record = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
						foreach (var property in Object(item, path).Properties()) if (property.Name != "geometry_ne_m" && property.Name != "bounds_ne_m" && property.Name != "attribute_index") record.Add(property.Name, Value(property.Value, path));
						if (name != "references") { var attributeIndex = Integer(item["attribute_index"], path); if (attributeIndex < 0 || attributeIndex >= attributes.Length) throw Error(path, "invalid attribute index."); record.Add("properties", attributes[attributeIndex]); }
						features.Add(new ChartFeature(id, (string)item["source_id"], (string)item["feature_class"], g, HarborValue.FromFields(record), a, e, c, d));
					}
					if (features.Count != Integer(counts[name], pathManifest)) throw Error(path, "feature count mismatch.");
					var grid = Object(root["grid"], path); if (Integer(grid["cell_m"], path) != 250) throw Error(path, "grid size mismatch.");
					var cells = Object(grid["cells"], path); if (cells.Count != expectedGrid.Count) throw Error(path, "grid inventory mismatch.");
					var loadedGrid = new Dictionary<long, int[]>();
					foreach (var cell in cells.Properties())
					{
						var coords = cell.Name.Split(','); if (coords.Length != 2 || !int.TryParse(coords[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || !int.TryParse(coords[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)) throw Error(path, "invalid grid key.");
						if (cell.Name != n.ToString(CultureInfo.InvariantCulture) + "," + e.ToString(CultureInfo.InvariantCulture)) throw Error(path, "noncanonical grid key.");
						var key = ChartFeatureLayer.GridKey(n, e); if (!expectedGrid.TryGetValue(key, out var expectedEntries)) throw Error(path, "unexpected grid cell.");
						var entries = Array(cell.Value, path); if (entries.Count != expectedEntries.Count) throw Error(path, "grid member count mismatch.");
						var indices = new int[entries.Count]; for (var j = 0; j < indices.Length; j++) { indices[j] = Integer(entries[j], path); if (indices[j] != expectedEntries[j]) throw Error(path, "grid member mismatch."); }
						loadedGrid.Add(key, indices);
					}
					layers.Add(new ChartFeatureLayer(features, loadedGrid));
				}
				return new HarborChartPack(digest, runtime.Digest, layers, Value(manifest, pathManifest));
			}
			catch (HarborChartPackException) { throw; }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OverflowException || ex is JsonException) { throw Error(folder, ex.Message, ex); }
		}
		private static void ValidateReference(JToken item, HarborRuntimePack runtime, string path)
		{
			var id = Text(item["runtime_id"], path); var layer = Text(item["runtime_layer"], path); var found = false;
			if (layer == "aids") { for (var i = 0; i < runtime.Aids.Count; i++) if (runtime.Aids[i].Id == id) found = true; }
			else if (layer == "bridges") { for (var i = 0; i < runtime.Bridges.Count; i++) if (runtime.Bridges[i].Id == id) found = true; }
			else if (layer == "footprints") { for (var i = 0; i < runtime.Footprints.Count; i++) if (runtime.Footprints[i].Id == id && runtime.Footprints[i].Semantic == "shoreline") found = true; }
			if (!found) throw Error(path, "missing runtime reference " + layer + ":" + id);
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
			catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
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
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				throw Error(path, e.Message, e);
			}
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

		private static HarborChartPackException Error(string path, string message, Exception inner = null) => new HarborChartPackException(path, message, inner);
	}
}
