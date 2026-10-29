using System;
using System.IO;
using CesiumForUnity;
using FerrySim.Controls;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.World.EditorTools;
using FerrySim.World.HarborTiles;
using FerrySim.World.Skyline;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.EditorTools
{
	/// <summary>Builds one standalone harbor scene. Never opens a slice scene or changes build settings.</summary>
	public static class HarborSceneBuilder
	{
		public const string Root = "Assets/FerrySim/Harbor";
		public const string ScenePath = Root + "/Scenes/Harbor.unity";
		public const string ConfigurationPath = Root + "/Data/harbor-scene.json";
		public const string SurfacePath = Root + "/Data/landing-contact-surfaces.json";
		public const string AuditPath = Root + "/Data/landing-audit.json";
		public const string WaterMaterialPath = "Assets/FerrySim/World/Materials/Water.mat";
		public const string TerrainMaterialPath = "Assets/FerrySim/World/HarborTiles/HarborTerrain.mat";
		public const string SkylineMaterialPath = "Assets/FerrySim/World/Skyline/HarborSkyline.mat";

		[MenuItem("FerrySim/Harbor/Rebuild Harbor Scene")]
		public static void Build()
		{
			Directory.CreateDirectory(Root + "/Scenes");
			AssetDatabase.Refresh();
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var configuration = Load<TextAsset>(ConfigurationPath);
			var settings = HarborSceneConfiguration.Parse(configuration.text);
			var terrainMaterial = MaterialAt(TerrainMaterialPath, "FerrySim/HarborSurface");
			var skylineMaterial = MaterialAt(SkylineMaterialPath, "FerrySim/HarborSurface");
			var skyMaterial = Load<Material>("Assets/FerrySim/World/Materials/Sky.mat");
			var hull = Load<Material>("Assets/FerrySim/Sim/Scenes/BridgeDevHull.mat");
			var cabin = Load<Material>("Assets/FerrySim/Sim/Scenes/BridgeDevCabin.mat");
			var actions = Load<InputActionAsset>("Assets/FerrySim/Input/FerrySimControls.inputactions");
			var vessel = VesselParametersJson.Load(SimPaths.Resolve("vessels/t-class.json"));
			var dimensions = GreyboxDimensions.From(vessel);

			var reference = New("CesiumGeoreference").AddComponent<CesiumGeoreference>();
			reference.SetOriginLongitudeLatitudeHeight(settings.originLongitude, settings.originLatitude, 0);
			reference.scale = 1;
			// This frame orients the sun only; near-field render objects have their own double globe anchors.
			var frame = New("World orientation", reference.transform).AddComponent<WorldFrame>();
			frame.Pin();
			var water = New("Harbor water — runtime service", reference.transform);
			water.layer = WorldLayers.Water;
			var navigation = New("Harbor navigation — runtime builder", reference.transform);
			var traffic = New("Harbor traffic — runtime builder", reference.transform);
			var terrain = AddLayer<HarborTileset>(reference.transform, settings.terrain, terrainMaterial);
			var skyline = AddLayer<SkylineTileset>(reference.transform, settings.skyline, skylineMaterial);

			var vesselObject = New("Vessel", reference.transform);
			vesselObject.layer = WorldLayers.Vessel;
			vesselObject.AddComponent<CesiumGlobeAnchor>();
			var placement = vesselObject.AddComponent<CesiumVesselPlacement>();
			Set(placement, "useGeoidOffsetFile", false);
			Set(placement, "geoidOffsetFile", string.Empty);
			var runner = vesselObject.AddComponent<VesselSimulationRunner>();
			runner.enabled = false;
			var helm = vesselObject.AddComponent<HelmInputController>();
			var greybox = vesselObject.AddComponent<VesselGreybox>();
			Set(runner, "controlSource", helm);
			Set(helm, "actions", actions);
			Set(helm, "runner", runner);
			Set(greybox, "runner", runner);
			Set(greybox, "hullMaterial", hull);
			Set(greybox, "cabinMaterial", cabin);
			VesselGreybox.Build(vesselObject.transform, dimensions, hull, cabin);
			var mount = New("Wheelhouse mount", vesselObject.transform).AddComponent<WheelhouseCameraMount>();
			Set(mount, "fieldOfView", settings.wheelhouseFieldOfView);
			mount.transform.localPosition = new Vector3(0, (float)dimensions.BridgeEyeHeight, (float)dimensions.BridgeEyeForward);
			mount.transform.localRotation = Quaternion.Euler(8, 0, 0);

			var cameraObject = New("Main Camera", reference.transform);
			cameraObject.tag = "MainCamera";
			var camera = cameraObject.AddComponent<Camera>();
			camera.fieldOfView = settings.cameraFieldOfView;
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = settings.cameraFarClipMetres;
			camera.clearFlags = CameraClearFlags.Skybox;
			var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
			cameraData.requiresDepthTexture = true;
			cameraData.renderShadows = true;
			cameraObject.AddComponent<AudioListener>();
			var anchor = cameraObject.AddComponent<CesiumGlobeAnchor>();
			anchor.adjustOrientationForGlobeWhenMoving = false;
			anchor.detectTransformChanges = true;
			cameraObject.AddComponent<CesiumOriginShift>().distance = settings.originShiftMetres;
			var freeFly = cameraObject.AddComponent<FreeFlyCameraController>();
			var chase = cameraObject.AddComponent<ChaseCameraController>();
			var wheelhouse = cameraObject.AddComponent<WheelhouseCameraController>();
			var rig = cameraObject.AddComponent<CameraRig>();
			Set(rig, "defaultFieldOfView", settings.cameraFieldOfView);
			rig.Configure(freeFly, chase, wheelhouse);
			rig.ChaseTarget = vesselObject.transform;
			rig.WheelhouseMount = mount;
			rig.CurrentMode = CameraRig.Mode.Wheelhouse;

			var sunObject = New("Sun and atmosphere");
			var light = sunObject.AddComponent<Light>();
			light.type = LightType.Directional;
			light.shadows = LightShadows.Hard;
			var sun = sunObject.AddComponent<SunController>();
			sun.Configure(light, frame);
			sun.Runner = runner;
			sunObject.AddComponent<Atmosphere>().Configure(sun, runner);
			RenderSettings.skybox = skyMaterial;
			RenderSettings.sun = light;
			sun.SetUtc(new DateTime(2025, 7, 20, 16, 0, 0, DateTimeKind.Utc));
			var lightingPath = Root + "/Data/HarborLighting.lighting";
			var lighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath);
			if (lighting == null)
			{
				lighting = new LightingSettings { name = "HarborLighting", bakedGI = false, realtimeGI = false };
				AssetDatabase.CreateAsset(lighting, lightingPath);
			}
			Lightmapping.lightingSettings = lighting;

			var ui = New("Wheelhouse UI").AddComponent<WheelhouseUI>();
			Set(ui, "telemetrySource", runner);
			Set(ui, "daylightSource", sun);
			Set(ui, "showPanel", true);
			Set(ui, "showHud", false);
			var bootstrap = New("Harbor composition").AddComponent<HarborBootstrap>();
			bootstrap.Configure(configuration, runner, placement, reference, rig, ui, helm, sun,
				new HarborTileset[] { terrain, skyline }, water.transform, navigation.transform, traffic.transform);
			// The composition root: the real services (Harbor/Integration/README.md).
			var factory = bootstrap.gameObject.AddComponent<HarborIntegrationFactory>();
			Set(factory, "landingSurfaces", Load<TextAsset>(SurfacePath));
			Set(factory, "landingAudit", Load<TextAsset>(AuditPath));
			Set(factory, "waterMaterial", Load<Material>(WaterMaterialPath));
			Set(factory, "wheelhouseUI", ui);
			Set(bootstrap, "serviceFactory", factory);
			AssetDatabase.SaveAssets();
			if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Could not save " + ScenePath);
			// Reuse the audited hierarchy-aware canonicalizer; IDs do not depend on an earlier scene being present.
			WorldFileIds.Stabilize(ScenePath, string.Empty);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		}

		public static void BuildFromCommandLine()
		{
			try { Build(); EditorApplication.Exit(0); }
			catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
		}

		private static void Set(Object target, string field, float value)
		{
			var serialized = new SerializedObject(target);
			serialized.FindProperty(field).floatValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		private static T AddLayer<T>(Transform parent, HarborSceneConfiguration.Layer settings, Material material) where T : HarborTileset
		{
			var host = New("Harbor " + settings.id, parent);
			host.SetActive(false);
			var layer = host.AddComponent<T>();
			layer.Configure(settings.id, settings.tilesetFile, settings.required, settings.maximumCachedBytes, settings.maximumScreenSpaceError, material);
			host.SetActive(true);
			return layer;
		}

		private static GameObject New(string name, Transform parent = null)
		{
			var host = new GameObject(name);
			if (parent != null) host.transform.SetParent(parent, false);
			return host;
		}

		private static Material MaterialAt(string path, string shaderName)
		{
			var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
			if (existing != null) return existing;
			var shader = Shader.Find(shaderName) ?? throw new InvalidOperationException("Missing shader: " + shaderName);
			var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
			AssetDatabase.CreateAsset(material, path);
			return material;
		}

		private static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new FileNotFoundException("Missing harbor scene dependency", path);
		private static void Set(Object target, string field, Object value) { var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
		private static void Set(Object target, string field, string value) { var data = new SerializedObject(target); data.FindProperty(field).stringValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
		private static void Set(Object target, string field, bool value) { var data = new SerializedObject(target); data.FindProperty(field).boolValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
	}
}
