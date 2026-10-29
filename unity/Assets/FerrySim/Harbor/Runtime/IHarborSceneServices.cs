using System;
using CesiumForUnity;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.Harbor
{
	/// <summary>
	/// Integration seam for the parallel 0005 tasks. The factory verifies the runtime pack and prepares the resident
	/// services; the scene does not implement a second loader, tide model, catalog, traffic replay, or simulation.
	/// </summary>
	public interface IHarborSceneServices
	{
		IWaterSurface WaterSurface { get; }
		/// <summary>The verified HarborRuntimePack instance. Opaque here until the coordinator joins the reader assembly.</summary>
		object RuntimePack { get; }
		LandingGeometry Landings { get; }
		IScenarioCatalog Catalog { get; }
		IScenarioLauncher Launcher { get; }
		Action<HarborSceneContext> BuildNavigation { get; }
		Action<HarborSceneContext> BuildTraffic { get; }
	}

	public sealed class HarborSceneContext
	{
		public VesselSimulationRunner Runner { get; }
		public CesiumGeoreference Georeference { get; }
		public CameraRig CameraRig { get; }
		public SunController Sun { get; }
		public Transform WaterRoot { get; }
		public Transform NavigationRoot { get; }
		public Transform TrafficRoot { get; }
		public HarborSceneConfiguration Configuration { get; }

		public HarborSceneContext(VesselSimulationRunner runner, CesiumGeoreference georeference, CameraRig cameraRig,
			SunController sun, Transform waterRoot, Transform navigationRoot, Transform trafficRoot, HarborSceneConfiguration configuration)
		{
			Runner = runner;
			Georeference = georeference;
			CameraRig = cameraRig;
			Sun = sun;
			WaterRoot = waterRoot;
			NavigationRoot = navigationRoot;
			TrafficRoot = trafficRoot;
			Configuration = configuration;
		}
	}
}
