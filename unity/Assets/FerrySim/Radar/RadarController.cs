using System;
using FerrySim.Core.Radar;
using FerrySim.Core.Traffic;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using FerrySim.Sim;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Radar
{
	/// <summary>Read-only adapter. Packs are supplied once by the composition root; no scene search or pack loading.</summary>
	public sealed class RadarController : MonoBehaviour
	{
		private IVesselTelemetrySource _source;
		private IEnvironmentReadoutSource _environment;
		private VesselSimulationRunner _runner;
		private TrafficRun _traffic;
		private RadarTarget[] _targets = Array.Empty<RadarTarget>();
		private long _runId = -1;
		private bool _available;
		private UIDocument _document;
		private PanelSettings _settings;
		public RadarEngine Engine { get; private set; }
		public RadarView View { get; private set; }
		public int PresentTargets { get; private set; }

		public void Connect(VesselSimulationRunner runner, HarborChartPack chart, HarborRuntimePack runtime, VisualElement parent = null)
		{
			Connect(runner, RadarScene.FromPacks(chart, runtime), parent);
		}
		public void Connect(IVesselTelemetrySource source, HarborChartPack chart, HarborRuntimePack runtime,
			TrafficRun traffic, IEnvironmentReadoutSource environment, VisualElement parent = null)
		{
			Connect(source, RadarScene.FromPacks(chart, runtime), traffic, environment, parent);
		}
		/// <summary>The composition root's seam: a semantic index built ahead (off the main thread, with the chart load); the view and texture are created here, on the main thread.</summary>
		public void Connect(VesselSimulationRunner runner, RadarScene scene, VisualElement parent = null, bool compact = false)
		{
			if (runner == null) throw new ArgumentNullException(nameof(runner));
			Connect(runner, scene, runner.Traffic, runner, parent, compact); _runner = runner;
		}
		public void Connect(IVesselTelemetrySource source, RadarScene scene, TrafficRun traffic, IEnvironmentReadoutSource environment, VisualElement parent = null, bool compact = false)
		{
			if (scene == null) throw new ArgumentNullException(nameof(scene));
			ReleaseView(false); _runner = null; _source = source ?? throw new ArgumentNullException(nameof(source)); _environment = environment;
			Engine = new RadarEngine(scene); View = new RadarView(Engine, RadarRaster.DefaultSize, compact);
			if (parent == null)
			{
				if (_document == null) { _settings = WheelhousePanelSettings.Create(); _document = gameObject.AddComponent<UIDocument>(); _document.panelSettings = _settings; }
				parent = _document.rootVisualElement;
			}
			parent.Add(View); BindTraffic(traffic); _runId = -1; _available = false; View.SetAvailable(false);
		}
		/// <summary>The traffic the radar samples now: the runner's current run after a new start or a replay.</summary>
		public TrafficRun Traffic => _traffic;
		private void Update() => Refresh();
		public void Refresh()
		{
			if (_source == null) return;
			if (_runId != _source.RunId)
			{
				_runId = _source.RunId; Engine.Reset(); View.Raster.RefreshAll(); if (_runner != null) BindTraffic(_runner.Traffic);
			}
			if (!_source.HasTelemetry)
			{
				if (_available) { Engine.Reset(); View.Raster.RefreshAll(); View.SetAvailable(false); _available = false; } return;
			}
			if (!_available) { View.SetAvailable(true); _available = true; }
			var state = _source.State; var pose = _runner != null ? _runner.RenderPose : VesselPose.FromState(state);
			var time = _runner != null ? _runner.RenderTime : state.Time;
			var env = _environment != null && _environment.HasEnvironment ? _environment.Environment : default;
			var tide = env.WaterLevelNavd88; if (double.IsNaN(tide)) tide = 0;
			var height = RadarConfiguration.AntennaHeight(_source.Parameters?.Id) - pose.Down;
			var frame = new RadarFrame(pose.North, pose.East, pose.Heading, tide, height, env.Wind.Magnitude);
			PresentTargets = SampleTraffic(_traffic, time, _targets);
			Engine.Advance(time, frame, _targets, PresentTargets); View.Refresh(pose.Heading);
		}
		private void BindTraffic(TrafficRun traffic) { _traffic = traffic; _targets = traffic == null ? Array.Empty<RadarTarget>() : new RadarTarget[traffic.Pack.Vessels.Count]; }
		/// <summary>Estimates height from AIS length (no height field): 15% of length, bounded 3–35 m.</summary>
		public static int SampleTraffic(TrafficRun traffic, double time, RadarTarget[] targets)
		{
			if (traffic == null) return 0; var count = 0;
			for (var i = 0; i < traffic.Pack.Vessels.Count; i++)
			{
				if (i == traffic.RemovedVessel || !traffic.Replay.TrySample(i, traffic.PackTime(time), out var sample)) continue;
				var vessel = traffic.Pack.Vessels[i]; var length = vessel.Length;
				targets[count++] = new RadarTarget(sample.North, sample.East, length, Math.Max(3, Math.Min(35, length * .15)));
			}
			return count;
		}
		private void ReleaseView(bool releaseDocument = true)
		{
			if (View != null) { View.RemoveFromHierarchy(); View.Dispose(); View = null; }
			if (releaseDocument && _document != null) { ReleaseObject(_document); _document = null; }
			if (releaseDocument && _settings != null) { ReleaseObject(_settings); _settings = null; }
		}
		private static void ReleaseObject(UnityEngine.Object value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
		private void OnDestroy() => ReleaseView();
	}
}
