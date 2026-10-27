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
			var names = new[] { "HullWhite", "DeckGrey", "WindowGlass", "DarkMetal", "BlackFender", "RailMetal", "CabinWhite" };
			var colours = new[] { new Color(.82f,.85f,.85f), new Color(.37f,.41f,.43f), new Color(.025f,.055f,.065f),
				new Color(.19f,.23f,.25f), new Color(.055f,.065f,.07f), new Color(.65f,.69f,.70f), new Color(.93f,.93f,.91f) };
			var smoothness = new[] { .62f, .15f, .92f, .55f, .05f, .78f, .68f };
			var surfaces = new Material[names.Length];
			for (var i = 0; i < names.Length; i++)
			{
				var surfacePath = Folder + "/Materials/" + names[i] + ".mat";
				var surface = AssetDatabase.LoadAssetAtPath<Material>(surfacePath);
				if (surface == null)
				{
					surface = new Material(Shader.Find("Universal Render Pipeline/Lit"));
					AssetDatabase.CreateAsset(surface, surfacePath);
				}

				surface.SetColor("_BaseColor", colours[i]);
				surface.SetFloat("_Smoothness", smoothness[i]);
				surface.SetFloat("_Metallic", i == 5 ? .8f : i == 3 ? .25f : 0f);
				surface.enableInstancing = true;
				EditorUtility.SetDirty(surface);
				surfaces[i] = surface;
			}

			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			foreach (var id in new[] { "t-class", "k-class" })
			{
				var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(repo, "vessels", id + ".json")));
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
						model.Length = (float)d.LengthOverall;
						model.Beam = (float)d.Beam;
						model.Draft = (float)d.Draft;
						foreach (var renderer in root.GetComponentsInChildren<Renderer>())
						{
							model.Height = Mathf.Max(model.Height, renderer.bounds.max.y);
						}

						foreach (var transform in root.GetComponentsInChildren<Transform>()) { transform.gameObject.layer = LayerMask.NameToLayer("Vessel"); }
						// Validate the FBX first, then remove only its metre/centimetre float round-trip noise.
						// The source values are the same estimates used by Blender and the existing cameras/lights.
						CanonicalMount(model, "Eye", new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward));
						CanonicalMount(model, "Bow", new Vector3(0, (float)d.Freeboard, (float)(d.LengthOverall * .5)));
						var layout = VesselLightLayout.ForGreybox(d);
						CanonicalMount(model, "Masthead", layout.Masthead);
						CanonicalMount(model, "PortSidelight", layout.PortSidelight);
						CanonicalMount(model, "StarboardSidelight", layout.StarboardSidelight);
						CanonicalMount(model, "Sternlight", layout.Sternlight);
						CanonicalMount(model, "ForwardAnchor", layout.ForwardAnchor);
						CanonicalMount(model, "AfterAnchor", layout.AfterAnchor);
						CanonicalMount(model, "AfterMasthead", layout.AfterMasthead);

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
