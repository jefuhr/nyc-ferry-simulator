using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Chart
{
	// Failure modes: corrupt/missing inventory, rehashed frame drift, invalid grid entries,
	// closed grid edges/corners, duplicate candidates, output truncation and per-query allocations.
	public class HarborChartPackTests
	{
		private string _folder;
		private HarborRuntimePack _runtime;
		private static readonly string[] Names = { "land", "depth_areas", "contours", "soundings", "hazards", "channels", "radar_obstructions", "references" };

		[SetUp]
		public void Setup()
		{
			_folder = Path.Combine(Path.GetTempPath(), "chart-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
			_runtime = HarborRuntimePackJson.Load(Path.Combine(RepoPaths.Root, "core/dotnet/FerrySim.Core.Tests/World/Fixtures"));
			var runtimeFields = new Dictionary<string, HarborValue>(_runtime.Manifest.Fields) { ["coordinate_frame"] = HarborValue.FromText("NED horizontal metres; north,east; heights NAVD88 unless explicitly stated") };
			_runtime = new HarborRuntimePack(_runtime.Region, _runtime.Digest, _runtime.EnvironmentDepthDigest, _runtime.Aids, _runtime.Bridges, _runtime.Stops, _runtime.Barges, _runtime.Geoid, _runtime.Coverage, _runtime.Footprints, HarborValue.FromFields(runtimeFields), _runtime.ContactsJson);
			var manifest = new JObject
			{
				["format"] = "ferrysim-harbor-chart/1",
				["schema_version"] = 1,
				["origin_latlon"] = new JArray(_runtime.Manifest["origin_latlon"].Items.Select(v => v.Number.Value)),
				["coordinate_frame"] = _runtime.Manifest["coordinate_frame"].Text,
				["runtime_digest"] = _runtime.Digest, ["depth_datum"] = "MLLW",
				["grid_cell_m"] = 250,
				["files"] = new JObject(),
				["counts"] = new JObject()
			};
			foreach (var name in Names)
			{
				var features = new JArray();
				var cells = new JObject();
				if (name == "soundings")
				{
					features.Add(new JObject { ["id"] = "a", ["source_id"] = "a", ["feature_class"] = "SOUNDG", ["bounds_ne_m"] = new JArray(250, 250, 250, 250), ["geometry_ne_m"] = new JObject { ["type"] = "Point", ["coordinates"] = new JArray(250, 250) }, ["attribute_index"] = 0 });
					cells["1,1"] = new JArray(0);
				}
				var fields = name == "references" ? new[] { "id", "runtime_layer", "runtime_id", "bounds_ne_m" } : new[] { "id", "source_id", "feature_class", "attribute_index", "geometry_ne_m", "cells" };
				var rows = new JArray(); foreach (var feature in features)
				{
					var geometry = feature["geometry_ne_m"]; feature["geometry_ne_m"] = new JArray(geometry["type"], geometry["coordinates"]);
					var values = new JArray(); foreach (var field in fields) values.Add(feature[field]?.DeepClone()); rows.Add(values);
				}
				var layer = new JObject { ["format"] = "ferrysim-chart-layer/1", ["fields"] = new JArray(fields), ["features"] = rows, ["attributes"] = name == "soundings" ? new JArray(new JObject { ["depth_m"] = 3.2 }) : new JArray(), ["grid"] = new JObject { ["cell_m"] = 250, ["cells"] = cells } };
				File.WriteAllText(Path.Combine(_folder, name + ".json"), layer.ToString(), new UTF8Encoding(false));
				manifest["counts"][name] = features.Count;
			}
			File.WriteAllText(Path.Combine(_folder, "manifest.json"), manifest.ToString(), new UTF8Encoding(false));
			Reseal();
		}

		[TearDown]
		public void Cleanup() => Directory.Delete(_folder, true);

		private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		private void Reseal()
		{
			var manifest = JObject.Parse(File.ReadAllText(Path.Combine(_folder, "manifest.json")));
			foreach (var name in Names) manifest["files"][name + ".json"] = Hash(File.ReadAllBytes(Path.Combine(_folder, name + ".json")));
			File.WriteAllText(Path.Combine(_folder, "manifest.json"), manifest.ToString(), new UTF8Encoding(false));
			using var stream = new MemoryStream();
			foreach (var file in new[] { "manifest.json" }.Concat(Names.Select(n => n + ".json")))
			{
				stream.Write(Encoding.UTF8.GetBytes(file + "\0")); stream.Write(File.ReadAllBytes(Path.Combine(_folder, file))); stream.WriteByte(0);
			}
			File.WriteAllText(Path.Combine(_folder, "DIGEST"), Hash(stream.ToArray()) + "\n", new UTF8Encoding(false));
		}

		[Test]
		public void LoadsAndPreservesEvidence()
		{
			var p = HarborChartPackJson.Load(_folder, _runtime);
			Assert.That(p[ChartLayer.Soundings].Features[0].Properties["depth_m"].Number, Is.EqualTo(3.2));
		}

		[TestCase("land.json")]
		[TestCase("manifest.json")]
		[TestCase("DIGEST")]
		public void ChangedByteIsRefused(string name)
		{
			File.AppendAllText(Path.Combine(_folder, name), " ");
			Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime));
		}

		[Test]
		public void EveryMissingFileIsRefused()
		{
			foreach (var name in Names.Select(n => n + ".json").Concat(new[] { "DIGEST", "manifest.json" }))
			{
				var file = Path.Combine(_folder, name); var bytes = File.ReadAllBytes(file); File.Delete(file);
				Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime), name);
				File.WriteAllBytes(file, bytes);
			}
		}

		[TestCase("origin_latlon")]
		[TestCase("coordinate_frame")]
		[TestCase("runtime_digest")]
		public void RehashedFrameOrRuntimeMismatchIsRefused(string field)
		{
			var m = JObject.Parse(File.ReadAllText(Path.Combine(_folder, "manifest.json")));
			m[field] = field == "origin_latlon" ? new JArray(0, 0) : new JValue("wrong");
			File.WriteAllText(Path.Combine(_folder, "manifest.json"), m.ToString()); Reseal();
			Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime));
		}

		[Test]
		public void RehashedMissingGridEntryIsRefused()
		{
			var file = Path.Combine(_folder, "soundings.json"); var m = JObject.Parse(File.ReadAllText(file)); m["grid"]["cells"] = new JObject(); File.WriteAllText(file, m.ToString()); Reseal();
			Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime));
		}

		[Test]
		public void ClosedGridCornerAndZeroRadiusQueriesHaveNoAllocations()
		{
			var layer = HarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings];
			var buffer = layer.CreateQueryBuffer(5);
			var firstBefore = GC.GetAllocatedBytesForCurrentThread();
			layer.QueryBox(0, 0, 250, 250, buffer);
			var firstAllocation = GC.GetAllocatedBytesForCurrentThread() - firstBefore;
			Assert.That(firstAllocation, Is.Zero);
			Assert.That(buffer.Count, Is.EqualTo(1)); Assert.That(buffer[0], Is.EqualTo(0));
			layer.QueryCircle(250, 250, 0, buffer); Assert.That(buffer.Count, Is.EqualTo(1));
			layer.QueryCircle(0, 0, 250, buffer); Assert.That(buffer.Count, Is.Zero);
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 10000; i++) { layer.QueryBox(0, 0, 250, 250, buffer); layer.QueryCircle(250, 250, 0, buffer); }
			Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
		}
		[Test]
		public void PolygonHolesLinesNegativeCellsOrderingAndTruncation()
		{
			HarborGeometry Geometry(params HorizontalVector[] ring) => new HarborGeometry("Polygon", new[] { new HarborPolygon(new IReadOnlyList<HorizontalVector>[] { ring, new[] { new HorizontalVector(100, 100), new HorizontalVector(100, 200), new HorizontalVector(200, 200), new HorizontalVector(200, 100), new HorizontalVector(100, 100) } }) }, System.Array.Empty<IReadOnlyList<HorizontalVector>>(), System.Array.Empty<HorizontalVector>());
			var polygon = Geometry(new HorizontalVector(-250, -250), new HorizontalVector(-250, 500), new HorizontalVector(500, 500), new HorizontalVector(500, -250), new HorizontalVector(-250, -250));
			var line = new HarborGeometry("LineString", System.Array.Empty<HarborPolygon>(), new[] { (IReadOnlyList<HorizontalVector>)new[] { new HorizontalVector(-250, -250), new HorizontalVector(500, 500) } }, System.Array.Empty<HorizontalVector>());
			var features = new[] { new ChartFeature("a", "a", "LNDARE", polygon, HarborValue.Null, -250, -250, 500, 500), new ChartFeature("b", "b", "COALNE", line, HarborValue.Null, -250, -250, 500, 500) };
			var grid = new Dictionary<long, int[]>(); for (var n = -1; n <= 2; n++) for (var e = -1; e <= 2; e++) grid.Add(ChartFeatureLayer.GridKey(n, e), new[] { 0, 1 });
			var layer = new ChartFeatureLayer(features, grid); var buffer = layer.CreateQueryBuffer(2);
			layer.QueryBox(-251, -251, -250, -250, buffer); Assert.That(buffer.Count, Is.EqualTo(2)); Assert.That(new[] { buffer[0], buffer[1] }, Is.EqualTo(new[] { 0, 1 }));
			layer.QueryBox(120, 150, 130, 160, buffer); Assert.That(buffer.Count, Is.Zero, "hole and off the diagonal line");
			layer.QueryCircle(150, 150, 1, buffer); Assert.That(buffer.Count, Is.EqualTo(1)); Assert.That(buffer[0], Is.EqualTo(1));
			layer.QueryCircle(0, 0, 0, buffer); Assert.That(buffer.Count, Is.EqualTo(2));
			var small = layer.CreateQueryBuffer(1); layer.QueryBox(-500, -500, 600, 600, small); Assert.That(small.Count, Is.EqualTo(1)); Assert.That(small.TotalMatches, Is.EqualTo(2)); Assert.That(small.Truncated, Is.True); Assert.That(small[0], Is.Zero);
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 10000; i++) { layer.QueryBox(-500, -500, 600, 600, buffer); layer.QueryCircle(150, 150, 1, buffer); }
			Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
		}

		[Test]
		public void PayloadHashIsCheckedAfterValidAggregate()
		{
			File.AppendAllText(Path.Combine(_folder,"land.json")," ");
			using var stream=new MemoryStream();
			foreach(var file in new[]{"manifest.json"}.Concat(Names.Select(n=>n+".json")))
			{
				stream.Write(Encoding.UTF8.GetBytes(file+"\0"));stream.Write(File.ReadAllBytes(Path.Combine(_folder,file)));stream.WriteByte(0);
			}
			File.WriteAllText(Path.Combine(_folder,"DIGEST"),Hash(stream.ToArray())+"\n");
			var exception=Assert.Throws<HarborChartPackException>(()=>HarborChartPackJson.Load(_folder,_runtime));
			Assert.That(exception.Message,Does.Contain("file hash mismatch"));
		}

		[Test]
		public void RehashedInvalidAttributeIndexIsRefused()
		{
			var file=Path.Combine(_folder,"soundings.json");var root=JObject.Parse(File.ReadAllText(file));root["features"][0][3]=99;File.WriteAllText(file,root.ToString());Reseal();
			Assert.Throws<HarborChartPackException>(()=>HarborChartPackJson.Load(_folder,_runtime));
		}

		// Streaming must retain the original fail-closed checks even when files have valid hashes.
		[TestCase("format")]
		[TestCase("fields")]
		[TestCase("tuple-length")]
		[TestCase("id-order")]
		[TestCase("attribute")]
		[TestCase("point-length")]
		[TestCase("encoded-short")]
		[TestCase("encoded-long")]
		[TestCase("line-size")]
		[TestCase("ring-open")]
		[TestCase("ring-zero")]
		[TestCase("empty-geometry")]
		[TestCase("geometry-type")]
		[TestCase("attribute-object")]
		[TestCase("attribute-float")]
		[TestCase("nonfinite")]
		[TestCase("bounds")]
		[TestCase("grid-size")]
		[TestCase("grid-inventory")]
		[TestCase("grid-member-count")]
		[TestCase("grid-member")]
		[TestCase("grid-key")]
		[TestCase("count")]
		[TestCase("duplicate-property")]
		[TestCase("unknown-nonfinite")]
		[TestCase("trailing-content")]
		public void RehashedMalformedLayerMatchesFrozenReader(string mutation)
		{
			var file = Path.Combine(_folder, "soundings.json"); var root = JObject.Parse(File.ReadAllText(file));
			switch (mutation)
			{
				case "format": root["format"] = "wrong"; break;
				case "fields": root["fields"][1] = "wrong"; break;
				case "tuple-length": ((JArray)root["features"][0]).RemoveAt(5); break;
				case "id-order": ((JArray)root["features"]).Add(root["features"][0].DeepClone()); break;
				case "attribute": root["features"][0][3] = 99; break;
				case "point-length": root["features"][0][4][1] = new JArray(250); break;
				case "encoded-short": root["features"][0][4] = new JArray("Point"); break;
				case "encoded-long": ((JArray)root["features"][0][4]).Add(JValue.CreateNull()); break;
				case "line-size": root["features"][0][4] = new JArray("LineString", new JArray(new JArray(250, 250))); break;
				case "ring-open": root["features"][0][4] = new JArray("Polygon", new JArray(new JArray(new JArray(250,250), new JArray(260,250), new JArray(260,260), new JArray(250,260)))); break;
				case "ring-zero": root["features"][0][4] = new JArray("Polygon", new JArray(new JArray(new JArray(250,250), new JArray(251,251), new JArray(252,252), new JArray(250,250)))); break;
				case "empty-geometry": root["features"][0][4] = new JArray("MultiPoint", new JArray()); break;
				case "geometry-type": root["features"][0][4][0] = "Circle"; break;
				case "attribute-object": root["attributes"][0] = new JArray(); break;
				case "attribute-float": root["features"][0][3] = 0.0; break;
				case "nonfinite": root["features"][0][4][1][0] = double.NaN; break;
				case "bounds": root["features"][0][4][1][0] = 1000001; break;
				case "grid-size": root["grid"]["cell_m"] = 251; break;
				case "grid-inventory": root["grid"]["cells"] = new JObject(); break;
				case "grid-member-count": root["grid"]["cells"]["1,1"] = new JArray(); break;
				case "grid-member": root["grid"]["cells"]["1,1"][0] = 2; break;
				case "grid-key": root["grid"]["cells"] = new JObject { ["01,1"] = new JArray(0) }; break;
				case "unknown-nonfinite": root["extra"] = double.PositiveInfinity; break;
			}
			var text = root.ToString();
			if (mutation == "duplicate-property") text = text.Replace("\"format\":", "\"format\": \"ignored\", \"format\":");
			if (mutation == "trailing-content") text += " {}";
			// Use bare nonfinite tokens so this exercises numeric rejection, not string validation.
			if (mutation == "nonfinite") text = text.Replace("\"NaN\"", "NaN");
			if (mutation == "unknown-nonfinite") text = text.Replace("\"Infinity\"", "Infinity");
			File.WriteAllText(file, text);
			if (mutation == "count")
			{
				var manifestFile = Path.Combine(_folder, "manifest.json"); var m = JObject.Parse(File.ReadAllText(manifestFile));
				m["counts"]["soundings"] = 9; File.WriteAllText(manifestFile, m.ToString());
			}
			Reseal();
			var expected = Assert.Throws<HarborChartPackException>(() => LegacyHarborChartPackJson.Load(_folder, _runtime));
			var actual = Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime));
			Assert.That(actual.File, Is.EqualTo(expected.File));
			if (mutation != "duplicate-property" && mutation != "trailing-content") Assert.That(actual.Message, Is.EqualTo(expected.Message));
		}

		[Test]
		public void LargeIntegerEvidenceMatchesFrozenReader()
		{
			var file = Path.Combine(_folder, "soundings.json"); var root = JObject.Parse(File.ReadAllText(file));
			root["attributes"][0]["large"] = JToken.Parse("123456789012345678901234567890");
			File.WriteAllText(file, root.ToString()); Reseal();
			var expected = LegacyHarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0].Properties["large"].Number.Value;
			var actual = HarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0].Properties["large"].Number.Value;
			Assert.That(BitConverter.DoubleToInt64Bits(actual), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
		}

		[Test]
		public void IgnoredGeometryExtensionsKeepOriginalAcceptance()
		{
			var file = Path.Combine(_folder, "soundings.json"); var root = JObject.Parse(File.ReadAllText(file));
			root["extra"] = new JConstructor("Ignored", 1);
			root["features"][0][4] = new JArray("GeometryCollection", new JArray(
				new JObject { ["coordinates"] = new JObject { ["ignored"] = new JConstructor("Ignored", 1) }, ["geometries"] = new JArray(new JObject { ["type"] = "Point", ["coordinates"] = new JArray(250,250) }), ["type"] = "GeometryCollection" },
				new JObject { ["geometries"] = new JArray(new JObject { ["type"] = "Unsupported" }), ["type"] = "Point", ["coordinates"] = new JArray(250,250) }));
			File.WriteAllText(file, root.ToString()); Reseal();
			var expected = LegacyHarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0];
			var actual = HarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0];
			HarborChartStreamingTests.CompareGeometry(actual.Geometry, expected.Geometry);
		}

		[TestCase("Point")]
		[TestCase("MultiPoint")]
		[TestCase("LineString")]
		[TestCase("MultiLineString")]
		[TestCase("Polygon")]
		[TestCase("MultiPolygon")]
		[TestCase("GeometryCollection")]
		public void AllGeometryFormsAndReorderedFieldsMatchFrozenReader(string type)
		{
			var point = new JArray(250.01, 250.02);
			var line = new JArray(point.DeepClone(), new JArray(259.99, 259.98));
			var ring = new JArray(new JArray(250,250), new JArray(260,250), new JArray(260,260), new JArray(250,260), new JArray(250,250));
			var hole = new JArray(new JArray(252,252), new JArray(254,252), new JArray(254,254), new JArray(252,254), new JArray(252,252));
			var polygon = new JArray(ring.DeepClone(), hole.DeepClone());
			JToken coordinates = type switch
			{
				"Point" => point,
				"MultiPoint" => line,
				"LineString" => line,
				"MultiLineString" => new JArray(line.DeepClone(), line.DeepClone()),
				"Polygon" => polygon,
				"MultiPolygon" => new JArray(polygon.DeepClone(), polygon.DeepClone()),
				_ => new JArray(
					new JObject { ["coordinates"] = polygon.DeepClone(), ["type"] = "Polygon" },
					new JObject { ["type"] = "LineString", ["coordinates"] = line.DeepClone() },
					new JObject { ["geometries"] = new JArray(new JObject { ["coordinates"] = point.DeepClone(), ["type"] = "Point" }), ["type"] = "GeometryCollection" },
					new JObject { ["coordinates"] = new JArray(polygon.DeepClone()), ["type"] = "MultiPolygon" },
					new JObject { ["coordinates"] = line.DeepClone(), ["type"] = "MultiPoint" },
					new JObject { ["coordinates"] = new JArray(line.DeepClone()), ["type"] = "MultiLineString" })
			};
			var file = Path.Combine(_folder, "soundings.json"); var root = JObject.Parse(File.ReadAllText(file));
			root["features"][0][4] = new JArray(type, coordinates);
			// Fixtures write features before attributes; reverse order also exercises fields/grid before features.
			root = new JObject(root.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value.DeepClone())));
			File.WriteAllText(file, root.ToString()); Reseal();
			var old = LegacyHarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0];
			var current = HarborChartPackJson.Load(_folder, _runtime)[ChartLayer.Soundings].Features[0];
			HarborChartStreamingTests.CompareGeometry(current.Geometry, old.Geometry);
		}

		[TestCase("duplicate-nested")]
		[TestCase("invalid-utf8")]
		[TestCase("bom")]
		[TestCase("trailing-comment")]
		public void RehashedTextFramingIsRefusedByBothReaders(string mutation)
		{
			var file = Path.Combine(_folder, "soundings.json"); var text = File.ReadAllText(file);
			if (mutation == "duplicate-nested") text = text.Replace("\"depth_m\":", "\"depth_m\": 4, \"depth_m\":");
			if (mutation == "trailing-comment") text += " /* comment */";
			if (mutation == "bom") text = "\uFEFF" + text;
			File.WriteAllText(file, text, new UTF8Encoding(false));
			if (mutation == "invalid-utf8") { var bytes = File.ReadAllBytes(file); bytes[10] = 255; File.WriteAllBytes(file, bytes); }
			Reseal();
			Assert.Throws<HarborChartPackException>(() => LegacyHarborChartPackJson.Load(_folder, _runtime));
			Assert.Throws<HarborChartPackException>(() => HarborChartPackJson.Load(_folder, _runtime));
		}

		[Test, Explicit("Requires the local adopted harbor and chart products; run by exact test filter.")]
		public void FullPackResidentMemoryAndQueries()
		{
			var harbor = Path.Combine(RepoPaths.Root, "data/processed/harbor");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var before = GC.GetTotalMemory(true);
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			var resident = GC.GetTotalMemory(true) - before;
			TestContext.Progress.WriteLine("chart_resident_managed_bytes=" + resident);
			for (var i = 0; i < 8; i++)
			{
				var layer = chart[(ChartLayer)i]; var buffer = layer.CreateQueryBuffer(layer.Features.Count);
				layer.QueryBox(-100000, -100000, 100000, 100000, buffer); Assert.That(buffer.Count, Is.EqualTo(layer.Features.Count));
				var bytes = GC.GetAllocatedBytesForCurrentThread(); for (var j = 0; j < 100; j++) layer.QueryCircle(0, 0, 1000, buffer);
				Assert.That(GC.GetAllocatedBytesForCurrentThread() - bytes, Is.Zero);
				TestContext.Progress.WriteLine(((ChartLayer)i) + "=" + layer.Features.Count);
			}
			GC.KeepAlive(chart); GC.KeepAlive(runtime);
		}

	}
}
