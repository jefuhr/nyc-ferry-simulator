using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
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
			var landings = HarborContacts.Load(SimPaths.Resolve(settings.contactsFile), landingSurfaces.text, SurfacesName);
			var audit = HarborLandingAudit.Parse(landingAudit.text, AuditName);
			if (audit.DepthDigest != pack.EnvironmentDepthDigest)
			{
				throw new InvalidOperationException(AuditName + ": assessed on depth set " + audit.DepthDigest + ", but " + Path.Combine(runtimeFolder, "manifest.json") +
					" links depth set " + pack.EnvironmentDepthDigest + ". Rerun the landing matrix on the adopted packs.");
			}

			var runner = context.Runner;
			var packs = runner.EnvironmentPacks;
			var launcher = new HarborLauncher(runner, runtimeFolder, landings, surfaces);
			var catalog = new HarborScenarioCatalog(landings, LoadVessels(), packs, trafficPacks: runner.TrafficPacks, marks: audit.Marks, launcher: launcher,
				depths: new SharedDepthEnvironments(packs));
			var indexFile = Path.Combine(Path.GetDirectoryName(runtimeFolder) ?? runtimeFolder, "index.json");
			_services = new HarborIntegrationServices(context, pack, landings, catalog, launcher, new HarborWaterForwarder(new HarborDatum(pack.Geoid)), waterMaterial, indexFile);
			return _services;
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

		/// <summary>How many harbor runs have bound their water and visual patches.</summary>
		public int RunsBound { get; private set; }

		private void OnRunStarted()
		{
			if (_disposed || _runner.HarborRuntime == null || _runner.RunEnvironment == null)
			{
				return;
			}

			var service = new HarborWaterService(new HarborDatum(_runner.HarborRuntime.Geoid), _runner.RunEnvironment);
			Water.Bind(service);
			RebuildPatches(service);
			RunsBound++;
		}

		private void RebuildPatches(HarborWaterService service)
		{
			ClearPatches();
			foreach (var grid in _runner.RunEnvironment.Pack.Depth.Grids)
			{
				var host = new GameObject("Harbor water " + grid.Id);
				host.layer = WorldLayers.Water;
				host.transform.SetParent(_context.WaterRoot, false);
				var patch = host.AddComponent<HarborWaterPatch>();
				var rows = Cells((grid.Rows - 1) * grid.CellSize);
				var columns = Cells((grid.Columns - 1) * grid.CellSize);
				patch.Configure(service, Index(grid.OriginNorth), Index(grid.OriginEast), rows, columns, _waterMaterial, _runner);
				_patches.Add(patch);
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
			if (_picker != null || Navigation == null || ui == null || ui.View == null || ui.View.Picker == null)
			{
				return;
			}

			_picker = ui.View.Picker;
			_picker.Landings.SelectionChanged += SyncSelection;
			_picker.Started += OnPickerStarted;
			SyncSelection();
		}

		private void OnPickerStarted(ScenarioChoice choice) => SyncSelection();

		private void SyncSelection()
		{
			if (Navigation == null || _picker == null)
			{
				return;
			}

			Navigation.SelectedStopId = _picker.Landings.SelectedIndex >= 0 ? _picker.Choice.StopId ?? string.Empty : string.Empty;
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
			}

			if (_picker != null)
			{
				_picker.Landings.SelectionChanged -= SyncSelection;
				_picker.Started -= OnPickerStarted;
				_picker = null;
			}

			ClearPatches();
		}
	}
}
