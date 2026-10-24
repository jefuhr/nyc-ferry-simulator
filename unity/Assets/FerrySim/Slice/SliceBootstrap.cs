using System.Collections.Generic;
using System.Diagnostics;
using FerrySim.Controls;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FerrySim.Slice
{
	/// <summary>
	/// Joins the Lower East River slice into one drivable scene. Before the runner's first run it builds the landing
	/// contact geometry from the world importer's contacts.json and the slice's fender parameters
	/// (<see cref="LandingGeometry"/>) and hands it to the runner; it puts the imported terrain under the world frame on
	/// the water's datum (<see cref="ImportedTerrain"/>), lights the world for each run's scenario start, checks every
	/// run starts clear of the faces, switches the wheelhouse panel and the HUD with the camera view, and lets the helm's
	/// camera button swap the wheelhouse and chase views. If the landing files are missing or malformed the runner is
	/// stopped with an error naming the file and the reason: the vessel never runs through the landings by accident.
	/// </summary>
	[DefaultExecutionOrder(-500)]
	[DisallowMultipleComponent]
	public sealed class SliceBootstrap : MonoBehaviour
	{
		[SerializeField]
		private VesselSimulationRunner runner;

		[Tooltip("The world importer's contacts.json (World/Generated/LowerEastRiver).")]
		[SerializeField]
		private TextAsset landingContacts;

		[Tooltip("Names the contacts file in messages.")]
		[SerializeField]
		private string landingContactsPath = LandingGeometry.DefaultContactsAssetPath;

		[Tooltip("The slice's landing fender parameters (Slice/Data/landing-contact-surfaces.json).")]
		[SerializeField]
		private TextAsset landingSurfaces;

		[Tooltip("Names the fender parameter file in messages.")]
		[SerializeField]
		private string landingSurfacesPath = LandingSurfaceTable.DefaultAssetPath;

		[Tooltip("The world importer's prefab (terrain, chart structures, aids); placed under the world frame at startup.")]
		[SerializeField]
		private GameObject terrainPrefab;

		[Tooltip("The importer's import-manifest.json: states how the prefab maps heights.")]
		[SerializeField]
		private TextAsset importManifest;

		[Tooltip("Move the imported terrain onto the drawn water's NAVD88 frame (geoid grid and Earth curvature).")]
		[SerializeField]
		private bool matchTerrainToWater = true;

		[SerializeField]
		private WorldFrame worldFrame;

		[SerializeField]
		private WaterSurface water;

		[SerializeField]
		private CameraRig cameraRig;

		[SerializeField]
		private WheelhouseUI wheelhouseUI;

		[SerializeField]
		private SunController sun;

		[SerializeField]
		private HelmInputController helm;

		[Tooltip("Light the world for each run's scenario start time.")]
		[SerializeField]
		private bool followScenarioTime = true;

		[Tooltip("Show the panel in the wheelhouse view and the HUD in the others.")]
		[SerializeField]
		private bool switchPanelWithCamera = true;

		private readonly List<Object> _owned = new List<Object>();
		private HelmInputMapper _mapper;
		private WheelhouseView _switchedView;
		private CameraRig.Mode _switchedMode;

		public VesselSimulationRunner Runner => runner;

		public CameraRig CameraRig => cameraRig;

		public WheelhouseUI WheelhouseUI => wheelhouseUI;

		public WorldFrame WorldFrame => worldFrame;

		/// <summary>The landing faces handed to the runner, or null when they failed to load.</summary>
		public LandingGeometry Landings { get; private set; }

		/// <summary>Why the landing faces did not load, or null.</summary>
		public string LoadError { get; private set; }

		/// <summary>The world importer's prefab the terrain is instantiated from.</summary>
		public GameObject TerrainPrefab => terrainPrefab;

		/// <summary>The instantiated terrain, or null without a prefab.</summary>
		public GameObject Terrain { get; private set; }

		/// <summary>How the terrain was moved onto the water's datum, or null when it was placed as imported.</summary>
		public TerrainCorrection TerrainCorrection { get; private set; }

		/// <summary>Clearance of the current run's start from the faces, checked at every live run start.</summary>
		public FaceClearanceResult StartClearance { get; private set; }

		public bool SwitchPanelWithCamera
		{
			get => switchPanelWithCamera;
			set
			{
				switchPanelWithCamera = value;
				_switchedView = null;
			}
		}

		/// <summary>Sets the runner and the landing files before the component wakes, for tests and bootstrap code.</summary>
		public void Configure(VesselSimulationRunner simulationRunner, TextAsset contacts, string contactsPath, TextAsset surfaces, string surfacesPath)
		{
			runner = simulationRunner;
			landingContacts = contacts;
			landingContactsPath = contactsPath;
			landingSurfaces = surfaces;
			landingSurfacesPath = surfacesPath;
		}

		private void Awake()
		{
			if (runner == null)
			{
				Debug.LogError("[FerrySim] SliceBootstrap has no VesselSimulationRunner.", this);
				return;
			}

			LoadLandings();
			PlaceTerrain();
		}

		private void OnEnable()
		{
			if (runner != null)
			{
				runner.RunStarted += OnRunStarted;
			}
		}

		private void OnDisable()
		{
			if (runner != null)
			{
				runner.RunStarted -= OnRunStarted;
			}

			Subscribe(null);
		}

		private void OnDestroy()
		{
			foreach (var owned in _owned)
			{
				if (owned != null)
				{
					Destroy(owned);
				}
			}

			_owned.Clear();
		}

		private void Update()
		{
			// The helm builds its mapper when it is enabled; follow whichever one is current.
			Subscribe(helm != null && helm.isActiveAndEnabled ? helm.Mapper : null);
		}

		private void LateUpdate()
		{
			var view = wheelhouseUI != null ? wheelhouseUI.View : null;
			if (!switchPanelWithCamera || cameraRig == null || view == null)
			{
				return;
			}

			var mode = cameraRig.CurrentMode;
			if (ReferenceEquals(view, _switchedView) && mode == _switchedMode)
			{
				return;
			}

			_switchedView = view;
			_switchedMode = mode;
			view.PanelVisible = mode == CameraRig.Mode.Wheelhouse;
			view.HudVisible = mode != CameraRig.Mode.Wheelhouse;
		}

		private void LoadLandings()
		{
			try
			{
				if (landingContacts == null)
				{
					throw new LandingDataException(landingContactsPath, new[] { "missing from the scene; run the world import and rebuild the slice scene" });
				}

				if (landingSurfaces == null)
				{
					throw new LandingDataException(landingSurfacesPath, new[] { "missing from the scene; rebuild the slice scene" });
				}

				var surfaces = LandingSurfaceTable.Parse(landingSurfaces.text, landingSurfacesPath);
				Landings = LandingGeometry.Parse(landingContacts.text, landingContactsPath, surfaces);
			}
			catch (LandingDataException exception)
			{
				LoadError = exception.Message;
				Debug.LogError($"[FerrySim] The slice's landing faces did not load, so the vessel does not run (it would pass through every landing): {LoadError}", this);
				runner.enabled = false;
				return;
			}

			runner.WorldGeometry = Landings.Geometry;
			foreach (var excluded in Landings.Excluded)
			{
				Debug.LogWarning("[FerrySim] Landing face left out of contact: " + excluded, this);
			}

			foreach (var crewOnly in Landings.CrewReportedOnly)
			{
				Debug.Log($"[FerrySim] Landing face {crewOnly.Id} (stop {crewOnly.StopId} {crewOnly.StopName}) is used on the crew's report of its water side; the pipeline's chart check did not confirm it.", this);
			}

			Debug.Log($"[FerrySim] Landing contact: {Landings.Segments.Count} faces from {landingContactsPath} ({Landings.Excluded.Count} left out), fenders from {landingSurfacesPath}, geometry {Landings.Digest}.", this);
		}

		private void PlaceTerrain()
		{
			if (terrainPrefab == null || worldFrame == null)
			{
				Debug.LogWarning("[FerrySim] SliceBootstrap has no terrain prefab or world frame; the slice shows water and landings only.", this);
				return;
			}

			Terrain = Instantiate(terrainPrefab, worldFrame.transform, false);
			Terrain.name = terrainPrefab.name + " (imported terrain)";
			if (!matchTerrainToWater)
			{
				return;
			}

			var zero = ImportedTerrain.FlatNavd88ZeroHeight(importManifest != null ? importManifest.text : null, out var problem);
			if (double.IsNaN(zero) || water == null || water.Datum == null || !water.Datum.IsPopulated)
			{
				Debug.LogWarning($"[FerrySim] The imported terrain is placed as imported, not on the water's datum: {problem ?? "the scene has no populated water datum"}.", this);
				return;
			}

			var watch = Stopwatch.StartNew();
			TerrainCorrection = ImportedTerrain.Apply(Terrain, water.Datum, zero, _owned);
			Debug.Log($"[FerrySim] Imported terrain: {TerrainCorrection} in {watch.Elapsed.TotalMilliseconds:F0} ms.", this);
		}

		private void OnRunStarted()
		{
			if (followScenarioTime && sun != null && runner.ScenarioStartUtc != default)
			{
				sun.SetUtc(runner.ScenarioStartUtc.UtcDateTime);
			}

			if (runner.IsReplaying || Landings == null || runner.Parameters?.Contact == null)
			{
				return;
			}

			var start = runner.State;
			StartClearance = FaceClearance.Check(runner.Parameters, start.North, start.East, start.Heading, Landings.Geometry.Segments);
			if (!StartClearance.IsClear())
			{
				var face = Landings.Find(StartClearance.NearestSegmentId);
				Debug.LogError(
					$"[FerrySim] Scenario '{runner.Scenario?.name}' starts {(StartClearance.Overlaps ? "overlapping" : $"{StartClearance.Distance:F2} m from")} landing face {StartClearance.NearestSegmentId} " +
					$"(stop {face?.StopId} {face?.StopName}); it needs {FaceClearance.MinimumMetres:F1} m. The core only takes contact from a face the hull crosses from its water side, so that face would not push this hull.",
					this);
			}
		}

		private void Subscribe(HelmInputMapper mapper)
		{
			if (ReferenceEquals(mapper, _mapper))
			{
				return;
			}

			if (_mapper != null)
			{
				_mapper.CameraCycleRequested -= CycleCamera;
			}

			_mapper = mapper;
			if (_mapper != null)
			{
				_mapper.CameraCycleRequested += CycleCamera;
			}
		}

		private void CycleCamera()
		{
			if (cameraRig != null)
			{
				cameraRig.CurrentMode = cameraRig.CurrentMode == CameraRig.Mode.Wheelhouse ? CameraRig.Mode.Chase : CameraRig.Mode.Wheelhouse;
			}
		}
	}
}
