using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FerrySim.World.Import
{
	public static class SliceImporter
	{
		public const string Output = "Assets/FerrySim/World/Generated/LowerEastRiver";
		public static string Repository => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		public static string Inputs => Path.Combine(Repository, "data/processed/lower-east-river");
		private const int Stride = 4;
		private const int TileCells = 64;
		private static readonly List<string> Warnings = new List<string>();
		private static readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);
		public static IEnumerable<JObject> ReadFeatures(string name) => ((JArray)JObject.Parse(File.ReadAllText(Path.Combine(Inputs, name)))["features"]).Cast<JObject>().OrderBy(FeatureName, StringComparer.Ordinal);
		public static double ReadGeoid() => (double)JObject.Parse(File.ReadAllText(Path.Combine(Inputs, "geoid_offset.json")))["points"]["centre"]["navd88_zero_ellipsoidal_height_m"];
		public static string FeatureName(JObject f) => (string)(f["properties"]["id"] ?? f["properties"]["stop_id"]) ?? throw new InvalidDataException("Feature has no stable id.");

		[MenuItem("FerrySim/Import/Lower East River")]
		public static void Import()
		{
			var watch = Stopwatch.StartNew();
			if (LayerMask.NameToLayer("Structure") != 8) throw new InvalidOperationException("Structure layer must be reserved at index 8.");
			var geoid = ReadGeoid();
			var lightDatum = LightHeightDatum.Read(Repository);
			var grid = new RawDepthGrid(Path.Combine(Inputs, "unity"));
			Directory.CreateDirectory(Output); AssetDatabase.Refresh();
			Written.Clear(); Warnings.Clear();
			var root = new GameObject("LowerEastRiver");
			try
			{
				var terrainMaterial = Material("depth", "FerrySim/DepthColour", Color.white);
				var structureMaterial = Material("structure", "FerrySim/SolidColour", new Color(.36f, .39f, .42f));
				var landingMaterial = Material("landing", "FerrySim/SolidColour", new Color(1, .58f, .05f));
				var greyMaterial = Material("buoy-grey", "FerrySim/SolidColour", new Color(.65f, .68f, .71f));
				var tileCount = BuildTerrain(Child(root, "Terrain"), grid, geoid, terrainMaterial);
				var structureCount = BuildStructures(Child(root, "Structures"), geoid, structureMaterial);
				BuildAids(Child(root, "Aids"), geoid, greyMaterial, lightDatum);
				var contacts = BuildLandings(Child(root, "Landings"), geoid, landingMaterial);
				var prefabPath = Output + "/lower-east-river.prefab";
				PrefabUtility.SaveAsPrefabAsset(root, prefabPath); Written.Add(prefabPath);
				WriteText(Output + "/contacts.json", contacts.ToString(Formatting.Indented) + "\n");
				var inputFiles = new[] { "depth_merged_navd88.tif", "depth_merged_navd88.json", "shoreline_constructions.geojson", "land_areas.geojson", "depth_areas.geojson", "aids.geojson", "landing_faces.geojson", "landings.geojson", "geoid_offset.json", "unity/depth.f32", "unity/depth.json" };
				var provenance = new JObject();
				foreach (var file in inputFiles) provenance[file] = Hash(File.ReadAllBytes(Path.Combine(Inputs, file)));
				var manifest = new JObject { ["slice"] = "lower-east-river", ["raster_stride"] = Stride, ["tile_cells"] = TileCells,
					["terrain_tiles"] = tileCount, ["structure_features"] = structureCount, ["aid_count"] = ReadFeatures("aids.geojson").Count(),
					["landing_count"] = ReadFeatures("landings.geojson").Count(), ["land_area_count"] = ReadFeatures("land_areas.geojson").Count(),
					["depth_area_count"] = ReadFeatures("depth_areas.geojson").Count(), ["navd88_zero_y_m"] = geoid,
					["vertical_mapping"] = "Unity y = NAVD88 elevation + slice centre geoid offset; horizontal coordinates use core ellipsoid-surface geodesy",
					["light_height_datum"] = lightDatum.Provenance,
					["terrain_note"] = "Merged raster sampled every fourth cell (~12 m); last row/column retained; nodata triangles omitted; no fabricated land elevations",
					["structure_note"] = "Chart geometry extruded as greybox collider strips from NAVD88 -60 m to +1 m; not surveyed structure heights; point-only objects omitted",
					["contact_note"] = "Oriented geometry only; bridge must choose reviewed ContactSurface parameters; pipeline provenance retained per segment",
					["source_sha256"] = provenance, ["warnings"] = new JArray(Warnings) };
				WriteText(Output + "/import-manifest.json", manifest.ToString(Formatting.Indented) + "\n");
				// Only importer-owned stale outputs are removed; .meta files for live assets survive.
				foreach (var file in Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta", StringComparison.Ordinal)).Select(p => p.Replace('\\', '/')))
					if (!Written.Contains(file)) AssetDatabase.DeleteAsset(file);
				AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
				var bytes = Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
				Debug.Log($"FerrySim import: {tileCount} terrain tiles, {structureCount} structure features, {bytes} bytes, {watch.Elapsed.TotalSeconds:F3}s; {Warnings.Count} warnings.");
			}
			finally { Object.DestroyImmediate(root); }
		}

		private static GameObject Child(GameObject parent, string name)
		{
			var child = new GameObject(name); child.transform.SetParent(parent.transform, false); return child;
		}
		private static Material Material(string name, string shader, Color color)
		{
			var value = new Material(Shader.Find(shader) ?? throw new InvalidOperationException("Missing shader " + shader)) { name = name };
			value.SetColor("_BaseColor", color); return Save(value, name + ".mat");
		}
		private static T Save<T>(T asset, string name) where T : Object
		{
			var path = Output + "/" + name; Written.Add(path);
			var existing = AssetDatabase.LoadAssetAtPath<T>(path);
			if (existing == null) { AssetDatabase.CreateAsset(asset, path); return asset; }
			EditorUtility.CopySerialized(asset, existing); Object.DestroyImmediate(asset); EditorUtility.SetDirty(existing); return existing;
		}
		private static void WriteText(string path, string text)
		{
			Written.Add(path);
			if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
		}
		private static GameObject MeshObject(GameObject parent, string name, Mesh mesh, Material material, bool collider)
		{
			var go = Child(parent, name); go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>().sharedMaterial = material;
			if (collider) { go.layer = 8; go.AddComponent<MeshCollider>().sharedMesh = mesh; }
			return go;
		}
		private static int[] Samples(int size) => Enumerable.Range(0, (size - 1) / Stride + 1).Select(i => i * Stride).Concat(new[] { size - 1 }).Distinct().ToArray();
		private static int BuildTerrain(GameObject parent, RawDepthGrid grid, double geoid, Material material)
		{
			var columns = Samples(grid.Width); var rows = Samples(grid.Height); var count = 0;
			for (var ty = 0; ty < rows.Length - 1; ty += TileCells)
			for (var tx = 0; tx < columns.Length - 1; tx += TileCells)
			{
				var width = Math.Min(TileCells + 1, columns.Length - tx); var height = Math.Min(TileCells + 1, rows.Length - ty);
				var vertices = new Vector3[width * height]; var colors = new Color[vertices.Length]; var valid = new bool[vertices.Length]; var triangles = new List<int>();
				for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
				{
					var column = columns[tx + x]; var row = rows[ty + y]; var i = y * width + x; var elevation = grid.Values[row * grid.Width + column];
					valid[i] = RawDepthGrid.IsValid(elevation, grid.NoData);
					vertices[i] = ImportCoordinates.Position(grid.Lon + (column + .5) * grid.CellLon, grid.Lat + (row + .5) * grid.CellLat, valid[i] ? elevation : 0, geoid);
					colors[i] = elevation > 0 ? Color.Lerp(new Color(.38f, .48f, .28f), new Color(.65f, .6f, .42f), elevation / 30) : Color.Lerp(new Color(.13f, .65f, .69f), new Color(.015f, .07f, .24f), -elevation / 45);
				}
				for (var y = 0; y < height - 1; y++) for (var x = 0; x < width - 1; x++)
				{
					var i = y * width + x; var b = i + width;
					if (valid[i] && valid[b] && valid[i + 1]) triangles.AddRange(new[] { i, i + 1, b });
					if (valid[i + 1] && valid[b] && valid[b + 1]) triangles.AddRange(new[] { i + 1, b + 1, b });
				}
				var name = $"terrain-{ty / TileCells:D2}-{tx / TileCells:D2}";
				var mesh = new Mesh { name = name, vertices = vertices, colors = colors, triangles = triangles.ToArray() }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
				MeshObject(parent, name, Save(mesh, name + ".asset"), material, false); count++;
			}
			return count;
		}

		private static IEnumerable<JArray> Lines(JObject geometry)
		{
			var coords = (JArray)geometry["coordinates"];
			switch ((string)geometry["type"])
			{
				case "LineString": yield return coords; break;
				case "Polygon": case "MultiLineString": foreach (JArray line in coords) yield return line; break;
				case "MultiPolygon": foreach (JArray polygon in coords) foreach (JArray line in polygon) yield return line; break;
			}
		}
		private static int BuildStructures(GameObject parent, double geoid, Material material)
		{
			var count = 0; var points = 0;
			foreach (var feature in ReadFeatures("shoreline_constructions.geojson"))
			{
				var vertices = new List<Vector3>(); var triangles = new List<int>();
				foreach (var line in Lines((JObject)feature["geometry"]))
				for (var i = 1; i < line.Count; i++)
				{
					var a = line[i - 1]; var b = line[i]; var offset = vertices.Count;
					vertices.Add(ImportCoordinates.Position((double)a[0], (double)a[1], -60, geoid));
					vertices.Add(ImportCoordinates.Position((double)b[0], (double)b[1], -60, geoid));
					vertices.Add(ImportCoordinates.Position((double)a[0], (double)a[1], 1, geoid));
					vertices.Add(ImportCoordinates.Position((double)b[0], (double)b[1], 1, geoid));
					triangles.AddRange(new[] { offset, offset + 2, offset + 1, offset + 1, offset + 2, offset + 3,
						offset + 1, offset + 2, offset, offset + 3, offset + 2, offset + 1 });
				}
				if (vertices.Count == 0) { points++; continue; }
				var name = "structure-" + FeatureName(feature); var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
				mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
				MeshObject(parent, name, Save(mesh, name + ".asset"), material, true); count++;
			}
			Warnings.Add($"{points} point-only chart structures have no extent and are omitted from colliders."); return count;
		}

		private static void BuildAids(GameObject parent, double geoid, Material grey, LightHeightDatum datum)
		{
			foreach (var f in ReadFeatures("aids.geojson"))
			{
				var aid = Child(parent, FeatureName(f)); var p = f["properties"]; var c = f["geometry"]["coordinates"];
				aid.transform.position = ImportCoordinates.Position((double)c[0], (double)c[1], 0, geoid);
				var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder); body.name = FeatureName(f) + "-Greybox"; body.transform.SetParent(aid.transform, false);
				body.transform.localPosition = Vector3.up * 1.25f; body.transform.localScale = new Vector3(1.5f, 1.25f, 1.5f); body.GetComponent<Renderer>().sharedMaterial = grey;
				Object.DestroyImmediate(body.GetComponent<Collider>());
				var lights = (JArray)p["lights"];
				for (var i = 0; i < lights.Count; i++)
				{
					var data = lights[i]; var notation = (string)data["notation"];
					if (!LightCharacteristic.TryParse(notation, out var characteristic)) { Warnings.Add(FeatureName(f) + ": no supported period/rhythm: " + notation); continue; }
					if (data["sequence"] is JArray sequence && sequence.Count > 0)
						characteristic.SetSequence(sequence.Select(s => (double)s["seconds"]).ToArray(), sequence.Select(s => (string)s["state"] == "light").ToArray());
					else
					{
						if (!characteristic.TryValidateFallback(out var warning)) { Warnings.Add(FeatureName(f) + ": " + warning + ": " + notation); continue; }
						Warnings.Add(FeatureName(f) + ": nominal pulse widths (no chart SIGSEQ): " + notation);
					}
					var color = characteristic.Colour == "G" ? Color.green : characteristic.Colour == "R" ? Color.red : characteristic.Colour == "Y" || characteristic.Colour == "Am" ? Color.yellow : characteristic.Colour == "Bu" ? Color.blue : Color.white;
					var lamp = Child(aid, FeatureName(f) + "-Light-" + i); lamp.transform.localPosition = Vector3.up * (data["height_m"].Type == JTokenType.Null ? 3 : (float)datum.ToNavd88((double)data["height_m"]));
					var light = lamp.AddComponent<Light>(); light.type = LightType.Point; light.color = color; light.range = 25; light.intensity = 2;
					var lens = GameObject.CreatePrimitive(PrimitiveType.Sphere); lens.name = FeatureName(f) + "-Lens-" + i; lens.transform.SetParent(lamp.transform, false); lens.transform.localScale = Vector3.one * .7f;
					Object.DestroyImmediate(lens.GetComponent<Collider>()); var renderer = lens.GetComponent<Renderer>(); renderer.sharedMaterial = Material("light-" + characteristic.Colour, "FerrySim/SolidColour", color);
					var flasher = lamp.AddComponent<BuoyFlasher>(); flasher.Characteristic = characteristic; flasher.Lamp = light; flasher.Lens = renderer;
				}
				var prefabPath = Output + "/aid-" + FeatureName(f) + ".prefab";
				PrefabUtility.SaveAsPrefabAsset(aid, prefabPath); Written.Add(prefabPath);
			}
		}

		public static IEnumerable<ContactSegment> LandingSegments(JObject feature)
		{
			var p = feature["properties"]; var coords = (JArray)p["face_geometry"]["coordinates"]; var outward = (JArray)p["outward_normal_ne"];
			for (var i = 1; i < coords.Count; i++)
			{
				var a = coords[i - 1]; var b = coords[i];
				var start = LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)a[1], (double)a[0]));
				var end = LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)b[1], (double)b[0]));
				if ((end.East - start.East) * (double)outward[0] - (end.North - start.North) * (double)outward[1] < 0) { var swap = start; start = end; end = swap; }
				// Surface is only needed to use the core's orientation contract; it is not exported as a physical calibration.
				yield return new ContactSegment(start, end, new ContactSurface(1, 0, 0, 1), StableId((string)p["stop_id"] + ":" + (i - 1)));
			}
		}
		private static int StableId(string value)
		{
			unchecked { uint h = 2166136261; foreach (var c in value) h = (h ^ c) * 16777619; return (int)(h & 0x7fffffff); }
		}
		private static JArray BuildLandings(GameObject parent, double geoid, Material material)
		{
			var contacts = new JArray(); var faces = ReadFeatures("landing_faces.geojson").ToDictionary(FeatureName, StringComparer.Ordinal);
			foreach (var stop in ReadFeatures("landings.geojson"))
			{
				var id = FeatureName(stop); if (!faces.TryGetValue(id, out var face)) throw new InvalidDataException("Missing landing face for GTFS stop " + id);
				var marker = Child(parent, id); var segments = LandingSegments(face).ToArray();
				for (var i = 0; i < segments.Length; i++)
				{
					var segment = segments[i]; var a = new Vector3((float)segment.Start.East, (float)geoid + .18f, (float)segment.Start.North); var b = new Vector3((float)segment.End.East, a.y, (float)segment.End.North);
					var strip = GameObject.CreatePrimitive(PrimitiveType.Cube); strip.name = id + "-Contact-" + i; strip.transform.SetParent(marker.transform, false); strip.transform.position = (a + b) * .5f;
					strip.transform.rotation = Quaternion.LookRotation(b - a); strip.transform.localScale = new Vector3(.65f, .35f, (float)segment.Length); strip.GetComponent<Renderer>().sharedMaterial = material;
					strip.layer = 8;
					contacts.Add(new JObject { ["id"] = segment.Id, ["stop_id"] = id, ["stop_name"] = stop["properties"]["stop_name"].DeepClone(),
						["start_ne_m"] = new JArray(segment.Start.North, segment.Start.East), ["end_ne_m"] = new JArray(segment.End.North, segment.End.East),
						["water_normal_ne"] = new JArray(segment.WaterNormal.North, segment.WaterNormal.East),
						["source"] = face["properties"]["source"].DeepClone(), ["confidence"] = face["properties"]["confidence"].DeepClone(),
						["verification"] = face["properties"]["verification"].DeepClone(), ["water_side_verified_by_pipeline"] = face["properties"]["water_side"].DeepClone() });
				}
			}
			return contacts;
		}
		private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
		public static string[] GeneratedHashes() => Directory.GetFiles(Output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(p => p + ":" + Hash(File.ReadAllBytes(p))).ToArray();
	}
}
