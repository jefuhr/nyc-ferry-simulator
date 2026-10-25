using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Evidence of the whole landing structures: the greybox at Wall St/Pier 11, South Williamsburg and DUMBO, steeply
	/// from the north-west and at an angle from the south-west, over the importer's seabed for context (moved onto the greybox's NAVD88 zero). Writes
	/// artifacts/unity/greybox-*.png. Needs graphics, so run it in batchmode without -nographics:
	/// <code>
	/// Unity -batchmode -projectPath unity -executeMethod FerrySim.World.EditorTools.GreyboxScreenshots.CaptureBatch -logFile ...
	/// </code>
	/// </summary>
	public static class GreyboxScreenshots
	{
		public static readonly (string StopId, string Name)[] Views = { ("87", "pier11"), ("8", "south-williamsburg"), ("20", "dumbo") };

		public const string ImportPrefabPath = "Assets/FerrySim/World/Generated/LowerEastRiver/lower-east-river.prefab";

		public static void CaptureBatch()
		{
			try
			{
				Capture();
				EditorApplication.Exit(0);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
				EditorApplication.Exit(1);
			}
		}

		[MenuItem("FerrySim/World/Capture Greybox Screenshots")]
		public static void Capture()
		{
			var data = PipelineData.Load();
			var antiAliasing = QualitySettings.antiAliasing;
			// The editor compiles shaders asynchronously and draws a cyan placeholder meanwhile: compile before drawing.
			var asynchronous = ShaderUtil.allowAsyncCompilation;
			ShaderUtil.allowAsyncCompilation = false;
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var greyboxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GreyboxBuilder.PrefabPath) ?? throw new FileNotFoundException("Rebuild the greybox first.", GreyboxBuilder.PrefabPath);
			var greybox = (GameObject)PrefabUtility.InstantiatePrefab(greyboxPrefab);
			foreach (var label in greybox.GetComponentsInChildren<Billboard>(true))
			{
				label.gameObject.SetActive(false); // camera-facing labels scale with distance and would cover the structures
			}

			var seabed = AssetDatabase.LoadAssetAtPath<GameObject>(ImportPrefabPath);
			GameObject imported = null;
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(GreyboxBuilder.DatumPath) ?? throw new FileNotFoundException("Rebuild the greybox first.", GreyboxBuilder.DatumPath);
			if (seabed != null)
			{
				// Both put NAVD88 zero on the geoid, about 31.8 m below the world frame: the importer flat at the slice
				// centre's offset, the greybox on the datum's curved surface (centimetres apart near these landings).
				imported = (GameObject)PrefabUtility.InstantiatePrefab(seabed);
				imported.transform.position = new Vector3(0f, -0.05f, 0f);
				foreach (var landing in imported.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Landings" || t.name == "Structures"))
				{
					landing.gameObject.SetActive(false); // the greybox draws the structures; the importer's strips would hide them
				}
			}

			var sunObject = new GameObject("Sun");
			var sun = sunObject.AddComponent<Light>();
			sun.type = LightType.Directional;
			sun.intensity = 1.2f;
			sun.shadows = LightShadows.None; // evidence of geometry and kinds, not of lighting
			sunObject.transform.rotation = Quaternion.Euler(50f, 150f, 0f);
			// No skybox: its default ground colour would otherwise tint every steeply viewed surface through reflections.
			RenderSettings.skybox = null;
			RenderSettings.reflectionIntensity = 0f;
			RenderSettings.ambientMode = AmbientMode.Flat;
			RenderSettings.ambientLight = new Color(.55f, .58f, .62f);
			var cameraObject = new GameObject("GreyboxEvidenceCamera");
			try
			{
				var camera = cameraObject.AddComponent<Camera>();
				cameraObject.AddComponent<UniversalAdditionalCameraData>();
				camera.clearFlags = CameraClearFlags.SolidColor;
				camera.backgroundColor = new Color(.08f, .16f, .24f);
				camera.nearClipPlane = .3f;
				camera.farClipPlane = 5000f;
				var directory = Path.Combine(PipelineData.RepoRoot, "artifacts", "unity");
				Directory.CreateDirectory(directory);
				foreach (var (stopId, name) in Views)
				{
					var stop = data.Landings.First(l => l.StopId == stopId);
					var target = new Vector3((float)stop.East, (float)WaterSurface.SurfaceUpAt(datum, stop.North, stop.East, 0.0), (float)stop.North);
					camera.fieldOfView = 45f;
					// Steeply down with the sun behind the camera: every berth, pier and bulkhead within the radius at once.
					camera.transform.position = target + new Vector3(-80f, 190f, 80f);
					camera.transform.LookAt(target, Vector3.up);
					Render(camera, Path.Combine(directory, $"greybox-{name}-steep.png"));
					camera.transform.position = target + new Vector3(-95f, 70f, -95f);
					camera.transform.LookAt(target, Vector3.up);
					Render(camera, Path.Combine(directory, $"greybox-{name}-oblique.png"));
				}
			}
			finally
			{
				Object.DestroyImmediate(cameraObject);
				Object.DestroyImmediate(sunObject);
				Object.DestroyImmediate(greybox);
				if (imported != null)
				{
					Object.DestroyImmediate(imported);
				}

				// URP synchronizes this global during its first render; evidence capture must restore it.
				QualitySettings.antiAliasing = antiAliasing;
				ShaderUtil.allowAsyncCompilation = asynchronous;
			}
		}

		private static void Render(Camera camera, string path)
		{
			var target = new RenderTexture(1600, 1000, 24);
			target.Create();
			var previous = RenderTexture.active;
			try
			{
				// A view's first render can draw newly visible objects with uninitialised lighting: keep the second.
				RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
				RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
				RenderTexture.active = target;
				var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
				try
				{
					image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
					image.Apply();
					File.WriteAllBytes(path, image.EncodeToPNG());
				}
				finally
				{
					Object.DestroyImmediate(image);
				}
			}
			finally
			{
				RenderTexture.active = previous;
				target.Release();
				Object.DestroyImmediate(target);
			}

			Debug.Log("Greybox evidence: " + path);
		}
	}
}
