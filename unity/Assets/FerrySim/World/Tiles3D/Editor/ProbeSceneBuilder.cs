using System;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FerrySim.World.Tiles3D.EditorTools
{
	/// <summary>
	/// Writes Tiles3D/Scenes/Tiles3DProbe.unity from scratch: a CesiumGeoreference at the shared world origin, the harbor
	/// tileset under it (disabled, its URL empty: the probe points it at StreamingAssets at run time, as a file:// URL that
	/// depends on where the player is), the main camera with a globe anchor and origin shifting as in the slice, a sun, flat
	/// ambient light and the <see cref="TilesetProbe"/>. No Cesium ion, no token. The scene is code-built so it can be
	/// reproduced; hand edits are lost on the next rebuild.
	/// <c>-executeMethod FerrySim.World.Tiles3D.EditorTools.ProbeSceneBuilder.BuildFromCommandLine</c>
	/// </summary>
	public static class ProbeSceneBuilder
	{
		public const string Folder = "Assets/FerrySim/World/Tiles3D";
		public const string ScenePath = Folder + "/Scenes/Tiles3DProbe.unity";
		public const string LightingPath = Folder + "/Scenes/Tiles3DProbeLighting.lighting";
		public const string ShaderName = "FerrySim/Tiles3DProbe";

		/// <summary>As in the slice: the camera may move this far, metres, before Cesium recentres the georeference on it.</summary>
		public const double OriginShiftDistance = 100.0;

		public static readonly Color Background = new Color(0.62f, 0.74f, 0.86f);

		[MenuItem("FerrySim/World/Tiles3D/Rebuild Probe Scene")]
		public static void Build()
		{
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var shader = Shader.Find(ShaderName) ?? throw new InvalidOperationException($"Shader {ShaderName} not found.");

			var georeferenceObject = new GameObject("CesiumGeoreference");
			var georeference = georeferenceObject.AddComponent<CesiumGeoreference>();
			georeference.SetOriginLongitudeLatitudeHeight(WorldOrigin.LongitudeDegrees, WorldOrigin.LatitudeDegrees, 0.0);
			georeference.scale = 1.0;

			// Disabled until the probe has set its file:// URL, so Cesium never tries an empty one.
			var tilesetObject = new GameObject("Harbor 3D Tiles");
			tilesetObject.SetActive(false);
			tilesetObject.transform.SetParent(georeferenceObject.transform, false);
			var tileset = tilesetObject.AddComponent<Cesium3DTileset>();
			tileset.tilesetSource = CesiumDataSource.FromUrl;
			tileset.url = string.Empty;
			tileset.showCreditsOnScreen = false;
			tileset.createPhysicsMeshes = false;
			tileset.maximumCachedBytes = TilesetProbe.MaximumCachedBytes;
			tileset.maximumScreenSpaceError = 16f;

			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			cameraObject.transform.SetParent(georeferenceObject.transform, false);
			var camera = cameraObject.AddComponent<Camera>();
			camera.nearClipPlane = 1f;
			camera.farClipPlane = 40000f;
			camera.fieldOfView = 60f;
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = Background;
			var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
			cameraData.renderShadows = false;
			cameraObject.AddComponent<AudioListener>();
			var anchor = cameraObject.AddComponent<CesiumGlobeAnchor>();
			anchor.adjustOrientationForGlobeWhenMoving = false;
			var originShift = cameraObject.AddComponent<CesiumOriginShift>();
			originShift.distance = OriginShiftDistance;

			var sunObject = new GameObject("Sun");
			sunObject.transform.rotation = Quaternion.Euler(45f, 210f, 0f);
			var sun = sunObject.AddComponent<Light>();
			sun.type = LightType.Directional;
			sun.intensity = 1.1f;
			sun.shadows = LightShadows.None;
			RenderSettings.sun = sun;
			RenderSettings.skybox = null;
			RenderSettings.ambientMode = AmbientMode.Flat;
			RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.5f);
			RenderSettings.fog = false;

			var lighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingPath);
			if (lighting == null)
			{
				lighting = new LightingSettings { name = "Tiles3DProbeLighting" };
				AssetDatabase.CreateAsset(lighting, LightingPath);
			}

			lighting.bakedGI = false;
			lighting.realtimeGI = false;
			EditorUtility.SetDirty(lighting);
			Lightmapping.lightingSettings = lighting;

			var probeObject = new GameObject("Tiles3D Probe");
			probeObject.AddComponent<TilesetProbe>().Wire(tileset, anchor, shader);

			if (!EditorSceneManager.SaveScene(scene, ScenePath))
			{
				throw new InvalidOperationException("Could not save " + ScenePath);
			}

			AssetDatabase.SaveAssets();
		}

		public static void BuildFromCommandLine()
		{
			var exit = 1;
			try
			{
				Build();
				exit = 0;
			}
			catch (Exception error)
			{
				Debug.LogError($"[FerrySim] Tiles3D probe scene not built: {error}");
			}

			EditorApplication.Exit(exit);
		}
	}
}
