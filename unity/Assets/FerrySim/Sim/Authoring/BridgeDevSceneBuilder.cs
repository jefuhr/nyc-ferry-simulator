using System;
using System.IO;
using FerrySim.Controls;
using FerrySim.Core.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace FerrySim.Sim.Authoring
{
	/// <summary>
	/// Builds <c>Sim/Scenes/BridgeDev.unity</c> and its assets from code, so the dev scene is reproducible and
	/// reviewable: a greybox catamaran sized from the vessel file, a grid of water, a chase camera, the UI
	/// Toolkit debug readout, and the runner with the wheelhouse controls. Run it from the menu or in batch
	/// mode with <c>-executeMethod FerrySim.Sim.Authoring.BridgeDevSceneBuilder.BuildFromCommandLine</c>.
	/// </summary>
	public static class BridgeDevSceneBuilder
	{
		public const string Folder = "Assets/FerrySim/Sim/Scenes";
		public const string ScenePath = Folder + "/BridgeDev.unity";
		public const string ScenarioPath = Folder + "/BridgeDevScenario.asset";
		public const string PanelSettingsPath = Folder + "/BridgeDevPanelSettings.asset";
		public const string ThemePath = Folder + "/BridgeDevTheme.tss";
		public const string WaterMaterialPath = Folder + "/BridgeDevWater.mat";
		public const string HullMaterialPath = Folder + "/BridgeDevHull.mat";
		public const string CabinMaterialPath = Folder + "/BridgeDevCabin.mat";
		public const string ActionsPath = "Assets/FerrySim/Input/FerrySimControls.inputactions";

		[MenuItem("FerrySim/Build BridgeDev Scene")]
		public static void Build()
		{
			// Open the new scene first: a single-mode scene switch unloads assets nothing references yet, which
			// would leave the references below pointing at destroyed objects (saved as null).
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var scenario = CreateOrLoad(ScenarioPath, () =>
			{
				var asset = ScriptableObject.CreateInstance<ScenarioAsset>();
				asset.Settings = DefaultScenario();
				return asset;
			});
			var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath) ?? throw new FileNotFoundException("Missing UI theme", ThemePath);
			var panelSettings = CreateOrLoad(PanelSettingsPath, () => ScriptableObject.CreateInstance<PanelSettings>());
			panelSettings.themeStyleSheet = theme;
			panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
			EditorUtility.SetDirty(panelSettings);
			var water = MakeMaterial(WaterMaterialPath, new Color(0.11f, 0.29f, 0.40f), 0.82f);
			var hull = MakeMaterial(HullMaterialPath, new Color(0.78f, 0.79f, 0.80f), 0.35f);
			var cabin = MakeMaterial(CabinMaterialPath, new Color(0.93f, 0.94f, 0.95f), 0.25f);
			var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ActionsPath) ?? throw new FileNotFoundException("Missing input actions", ActionsPath);
			AssetDatabase.SaveAssets();

			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.82f);
			RenderSettings.ambientEquatorColor = new Color(0.50f, 0.56f, 0.60f);
			RenderSettings.ambientGroundColor = new Color(0.18f, 0.22f, 0.25f);

			var sun = new GameObject("Sun");
			var light = sun.AddComponent<Light>();
			light.type = LightType.Directional;
			light.intensity = 1.3f;
			light.shadows = LightShadows.Soft;
			sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

			var vessel = new GameObject("Vessel");
			var runner = vessel.AddComponent<VesselSimulationRunner>();
			var controller = vessel.AddComponent<HelmInputController>();
			var greybox = vessel.AddComponent<VesselGreybox>();

			var waterObject = GameObject.CreatePrimitive(PrimitiveType.Plane);
			waterObject.name = "Water";
			UnityEngine.Object.DestroyImmediate(waterObject.GetComponent<Collider>());
			waterObject.transform.localScale = new Vector3(400f, 1f, 400f);
			waterObject.GetComponent<MeshRenderer>().sharedMaterial = water;
			var waterLayer = LayerMask.NameToLayer("Water");
			if (waterLayer >= 0)
			{
				waterObject.layer = waterLayer;
			}

			var devWater = waterObject.AddComponent<DevWater>();

			var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = new Color(0.66f, 0.78f, 0.89f);
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = 5000f;
			camera.fieldOfView = 50f;
			cameraObject.AddComponent<AudioListener>();
			var chase = cameraObject.AddComponent<ChaseCamera>();
			cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 24f, -75f), Quaternion.Euler(14f, 0f, 0f));

			var readoutObject = new GameObject("Debug Readout");
			var document = readoutObject.AddComponent<UIDocument>();
			document.panelSettings = panelSettings;
			var readout = readoutObject.AddComponent<SimDebugReadout>();

			Set(runner, "scenario", scenario);
			Set(runner, "controlSource", controller);
			Set(controller, "actions", actions);
			Set(controller, "runner", runner);
			Set(controller, "chaseCamera", chase);
			Set(greybox, "runner", runner);
			Set(greybox, "hullMaterial", hull);
			Set(greybox, "cabinMaterial", cabin);
			Set(chase, "target", vessel.transform);
			Set(chase, "runner", runner);
			Set(devWater, "follow", vessel.transform);
			Set(readout, "runner", runner);
			var extra = new SerializedObject(readout);
			var sources = extra.FindProperty("extraSources");
			sources.arraySize = 1;
			sources.GetArrayElementAtIndex(0).objectReferenceValue = chase;
			extra.ApplyModifiedPropertiesWithoutUndo();

			// Bake a greybox so the scene reads in edit mode; at runtime it is rebuilt from the loaded vessel.
			var parameters = VesselParametersJson.Load(SimPaths.Resolve(scenario.Settings.vesselFile));
			VesselGreybox.Build(vessel.transform, GreyboxDimensions.From(parameters), hull, cabin);

			if (!EditorSceneManager.SaveScene(scene, ScenePath))
			{
				throw new IOException("Could not save " + ScenePath);
			}

			AssetDatabase.SaveAssets();
			Debug.Log($"[FerrySim] Built {ScenePath} for {parameters.Id}.");
		}

		/// <summary>Batch-mode entry point: builds the scene and exits with 0, or 1 on failure.</summary>
		public static void BuildFromCommandLine()
		{
			try
			{
				Build();
				EditorApplication.Exit(0);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
				EditorApplication.Exit(1);
			}
		}

		/// <summary>The dev scenario: a T-class just off the lower Manhattan ferry piers, heading up the East River, in calm water.</summary>
		public static ScenarioSettings DefaultScenario() => new ScenarioSettings
		{
			name = "BridgeDev",
			vesselFile = "vessels/t-class.json",
			startLatitude = 40.7040,
			startLongitude = -74.0010,
			startHeadingDegrees = 35.0,
			startSpeedKnots = 0.0,
			scenarioStartUtc = "2026-06-01T13:00:00Z",
			environment = new ScenarioEnvironment { calm = true },
		};

		private static T CreateOrLoad<T>(string path, Func<T> create) where T : UnityEngine.Object
		{
			var existing = AssetDatabase.LoadAssetAtPath<T>(path);
			if (existing != null)
			{
				return existing;
			}

			var asset = create();
			AssetDatabase.CreateAsset(asset, path);
			return asset;
		}

		private static Material MakeMaterial(string path, Color color, float smoothness)
		{
			var shader = Shader.Find("Universal Render Pipeline/Lit") ?? throw new InvalidOperationException("URP Lit shader not found.");
			var material = CreateOrLoad(path, () => new Material(shader));
			material.shader = shader;
			material.SetColor("_BaseColor", color);
			material.SetFloat("_Smoothness", smoothness);
			EditorUtility.SetDirty(material);
			return material;
		}

		private static void Set(UnityEngine.Object target, string property, UnityEngine.Object value)
		{
			var serialized = new SerializedObject(target);
			var field = serialized.FindProperty(property) ?? throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{property}'.");
			field.objectReferenceValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}
	}
}
