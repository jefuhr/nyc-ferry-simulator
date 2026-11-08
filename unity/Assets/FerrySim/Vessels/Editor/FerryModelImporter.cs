using System;
using System.IO;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Vessels.Editor
{
	/// <summary>Reproducible native FBX import and prefab authoring. Writes only the Vessels subtree.</summary>
	public static class FerryModelImporter
	{
		private const string Folder = "Assets/FerrySim/Vessels";

		[MenuItem("FerrySim/Vessels/Rebuild FBX Prefabs")]
		public static void Build()
		{
			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
			var palettePath = Folder + "/Models/NeutralPalette.png";
			var texture = (TextureImporter)AssetImporter.GetAtPath(palettePath);
			texture.filterMode = FilterMode.Point;
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.mipmapEnabled = false;
			texture.textureCompression = TextureImporterCompression.Uncompressed;
			texture.SaveAndReimport();
			Directory.CreateDirectory(Folder + "/Materials");
			var materialPath = Folder + "/Materials/NeutralFerry.mat";
			var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
			if (material == null)
			{
				material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
				AssetDatabase.CreateAsset(material, materialPath);
			}

			material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(palettePath));
			material.SetColor("_BaseColor", Color.white);
			material.SetFloat("_Smoothness", .3f);
			material.enableInstancing = true;
			EditorUtility.SetDirty(material);
			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var palette = ReadLayout(repo, "t-class").materials;
			var names = new[] { "HullWhite", "DeckGrey", "WindowGlass", "DarkMetal", "BlackFender", "RailMetal", "CabinWhite" };
			var surfaces = new Material[names.Length];
			for (var i = 0; i < names.Length; i++)
			{
				if (palette.Length != names.Length || palette[i].name != names[i]) { throw new InvalidDataException("Unexpected neutral material list"); }
				var colour = palette[i].colour;
				var surfacePath = Folder + "/Materials/" + names[i] + ".mat";
				var surface = AssetDatabase.LoadAssetAtPath<Material>(surfacePath);
				if (surface == null)
				{
					surface = new Material(Shader.Find("Universal Render Pipeline/Lit"));
					AssetDatabase.CreateAsset(surface, surfacePath);
				}

				surface.SetColor("_BaseColor", new Color(colour[0], colour[1], colour[2]));
				surface.SetFloat("_Smoothness", palette[i].smoothness);
				surface.SetFloat("_Metallic", palette[i].metal);
				surface.enableInstancing = true;
				EditorUtility.SetDirty(surface);
				surfaces[i] = surface;
			}

			foreach (var id in new[] { "t-class", "k-class" })
			{
				var sourceLayout = ReadLayout(repo, id);
				if (sourceLayout.materials.Length != palette.Length) { throw new InvalidDataException("Class palettes differ"); }
				for (var i = 0; i < palette.Length; i++)
				{
					if (JsonUtility.ToJson(sourceLayout.materials[i]) != JsonUtility.ToJson(palette[i])) { throw new InvalidDataException("Class palettes differ"); }
				}
				var d = sourceLayout.values;
				for (var detail = 0; detail < 2; detail++)
				{
					var path = Folder + "/Models/" + id + "/LOD" + detail + ".fbx";
					var importer = (ModelImporter)AssetImporter.GetAtPath(path);
					importer.globalScale = 1f;
					importer.useFileScale = true;
					importer.bakeAxisConversion = true;
					importer.importAnimation = false;
					importer.importCameras = false;
					importer.importLights = false;
					importer.addCollider = false;
					importer.isReadable = true;
					importer.meshCompression = ModelImporterMeshCompression.Off;
					importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
					// Unity can reorder mesh submeshes. Remap FBX material names instead of assigning slot numbers.
					if (detail == 0)
					{
						for (var i = 0; i < names.Length; i++)
						{
							importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), names[i]), surfaces[i]);
						}
					}
					else { importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "NeutralFerry"), material); }
					importer.SaveAndReimport();
					var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
					var root = new GameObject(id + " LOD" + detail);
					try
					{
						var instance = Object.Instantiate(source, root.transform, false);
						instance.name = "ImportedFBX";
						foreach (var transform in instance.GetComponentsInChildren<Transform>())
						{
							if (transform == instance.transform || transform.GetComponent<MeshFilter>() != null) { continue; }
							transform.SetParent(root.transform, true);
						}

						var model = root.AddComponent<FerryModel>();
						model.ClassId = id;
						model.Detail = detail;
						model.Length = d.length_overall.value;
						model.Beam = d.beam.value;
						model.Draft = -d.baseline_height.value;
						foreach (var renderer in root.GetComponentsInChildren<Renderer>())
						{
							model.Height = Mathf.Max(model.Height, renderer.bounds.max.y);
						}

						foreach (var transform in root.GetComponentsInChildren<Transform>()) { transform.gameObject.layer = LayerMask.NameToLayer("Vessel"); }
						// Validate FBX mount conversion against the same measured layout, then remove only float round-trip noise.
						CanonicalMount(model, "Eye", Eye(repo, id));
						CanonicalMount(model, "Bow", new Vector3(0, d.bow_height.value, d.length_overall.value / 2));
						foreach (var field in typeof(LightValues).GetFields())
						{
							CanonicalMount(model, field.Name, Vector(((Point)field.GetValue(d.light_mounts)).value));
						}

						var output = Folder + "/Resources/FerrySimVessels/" + id;
						Directory.CreateDirectory(output);
						PrefabUtility.SaveAsPrefabAsset(root, output + "/LOD" + detail + ".prefab");
						Debug.Log("[FerryModels] " + id + " LOD" + detail + " eye=" + model.Mount("Eye").localPosition.ToString("F6") + " height=" + model.Height);
					}
					finally { Object.DestroyImmediate(root); }
				}
			}

			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
		}

		[Serializable] private sealed class Number { public float value; public string source; }
		[Serializable] private sealed class Point { public float[] value; }
		[Serializable] private sealed class Surface { public string name; public float[] colour; public float metal, smoothness; }
		[Serializable] private sealed class LightValues
		{
			public Point Masthead, PortSidelight, StarboardSidelight, Sternlight, ForwardAnchor, AfterAnchor, AfterMasthead;
		}
		[Serializable] private sealed class Values
		{
			public Number length_overall, beam, baseline_height, bow_height, upper_deck_height;
			public LightValues light_mounts;
		}
		[Serializable] private sealed class Layout { public string subject, kind; public int schema_version; public Values values; public Surface[] materials; }
		[Serializable] private sealed class EyeValues { public Point eye_position, eye_seated, floor_origin, footprint; }
		[Serializable] private sealed class EyeLayout { public EyeValues values; }

		private static Layout ReadLayout(string repo, string id)
		{
			var result = JsonUtility.FromJson<Layout>(File.ReadAllText(Path.Combine(repo, "art/blender/data/" + id + "-hull.json")));
			if (result.schema_version != 1 || result.subject != id || result.kind != "hull" || result.values == null || result.materials == null)
			{ throw new InvalidDataException("Invalid hull layout for " + id); }
			foreach (var value in new[] { result.values.length_overall, result.values.beam, result.values.baseline_height, result.values.bow_height })
			{
				if (value == null || string.IsNullOrEmpty(value.source) || float.IsNaN(value.value) || float.IsInfinity(value.value))
				{ throw new InvalidDataException("Missing layout value or provenance for " + id); }
			}
			return result;
		}

		private static Vector3 Vector(float[] values)
		{
			if (values == null || values.Length != 3) { throw new InvalidDataException("Expected a three-component mount"); }
			foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value)) { throw new InvalidDataException("Non-finite mount"); }
			return new Vector3(values[0], values[1], values[2]);
		}

		private static Vector3 Eye(string repo, string id)
		{
			var path = Path.Combine(repo, "art/blender/data/" + id + "-wheelhouse.json");
			var values = File.Exists(path) ? JsonUtility.FromJson<EyeLayout>(File.ReadAllText(path)).values : null;
			if (values?.eye_position?.value != null) { return Vector(values.eye_position.value); }
			if (values?.eye_seated?.value != null)
			{
				var floor = Vector(values.floor_origin.value);
				var eye = Vector(values.eye_seated.value);
				var footprint = Vector(values.footprint?.value);
				if (Mathf.Abs(floor.y - ReadLayout(repo, id).values.upper_deck_height.value) > .001f)
				{ throw new InvalidDataException("Interior floor differs from the hull upper deck"); }
				// The interior's contract: y above the floor, z offset from the wheelhouse front.
				return new Vector3(eye.x, floor.y + eye.y, floor.z + footprint.z / 2f + eye.z);
			}
			// Eye ownership remains with the interior layout; preserve today's fallback until provided.
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(repo, "vessels", id + ".json")));
			return new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward);
		}

		private static void CanonicalMount(FerryModel model, string name, Vector3 expected)
		{
			var mount = model.Mount(name);
			if (mount == null || Vector3.Distance(mount.localPosition, expected) > .001f)
			{
				throw new InvalidOperationException("FBX mount conversion for " + model.ClassId + "/" + name + ": " +
					(mount != null ? mount.localPosition.ToString("F9") : "missing") + "; expected=" + expected.ToString("F9"));
			}

			Debug.Log("[FerryModels] " + model.ClassId + " LOD" + model.Detail + " " + name + " import error m=" + Vector3.Distance(mount.localPosition, expected).ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
			mount.localPosition = expected;
		}
	}
}
