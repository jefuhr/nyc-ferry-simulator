using System;
using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Writes World/Scenes/LowerEastRiver.unity from scratch: Cesium georeference at the shared world
	/// origin, the world frame, water, the greybox prefab, sun and sky, the main camera with origin
	/// shifting and the three camera controllers, the wheelhouse mount and the georeference check.
	/// Run after <see cref="GreyboxBuilder"/>. The scene is code-built so it can be reproduced and
	/// reviewed; hand edits to it are lost on the next rebuild.
	/// </summary>
	public static class SceneBuilder
	{
		public const string ScenePath = GreyboxBuilder.WorldFolder + "/Scenes/LowerEastRiver.unity";
		public const string LightingPath = GreyboxBuilder.WorldFolder + "/Scenes/LowerEastRiverLighting.lighting";
		public const string MaterialFolder = GreyboxBuilder.WorldFolder + "/Materials";
		public const string WaterMaterialPath = MaterialFolder + "/Water.mat";
		public const string SkyMaterialPath = MaterialFolder + "/Sky.mat";

		/// <summary>GTFS stop used for the georeference check and the default view: Wall St/Pier 11.</summary>
		public const string ReferenceStopId = "87";

		/// <summary>Distance the camera may move before Cesium recentres the georeference on it, metres.</summary>
		public const double OriginShiftDistance = 100.0;

		public static void Build(PipelineData data, string worldFolder = GreyboxBuilder.WorldFolder)
		{
			var scenePath = WorldBuildPaths.At(worldFolder, ScenePath);
			var previous = WorldFileIds.Read(scenePath);
			var lightingPath = WorldBuildPaths.At(worldFolder, LightingPath);
			var materialFolder = WorldBuildPaths.At(worldFolder, MaterialFolder);
			var reference = data.Landings.FirstOrDefault(l => l.StopId == ReferenceStopId)
				?? throw new InvalidOperationException($"GTFS stop {ReferenceStopId} is missing from landings.geojson.");

			// A new single scene unloads unreferenced assets, so load everything after creating it.
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(WorldBuildPaths.At(worldFolder, GreyboxBuilder.DatumPath));
			var greybox = AssetDatabase.LoadAssetAtPath<GameObject>(WorldBuildPaths.At(worldFolder, GreyboxBuilder.PrefabPath));
			if (datum == null || !datum.IsPopulated || greybox == null)
			{
				throw new InvalidOperationException("Build the greybox first (FerrySim > World > Rebuild Greybox From Pipeline Data).");
			}

			GreyboxBuilder.EnsureFolder(materialFolder);
			GreyboxBuilder.EnsureFolder(worldFolder + "/Scenes");
			var waterMaterial = UpsertMaterial(WorldBuildPaths.At(worldFolder, WaterMaterialPath), "FerrySim/Water", null);
			var skyMaterial = UpsertMaterial(WorldBuildPaths.At(worldFolder, SkyMaterialPath), "FerrySim/Sky", null);

			// Georeference first: anchors added under it take their ECEF pose from its origin.
			var georeferenceObject = new GameObject("CesiumGeoreference");
			var georeference = georeferenceObject.AddComponent<CesiumGeoreference>();
			georeference.SetOriginLongitudeLatitudeHeight(WorldOrigin.LongitudeDegrees, WorldOrigin.LatitudeDegrees, 0.0);
			georeference.scale = 1.0;

			var worldObject = new GameObject("World");
			worldObject.transform.SetParent(georeferenceObject.transform, false);
			worldObject.AddComponent<CesiumGlobeAnchor>();
			var worldFrame = worldObject.AddComponent<WorldFrame>();
			worldFrame.Pin();

			var waterObject = new GameObject("Water") { layer = WorldLayers.Water };
			waterObject.transform.SetParent(worldObject.transform, false);
			waterObject.AddComponent<MeshFilter>();
			var waterRenderer = waterObject.AddComponent<MeshRenderer>();
			waterRenderer.sharedMaterial = waterMaterial;
			waterRenderer.shadowCastingMode = ShadowCastingMode.Off;
			waterRenderer.receiveShadows = false;
			waterRenderer.lightProbeUsage = LightProbeUsage.Off;
			waterRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
			var water = waterObject.AddComponent<WaterSurface>();
			water.Configure(datum);

			var greyboxInstance = (GameObject)PrefabUtility.InstantiatePrefab(greybox, worldObject.transform);
			greyboxInstance.transform.localPosition = Vector3.zero;
			greyboxInstance.transform.localRotation = Quaternion.identity;

			// The wheelhouse mount waits off Pier 11 until the bridge task parents it to the vessel.
			var pier = greyboxInstance.GetComponentsInChildren<LandingBarge>().First(b => b.StopId == ReferenceStopId);
			var mountObject = new GameObject("WheelhouseMount");
			mountObject.transform.SetParent(worldObject.transform, false);
			var faceMiddle = new Vector3((float)(0.5 * (pier.FaceStartEast + pier.FaceEndEast)), 0f, (float)(0.5 * (pier.FaceStartNorth + pier.FaceEndNorth)));
			var outward = new Vector3((float)pier.OutwardNormalEast, 0f, (float)pier.OutwardNormalNorth);
			var mountPosition = faceMiddle + outward * 40f;
			mountPosition.y = (float)water.SurfaceUp(mountPosition.z, mountPosition.x) + 4.5f;
			mountObject.transform.localPosition = mountPosition;
			mountObject.transform.localRotation = Quaternion.LookRotation(-outward, Vector3.up);
			var mount = mountObject.AddComponent<WheelhouseCameraMount>();

			// Main camera: under the georeference with a globe anchor and origin shift, as Cesium requires.
			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			cameraObject.transform.SetParent(georeferenceObject.transform, false);
			var camera = cameraObject.AddComponent<Camera>();
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = 40000f;
			camera.fieldOfView = 60f;
			camera.clearFlags = CameraClearFlags.Skybox;
			var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
			cameraData.requiresDepthTexture = true;
			cameraData.renderShadows = true;
			cameraObject.AddComponent<AudioListener>();
			var cameraAnchor = cameraObject.AddComponent<CesiumGlobeAnchor>();
			cameraAnchor.adjustOrientationForGlobeWhenMoving = false;
			cameraAnchor.detectTransformChanges = true;
			var originShift = cameraObject.AddComponent<CesiumOriginShift>();
			originShift.distance = OriginShiftDistance;
			var freeFly = cameraObject.AddComponent<FreeFlyCameraController>();
			var chase = cameraObject.AddComponent<ChaseCameraController>();
			var wheelhouse = cameraObject.AddComponent<WheelhouseCameraController>();
			wheelhouse.Mount = mount;
			var rig = cameraObject.AddComponent<CameraRig>();
			rig.Configure(freeFly, chase, wheelhouse);

			// Default view: Pier 11 from the water, 150 m out along the face normal.
			var pierCentre = pier.transform.position;
			var viewPosition = worldFrame.transform.TransformPoint(faceMiddle + outward * 150f + new Vector3(0f, (float)water.SurfaceUp(faceMiddle.z, faceMiddle.x) + 5f, 0f));
			freeFly.SetPoseLookingAt(viewPosition, pierCentre);

			var sunObject = new GameObject("Sun");
			var light = sunObject.AddComponent<Light>();
			light.type = LightType.Directional;
			light.shadows = LightShadows.Hard;
			var sun = sunObject.AddComponent<SunController>();
			sun.Configure(light, worldFrame);

			RenderSettings.skybox = skyMaterial;
			RenderSettings.sun = light;
			sun.SetLocalTime(2026, 6, 15, 12.0);

			var lighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath);
			if (lighting == null)
			{
				lighting = new LightingSettings { name = "LowerEastRiverLighting" };
				AssetDatabase.CreateAsset(lighting, lightingPath);
			}

			lighting.bakedGI = false;
			lighting.realtimeGI = false;
			EditorUtility.SetDirty(lighting);
			Lightmapping.lightingSettings = lighting;

			var checkObject = new GameObject("GeoreferenceCheck");
			var check = checkObject.AddComponent<GeoreferenceCheck>();
			check.Configure(georeference, worldFrame, reference.StopName, reference.Latitude, reference.Longitude,
				$"NYC Ferry GTFS stop {reference.StopId} via data/processed/{PipelineData.DefaultSlice}/landings.geojson");

			if (!EditorSceneManager.SaveScene(scene, scenePath))
			{
				throw new InvalidOperationException("Could not save " + scenePath);
			}

			AssetDatabase.SaveAssets();
			WorldFileIds.Stabilize(scenePath, previous);
		}

		private static Material UpsertMaterial(string path, string shaderName, Action<Material> configure)
		{
			var shader = Shader.Find(shaderName) ?? throw new InvalidOperationException($"Shader {shaderName} not found.");
			var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
			if (existing != null && existing.shader == shader)
			{
				configure?.Invoke(existing);
				EditorUtility.SetDirty(existing);
				return existing;
			}

			// A different shader leaves the old one's properties in the file; start clean instead.
			if (existing != null)
			{
				AssetDatabase.DeleteAsset(path);
			}

			var material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
			configure?.Invoke(material);
			AssetDatabase.CreateAsset(material, path);
			return material;
		}
	}
}
