using System;
using System.Collections.Generic;
using System.IO;
using CesiumForUnity;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.World.HarborTiles;
using UnityEngine;

namespace FerrySim.Harbor.Tests.PlayMode
{
	internal sealed class HarborTestRig : IDisposable
	{
		public static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		public readonly GameObject Root;
		public readonly VesselSimulationRunner Runner;
		public readonly HarborBootstrap Bootstrap;
		public readonly TestLayer Layer = new TestLayer();
		public readonly TestServices Services;
		private readonly TextAsset _configuration;

		public HarborTestRig(bool connect = true)
		{
			Root = new GameObject("Harbor test");
			Root.SetActive(false);
			var reference = Child("Reference", Root.transform).AddComponent<CesiumGeoreference>();
			reference.Initialize();
			reference.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
			var vessel = Child("Vessel", reference.transform);
			var placement = vessel.AddComponent<CesiumVesselPlacement>();
			Runner = vessel.AddComponent<VesselSimulationRunner>();
			Runner.enabled = false;
			Runner.RecordRuns = false;
			var camera = Child("Camera", reference.transform);
			camera.AddComponent<Camera>();
			camera.AddComponent<CesiumGlobeAnchor>();
			var free = camera.AddComponent<FreeFlyCameraController>();
			var chase = camera.AddComponent<ChaseCameraController>();
			var wheel = camera.AddComponent<WheelhouseCameraController>();
			var rig = camera.AddComponent<CameraRig>();
			rig.Configure(free, chase, wheel);
			rig.ChaseTarget = vessel.transform;
			rig.WheelhouseMount = Child("Mount", vessel.transform).AddComponent<WheelhouseCameraMount>();
			rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			var ui = Child("UI", Root.transform).AddComponent<WheelhouseUI>();
			ui.Configure(Runner, null);
			ui.ConnectScenario(Runner, null, null);
			var light = Child("Sun", Root.transform).AddComponent<Light>();
			light.type = LightType.Directional;
			var sun = light.gameObject.AddComponent<SunController>();
			sun.Configure(light, null);
			_configuration = new TextAsset(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Harbor/Data/harbor-scene.json")));
			Bootstrap = Child("Bootstrap", Root.transform).AddComponent<HarborBootstrap>();
			Bootstrap.Configure(_configuration, Runner, placement, reference, rig, ui, null, sun, null,
				Child("Water", reference.transform).transform, Child("Navigation", reference.transform).transform, Child("Traffic", reference.transform).transform);
			Bootstrap.SetViewLayers(new IHarborViewLayer[] { Layer });
			Services = new TestServices(Runner);
			Root.SetActive(true);
			if (connect && !Bootstrap.Connect(Services, out var problem)) throw new InvalidOperationException(problem);
		}

		public ScenarioSettings Scenario() => new ScenarioSettings
		{
			name = "Harbor scene fixture",
			vesselFile = "vessels/t-class.json",
			startLatitude = 40.7,
			startLongitude = -74.015,
			scenarioStartUtc = "2025-07-20T16:00:00Z",
			environment = new ScenarioEnvironment { calm = true }
		};

		public void Dispose() { UnityEngine.Object.Destroy(Root); UnityEngine.Object.Destroy(_configuration); }
		private static GameObject Child(string name, Transform parent) { var host = new GameObject(name); host.transform.SetParent(parent, false); return host; }
	}

	internal sealed class TestLayer : IHarborViewLayer
	{
		public string LayerId => "fixture terrain";
		public bool Required => true;
		public string Failure { get; set; }
		public bool HasContent { get; set; } = true;
		public float LoadProgress { get; set; }
	}

	internal sealed class TestServices : IHarborSceneServices, IScenarioCatalog, IScenarioLauncher
	{
		private readonly VesselSimulationRunner _runner;
		public TestServices(VesselSimulationRunner runner)
		{
			_runner = runner;
			var surfaces = File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Harbor/Data/landing-contact-surfaces.json"));
			Landings = LandingGeometry.Parse("[]", "fixture contacts", LandingSurfaceTable.Parse(surfaces, "fixture surfaces"));
		}
		public IWaterSurface WaterSurface { get; set; } = new ConstantWaterSurface(-31.8);
		public object RuntimePack { get; set; } = new object();
		public LandingGeometry Landings { get; set; }
		public IScenarioCatalog Catalog => this;
		public IScenarioLauncher Launcher => this;
		public Action<HarborSceneContext> BuildNavigation => NoBuild;
		public Action<HarborSceneContext> BuildTraffic => NoBuild;
		private static void NoBuild(HarborSceneContext context) { }
		IReadOnlyList<ScenarioLanding> IScenarioCatalog.Landings => Array.Empty<ScenarioLanding>();
		public IReadOnlyList<ScenarioDay> Days => Array.Empty<ScenarioDay>();
		public IReadOnlyList<ScenarioVessel> Vessels => Array.Empty<ScenarioVessel>();
		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem) { settings = null; problem = "Test fixture only"; return false; }
		public bool TryStart(ScenarioSettings settings, out string problem) { _runner.Initialize(settings); problem = null; return true; }
	}
}
