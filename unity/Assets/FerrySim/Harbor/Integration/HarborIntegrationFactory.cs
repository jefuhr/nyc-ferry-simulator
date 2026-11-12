using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Controls;
using FerrySim.Core.World;
using FerrySim.HarborWater;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.Traffic;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.World.HarborNavigation;
using UnityEngine;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The harbor scene's composition root (Harbor/README.md, Integration): verifies the runtime pack and prepares the
	/// resident services that <see cref="HarborBootstrap"/> connects. It wires the siblings together and implements no
	/// loader, tide model, catalog, traffic replay or simulation of its own.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class HarborIntegrationFactory : HarborSceneServiceFactory
	{
		public const string SurfacesName = "Assets/FerrySim/Harbor/Data/landing-contact-surfaces.json";
		public const string AuditName = "Assets/FerrySim/Harbor/Data/landing-audit.json";

		/// <summary>The vessels a harbor run can use, in the order the picker shows them.</summary>
		public static readonly string[] VesselFiles = { "vessels/t-class.json", "vessels/k-class.json" };

		[SerializeField] private TextAsset landingSurfaces;
		[SerializeField] private TextAsset landingAudit;
		[SerializeField] private Material waterMaterial;
		[SerializeField] private WheelhouseUI wheelhouseUI;
		private HarborIntegrationServices _services;

		/// <summary>The services this factory created, or null before <see cref="Create"/>.</summary>
		public HarborIntegrationServices Services => _services;

		public void Configure(TextAsset surfaces, TextAsset audit, Material water, WheelhouseUI ui)
		{
			landingSurfaces = surfaces;
			landingAudit = audit;
			waterMaterial = water;
			wheelhouseUI = ui;
		}

		public override IHarborSceneServices Create(HarborSceneContext context)
		{
			if (context == null)
			{
				throw new ArgumentNullException(nameof(context));
			}

			if (_services != null)
			{
				throw new InvalidOperationException("The harbor services were already created.");
			}

			if (landingSurfaces == null || landingAudit == null || waterMaterial == null)
			{
				throw new InvalidOperationException("The harbor composition is missing its landing surfaces, landing audit or water material. Rebuild Harbor.unity.");
			}

			var settings = context.Configuration;
			var environmentFolder = EnvironmentPacks.RegionFolder(HarborScenarioCatalog.Region);
			if (settings.environmentFolder != environmentFolder)
			{
				throw new InvalidOperationException("harbor-scene.json: environmentFolder is " + settings.environmentFolder + ", but the harbor catalog reads " + environmentFolder + ".");
			}

			var runtimeFolder = SimPaths.Resolve(settings.runtimePackFolder);
			var pack = HarborRuntimePackJson.Load(runtimeFolder);
			var surfaces = LandingSurfaceTable.Parse(landingSurfaces.text, SurfacesName);
			var contactsFile = SimPaths.Resolve(settings.contactsFile);
			if (File.ReadAllText(contactsFile) != pack.ContactsJson)
			{
				throw new InvalidOperationException(contactsFile + ": does not match the verified runtime contacts snapshot.");
			}
			var landings = LandingGeometry.Parse(pack.ContactsJson, contactsFile, surfaces);
			var audit = HarborLandingAudit.Parse(landingAudit.text, AuditName);
			if (audit.DepthDigest != pack.EnvironmentDepthDigest)
			{
				throw new InvalidOperationException(AuditName + ": assessed on depth set " + audit.DepthDigest + ", but " + Path.Combine(runtimeFolder, "manifest.json") +
					" links depth set " + pack.EnvironmentDepthDigest + ". Rerun the landing matrix on the adopted packs.");
			}

			var runner = context.Runner;
			var packs = runner.EnvironmentPacks;
			runner.EnvironmentPacks = packs;
			packs.BindHarborRuntime(runtimeFolder, pack, landings.Geometry);
			var launcher = new HarborLauncher(runner, runtimeFolder, landings, surfaces);
			var catalog = new HarborScenarioCatalog(landings, LoadVessels(), packs, trafficPacks: runner.TrafficPacks, marks: audit.Marks, launcher: launcher,
				depths: new SharedDepthEnvironments(packs), runtimeStops: pack.Stops);
			var harborFolder = Path.GetDirectoryName(runtimeFolder) ?? runtimeFolder;
			var indexFile = Path.Combine(harborFolder, "index.json");
			_services = new HarborIntegrationServices(context, pack, landings, catalog, launcher, new HarborWaterForwarder(new HarborDatum(pack.Geoid)), waterMaterial, indexFile);
			// The chart pack loads once per process on a worker thread from here on, so no Start waits on it; the displays
			// connect themselves when it is ready and show CHART LOADING until then (HarborDisplays).
			var chart = HarborChartLoad.Start(Path.Combine(harborFolder, ChartFolderName), pack);
			_services.Displays = CreateDisplays(context, chart);
			return _services;
		}

		/// <summary>The chart pack beside the runtime pack: data/processed/harbor/chart.</summary>
		public const string ChartFolderName = "chart";

		private HarborDisplays CreateDisplays(HarborSceneContext context, HarborChartLoad chart)
		{
			var host = new GameObject("Harbor displays");
			host.transform.SetParent(transform, false);
			var displays = host.AddComponent<HarborDisplays>();
			displays.Configure(context.Runner, chart, wheelhouseUI, context.CameraRig, context.Sun, context.Runner.GetComponent<HelmInputController>());
			return displays;
		}

		private static IReadOnlyList<KeyValuePair<string, VesselParameters>> LoadVessels()
		{
			var vessels = new List<KeyValuePair<string, VesselParameters>>();
			foreach (var file in VesselFiles)
			{
				vessels.Add(new KeyValuePair<string, VesselParameters>(file, VesselParametersJson.Load(SimPaths.Resolve(file))));
			}

			return vessels;
		}

		private void LateUpdate() => _services?.FollowPicker(wheelhouseUI);

		private void OnDestroy()
		{
			_services?.Dispose();
			_services = null;
		}
	}

	/// <summary>
	/// The connected harbor services. Water: one forwarding surface that takes each harbor run's
	/// <see cref="HarborWaterService"/> on the runner's <c>RunStarted</c>, subscribed here before the navigation and
	/// traffic builders subscribe theirs, so it is bound before they react and before the vessel is first placed.
	/// Visual water: one <see cref="HarborWaterPatch"/> per depth grid under the water root, rebuilt for each run.
	/// </summary>
	public sealed class HarborIntegrationServices : IHarborSceneServices, IDisposable
	{
		private readonly HarborSceneContext _context;
		private readonly VesselSimulationRunner _runner;
		private readonly Material _waterMaterial;
		private readonly string _indexFile;
		private readonly List<HarborWaterPatch> _patches = new List<HarborWaterPatch>();
		private ScenarioPicker _picker;
		private WheelhouseView _view;
		private string _activeStopId = string.Empty;
		private bool _disposed;

		public HarborIntegrationServices(HarborSceneContext context, HarborRuntimePack pack, LandingGeometry landings, HarborScenarioCatalog catalog,
			HarborLauncher launcher, HarborWaterForwarder water, Material waterMaterial, string indexFile)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));
			_runner = context.Runner;
			Pack = pack ?? throw new ArgumentNullException(nameof(pack));
			Landings = landings ?? throw new ArgumentNullException(nameof(landings));
			HarborCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
			HarborLauncher = launcher ?? throw new ArgumentNullException(nameof(launcher));
			Water = water ?? throw new ArgumentNullException(nameof(water));
			_waterMaterial = waterMaterial;
			_indexFile = indexFile;
			// Before the navigation and traffic builders run: their run callbacks must see the new water first.
			_runner.RunStarted += OnRunStarted;
			_runner.RunEnded += SyncSelection;
			BuildNavigation = BuildNavigationWorld;
			BuildTraffic = BuildTrafficView;
		}

		public IWaterSurface WaterSurface => Water;

		public HarborWaterForwarder Water { get; }

		public object RuntimePack => Pack;

		public HarborRuntimePack Pack { get; }

		public LandingGeometry Landings { get; }

		public IScenarioCatalog Catalog => HarborCatalog;

		public HarborScenarioCatalog HarborCatalog { get; }

		public IScenarioLauncher Launcher => HarborLauncher;

		public HarborLauncher HarborLauncher { get; }

		public Action<HarborSceneContext> BuildNavigation { get; }

		public Action<HarborSceneContext> BuildTraffic { get; }

		/// <summary>The resident navigation world, after <see cref="BuildNavigation"/>.</summary>
		public HarborNavigationWorld Navigation { get; private set; }

		/// <summary>The harbor traffic renderer, after <see cref="BuildTraffic"/>.</summary>
		public TrafficView Traffic { get; private set; }

		/// <summary>The visual water of the current run, one patch per depth grid.</summary>
		public IReadOnlyList<HarborWaterPatch> Patches => _patches;

		/// <summary>The wheelhouse chartplotter and radar with their shared chart load, created by the factory.</summary>
		public HarborDisplays Displays { get; internal set; }

		/// <summary>How many harbor runs have bound their water and visual patches.</summary>
		public int RunsBound { get; private set; }

		private void OnRunStarted()
		{
			if (_disposed || _runner.RunEnvironment?.Pack == null)
			{
				return;
			}

			_activeStopId = HarborCatalog.StartStopId(_runner.Scenario);
			HarborCatalog.ReleaseDepthPreview();
			SyncSelection();
			var service = new HarborWaterService(new HarborDatum((_runner.HarborRuntime ?? Pack).Geoid), _runner.RunEnvironment);
			Water.Bind(service);
			RebuildPatches(service);
			// A hidden radar does not Update. Rebind its run now so it cannot retain retired traffic packs.
			if (Displays != null && Displays.Connected) Displays.Radar.Refresh();
			RunsBound++;
		}

		private void RebuildPatches(HarborWaterService service)
		{
			var grids = _runner.RunEnvironment.Pack.Depth.Grids;
			if (_patches.Count != grids.Count) ClearPatches();
			var index = 0;
			foreach (var grid in _runner.RunEnvironment.Pack.Depth.Grids)
			{
				HarborWaterPatch patch;
				if (index < _patches.Count)
				{
					patch = _patches[index];
				}
				else
				{
					var host = new GameObject("Harbor water " + grid.Id);
					host.layer = WorldLayers.Water;
					host.transform.SetParent(_context.WaterRoot, false);
					patch = host.AddComponent<HarborWaterPatch>();
					_patches.Add(patch);
				}
				patch.name = "Harbor water " + grid.Id;
				var rows = Cells((grid.Rows - 1) * grid.CellSize);
				var columns = Cells((grid.Columns - 1) * grid.CellSize);
				patch.Configure(service, Index(grid.OriginNorth), Index(grid.OriginEast), rows, columns, _waterMaterial, _runner);
				index++;
			}
		}

		private static int Index(double metres) => (int)Math.Round(metres / HarborWaterPatch.Spacing);

		private static int Cells(double metres) => Math.Max(1, Math.Min(40, (int)Math.Round(metres / HarborWaterPatch.Spacing)));

		private void ClearPatches()
		{
			foreach (var patch in _patches)
			{
				if (patch != null)
				{
					UnityEngine.Object.Destroy(patch.gameObject);
				}
			}

			_patches.Clear();
		}

		private void BuildNavigationWorld(HarborSceneContext context)
		{
			var atmosphere = context.Sun != null ? context.Sun.GetComponent<Atmosphere>() : null;
			if (atmosphere == null)
			{
				throw new InvalidOperationException("The harbor sun has no Atmosphere component. Rebuild Harbor.unity.");
			}

			var host = new GameObject("Harbor navigation");
			host.transform.SetParent(context.NavigationRoot, false);
			Navigation = host.AddComponent<HarborNavigationWorld>();
			Navigation.Build(Pack, context.Georeference, Water, atmosphere);
			Navigation.Follow(_runner, context.CameraRig.GetComponent<Camera>());
		}

		private void BuildTrafficView(HarborSceneContext context)
		{
			var area = HarborTrafficCoverage.Load(_indexFile);
			var host = new GameObject("Harbor traffic");
			host.transform.SetParent(context.TrafficRoot, false);
			Traffic = host.AddComponent<TrafficView>();
			Traffic.ConnectHarbor(_runner, context.Georeference, Water, area, context.CameraRig.GetComponent<Camera>(), context.Sun);
		}

		/// <summary>Binds the picker's landing selection to the navigation labels once the wheelhouse view exists.</summary>
		public void FollowPicker(WheelhouseUI ui)
		{
			var view = ui != null ? ui.View : null;
			if (ReferenceEquals(_view, view)) return;
			DetachPicker();
			if (Navigation == null || view?.Picker == null)
			{
				SyncSelection();
				return;
			}
			_view = view;
			_picker = view.Picker;
			_picker.Landings.SelectionChanged += SyncSelection;
			_picker.Started += OnPickerStarted;
			_view.PickerVisibilityChanged += OnPickerVisibilityChanged;
			SyncSelection();
		}

		private void OnPickerStarted(ScenarioChoice choice)
		{
			_activeStopId = choice.StopId ?? string.Empty;
			SyncSelection();
		}

		private void OnPickerVisibilityChanged(bool visible) => SyncSelection();

		private void SyncSelection()
		{
			if (Navigation == null) return;
			if (_view != null && _view.PickerVisible)
			{
				Navigation.SelectedStopId = _picker.Landings.SelectedIndex >= 0 ? _picker.Choice.StopId ?? string.Empty : string.Empty;
			}
			else
			{
				Navigation.SelectedStopId = _runner.IsRunActive ? _activeStopId : string.Empty;
			}
		}

		private void DetachPicker()
		{
			if (_picker != null)
			{
				_picker.Landings.SelectionChanged -= SyncSelection;
				_picker.Started -= OnPickerStarted;
			}
			if (_view != null) _view.PickerVisibilityChanged -= OnPickerVisibilityChanged;
			_picker = null;
			_view = null;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			if (_runner != null)
			{
				_runner.RunStarted -= OnRunStarted;
				_runner.RunEnded -= SyncSelection;
			}

			DetachPicker();

			ClearPatches();
			if (Displays != null)
			{
				UnityEngine.Object.Destroy(Displays.gameObject);
				Displays = null;
			}
		}
	}
}
