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
					layers.Add(ReadLayer(path, bytes[file], name == "references", Integer(counts[name], pathManifest), runtime));
					bytes.Remove(file); // Release verified bytes as each layer becomes resident.
				}
				return new HarborChartPack(digest, runtime.Digest, layers, Value(manifest, pathManifest));
			}
			catch (HarborChartPackException) { throw; }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OverflowException || ex is JsonException) { throw Error(folder, ex.Message, ex); }
		}
		private static ChartFeatureLayer ReadLayer(string path, byte[] bytes, bool references, int count, HarborRuntimePack runtime)
		{
			try
			{
				using var stream = new MemoryStream(bytes, false);
				using var text = new StreamReader(stream, Utf8, false, 16384);
				using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 100 };
				return new LayerReader(reader, path, references, count, runtime).Read();
			}
			catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
			{
				throw Error(path, "invalid JSON: " + e.Message, e);
			}
		}

		// A layer is decoded directly into geometry arrays, shared evidence and tuple storage.
		// No JSON DOM or full-file UTF-16 string is created. Property order is unrestricted.
		private sealed class LayerReader
		{
			private readonly JsonTextReader _reader;
			private readonly string _path;
			private readonly bool _references;
			private readonly int _count;
			private readonly HarborRuntimePack _runtime;
			private readonly Dictionary<string, HarborValue> _texts = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
			private readonly Dictionary<string, HarborValue> _cellLists = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
			private struct Row
			{
				public string Id, Source, Class;
				public HarborGeometry Geometry;
				public HarborValue Cells;
				public int Attribute;
				public double A, B, C, D;
			}

			public LayerReader(JsonTextReader reader, string path, bool references, int count, HarborRuntimePack runtime)
			{
				_reader = reader; _path = path; _references = references; _count = count; _runtime = runtime;
			}
			private void Next()
			{
				do { if (!_reader.Read()) throw Error(_path, "invalid JSON: unexpected end."); } while (_reader.TokenType == JsonToken.Comment);
			}
			private void Start(JsonToken token, string message)
			{
				if (_reader.TokenType != token) throw Error(_path, message);
				Next();
			}
			private void ArrayStart() => Start(JsonToken.StartArray, "expected JSON array.");
			private void ObjectStart() => Start(JsonToken.StartObject, "expected JSON object.");
			private string Text()
			{
				if (_reader.TokenType != JsonToken.String) throw Error(_path, "expected JSON string.");
				var result = (string)_reader.Value; Next(); return result;
			}
			private double NumericValue() => _reader.Value is System.Numerics.BigInteger integer ? (double)integer : Convert.ToDouble(_reader.Value, CultureInfo.InvariantCulture);
			private double Number()
			{
				if ((_reader.TokenType != JsonToken.Integer && _reader.TokenType != JsonToken.Float) || !Finite(NumericValue())) throw Error(_path, "expected a finite JSON number.");
				var result = NumericValue(); Next(); return result;
			}
			private int Integer()
			{
				if (_reader.TokenType != JsonToken.Integer || NumericValue() < 0 || NumericValue() > int.MaxValue) throw Error(_path, "expected a nonnegative int32 JSON integer.");
				var result = Convert.ToInt32(_reader.Value, CultureInfo.InvariantCulture); Next(); return result;
			}
			private string Property(HashSet<string> seen)
			{
				if (_reader.TokenType != JsonToken.PropertyName) throw Error(_path, "invalid JSON: expected a property.");
				var name = (string)_reader.Value;
				if (!seen.Add(name)) throw Error(_path, "invalid JSON: duplicate property " + name);
				Next(); return name;
			}
			private HarborValue SharedText(string text)
			{
				if (!_texts.TryGetValue(text, out var value)) { value = HarborValue.FromText(text); _texts.Add(text, value); }
				return value;
			}
			public ChartFeatureLayer Read()
			{
				Next(); ObjectStart();
				string format = null;
				List<HarborValue> attributes = null;
				List<Row> rows = null;
				Dictionary<long, int[]> grid = null;
				var fieldsPresent = false; var cellSize = -1;
				var seen = new HashSet<string>(StringComparer.Ordinal);
				while (_reader.TokenType != JsonToken.EndObject)
				{
					switch (Property(seen))
					{
						case "format": format = Text(); break;
						case "attributes":
							ArrayStart(); attributes = new List<HarborValue>();
							while (_reader.TokenType != JsonToken.EndArray)
							{
								if (_reader.TokenType != JsonToken.StartObject) throw Error(_path, "expected JSON object.");
								attributes.Add(Evidence());
							}
							Next(); break;
						case "fields": ReadFields(); fieldsPresent = true; break;
						case "features": rows = ReadRows(); break;
						case "grid": grid = ReadGrid(out cellSize); break;
						default: Skip(); break;
					}
				}
				if (_reader.Read()) throw Error(_path, "trailing JSON content.");
				if (format == null) throw Error(_path, "expected JSON string.");
				if (format != "ferrysim-chart-layer/1") throw Error(_path, "unsupported format; expected ferrysim-chart-layer/1");
				if (attributes == null || !fieldsPresent || rows == null) throw Error(_path, "expected JSON array.");
				if (rows.Count != _count) throw Error(_path, "feature count mismatch.");
				if (grid == null) throw Error(_path, "expected JSON object.");
				if (cellSize != 250) throw Error(_path, "grid size mismatch.");

				var features = new ChartFeature[rows.Count];
				var record = new Dictionary<string, HarborValue>(StringComparer.Ordinal);
				var expectedGrid = new Dictionary<long, List<int>>();
				for (var i = 0; i < rows.Count; i++)
				{
					var row = rows[i]; record.Clear(); record.Add("id", HarborValue.FromText(row.Id));
					if (_references)
					{
						ValidateReference(row.Source, row.Class);
						record.Add("runtime_layer", SharedText(row.Source)); record.Add("runtime_id", HarborValue.FromText(row.Class));
					}
					else
					{
						if (row.Attribute >= attributes.Count) throw Error(_path, "invalid attribute index.");
						record.Add("source_id", HarborValue.FromText(row.Source)); record.Add("feature_class", SharedText(row.Class));
						record.Add("cells", row.Cells); record.Add("properties", attributes[row.Attribute]);
					}
					features[i] = new ChartFeature(row.Id, _references ? null : row.Source, _references ? null : SharedText(row.Class).Text, row.Geometry, HarborValue.FromFields(record), row.A, row.B, row.C, row.D);
					for (var n = (int)Math.Floor(row.A / 250); n <= Math.Floor(row.C / 250); n++) for (var e = (int)Math.Floor(row.B / 250); e <= Math.Floor(row.D / 250); e++)
					{
						var key = ChartFeatureLayer.GridKey(n, e);
						if (!expectedGrid.TryGetValue(key, out var entries)) { entries = new List<int>(); expectedGrid.Add(key, entries); }
						entries.Add(i);
					}
				}
				if (grid.Count != expectedGrid.Count) throw Error(_path, "grid inventory mismatch.");
				foreach (var cell in grid)
				{
					if (!expectedGrid.TryGetValue(cell.Key, out var expected)) throw Error(_path, "unexpected grid cell.");
					if (cell.Value.Length != expected.Count) throw Error(_path, "grid member count mismatch.");
					for (var i = 0; i < expected.Count; i++) if (cell.Value[i] != expected[i]) throw Error(_path, "grid member mismatch.");
				}
				return new ChartFeatureLayer(features, grid);
			}
			private void ReadFields()
			{
				var expected = _references ? new[] { "id", "runtime_layer", "runtime_id", "bounds_ne_m" } : new[] { "id", "source_id", "feature_class", "attribute_index", "geometry_ne_m", "cells" };
				ArrayStart(); var fields = new List<string>();
				while (_reader.TokenType != JsonToken.EndArray) fields.Add(Text());
				Next();
				if (fields.Count != expected.Length) throw Error(_path, "chart tuple fields mismatch.");
				for (var i = 0; i < fields.Count; i++) if (fields[i] != expected[i]) throw Error(_path, "chart tuple field mismatch.");
			}
			private void Column()
			{
				if (_reader.TokenType == JsonToken.EndArray) throw Error(_path, "chart tuple length mismatch.");
			}
			private List<Row> ReadRows()
			{
				ArrayStart(); var rows = new List<Row>(Math.Min(_count, 100000)); string previous = null;
				while (_reader.TokenType != JsonToken.EndArray)
				{
					ArrayStart(); Column(); var row = new Row { Id = Text() };
					Column(); row.Source = Text(); Column(); row.Class = Text();
					Column();
					if (_references)
					{
						ArrayStart(); var bounds = new List<double>(4);
						while (_reader.TokenType != JsonToken.EndArray) bounds.Add(Number()); Next();
						if (bounds.Count != 4) throw Error(_path, "four bounds required.");
						row.A = bounds[0]; row.B = bounds[1]; row.C = bounds[2]; row.D = bounds[3];
					}
					else
					{
						row.Attribute = Integer(); Column(); row.Geometry = ReadGeometry(true);
						row.A = row.Geometry.MinimumNorth; row.B = row.Geometry.MinimumEast; row.C = row.Geometry.MaximumNorth; row.D = row.Geometry.MaximumEast;
						Column(); row.Cells = ReadCells();
						row.Class = SharedText(row.Class).Text;
					}
					if (_reader.TokenType != JsonToken.EndArray) throw Error(_path, "chart tuple length mismatch."); Next();
					if (previous != null && string.CompareOrdinal(previous, row.Id) >= 0) throw Error(_path, "feature IDs must be unique and sorted."); previous = row.Id;
					if (row.A > row.C || row.B > row.D || Math.Max(Math.Max(Math.Abs(row.A), Math.Abs(row.B)), Math.Max(Math.Abs(row.C), Math.Abs(row.D))) > 1000000) throw Error(_path, "invalid harbor bounds.");
					rows.Add(row);
				}
				Next(); return rows;
			}
			private HarborValue ReadCells()
			{
				var value = Evidence();
				if (value.Items == null) return value;
				var key = new StringBuilder();
				foreach (var item in value.Items)
				{
					// Keep arbitrary evidence (including nested values) exactly as the old reader did.
					if (item.Text == null) return value;
					key.Append(item.Text.Length).Append(':').Append(item.Text);
				}
				var text = key.ToString();
				if (_cellLists.TryGetValue(text, out var shared)) return shared;
				_cellLists.Add(text, value); return value;
			}
			private Dictionary<long, int[]> ReadGrid(out int size)
			{
				ObjectStart(); size = -1; Dictionary<long, int[]> grid = null;
				var seen = new HashSet<string>(StringComparer.Ordinal);
				while (_reader.TokenType != JsonToken.EndObject)
				{
					switch (Property(seen))
					{
						case "cell_m": size = Integer(); break;
						case "cells":
							ObjectStart(); grid = new Dictionary<long, int[]>(); var keys = new HashSet<string>(StringComparer.Ordinal);
							while (_reader.TokenType != JsonToken.EndObject)
							{
								var name = Property(keys); var parts = name.Split(',');
								if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)) throw Error(_path, "invalid grid key.");
								if (name != n.ToString(CultureInfo.InvariantCulture) + "," + e.ToString(CultureInfo.InvariantCulture)) throw Error(_path, "noncanonical grid key.");
								ArrayStart(); var indices = new List<int>();
								while (_reader.TokenType != JsonToken.EndArray) indices.Add(Integer()); Next();
								grid.Add(ChartFeatureLayer.GridKey(n, e), indices.ToArray());
							}
							Next(); break;
						default: Skip(); break;
					}
				}
				Next();
				if (size == -1) throw Error(_path, "expected a nonnegative int32 JSON integer.");
				return grid ?? throw Error(_path, "expected JSON object.");
			}
			private void ValidateReference(string layer, string id)
			{
				var found = false;
				if (layer == "aids") { for (var i = 0; i < _runtime.Aids.Count; i++) if (_runtime.Aids[i].Id == id) found = true; }
				else if (layer == "bridges") { for (var i = 0; i < _runtime.Bridges.Count; i++) if (_runtime.Bridges[i].Id == id) found = true; }
				else if (layer == "footprints") { for (var i = 0; i < _runtime.Footprints.Count; i++) if (_runtime.Footprints[i].Id == id && _runtime.Footprints[i].Semantic == "shoreline") found = true; }
				if (!found) throw Error(_path, "missing runtime reference " + layer + ":" + id);
			}
			private HarborGeometry ReadGeometry(bool encoded)
			{
				var polygons = new List<HarborPolygon>(); var lines = new List<IReadOnlyList<HorizontalVector>>(); var points = new List<HorizontalVector>();
				string type;
				if (encoded)
				{
					ArrayStart();
					if (_reader.TokenType == JsonToken.EndArray) throw Error(_path, "invalid encoded geometry.");
					type = Text();
					if (_reader.TokenType == JsonToken.EndArray) throw Error(_path, "invalid encoded geometry.");
					ReadCoordinates(type, polygons, lines, points);
					if (_reader.TokenType != JsonToken.EndArray) throw Error(_path, "invalid encoded geometry."); Next();
				}
				else
				{
					// A child's type may follow coordinates. Buffer only its raw coordinate JSON,
					// then replay into final arrays after type selection; never build scalar/tree nodes.
					ObjectStart(); type = null; string coordinates = null, geometries = null;
					var seen = new HashSet<string>(StringComparer.Ordinal);
					while (_reader.TokenType != JsonToken.EndObject)
					{
						switch (Property(seen))
						{
							case "type": type = Text(); break;
							case "coordinates": coordinates = Capture(); break;
							case "geometries": geometries = Capture(); break;
							default: Skip(); break;
						}
					}
					Next();
					if (type == null) throw Error(_path, "expected JSON string.");
					var payload = type == "GeometryCollection" ? geometries : coordinates;
					if (payload == null) throw Error(_path, "expected JSON array.");
					using var text = new StringReader("[" + payload + "]");
					using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 100 };
					var child = new LayerReader(reader, _path, _references, _count, _runtime);
					child.Next(); child.ArrayStart(); child.ReadCoordinates(type, polygons, lines, points);
				}
				if (polygons.Count + lines.Count + points.Count == 0) throw Error(_path, "empty geometry.");
				return new HarborGeometry(type, polygons, lines, points);
			}
			private void ReadCoordinates(string type, List<HarborPolygon> polygons, List<IReadOnlyList<HorizontalVector>> lines, List<HorizontalVector> points)
			{
				switch (type)
				{
					case "Point": points.Add(Point()); break;
					case "MultiPoint": ArrayStart(); while (_reader.TokenType != JsonToken.EndArray) points.Add(Point()); Next(); break;
					case "LineString": lines.Add(Line()); break;
					case "MultiLineString": ArrayStart(); while (_reader.TokenType != JsonToken.EndArray) lines.Add(Line()); Next(); break;
					case "Polygon": polygons.Add(Polygon()); break;
					case "MultiPolygon": ArrayStart(); while (_reader.TokenType != JsonToken.EndArray) polygons.Add(Polygon()); Next(); break;
					case "GeometryCollection":
						ArrayStart();
						while (_reader.TokenType != JsonToken.EndArray)
						{
							var g = ReadGeometry(false); polygons.AddRange(g.Polygons); lines.AddRange(g.Lines); points.AddRange(g.Points);
						}
						Next(); break;
					default: throw Error(_path, "unsupported geometry type: " + type);
				}
			}
			private HorizontalVector Point()
			{
				ArrayStart();
				if (_reader.TokenType == JsonToken.EndArray) throw Error(_path, "expected two coordinates."); var n = Number();
				if (_reader.TokenType == JsonToken.EndArray) throw Error(_path, "expected two coordinates."); var e = Number();
				if (_reader.TokenType != JsonToken.EndArray) throw Error(_path, "expected two coordinates."); Next();
				return new HorizontalVector(n, e);
			}
			private IReadOnlyList<HorizontalVector> Line()
			{
				ArrayStart(); var vertices = new List<HorizontalVector>();
				while (_reader.TokenType != JsonToken.EndArray) vertices.Add(Point()); Next();
				if (vertices.Count < 2) throw Error(_path, "line needs at least two points.");
				return vertices;
			}
			private HarborPolygon Polygon()
			{
				ArrayStart(); var rings = new List<IReadOnlyList<HorizontalVector>>();
				while (_reader.TokenType != JsonToken.EndArray) { var ring = Line(); ValidateRing(ring); rings.Add(ring); } Next();
				if (rings.Count == 0) throw Error(_path, "empty polygon.");
				return new HarborPolygon(rings);
			}
			private void ValidateRing(IReadOnlyList<HorizontalVector> ring)
			{
				if (ring.Count < 4 || !Equal(ring[0], ring[ring.Count - 1]) || Area(ring) == 0) throw Error(_path, "polygon rings must be closed and have nonzero area.");
			}

			private string Capture()
			{
				using var text = new StringWriter(CultureInfo.InvariantCulture);
				using var writer = new JsonTextWriter(text);
				void Copy()
				{
					if (_reader.TokenType == JsonToken.StartObject)
					{
						writer.WriteStartObject(); ObjectStart(); var seen = new HashSet<string>(StringComparer.Ordinal);
						while (_reader.TokenType != JsonToken.EndObject) { writer.WritePropertyName(Property(seen)); Copy(); }
						writer.WriteEndObject(); Next();
					}
					else if (_reader.TokenType == JsonToken.StartArray || _reader.TokenType == JsonToken.StartConstructor)
					{
						var constructor = _reader.TokenType == JsonToken.StartConstructor;
						var end = constructor ? JsonToken.EndConstructor : JsonToken.EndArray;
						if (constructor) writer.WriteStartConstructor((string)_reader.Value); else writer.WriteStartArray();
						Next(); while (_reader.TokenType != end) Copy();
						if (constructor) writer.WriteEndConstructor(); else writer.WriteEndArray(); Next();
					}
					else
					{
						if ((_reader.TokenType == JsonToken.Float || _reader.TokenType == JsonToken.Integer) && !Finite(NumericValue())) throw Error(_path, "expected a finite JSON number.");
						writer.WriteToken(_reader, false); Next();
					}
				}
				Copy(); writer.Flush(); return text.ToString();
			}
			private HarborValue Evidence()
			{
				switch (_reader.TokenType)
				{
					case JsonToken.Null: Next(); return HarborValue.Null;
					case JsonToken.String: return HarborValue.FromText(Text());
					case JsonToken.Integer:
					case JsonToken.Float: return HarborValue.FromNumber(Number());
					case JsonToken.Boolean: var boolean = (bool)_reader.Value; Next(); return HarborValue.FromBoolean(boolean);
					case JsonToken.StartArray:
						ArrayStart(); var items = new List<HarborValue>(); while (_reader.TokenType != JsonToken.EndArray) items.Add(Evidence()); Next(); return HarborValue.FromItems(items);
					case JsonToken.StartObject:
						ObjectStart(); var fields = new Dictionary<string, HarborValue>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
						while (_reader.TokenType != JsonToken.EndObject) { var name = Property(seen); fields.Add(name, Evidence()); } Next(); return HarborValue.FromFields(fields);
					default: throw Error(_path, "unsupported JSON token.");
				}
			}
			private void Skip()
			{
				// Ignored fields still participate in duplicate-property and finite-number validation.
				if (_reader.TokenType == JsonToken.StartObject)
				{
					ObjectStart(); var seen = new HashSet<string>(StringComparer.Ordinal);
					while (_reader.TokenType != JsonToken.EndObject) { Property(seen); Skip(); } Next();
				}
				else if (_reader.TokenType == JsonToken.StartArray || _reader.TokenType == JsonToken.StartConstructor)
				{
					var end = _reader.TokenType == JsonToken.StartArray ? JsonToken.EndArray : JsonToken.EndConstructor;
					Next(); while (_reader.TokenType != end) Skip(); Next();
				}
				else if (_reader.TokenType == JsonToken.Float || _reader.TokenType == JsonToken.Integer) Number();
				else if (_reader.TokenType == JsonToken.String || _reader.TokenType == JsonToken.Boolean || _reader.TokenType == JsonToken.Null || _reader.TokenType == JsonToken.Undefined) Next();
				else throw Error(_path, "unsupported JSON token.");
			}
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
