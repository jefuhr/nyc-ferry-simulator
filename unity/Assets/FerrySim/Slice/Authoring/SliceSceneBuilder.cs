using System;
using System.IO;
using System.Linq;
using System.Text;
using CesiumForUnity;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Authoring
{
	/// <summary>
	/// Builds <c>Slice/Scenes/LowerEastRiverSlice.unity</c>, the drivable Lower East River slice: the world scene
	/// (opened, never saved back) plus the bridge's runner with the T-class on the Cesium placement, the helm, the
	/// wheelhouse UI bound to the runner and <see cref="SliceBootstrap"/>, which builds the landing contact geometry
	/// from data, places the imported terrain at startup and is the scenario catalog and launcher. The scenario starts at rest <see cref="ApproachBowClearance"/> m off South
	/// Williamsburg's primary landing face, heading in, on the environment pack <see cref="DefaultPack"/> at
	/// <see cref="ScenarioStartUtc"/> (<see cref="DefaultScenario"/>; Slice/README.md says why). Object ids are made
	/// reproducible (<see cref="SceneFileIds"/>), so a rebuild from the same inputs gives the same bytes. Run it from
	/// the menu or with
	/// <code>-executeMethod FerrySim.Slice.Authoring.SliceSceneBuilder.BuildFromCommandLine</code>
	/// </summary>
	public static class SliceSceneBuilder
	{
		public const string Folder = "Assets/FerrySim/Slice/Scenes";
		public const string ScenePath = Folder + "/LowerEastRiverSlice.unity";
		public const string ScenarioPath = Folder + "/LowerEastRiverSliceScenario.asset";
		public const string WorldScenePath = "Assets/FerrySim/World/Scenes/LowerEastRiver.unity";
		public const string TerrainPrefabPath = "Assets/FerrySim/World/Generated/LowerEastRiver/lower-east-river.prefab";
		public const string ImportManifestPath = "Assets/FerrySim/World/Generated/LowerEastRiver/import-manifest.json";
		public const string ActionsPath = "Assets/FerrySim/Input/FerrySimControls.inputactions";
		public const string HullMaterialPath = "Assets/FerrySim/Sim/Scenes/BridgeDevHull.mat";
		public const string CabinMaterialPath = "Assets/FerrySim/Sim/Scenes/BridgeDevCabin.mat";
		public const string VesselFile = "vessels/t-class.json";

		/// <summary>South Williamsburg: an ENC barge outline; crew report its primary face's water side.</summary>
		public const string ApproachStopId = "8";

		/// <summary>Bow to face at the start, m.</summary>
		public const double ApproachBowClearance = 40.0;

		/// <summary>Wheelhouse eye tilt below the bow's horizontal, degrees: puts the horizon at about 60% of the screen height.</summary>
		public const float WheelhouseDownTiltDegrees = 8f;

		/// <summary>The scenario's real day: Sunday 20 July 2025 (Slice/README.md, The scene, says why).</summary>
		public const string DefaultPack = "lower-east-river/2025-07-20";

		/// <summary>18:48 EDT on that day: slack water an hour and three quarters after high water, in daylight.</summary>
		public const string ScenarioStartUtc = "2025-07-20T22:48:00Z";

		[MenuItem("FerrySim/Slice/Build Lower East River Slice Scene")]
		public static void Build()
		{
			// Open the world first: a single-mode scene switch unloads assets nothing references yet.
			var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
			var worldIds = SceneFileIds.Ids(File.ReadAllText(ProjectPath(WorldScenePath)));

			var contacts = Load<TextAsset>(LandingGeometry.DefaultContactsAssetPath);
			var surfaces = Load<TextAsset>(LandingSurfaceTable.DefaultAssetPath);
			var terrain = Load<GameObject>(TerrainPrefabPath);
			var manifest = Load<TextAsset>(ImportManifestPath);
			var actions = Load<InputActionAsset>(ActionsPath);
			var hull = Load<Material>(HullMaterialPath);
			var cabin = Load<Material>(CabinMaterialPath);
			var vessel = VesselParametersJson.Load(SimPaths.Resolve(VesselFile));
			var landings = LandingGeometry.Parse(contacts.text, LandingGeometry.DefaultContactsAssetPath,
				LandingSurfaceTable.Parse(surfaces.text, LandingSurfaceTable.DefaultAssetPath));
			var settings = DefaultScenario(landings, vessel);
			var start = settings.InitialState(settings.CreateEnvironment().Observer);
			var clearance = FaceClearance.Check(vessel, start.North, start.East, start.Heading, landings.Geometry.Segments);
			if (!clearance.IsClear(5.0))
			{
				throw new InvalidOperationException($"The slice scenario starts {clearance.Distance:F2} m from landing face {clearance.NearestSegmentId}; it must be at least 5 m clear.");
			}

			var scenario = AssetDatabase.LoadAssetAtPath<ScenarioAsset>(ScenarioPath);
			if (scenario == null)
			{
				scenario = ScriptableObject.CreateInstance<ScenarioAsset>();
				AssetDatabase.CreateAsset(scenario, ScenarioPath);
			}

			scenario.Settings = settings;
			EditorUtility.SetDirty(scenario);
			AssetDatabase.SaveAssetIfDirty(scenario);

			var georeference = Single<CesiumGeoreference>();
			var frame = Single<WorldFrame>();
			var water = Single<WaterSurface>();
			var rig = Single<CameraRig>();
			var sun = Single<SunController>();
			var mount = Single<WheelhouseCameraMount>();

			// The vessel sits under the georeference: its globe anchor holds it in ECEF through origin shifts.
			var vesselObject = new GameObject("Vessel") { layer = WorldLayers.Vessel };
			vesselObject.transform.SetParent(georeference.transform, false);
			vesselObject.AddComponent<CesiumGlobeAnchor>();
			var placement = vesselObject.AddComponent<CesiumVesselPlacement>();
			var runner = vesselObject.AddComponent<VesselSimulationRunner>();
			var helm = vesselObject.AddComponent<HelmInputController>();
			var greybox = vesselObject.AddComponent<VesselGreybox>();

			var sliceObject = new GameObject("Slice");
			var waterHeight = sliceObject.AddComponent<WorldWaterHeight>();
			var bootstrap = sliceObject.AddComponent<SliceBootstrap>();

			var uiObject = new GameObject("Wheelhouse UI");
			var ui = uiObject.AddComponent<WheelhouseUI>();

			Set(waterHeight, "water", water);
			Set(placement, "waterSurfaceSource", waterHeight);
			Set(placement, "useGeoidOffsetFile", false);
			Set(runner, "scenario", scenario);
			Set(runner, "controlSource", helm);
			Set(helm, "actions", actions);
			Set(helm, "runner", runner);
			Set(greybox, "runner", runner);
			Set(greybox, "hullMaterial", hull);
			Set(greybox, "cabinMaterial", cabin);
			Set(ui, "telemetrySource", runner);
			Set(ui, "showPanel", true);
			Set(ui, "showHud", false);
			Set(bootstrap, "runner", runner);
			Set(bootstrap, "landingContacts", contacts);
			Set(bootstrap, "landingContactsPath", LandingGeometry.DefaultContactsAssetPath);
			Set(bootstrap, "landingSurfaces", surfaces);
			Set(bootstrap, "landingSurfacesPath", LandingSurfaceTable.DefaultAssetPath);
			Set(bootstrap, "terrainPrefab", terrain);
			Set(bootstrap, "importManifest", manifest);
			Set(bootstrap, "worldFrame", frame);
			Set(bootstrap, "water", water);
			Set(bootstrap, "cameraRig", rig);
			Set(bootstrap, "wheelhouseUI", ui);
			Set(bootstrap, "sun", sun);
			Set(bootstrap, "helm", helm);
			Set(bootstrap, "vesselFile", settings.vesselFile);
			Set(bootstrap, "environmentPack", settings.environment.pack);

			// A greybox so the scene reads in edit mode; at runtime it is rebuilt from the loaded vessel file.
			var dimensions = GreyboxDimensions.From(vessel);
			VesselGreybox.Build(vesselObject.transform, dimensions, hull, cabin);

			// The world's wheelhouse mount goes aboard at the helm eye point, facing the bow (World/README.md), tilted
			// down a little: the panel covers the bottom 42% of the screen, and a level eye 6.9 m up would lose the
			// water under it out to about 65 m ahead (28 m with the tilt). Head look still turns the view within the
			// mount's limits.
			mount.transform.SetParent(vesselObject.transform, false);
			mount.transform.localPosition = new Vector3(0f, (float)dimensions.BridgeEyeHeight, (float)dimensions.BridgeEyeForward);
			mount.transform.localRotation = Quaternion.Euler(WheelhouseDownTiltDegrees, 0f, 0f);
			rig.ChaseTarget = vesselObject.transform;
			rig.WheelhouseMount = mount;
			rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			EditorUtility.SetDirty(rig);
			EditorUtility.SetDirty(rig.Chase);
			EditorUtility.SetDirty(rig.Wheelhouse);
			EditorUtility.SetDirty(rig.FreeFly);

			// Show the vessel at the start in edit mode; the runner places it from then on.
			placement.WaterSurface = waterHeight;
			placement.Place(vesselObject.transform, VesselPose.FromState(start), 0.0);
			sun.SetUtc(settings.StartUtc.UtcDateTime);

			if (!EditorSceneManager.SaveScene(scene, ScenePath))
			{
				throw new IOException("Could not save " + ScenePath);
			}

			var path = ProjectPath(ScenePath);
			File.WriteAllText(path, SceneFileIds.Canonicalize(File.ReadAllText(path), worldIds), new UTF8Encoding(false));
			AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceUpdate);
			AddToBuildSettings();
			Debug.Log($"[FerrySim] Built {ScenePath}: {settings.name} at {settings.startLatitude:F6}, {settings.startLongitude:F6}, heading {settings.startHeadingDegrees:F1}°, {clearance.Distance:F1} m clear of the faces; landing geometry {landings.Digest} ({landings.Segments.Count} faces, {landings.Excluded.Count} left out).");
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

		/// <summary>
		/// The scene's scenario: the T-class at rest, bow <see cref="ApproachBowClearance"/> m off the middle of South
		/// Williamsburg's primary landing face, heading straight in, on the real day <see cref="DefaultPack"/> from
		/// <see cref="ScenarioStartUtc"/>. The face is the one the pipeline selected, every segment of the stop that
		/// contacts.json flags <c>primary_face</c> (<see cref="LandingGeometry.PrimaryFace"/>); the stop's other usable
		/// segments, such as the barge's SW end, are never chosen in its place, and a primary face left out of contact
		/// stops the build.
		/// </summary>
		public static ScenarioSettings DefaultScenario(LandingGeometry landings, VesselParameters vessel)
		{
			var face = landings.PrimaryFace(ApproachStopId);
			return LandingApproach.BowOn(face, vessel, ApproachBowClearance, "LowerEastRiverSlice: " + face.StopName + " approach", VesselFile, ScenarioStartUtc,
				0.0, new ScenarioEnvironment { pack = DefaultPack });
		}

		/// <summary>
		/// Coordinator-approved: appends this scene, enabled, to the build settings; existing entries and their
		/// order are untouched.
		/// </summary>
		private static void AddToBuildSettings()
		{
			var scenes = EditorBuildSettings.scenes.ToList();
			if (scenes.Any(s => s.path == ScenePath))
			{
				return;
			}

			scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
			EditorBuildSettings.scenes = scenes.ToArray();
		}

		private static string ProjectPath(string assetPath) => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", assetPath);

		private static T Load<T>(string path) where T : Object =>
			AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new FileNotFoundException($"Missing {typeof(T).Name} for the slice scene", path);

		private static T Single<T>() where T : Object
		{
			var found = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
			if (found.Length != 1)
			{
				throw new InvalidOperationException($"{WorldScenePath} should hold exactly one {typeof(T).Name}, found {found.Length}.");
			}

			return found[0];
		}

		private static void Set(Object target, string property, Object value)
		{
			var serialized = new SerializedObject(target);
			Property(serialized, target, property).objectReferenceValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		private static void Set(Object target, string property, bool value)
		{
			var serialized = new SerializedObject(target);
			Property(serialized, target, property).boolValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		private static void Set(Object target, string property, string value)
		{
			var serialized = new SerializedObject(target);
			Property(serialized, target, property).stringValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		private static SerializedProperty Property(SerializedObject serialized, Object target, string property) =>
			serialized.FindProperty(property) ?? throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{property}'.");
	}
}
