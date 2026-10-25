using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
	/// the water's datum (<see cref="ImportedTerrain"/>), keeps the drawn water at the run's tide (<see cref="TideFollower"/>),
	/// lights the world for each run's scenario start, puts the wheelhouse eye aboard each run's vessel, checks every
	/// run starts clear of the faces, switches the wheelhouse panel and the HUD with the camera view, and lets the helm's
	/// camera button swap the wheelhouse and chase views. It is the slice's hook point for a scenario picker: it is the
	/// catalog and the launcher (<see cref="IScenarioCatalog"/> and <see cref="IScenarioLauncher"/>, through
	/// <see cref="SliceScenarios"/>), and <see cref="EnvironmentSource"/> is the runner's environment readout. It fails
	/// closed: if the landing files are missing or malformed the runner is stopped with an error naming the file and the
	/// reason, and any run that starts anyway, or starts less than <see cref="FaceClearance.MinimumMetres"/> from a face, is
	/// ended before its first physics step with the reason kept in <see cref="StartRefusal"/>; the vessel never runs through
	/// the landings by accident. A player reads the scenario's vessel file and environment pack from StreamingAssets, where
	/// every player build copies them (FerrySim.Build.PlayerPayload); a player without them stops with an error naming what
	/// is missing.
	/// </summary>
	[DefaultExecutionOrder(-500)]
	[DisallowMultipleComponent]
	public sealed class SliceBootstrap : MonoBehaviour, IScenarioCatalog, IScenarioLauncher
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

		[Tooltip("The vessel file the runner's scenario loads. Player builds copy it into StreamingAssets; a player without it stops with an error (docs/releasing.md).")]
		[SerializeField]
		private string vesselFile = string.Empty;

		[Tooltip("The environment pack (region/date) the runner's scenario runs on, empty for calm or uniform water. A player without it stops with an error.")]
		[SerializeField]
		private string environmentPack = string.Empty;

		[Tooltip("Vessel files the scenario picker offers, in its order, relative to the repository root. Player builds copy them into StreamingAssets.")]
		[SerializeField]
		private string[] vesselFiles = { "vessels/t-class.json", "vessels/k-class.json" };

		[Tooltip("The region's environment packs the scenario picker offers, data/processed/<region>/env. Player builds copy every file in it into StreamingAssets.")]
		[SerializeField]
		private string environmentFolder = EnvironmentPacks.RegionFolder("lower-east-river");

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
		private TideFollower _tide;
		private SliceScenarios _scenarios;
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

		/// <summary>Why the current run was ended at its start, before its first physics step, or null.</summary>
		public string StartRefusal { get; private set; }

		/// <summary>The vessel file a player must have, the one the runner's scenario loads.</summary>
		public string VesselFile => vesselFile;

		/// <summary>The environment pack a player must have, the one the runner's scenario runs on; empty for calm or uniform water.</summary>
		public string EnvironmentPack => environmentPack;

		/// <summary>The scenario catalog and launcher behind this component's <see cref="IScenarioCatalog"/> and <see cref="IScenarioLauncher"/>.</summary>
		public SliceScenarios Scenarios => _scenarios ??= new SliceScenarios(this, vesselFiles, environmentFolder);

		/// <summary>What a scenario picker offers: the slice's landings, the pack days and the vessels. This component.</summary>
		public IScenarioCatalog ScenarioCatalog => this;

		/// <summary>Starts a picked scenario on the slice's runner. This component.</summary>
		public IScenarioLauncher ScenarioLauncher => this;

		/// <summary>Vessel files the scenario picker offers, in order.</summary>
		public IReadOnlyList<string> VesselFiles => vesselFiles;

		/// <summary>The region's pack folder the scenario picker offers days from, <c>data/processed/&lt;region&gt;/env</c>.</summary>
		public string EnvironmentFolder => environmentFolder;

		IReadOnlyList<ScenarioLanding> IScenarioCatalog.Landings => Scenarios.Landings;

		IReadOnlyList<ScenarioDay> IScenarioCatalog.Days => Scenarios.Days;

		IReadOnlyList<ScenarioVessel> IScenarioCatalog.Vessels => Scenarios.Vessels;

		bool IScenarioCatalog.TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem) => Scenarios.TryCreate(choice, out settings, out problem);

		bool IScenarioLauncher.TryStart(ScenarioSettings settings, out string problem) => Scenarios.TryStart(settings, out problem);

		/// <summary>The water and air at the vessel for the UI: the runner's readout.</summary>
		public IEnvironmentReadoutSource EnvironmentSource => runner;

		/// <summary>The drawn water's tide driver, or null when the scene has no water surface.</summary>
		public TideFollower Tide => _tide;

		/// <summary>Why this player cannot run the slice (its vessel file is not packaged), or null.</summary>
		public string PlayerError { get; private set; }

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
			CheckPlayer();
			if (water != null)
			{
				_tide = new TideFollower(runner, water);
			}
		}

		/// <summary>
		/// Why a player cannot run the slice, or null. A player reads the scenario's vessel file from StreamingAssets,
		/// where every player build copies it (FerrySim.Build.PlayerPayload, docs/releasing.md); without it the runner
		/// would have only its own file-not-found exception to show. The editor resolves the file from the repository and
		/// reports a missing one itself.
		/// </summary>
		public static string PlayerProblem(bool isEditor, string vesselFile)
		{
			if (isEditor)
			{
				return null;
			}

			var path = SimPaths.Resolve(vesselFile);
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				return null;
			}

			return $"This player is missing the vessel file '{vesselFile}' (looked at {path}). Player builds copy it into StreamingAssets; " +
				"rebuild the player with scripts/release.sh or the editor's Build menu (docs/releasing.md).";
		}

		/// <summary>
		/// As <see cref="PlayerProblem(bool, string)"/>, and also why the scenario's environment pack is missing from a player's
		/// <paramref name="packs"/> (StreamingAssets, then the repository, when null). An empty pack name is calm or uniform water.
		/// </summary>
		public static string PlayerProblem(bool isEditor, string vesselFile, string environmentPack, EnvironmentPacks packs = null)
		{
			var problem = PlayerProblem(isEditor, vesselFile);
			if (problem != null || isEditor || string.IsNullOrEmpty(environmentPack))
			{
				return problem;
			}

			packs ??= EnvironmentPacks.Default;
			if (EnvironmentPacks.TryParseName(environmentPack, out _, out _, out var nameProblem) && packs.DayFolder(environmentPack) != null)
			{
				return null;
			}

			return $"This player cannot run its scenario: {nameProblem ?? packs.NotFound(environmentPack)}. Player builds copy the environment packs into StreamingAssets; " +
				"rebuild the player with scripts/release.sh or the editor's Build menu (docs/releasing.md).";
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
			_tide?.Dispose();
			_tide = null;
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
			var file = landingSurfacesPath;
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
				file = landingContactsPath;
				Landings = LandingGeometry.Parse(landingContacts.text, landingContactsPath, surfaces);
			}
			catch (LandingDataException exception)
			{
				FailLandings(exception.Message);
				return;
			}
			catch (Exception exception)
			{
				// A failure the loaders do not name is a bug in them, not in the data: still fail closed, and keep the
				// exception for whoever fixes it.
				Debug.LogException(exception, this);
				FailLandings($"{file}: could not be read ({exception.GetType().Name}: {exception.Message})");
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

		private void FailLandings(string error)
		{
			Landings = null;
			LoadError = error;
			Debug.LogError($"[FerrySim] The slice's landing faces did not load, so the vessel does not run (it would pass through every landing): {LoadError}", this);
			runner.enabled = false;
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

		private void CheckPlayer()
		{
			PlayerError = PlayerProblem(Application.isEditor, vesselFile, environmentPack, runner.EnvironmentPacks);
			if (PlayerError != null)
			{
				Debug.LogError($"[FerrySim] {PlayerError} The vessel does not run.", this);
				runner.enabled = false;
			}
		}

		private void OnRunStarted()
		{
			StartRefusal = null;
			if (followScenarioTime && sun != null && runner.ScenarioStartUtc != default)
			{
				sun.SetUtc(runner.ScenarioStartUtc.UtcDateTime);
			}

			PlaceWheelhouseEye();

			if (runner.IsReplaying)
			{
				return;
			}

			if (Landings == null)
			{
				// Fail closed: code that starts the runner after a failed load gets no run through the landings.
				if (LoadError != null)
				{
					EndAtStart($"the slice's landing faces did not load ({LoadError})");
				}

				return;
			}

			if (runner.Parameters?.Contact == null)
			{
				return;
			}

			var start = runner.State;
			StartClearance = FaceClearance.Check(runner.Parameters, start.North, start.East, start.Heading, Landings.Geometry.Segments);
			if (!StartClearance.IsClear())
			{
				var invariant = CultureInfo.InvariantCulture;
				var face = Landings.Find(StartClearance.NearestSegmentId);
				var where = StartClearance.Overlaps ? "overlapping" : StartClearance.Distance.ToString("F2", invariant) + " m from";
				EndAtStart(
					$"scenario '{runner.Scenario?.name}' starts {where} landing face {StartClearance.NearestSegmentId} (stop {face?.StopId} {face?.StopName}); " +
					$"it needs {FaceClearance.MinimumMetres.ToString("F1", invariant)} m. The core only takes contact from a face the hull crosses from its water side, so that face would not push this hull.");
			}
		}

		/// <summary>
		/// Puts the wheelhouse eye at the helm of the run's vessel, as the scene builder put it for the T-class: the eye height
		/// and forward position of <see cref="GreyboxDimensions"/> from the vessel file, tilted as built. Only a mount aboard the
		/// vessel moves.
		/// </summary>
		private void PlaceWheelhouseEye()
		{
			var mount = cameraRig != null ? cameraRig.WheelhouseMount : null;
			if (mount == null || runner.Parameters == null || mount.transform.parent != runner.transform)
			{
				return;
			}

			var eye = GreyboxDimensions.From(runner.Parameters);
			mount.transform.localPosition = new Vector3(0f, (float)eye.BridgeEyeHeight, (float)eye.BridgeEyeForward);
		}

		/// <summary>
		/// Ends the run that is starting, before its first physics step: called from <see cref="VesselSimulationRunner.RunStarted"/>,
		/// which every live start raises (Initialize and ResetRun) before the runner's next frame can step.
		/// </summary>
		private void EndAtStart(string reason)
		{
			StartRefusal = reason;
			Debug.LogError($"[FerrySim] Run {runner.RunId} stopped before its first step: {reason}", this);
			runner.EndRun();
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
