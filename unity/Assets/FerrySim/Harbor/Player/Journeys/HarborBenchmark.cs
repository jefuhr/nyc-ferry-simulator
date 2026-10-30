using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using FerrySim.World;
using FerrySim.World.HarborTiles;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Profiling;

namespace FerrySim.Harbor.Player.Journeys
{
	/// <summary>Opt-in installed-player evidence. No simulated pose is set by the benchmark.</summary>
	[DefaultExecutionOrder(-300)]
	public sealed partial class HarborBenchmark : MonoBehaviour
	{
		private const int FrameCapacity = 900000, EventCapacity = 100000;
		private struct FrameRow
		{
			public int Frame, Phase, Width, Height;
			public double Wall, WallMs, SimTime, North, East, Cpu, CpuTotal, PresentWait, Render, Gpu, Radar, Chart;
			public bool Focused;
			public ulong TimingStamp;
			public long Step, System, Mono, Reserved, Allocated, Gfx, Mesh;
		}
		private struct EventRow { public int Frame, Kind, Layer, Id, RelatedFrame; public double Value; }
		private readonly FrameRow[] _frames = new FrameRow[FrameCapacity];
		private readonly EventRow[] _events = new EventRow[EventCapacity];
		private int _frameCount, _eventCount, _phase, _pendingFrame = -1;
		private double _lastWall;
		private FrameRow _pending;
		private string _output, _name, _routeFile, _renderMode;
		private bool _traffic, _displays, _capture, _finished, _overflow;
		private int _minute = -1;
		private double _duration = 6000, _startAccepted, _firstFrame = -1, _ready = -1, _catalogMs, _atomicMs;
		private double _visibility;
		private int _loadScreenFrames;
		private bool _timing, _arrivalSelected, _switchDiagnostics, _switchCollect;
		private int _switchCycles=1;
		private double _rate = 1, _utcZero;
		private readonly FrameTiming[] _frameTiming = new FrameTiming[1];
		private readonly List<string> _failures = new List<string>(32);
		private readonly JObject _scenario = new JObject();
		private HarborBootstrap _bootstrap;
		private VesselSimulationRunner _runner;
		private HarborIntegrationServices _services;
		private HarborJourneyControls _controls;
		private HarborJourneyObserver _observer;
		private HarborJourney _route;
		private HarborTileset[] _layers;
		private ProfilerRecorder _system, _gfx, _texture, _mesh, _main, _render, _radarTick, _chartTick;
		private static readonly string[] Phases = { "prepare", "ready", "drive", "capture", "replay", "finish", "switch" };
		private static readonly string[] Kinds = { "tile_created", "tile_enabled", "tile_disabled", "tile_destroyed", "load_progress", "origin_shift", "tileset_failure", "start_accepted", "view_ready", "screenshot", "scene_loaded", "scene_unloaded", "frame_dropped" };

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Boot()
		{
			if (Application.isEditor) return;
			var args = System.Environment.GetCommandLineArgs();
			var name = Option(args, "-ferrysim-route", null);
			if (name == null) return;
			var host = new GameObject("Harbor installed-player benchmark");
			DontDestroyOnLoad(host);
			var run = host.AddComponent<HarborBenchmark>();
			run._name = name;
			run._output = Path.GetFullPath(Option(args, "-ferrysim-benchmark-output", "harbor-benchmark"));
			run._routeFile = Path.GetFullPath(Option(args, "-ferrysim-route-file", "routes.json"));
			run._traffic = Option(args, "-ferrysim-traffic", "0") == "1";
			run._displays = Option(args, "-ferrysim-displays", "1") == "1";
			run._capture = Option(args, "-ferrysim-capture", "0") == "1";
			run._renderMode = Option(args, "-ferrysim-render", "both");
			run._duration = double.Parse(Option(args, "-ferrysim-duration", "6000"), CultureInfo.InvariantCulture);
			run._minute = int.Parse(Option(args, "-ferrysim-minute", "-1"), CultureInfo.InvariantCulture);
			run._visibility = double.Parse(Option(args, "-ferrysim-visibility", "0"), CultureInfo.InvariantCulture);
			run._timing = Option(args, "-ferrysim-timing", "0") == "1";
			run._rate = double.Parse(Option(args, "-ferrysim-rate", "1"), CultureInfo.InvariantCulture);
			run._switchDiagnostics = Option(args,"-ferrysim-switch-diagnostics","0") == "1";
			run._switchCollect = Option(args,"-ferrysim-switch-collect","0") == "1";
			run._switchCycles = int.Parse(Option(args,"-ferrysim-switch-cycles","1"),CultureInfo.InvariantCulture);
			if (run._switchCycles < 1 || run._switchCycles > 2) throw new ArgumentOutOfRangeException("-ferrysim-switch-cycles");
			Application.runInBackground = true;
			// Same explicit pacing on both platforms; no physics or quality setting is changed.
			QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
		}
		public static void ConfigureRate(VesselSimulationRunner runner, double rate)
		{
			if (rate != 1 && rate != 5) throw new ArgumentOutOfRangeException(nameof(rate));
			if (rate > 1) runner.MaxStepsPerFrame=500;
			Time.timeScale=(float)rate;
		}

		public static string Option(string[] args, string key, string fallback)
		{
			var index = Array.IndexOf(args, key);
			return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
		}
		private void Awake()
		{
			_utcZero = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - Time.realtimeSinceStartupAsDouble;
			_system = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
			_gfx = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
			_texture = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
			_mesh = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Mesh Memory");
			_main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
			_render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread");
			_radarTick = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RadarController.Refresh");
			_chartTick = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "FerrySim.Chartplotter.Refresh");
			SceneManager.sceneLoaded += SceneLoaded; SceneManager.sceneUnloaded += SceneUnloaded;
			Cesium3DTileset.OnCesium3DTilesetLoadFailure += TilesetFailure;
			Application.logMessageReceived += Log;
		}
		private void SceneLoaded(Scene scene, LoadSceneMode mode)
		{
			Event(10, -1, scene.handle, 0);
			_layers = FindObjectsByType<HarborTileset>(FindObjectsSortMode.None);
			for (var i = 0; i < _layers.Length; i++)
			{
				var index = i;
				_layers[i].Tileset.OnTileGameObjectCreated += tile => TileCreated(tile, index);
			}
			var geo = FindFirstObjectByType<CesiumGeoreference>();
			if (geo != null) geo.changed += () => Event(5, -1, 0, 0);
		}
		private void SceneUnloaded(Scene scene) => Event(11, -1, scene.handle, 0);
		private void TilesetFailure(Cesium3DTilesetLoadFailureDetails detail) => Event(6, -1, 0, 0);
		private void Log(string message, string stack, LogType type)
		{
			if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && _failures.Count < 32) _failures.Add(type + ": " + message);
		}
		private void TileCreated(GameObject tile, int layer)
		{
			Event(0, layer, tile.GetInstanceID(), 0);
			var lifetime = tile.AddComponent<HarborBenchmarkTile>();
			lifetime.Owner = this; lifetime.Layer = layer; lifetime.Id = tile.GetInstanceID();
		}
		internal void Event(int kind, int layer, int id, double value)
		{
			if (_finished) return;
			if (_eventCount >= _events.Length) { _overflow = true; return; }
			_events[_eventCount++] = new EventRow { Frame = Time.frameCount, Kind = kind, Layer = layer, Id = id, RelatedFrame = kind == 8 ? Time.frameCount - 1 : -1, Value = value };
		}
		private readonly float[] _progress = { -1, -1, -1, -1 };
		public void InitializeSamplerForTests() { if (!_system.Valid) Awake(); }
		public bool SystemCounterValid => _system.Valid;
		public void SampleFrameForTests() => Update();
		private void Update()
		{
			if (_finished) return;
			if (_phase==2 && _bootstrap!=null && (_bootstrap.IsHeld || _bootstrap.ViewState!=HarborViewState.Ready ||
				(_bootstrap.WheelhouseUI.View!=null && (_bootstrap.WheelhouseUI.View.PickerVisible || _bootstrap.WheelhouseUI.View.DebriefVisible)))) _loadScreenFrames++;
			if (!_arrivalSelected && _controls != null && _route != null && _controls.Phase >= _route.Points.Length)
			{ _arrivalSelected=true; _services.Navigation.SelectedStopId=_route.Arrival; }
			var now = Time.realtimeSinceStartupAsDouble;
			if (_timing) FrameTimingManager.CaptureFrameTimings();
			if (_pendingFrame >= 0)
			{
				_pending.WallMs = (now - _lastWall) * 1000;
				_pending.Cpu = Counter(_main) * 1e-6; _pending.Render = Counter(_render) * 1e-6;
				_pending.Radar = Counter(_radarTick) * 1e-6; _pending.Chart = _services != null && _services.Displays.ChartplotterVisible ? _services.Displays.Chartplotter.LastRefreshMilliseconds : 0;
				if (_timing && FrameTimingManager.GetLatestTimings(1, _frameTiming) > 0)
				{ _pending.Cpu = _frameTiming[0].cpuMainThreadFrameTime; _pending.Render = _frameTiming[0].cpuRenderThreadFrameTime; _pending.Gpu = _frameTiming[0].gpuFrameTime; _pending.TimingStamp = _frameTiming[0].frameStartTimestamp; _pending.CpuTotal = _frameTiming[0].cpuFrameTime; _pending.PresentWait = _frameTiming[0].cpuMainThreadPresentWaitTime; }
				_pending.System = Counter(_system); _pending.Gfx = Counter(_gfx); _pending.Mesh = Counter(_mesh);
				_pending.Mono = Profiler.GetMonoUsedSizeLong(); _pending.Reserved = Profiler.GetTotalReservedMemoryLong(); _pending.Allocated = Profiler.GetTotalAllocatedMemoryLong();
				if (_frameCount < _frames.Length) _frames[_frameCount++] = _pending; else _overflow = true;
			}
			_pendingFrame = Time.frameCount; _lastWall = now;
			_pending = new FrameRow { Frame = Time.frameCount, Phase = _phase, Wall = now, Focused = Application.isFocused, Width = Screen.width, Height = Screen.height,
				Step = _runner?.Driver?.StepCount ?? 0, SimTime = _runner?.State.Time ?? 0, North = _runner?.State.North ?? 0, East = _runner?.State.East ?? 0 };
			if (_firstFrame < 0 && _startAccepted > 0) _firstFrame = now;
			if (_layers != null)
			for (var i = 0; i < _layers.Length && i < _progress.Length; i++)
			{
				var progress = _layers[i].LoadProgress;
				if (progress != _progress[i]) { _progress[i] = progress; Event(4, i, 0, progress); }
			}
		}
		private void SetPhase(int phase) { _phase=phase; if (_pendingFrame==Time.frameCount) _pending.Phase=phase; }

		private static long Counter(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue : 0;

		private IEnumerator Start()
		{
			var stack = new Stack<IEnumerator>(8); stack.Push(Run());
			while (stack.Count > 0)
			{
				object next = null; var move = false; Exception failure = null;
				try { move=stack.Peek().MoveNext(); if (move) next=stack.Peek().Current; }
				catch (Exception error) { failure=error; }
				if (failure != null) { _failures.Add(failure.ToString()); Finish(); yield break; }
				if (!move) { stack.Pop(); continue; }
				if (next is IEnumerator nested) { stack.Push(nested); continue; }
				yield return next;
			}
		}
		private IEnumerator Run()
		{
			Directory.CreateDirectory(_output);
			var timeout = Time.realtimeSinceStartupAsDouble + 180;
			while (_bootstrap == null || _bootstrap.Services == null)
			{
				if (_bootstrap == null) _bootstrap = FindFirstObjectByType<HarborBootstrap>();
				if ((_bootstrap != null && _bootstrap.Problem != null) || Time.realtimeSinceStartupAsDouble > timeout)
				{ _failures.Add(_bootstrap?.Problem ?? "Composition timeout"); Finish(); yield break; }
				yield return null;
			}
			_runner = _bootstrap.Runner; _services = _bootstrap.Services as HarborIntegrationServices;
			_runner.FrameDropped += Dropped;
			if (SpecialStart) { Identity(); yield return CatalogStarts(); Finish(); yield break; }
			JObject definitions = null;
			try
			{
				definitions = JObject.Parse(File.ReadAllText(_routeFile));
				foreach (var item in definitions["routes"]) if ((string)item["name"] == (_name == "camera-sweep" ? "soundview" : _name)) _route = HarborJourney.Parse(item);
				if (_route == null) throw new InvalidOperationException("Unknown route " + _name);
				Identity();
			}
			catch (Exception e) { _failures.Add(e.ToString()); }
			if (_route == null || _failures.Count > 0) { Finish(); yield break; }
			if (_capture) yield return Capture("before-start");
			SetPhase(0);
			_startAccepted = Time.realtimeSinceStartupAsDouble;
			Event(7, -1, 0, 0);
			try
			{
				var choice = new ScenarioChoice(_route.Departure, _route.Date, _minute >= 0 ? _minute : _route.Minute, "t-class", traffic: _traffic, visibilityMetres: _visibility);
				var catalogAt = Time.realtimeSinceStartupAsDouble;
				if (!_services.HarborCatalog.TryCreate(choice, out var settings, out var problem)) throw new InvalidOperationException(problem);
				_catalogMs = (Time.realtimeSinceStartupAsDouble - catalogAt) * 1000;
				_runner.RecordRuns = true; _runner.TelemetrySampleHz = 100; _runner.RecordingDirectory = Path.Combine(_output, "runs");
				var atomicAt = Time.realtimeSinceStartupAsDouble;
				if (!_bootstrap.TryStart(settings, out problem)) throw new InvalidOperationException(problem);
				_atomicMs = (Time.realtimeSinceStartupAsDouble - atomicAt) * 1000;
				var simulation = (VesselDynamicsSimulation)_runner.Driver.Simulation;
				_controls = new HarborJourneyControls(_route, () => _runner.State, () => simulation.ContactCount > 0, _runner.Parameters.Particulars.LengthOverall.Value);
				_runner.Driver.Controls = _controls;
				_observer = new HarborJourneyObserver(_route, simulation); _runner.Driver.AddObserver(_observer);
				_runner.Driver.AddObserver(new JourneyEnd(_runner.Driver, _controls));
				if (_rate > 1) ConfigureRate(_runner,_rate);
				if (_duration < 6000) _runner.Driver.StepLimit = (long)Math.Round(_duration * 100);
				if (_bootstrap.WheelhouseUI.View != null) _bootstrap.WheelhouseUI.View.PickerVisible = false;
				_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
				_services.Navigation.SelectedStopId = _route.Departure;
				_services.Displays.RadarShown = _displays; _services.Displays.ChartplotterShown = _displays;
			}
			catch (Exception e) { _failures.Add(e.ToString()); }
			if (_failures.Count > 0) { Finish(); yield break; }
			SetPhase(1);
			timeout = Time.realtimeSinceStartupAsDouble + 90;
			while (_bootstrap.ViewState != HarborViewState.Ready && _bootstrap.Problem == null && Time.realtimeSinceStartupAsDouble < timeout) yield return null;
			_ready = Time.realtimeSinceStartupAsDouble;
			_scenario["readiness_complete"] = _bootstrap.ViewState == HarborViewState.Ready && ReadinessPlacement();
			if (!(bool)_scenario["readiness_complete"]) { _failures.Add(_bootstrap.Problem ?? "Readiness or placement failed"); Finish(); yield break; }
			Event(8, -1, 0, _ready - _startAccepted);
			if (_name == "camera-sweep") { yield return CameraSweep(); Finish(); yield break; }
			// Diagnostic render controls start after the same real preparation/readiness gate.
			if (_layers != null) foreach (var layer in _layers)
				if (_renderMode == "none" || (_renderMode == "terrain" && layer.LayerId != "terrain")) layer.Tileset.enabled = false;
			if (_capture) yield return Capture("departure-" + _route.Departure);
			SetPhase(2);
			timeout = Time.realtimeSinceStartupAsDouble + _duration * 3 + 120;
			var capturedBridges=0;
			var priorNorth=(int)Math.Floor(_runner.State.North/4000); var priorEast=(int)Math.Floor(_runner.State.East/4000);
			var beforeSeam=false;
			while (!_controls.Complete && _runner.State.Time < _duration && _runner.Hold == RunHold.None && _runner.IsRunActive && _observer.Failures.Count == 0 && _observer.GroundedSteps == 0 && Time.realtimeSinceStartupAsDouble < timeout)
			{
				if (_capture)
				{
					var north=(int)Math.Floor(_runner.State.North/4000); var east=(int)Math.Floor(_runner.State.East/4000);
					var n=_runner.State.North-north*4000; var e=_runner.State.East-east*4000;
					var near=Math.Min(Math.Min(n,4000-n),Math.Min(e,4000-e))<20;
					string shot=null;
					if (_observer.Crossings.Count>capturedBridges) { capturedBridges=_observer.Crossings.Count;shot="bridge-"+capturedBridges; }
					else if (north!=priorNorth || east!=priorEast) {shot="seam-after-"+north+"-"+east;beforeSeam=false;priorNorth=north;priorEast=east;}
					else if (near && !beforeSeam) {shot="seam-before-"+north+"-"+east;beforeSeam=true;}
					if (shot!=null) { _runner.Pause();yield return Capture(shot);_runner.Resume();SetPhase(2); }
				}
				yield return null;
			}
			_runner.Pause();
			SetPhase(5);
			ScenarioEvidence();
			if (_capture) yield return Capture("arrival-" + _route.Arrival);
			_runner.EndRun();
			var path = _runner.LastRecordingPath;
			_scenario["recording"] = path;
			if (string.IsNullOrEmpty(path) || _runner.RecordingError != null) _failures.Add("Incomplete 100 Hz recording: " + _runner.RecordingError);
			else
			{
				SetPhase(4);
				try
				{
					var recording = RecordedRun.Load(path);
					var result = ReplayPlayer.Verify(recording,_runner.Parameters,_runner.Factory,geometry:_runner.WorldGeometry,
						pack:_runner.RunEnvironment.Pack,packs:_runner.EnvironmentPacks,traffic:_runner.Traffic?.Pack,trafficPacks:_runner.TrafficPacks);
					_scenario["replay_identical"] = recording.StepCount > 0 && result.Matches && recording.StateDigest != null && result.RecordedDigest == result.ReplayedDigest;
					_scenario["replay_result"] = result.ToString(); _scenario["replay_rows"] = result.RowsCompared;
					_scenario["recorded_state_digest"] = result.RecordedDigest; _scenario["replayed_state_digest"] = result.ReplayedDigest;
					_scenario["replay_mode"] = "ReplayPlayer.Verify, same-runtime 100 Hz core states and traffic events from the actual player recording";
					if (!(bool)_scenario["replay_identical"]) _failures.Add("Same-runtime state/event replay did not match");
				}
				catch (Exception e) { _failures.Add("Replay: " + e.ToString()); }

			}
			SetPhase(5); yield return null; Finish();
		}
		private sealed class JourneyEnd : IStepObserver
		{
			private readonly FixedStepDriver _driver; private readonly HarborJourneyControls _controls;
			public JourneyEnd(FixedStepDriver driver, HarborJourneyControls controls) { _driver = driver; _controls = controls; }
			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }
			public void AfterStep(long step, in VesselState state, in VesselTelemetry telemetry) { if (_controls.Complete) _driver.StepLimit = step; }
		}

		private void Dropped(FrameAdvance advance) => Event(12, -1, advance.Steps, advance.DroppedTime);
		private void ScenarioEvidence()
		{
			var sim = (VesselDynamicsSimulation)_runner.Driver.Simulation;
			_scenario["load_screen_frames"]=_loadScreenFrames;
			if (_loadScreenFrames!=0) _failures.Add("A loading/held/picker/debrief view interrupted the driven journey");
			if ((_runner.TrafficMonitor?.CollisionsStarted ?? 0)>0) _failures.Add("Unintended AIS traffic collision");
			_scenario["catalog_create_ms"] = _catalogMs; _scenario["atomic_start_ms"] = _atomicMs;
			_scenario["atomic_load_timings"] = JObject.FromObject(_runner.HarborLoadTimings);
			_scenario["scenario_start_utc"] = _runner.ScenarioStartUtc.ToString("O");
			_scenario["completed_journey"] = _controls.Complete;
			_scenario["state_finite"] = _runner.State.IsFinite();
			if (!_runner.State.IsFinite()) _failures.Add("Non-finite core state");
			_scenario["phase_reached"] = _controls.Phase; _scenario["duration_s"] = _runner.State.Time;
			_scenario["depth_misses"] = _runner.Environment.DepthMisses; _scenario["time_holds"] = _runner.Environment.TimeHolds;
			_scenario["query_saturation"] = sim.SaturatedSegmentQueries; _scenario["grounded_steps"] = _observer.GroundedSteps;
			_scenario["minimum_under_keel_m"] = double.IsInfinity(_observer.MinimumUnderKeel) ? null : (JToken)_observer.MinimumUnderKeel; _scenario["hold"] = _runner.Hold.ToString();
			_scenario["dropped_simulation_s"] = _runner.Driver.TotalDroppedTime;
			_scenario["ordered_crossings"] = new JArray(_observer.Crossings);
			_scenario["traffic_alarms"] = _runner.TrafficMonitor?.AlarmsRaised; _scenario["traffic_collisions"] = _runner.TrafficMonitor?.CollisionsStarted;
			_scenario["first_contact_speed_mps"] = _observer.FirstContactSpeed; _scenario["first_contact_id"] = _observer.FirstContactId;
			_scenario["arrival_verdict"] = _controls.Complete || _observer.Failures.Count > 0 || _observer.GroundedSteps > 0 ? _observer.LandingVerdict(_runner.State) : "unverified (incomplete journey)";
			_scenario["peak_contact_force_n"] = _observer.PeakForce; _scenario["estimated_force_limit_n"] = _route.ForceLimit;
			_scenario["peak_compression_m"] = _observer.PeakCompression; _scenario["bottomed_out"] = _observer.BottomedOut; _scenario["final_hold_okay"] = _observer.FinalHoldOkay;
			_scenario["arrival_stop_id"] = _route.Arrival;
			_scenario["environment_digest"] = _runner.RunEnvironment.Pack.Digest; _scenario["depth_digest"] = _runner.RunEnvironment.Pack.Depth.Digest;
			_scenario["runtime_digest"] = _runner.HarborRuntime.Digest; _scenario["traffic_digest"] = _runner.Traffic?.Pack.Digest;
			_scenario["chart_digest"] = _services.Displays.Load.Pack?.Digest;
			_scenario["displays_connected"] = _services.Displays.Connected;
			_scenario["displays_visible"] = _services.Displays.RadarVisible && _services.Displays.ChartplotterVisible;
			foreach (var error in _observer.Failures) _failures.Add(error);
			if (_runner.Environment.DepthMisses != 0 || _runner.Environment.TimeHolds != 0 || _runner.Hold != RunHold.None || sim.SaturatedSegmentQueries != 0 || _observer.GroundedSteps != 0) _failures.Add("Physics coverage, hold, grounding or query saturation failed");
			if (_runner.Driver.TotalDroppedTime != 0) _failures.Add("Fixed-step time was dropped");
			if (!_controls.Complete && _runner.State.Time + 1e-9 < _duration) _failures.Add("Run ended before its requested duration");
			if (_controls.Complete && !_route.HoldOnly && _observer.LandingVerdict(_runner.State) != "pass") _failures.Add("Soft landing or final hold failed");
			if (_controls.Complete && _observer.Crossings.Count != _route.SpanIds.Length) _failures.Add("Missing required ordered bridge crossings");
		}
		private IEnumerator Capture(string name)
		{
			SetPhase(3);
			var wasPaused = _runner.IsPaused;
			if (!wasPaused && _services.Displays.Connected && _services.Displays.RadarVisible)
			{
				var engine = _services.Displays.Radar.Engine;
				var sweep = new RadarCaptureReadiness(engine.Configuration.Spokes);
				var until = Time.realtimeSinceStartupAsDouble + 30;
				while (!sweep.Complete && Time.realtimeSinceStartupAsDouble < until)
				{ sweep.Observe(Time.frameCount,engine.LastFirstSpoke,engine.LastSpokeCount,engine.Cleared); yield return null; }
				_scenario["capture_swept_spokes"] = sweep.ObservedSpokes;
			}
			_runner.Pause();
			var originalMode = _bootstrap.CameraRig.CurrentMode;
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			for (var i=0;i<3;i++) yield return null;
			yield return CaptureImage(name + "-wheelhouse");
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			for (var i=0;i<3;i++) yield return null;
			yield return CaptureImage(name + "-chase");
			_bootstrap.CameraRig.CurrentMode = originalMode;
			yield return null;
			if (!wasPaused) _runner.Resume();
		}
		private IEnumerator CaptureImage(string name)
		{
			yield return new WaitForEndOfFrame();
			Event(9, -1, 0, 0);
			Texture2D texture = null;
			try { texture=ScreenCapture.CaptureScreenshotAsTexture(); File.WriteAllBytes(Path.Combine(_output,name+".png"),texture.EncodeToPNG()); }
			catch (Exception e) { _failures.Add("Capture: "+e.Message); }
			finally { if (texture != null) Destroy(texture); }
		}

		private void Identity()
		{
			var layers = new JArray();
			foreach (var layer in _layers) layers.Add(new JObject { ["id"] = layer.LayerId, ["maximum_cached_bytes"] = layer.Tileset.maximumCachedBytes, ["screen_space_error"] = layer.Tileset.maximumScreenSpaceError, ["path"] = layer.ResolvedPath });
			var identity = new JObject { ["platform"] = Application.platform.ToString(), ["os"] = SystemInfo.operatingSystem, ["cpu"] = SystemInfo.processorType,
				["gpu"] = SystemInfo.graphicsDeviceName, ["graphics_api"] = SystemInfo.graphicsDeviceVersion, ["gpu_memory_mb"] = SystemInfo.graphicsMemorySize,
				["unity"] = Application.unityVersion, ["width"] = Screen.width, ["height"] = Screen.height,
				["utc_zero_unix_s"] = _utcZero, ["frame_capacity"] = FrameCapacity, ["event_capacity"] = EventCapacity, ["quality_level"] = QualitySettings.GetQualityLevel(), ["quality_name"] = QualitySettings.names[QualitySettings.GetQualityLevel()], ["anti_aliasing"] = QualitySettings.antiAliasing, ["v_sync"] = QualitySettings.vSyncCount, ["target_frame_rate"] = Application.targetFrameRate,
				["layers"] = layers, ["streaming_assets"] = Application.streamingAssetsPath,
				["system_used_counter_valid"] = _system.Valid, ["main_thread_counter_valid"] = _main.Valid, ["render_thread_counter_valid"] = _render.Valid,
				["radar_counter_valid"] = _radarTick.Valid, ["gfx_counter_valid"] = _gfx.Valid, ["mesh_counter_valid"] = _mesh.Valid,
				["cpu_timing_source"] = "ProfilerRecorder previous completed frame; wall interval is joined to the frame that actually executed",
				["gpu_timing"] = _timing ? "FrameTimingManager latest capture; raw frameStartTimestamp retained, GPU capture latency is not inferred" : "disabled in shipping build; diagnostic benchmark enables frame timing; zero is not evidence", ["queue_counts"] = "Cesium 1.26.0 exposes no public queue counter; load-progress events are a proxy, complete queue coverage unverified" };
			try { HarborProcessMemory.Read(out var rss, out var peak, out var commit, out var source); identity["native_process_memory"] = new JObject { ["working_bytes"] = rss, ["peak_bytes"] = peak, ["pagefile_usage_bytes"] = commit, ["source"] = source }; }
			catch (Exception e) { identity["native_process_memory_error"] = e.Message; }
			File.WriteAllText(Path.Combine(_output, "identity.json"), identity.ToString() + "\n");
			var build = Path.Combine(Application.streamingAssetsPath, "build-info.json");
			if (File.Exists(build)) File.Copy(build, Path.Combine(_output, "build-info.json"), true);
		}
		private void Finish()
		{
			if (_finished) return;
			_finished = true;
			if (_runner != null && _runner.IsRunActive) _runner.EndRun();
			if (_overflow) _failures.Add("Evidence buffer overflow; run is incomplete");
			_scenario["platform"] = Application.platform.ToString(); _scenario["route"] = _name; _scenario["traffic"] = _traffic; _scenario["displays"] = _displays; _scenario["render_mode"] = _renderMode;
			_scenario["visibility_m"] = _visibility; _scenario["minute"] = _minute; if (_scenario["stress"] == null) _scenario["stress"] = _rate > 1;
			_scenario["requested_duration_s"] = _duration; _scenario["simulation_rate"] = _rate; _scenario["capture_pass"] = _capture;
			_scenario["streaming_instrumentation_complete"] = false;
			_scenario["start_to_ready_s"] = _ready > 0 ? (JToken)(_ready - _startAccepted) : null;
			_scenario["request_to_first_frame_s"] = _firstFrame > 0 ? (JToken)(_firstFrame - _startAccepted) : null;
			_scenario["first_frame_to_ready_s"] = _firstFrame > 0 && _ready > 0 ? (JToken)(_ready - _firstFrame) : null;
			_scenario["failures"] = new JArray(_failures); _scenario["functional_verdict"] = _failures.Count == 0 ? "pass" : "fail";
			Directory.CreateDirectory(_output);
			WriteCsv();
			File.WriteAllText(Path.Combine(_output, "scenario.json"), _scenario.ToString() + "\n");
			Application.Quit(_failures.Count == 0 ? 0 : 2);
		}
		private void WriteCsv()
		{
			using (var f = new StreamWriter(Path.Combine(_output, "frames.csv")))
			using (var m = new StreamWriter(Path.Combine(_output, "memory.csv")))
			{
				f.WriteLine("frame,wall_s,wall_ms,phase,step,sim_time_s,north_m,east_m,cpu_main_ms,cpu_render_ms,gpu_ms,radar_tick_ms,chart_tick_ms,width,height,frame_timing_timestamp,cpu_total_ms,cpu_present_wait_ms,focused");
				m.WriteLine("frame,wall_s,phase,system_used_bytes,mono_used_bytes,total_allocated_bytes,total_reserved_bytes,gfx_bytes,mesh_bytes");
				for (var i = 0; i < _frameCount; i++)
				{
					var r = _frames[i];
					f.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3},{4},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R},{13},{14},{15},{16:R},{17:R},{18}", r.Frame,r.Wall,r.WallMs,Phases[r.Phase],r.Step,r.SimTime,r.North,r.East,r.Cpu,r.Render,r.Gpu,r.Radar,r.Chart,r.Width,r.Height,r.TimingStamp,r.CpuTotal,r.PresentWait,r.Focused ? 1 : 0));
					m.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2},{3},{4},{5},{6},{7},{8}",r.Frame,r.Wall,Phases[r.Phase],r.System,r.Mono,r.Allocated,r.Reserved,r.Gfx,r.Mesh));
				}
			}
			using (var e = new StreamWriter(Path.Combine(_output, "events.csv")))
			{
				e.WriteLine("frame,kind,layer,instance_id,value,related_frame");
				for (var i = 0; i < _eventCount; i++)
				{
					var r = _events[i];
					e.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:R},{5}",r.Frame,Kinds[r.Kind],r.Layer,r.Id,r.Value,r.RelatedFrame));
				}
			}
		}
		private void OnDestroy()
		{
			SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
			Cesium3DTileset.OnCesium3DTilesetLoadFailure -= TilesetFailure; Application.logMessageReceived -= Log;
			_system.Dispose(); _gfx.Dispose(); _texture.Dispose(); _mesh.Dispose(); _main.Dispose(); _render.Dispose(); _radarTick.Dispose(); _chartTick.Dispose();
		}
	}
	public sealed class HarborBenchmarkTile : MonoBehaviour
	{
		internal HarborBenchmark Owner;
		internal int Layer, Id;
		private void OnEnable() { if (Owner != null) Owner.Event(1, Layer, Id, 0); }
		private void OnDisable() { if (Owner != null) Owner.Event(2, Layer, Id, 0); }
		private void OnDestroy() { if (Owner != null) Owner.Event(3, Layer, Id, 0); }
	}
}
