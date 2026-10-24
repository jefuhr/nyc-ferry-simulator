using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FerrySim.World.Import
{
	public static class ImportScreenshots
	{
		public static void ImportAndCapture() { SliceImporter.Import(); Capture(); }

		public static void Capture()
		{
			var antiAliasing = QualitySettings.antiAliasing;
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SliceImporter.Output + "/lower-east-river.prefab");
			if (prefab == null) throw new FileNotFoundException("Run SliceImporter.Import first.");
			var world = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			var cameraObject = new GameObject("ImportEvidenceCamera");
			try
			{
				var camera = cameraObject.AddComponent<Camera>(); cameraObject.AddComponent<UniversalAdditionalCameraData>();
				camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f, .04f, .07f);
				camera.orthographic = true; camera.nearClipPlane = .1f; camera.farClipPlane = 20000;
				var directory = Path.Combine(SliceImporter.Repository, "artifacts/unity"); Directory.CreateDirectory(directory);
				var bounds = world.GetComponentsInChildren<MeshRenderer>().First().bounds;
				foreach (var renderer in world.transform.Find("Terrain").GetComponentsInChildren<MeshRenderer>()) bounds.Encapsulate(renderer.bounds);
				Render(camera, bounds.center, Mathf.Max(bounds.size.z * .55f, bounds.size.x * .55f / (16f / 10)), "import-seabed", directory, true);
				foreach (var name in new[] { "Pier 11", "DUMBO" })
				{
					var face = SliceImporter.ReadFeatures("landing_faces.geojson").First(f => ((string)f["properties"]["stop_name"]).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
					var midpoint = face["properties"]["face_midpoint"];
					var point = ImportCoordinates.Position((double)midpoint["lon"], (double)midpoint["lat"], 0, SliceImporter.ReadGeoid());
					Render(camera, point, 95, "import-" + name.ToLowerInvariant().Replace(" ", "-"), directory, true);
				}
				var buoyFeature = SliceImporter.ReadFeatures("aids.geojson").First(f => ((string)f["properties"]["feature_class"]).StartsWith("BOY", StringComparison.Ordinal));
				var buoy = world.transform.Find("Aids/" + SliceImporter.FeatureName(buoyFeature));
				foreach (var flasher in buoy.GetComponentsInChildren<BuoyFlasher>()) flasher.SetSimulationTime(.1);
				Render(camera, buoy.position + Vector3.up * 1.5f, 5, "import-buoy", directory, false);
			}
			finally
			{
				Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(world);
				// URP synchronizes this global during its first render; evidence capture must restore it.
				QualitySettings.antiAliasing = antiAliasing;
			}
		}
		private static void Render(Camera camera, Vector3 point, float size, string name, string directory, bool overhead)
		{
			camera.orthographicSize = size;
			camera.transform.position = point + (overhead ? new Vector3(0, 6000, 0) : new Vector3(12, 8, -12));
			camera.transform.LookAt(point, overhead ? Vector3.forward : Vector3.up);
			var target = new RenderTexture(1600, 1000, 24); target.Create();
			var previous = RenderTexture.active;
			try
			{
				RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
				RenderTexture.active = target;
				var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
				try { image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); File.WriteAllBytes(Path.Combine(directory, name + ".png"), image.EncodeToPNG()); }
				finally { Object.DestroyImmediate(image); }
			}
			finally { RenderTexture.active = previous; target.Release(); Object.DestroyImmediate(target); }
			Debug.Log("Import evidence: " + Path.Combine(directory, name + ".png"));
		}
	}
}
