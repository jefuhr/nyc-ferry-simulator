using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World;
using FerrySim.World.HarborTiles;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FerrySim.Harbor.Player
{
	/// <summary>Installed/extracted, offline harbor smoke. Uses the real catalog, launcher, readiness gate and traffic.</summary>
	public sealed class HarborPlayerSmokeRun : MonoBehaviour
	{
		private readonly List<string> _failures = new List<string>();
		private readonly List<string> _errors = new List<string>();
		private readonly List<string> _screenshots = new List<string>();
		private PlayerSmokeOptions _options;
		private PlayerSmokeReport _report;
		private JObject _harbor = new JObject();
		private readonly object _logLock = new object();
		private int _warnings;
		private int _errorCount;
		private double _started;
		private int _frame;
		private bool _met;
		private double _scenarioStart;
		private readonly JObject _memory = new JObject();
		private long _peakMonoUsed, _peakMonoHeap, _peakAllocated, _peakReserved;
		private bool _displaysOff;
		private HarborBootstrap _bootstrap;
		private bool _chartSnapshot;

		/// <summary>The flag that leaves both displays off, for the comparison run the release script makes.</summary>
		public const string NoDisplaysFlag = "-ferrysim-smoke-no-displays";

		public static bool DriveComplete(double time, RunHold hold, bool active, double duration) => time >= duration || hold != RunHold.None || !active;

		public static bool IsHarborPlayer(string scene) => scene == "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		public static bool IsWithin(string root, string path) => !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Boot()
		{
			if (Application.isEditor || !IsHarborPlayer(SceneUtility.GetScenePathByBuildIndex(0))) return;
			try
			{
				var options = PlayerSmokeOptions.Parse(System.Environment.GetCommandLineArgs());
				if (options == null) return;
				Application.runInBackground = true;
				var host = new GameObject("Harbor player smoke run");
				DontDestroyOnLoad(host);
				var run = host.AddComponent<HarborPlayerSmokeRun>();
				run._options = options;
				run._displaysOff = Array.IndexOf(System.Environment.GetCommandLineArgs(), NoDisplaysFlag) >= 0;
			}
			catch (Exception error) { Debug.LogError(error); Application.Quit(2); }
		}
		private void OnEnable() => Application.logMessageReceivedThreaded += Record;
		private void OnDisable() => Application.logMessageReceivedThreaded -= Record;
		private void Record(string message, string stack, LogType type)
		{
			lock (_logLock)
			{
				if (type == LogType.Warning) _warnings++;
				if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
				{
					_errorCount++;
					if (_errors.Count < 20) _errors.Add(type + ": " + message);
				}
			}
		}

		/// <summary>Every frame: the four Profiler figures, kept as peaks (coordinator-required installed-player memory evidence).</summary>
		private void Update()
		{
			SampleMemory();
			// The chart's share on the frame the displays connected to it, whenever that falls (usually before the tiles are ready).
			if (!_chartSnapshot && _bootstrap != null && _bootstrap.Services is HarborIntegrationServices services && services.Displays != null && services.Displays.Connected)
			{
				_chartSnapshot = true;
				Snapshot("chart_ready");
			}
		}

		private void SampleMemory()
		{
			_peakMonoUsed = Math.Max(_peakMonoUsed, UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong());
			_peakMonoHeap = Math.Max(_peakMonoHeap, UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong());
			_peakAllocated = Math.Max(_peakAllocated, UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
			_peakReserved = Math.Max(_peakReserved, UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong());
		}

		private void Snapshot(string when)
		{
			SampleMemory();
			var snapshot = new JObject
			{
				["wall_s"] = Time.realtimeSinceStartupAsDouble - _started,
				["mono_used_bytes"] = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), ["mono_heap_bytes"] = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(),
				["total_allocated_bytes"] = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), ["total_reserved_bytes"] = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
			};
			try
			{
				HarborProcessMemory.Read(out var working, out var peak, out var pagefile, out var source);
				snapshot["working_set_bytes"] = working; snapshot["peak_working_set_bytes"] = peak;
				snapshot["pagefile_usage_bytes"] = pagefile; snapshot["process_source"] = source;
			}
			catch (Exception error) { snapshot["process_error"] = error.Message; Fail("Process memory unavailable: " + error.Message); }
			_memory[when] = snapshot;
		}

		private IEnumerator Start()
		{
			_started = Time.realtimeSinceStartupAsDouble;
			_frame = Time.frameCount;
			Directory.CreateDirectory(_options.OutputFolder);
			_report = new PlayerSmokeReport
			{
				product = Application.productName, version = Application.version, unity_version = Application.unityVersion,
				platform = Application.platform.ToString(), operating_system = SystemInfo.operatingSystem, processor = SystemInfo.processorType,
				graphics_device = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVersion + ")",
				streaming_assets = Application.streamingAssetsPath, persistent_data = Application.persistentDataPath,
				build = PlayerBuildInfo.TryLoad(Application.streamingAssetsPath, out var info, out var problem) ? info.Describe() : "no build record: " + problem,
				drive_lever = -0.3,
			};
			HarborBootstrap bootstrap = null;
			yield return Wait(() => { if (bootstrap == null) bootstrap = FindFirstObjectByType<HarborBootstrap>(); return bootstrap != null && (bootstrap.Services != null || bootstrap.Problem != null); }, 120);
			if (!_met || bootstrap.Services == null) { Fail(bootstrap?.Problem ?? "Harbor scene/services did not load"); Finish(); yield break; }
			_bootstrap = bootstrap;
			Snapshot("composed");
			var services = bootstrap.Services as HarborIntegrationServices;
			var runner = bootstrap.Runner;
			var initialized = false;
			try
			{
				if (services == null) throw new InvalidOperationException("Missing real harbor composition");
				var catalog = services.HarborCatalog;
				// Astoria is outside the slice and is not one of the three crew-refused faces.
				var choice = new ScenarioChoice("89", "2025-07-16", 12 * 60 + 18, catalog.Vessels[0].Id, traffic: true);
				_scenarioStart = Time.realtimeSinceStartupAsDouble;
				if (!catalog.TryCreate(choice, out var settings, out var refusal)) throw new InvalidOperationException(refusal);
				runner.RecordRuns = true;
				runner.RecordingDirectory = Path.Combine(_options.OutputFolder, "runs");
				if (!bootstrap.TryStart(settings, out refusal)) throw new InvalidOperationException(refusal);
				runner.Driver.Controls = new ConstantControls(new ControlInputs(-0.3, -0.3, 0));
				if (bootstrap.WheelhouseUI.View != null) bootstrap.WheelhouseUI.View.PickerVisible = false;
				_harbor["landing_stop_id"] = choice.StopId;
				_harbor["landing"] = "Astoria";
				_harbor["start_north_m"] = runner.State.North; _harbor["start_east_m"] = runner.State.East;
				_harbor["date"] = choice.Date;
				_harbor["runtime_digest"] = runner.HarborRuntime.Digest;
				_harbor["depth_digest"] = runner.RunEnvironment.Pack.Depth.Digest;
				Snapshot("run_started");
				initialized = true;
			}
			catch (Exception error) { Fail("Harbor start failed: " + error.Message); }
			if (!initialized) { Finish(); yield break; }
			var start = runner.State;
			yield return Wait(() => bootstrap.ViewState == HarborViewState.Ready || bootstrap.Problem != null, 90);
			if (!_met || bootstrap.ViewState != HarborViewState.Ready) { Fail(bootstrap.Problem ?? "Actual harbor tiles did not become ready"); runner.EndRun(); Finish(); yield break; }
			_harbor["tiles_ready"] = true;
			_harbor["start_to_ready_s"] = Time.realtimeSinceStartupAsDouble - _scenarioStart;
			Snapshot("scene_ready");
			var layers = FindObjectsByType<HarborTileset>(FindObjectsSortMode.None);
			var layerReports = new JArray();
			foreach (var layer in layers)
			{
				layerReports.Add(new JObject { ["id"] = layer.LayerId, ["path"] = layer.ResolvedPath, ["created_tiles"] = layer.CreatedTiles, ["progress"] = layer.LoadProgress });
				if (!layer.HasContent || layer.CreatedTiles == 0 || layer.LoadProgress < 100 || layer.Failure != null || !IsWithin(Application.streamingAssetsPath, layer.ResolvedPath)) Fail("Layer not ready from installed data: " + layer.LayerId);
			}
			if (layers.Length != 2) Fail("Expected terrain and skyline layers");
			_harbor["layers"] = layerReports;
			var maximumSpeed = runner.Telemetry.SpeedOverGround;
			yield return Wait(() =>
			{
				if (runner.HasTelemetry) maximumSpeed = Math.Max(maximumSpeed, runner.Telemetry.SpeedOverGround);
				return DriveComplete(runner.Driver.SimulationTime, runner.Hold, runner.IsRunActive, _options.DriveSeconds);
			}, Math.Max(60, _options.DriveSeconds * 6));
			if (!_met || runner.Driver.SimulationTime < _options.DriveSeconds) Fail("Drive did not finish: " + runner.Hold);
			try
			{
				_report.drive_simulated_s = runner.Driver.SimulationTime;
				_report.state_finite = runner.State.IsFinite();
				_report.displacement_m = Math.Sqrt(Math.Pow(runner.State.North - start.North, 2) + Math.Pow(runner.State.East - start.East, 2));
				_report.max_speed_over_ground_mps = maximumSpeed;
				_report.water_level_navd88_m = runner.Environment.WaterLevelNavd88;
				_report.landing_segments = services.Landings.Segments.Count;
				_report.environment = "pack"; _report.environment_pack = runner.RunEnvironment.Pack.Name;
				_report.environment_digest = runner.RunEnvironment.Pack.Digest; _report.environment_folder = runner.RunEnvironment.PackFolder;
				_report.environment_packaged = IsWithin(Application.streamingAssetsPath, _report.environment_folder);
				_report.vessel_file = SimPaths.Resolve(runner.Scenario.vesselFile); _report.vessel_file_packaged = IsWithin(Application.streamingAssetsPath, _report.vessel_file);
				_harbor["traffic_roster"] = services.Traffic.RosterCount; _harbor["traffic_active"] = services.Traffic.ActiveCount;
				_harbor["traffic_enabled"] = runner.TrafficEnabled;
				_harbor["traffic_digest"] = runner.Traffic.Pack.Digest;
				_harbor["depth_misses"] = runner.Environment.DepthMisses; _harbor["time_holds"] = runner.Environment.TimeHolds;
				if (!runner.TrafficEnabled || services.Traffic.RosterCount == 0 || services.Traffic.ActiveCount == 0) Fail("Actual harbor traffic is absent");
				if (!_report.environment_packaged || !_report.vessel_file_packaged) Fail("Environment or vessel resolved outside installed data");
				if (!_report.state_finite || _report.displacement_m < 1 || runner.Environment.DepthMisses != 0 || runner.Environment.TimeHolds != 0) Fail("Drive state, displacement or coverage failed");
			}
			catch (Exception error) { Fail("Drive evidence failed: " + error.Message); }
			// The chart pack and the wheelhouse displays: the shared load (started when the scene composed, never waited on by the
			// start above), then both displays opened in the wheelhouse view and proved to draw real content before their capture.
			var displays = services.Displays;
			if (displays == null) Fail("Harbor displays are missing from the composition");
			else
			{
				yield return Wait(() => displays.ChartState != HarborChartLoadState.Loading, 180);
				_harbor["chart_state"] = displays.ChartState.ToString();
				_harbor["displays_shown"] = !_displaysOff;
				if (!_met || displays.ChartState != HarborChartLoadState.Ready) Fail("Chart pack did not become ready: " + (displays.Load.Error ?? "timed out"));
				else
				{
					// The displays connect on the frame after the load is ready, shown or not: the chart's share of memory is measured here.
					yield return Wait(() => displays.Connected, 30);
					if (!_met) Fail("Displays did not connect to the ready chart");
					_harbor["chart_ready_s"] = displays.ChartReadySeconds;
					if (!_chartSnapshot) { _chartSnapshot = true; Snapshot("chart_ready"); }
					Snapshot("after_drive");
					try
					{
						var load = displays.Load;
						_harbor["chart_digest"] = load.Pack.Digest; _harbor["chart_runtime_digest"] = load.Pack.RuntimeDigest;
						_harbor["chart_load_ms"] = load.LoadMilliseconds; _harbor["chart_index_ms"] = load.IndexMilliseconds;
						_harbor["chart_tessellation_ms"] = load.TessellationMilliseconds; _harbor["chart_total_ms"] = load.TotalMilliseconds;
						_harbor["chart_managed_bytes_before"] = load.ManagedBytesBefore; _harbor["chart_managed_bytes_after"] = load.ManagedBytesAfter;
						_harbor["chart_peak_mono_used_bytes_while_loading"] = displays.PeakMonoUsedBytes;
						if (load.Pack.RuntimeDigest != runner.HarborRuntime.Digest) Fail("Chart pack links another runtime than the run's");
					}
					catch (Exception error) { Fail("Chart evidence failed: " + error.Message); }
					if (!_displaysOff)
					{
						bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
						displays.RadarShown = true; displays.ChartplotterShown = true;
						yield return Wait(() => displays.Connected && displays.RadarVisible && displays.ChartplotterVisible, 30);
						if (!_met) Fail("Displays did not open");
						var sweep = new RadarCaptureReadiness(displays.Radar.Engine.Configuration.Spokes);
						var sweepStarted = Time.realtimeSinceStartupAsDouble;
						var echoBins = 0; var vertices = 0; var drawn = 0; var renders = 0;
						yield return Wait(() =>
						{
							var engine = displays.Radar.Engine;
							sweep.Observe(Time.frameCount, engine.LastFirstSpoke, engine.LastSpokeCount, engine.Cleared);
							// Do not repeatedly scan the whole picture while only its first arc has been swept.
							if (!sweep.Complete) return false;
							echoBins = HarborDisplays.CountEchoBins(engine);
							var view = displays.Chartplotter.View;
							vertices = view != null ? view.Canvas.LastVertices : 0; drawn = view != null ? view.Targets.TargetsDrawn : 0; renders = view != null ? view.Canvas.RenderCount : 0;
							return RadarCaptureReadiness.ContentReady(sweep.Complete, echoBins, vertices, drawn);
						}, 30);
						_harbor["radar_swept_spokes"] = sweep.ObservedSpokes;
						_harbor["radar_required_spokes"] = displays.Radar.Engine.Configuration.Spokes;
						_harbor["radar_capture_wait_s"] = Time.realtimeSinceStartupAsDouble - sweepStarted;
						_harbor["radar_minimum_echo_bins"] = RadarCaptureReadiness.MinimumEchoBins;
						_harbor["radar_echo_bins"] = echoBins; _harbor["radar_range_nm"] = displays.Radar.Engine != null ? displays.Radar.Engine.Scale.RangeMetres / 1852 : 0;
						_harbor["radar_targets_present"] = displays.Radar.PresentTargets;
						_harbor["chart_vertices"] = vertices; _harbor["chart_targets_drawn"] = drawn; _harbor["chart_static_renders"] = renders;
						if (!_met) Fail("Displays did not draw real content: radar echo bins " + echoBins + ", chart vertices " + vertices + ", targets drawn " + drawn);
						for (var i = 0; i < 5; i++) yield return null;
						yield return new WaitForEndOfFrame();
						Texture2D texture = null;
						try
						{
							texture = ScreenCapture.CaptureScreenshotAsTexture();
							var colours = PlayerSmokeRun.DistinctColours(texture);
							_harbor["display_screenshot_colours"] = colours;
							if (colours <= 50) Fail("Blank displays screenshot");
							var path = Path.Combine(_options.OutputFolder, "smoke-displays.png");
							File.WriteAllBytes(path, texture.EncodeToPNG()); _harbor["display_screenshot"] = path;
						}
						catch (Exception error) { Fail("Displays screenshot failed: " + error.Message); }
						finally { if (texture != null) Destroy(texture); }
						Snapshot("displays_shown");
						displays.RadarShown = false; displays.ChartplotterShown = false;
						yield return null;
					}
				}
			}
			foreach (var view in new[] { CameraRig.Mode.Wheelhouse, CameraRig.Mode.Chase })
			{
				bootstrap.CameraRig.CurrentMode = view;
				for (var i = 0; i < 10; i++) yield return null;
				yield return new WaitForEndOfFrame();
				Texture2D texture = null;
				try
				{
					texture = ScreenCapture.CaptureScreenshotAsTexture();
					var colours = PlayerSmokeRun.DistinctColours(texture);
					_report.min_screenshot_colours = _screenshots.Count == 0 ? colours : Math.Min(_report.min_screenshot_colours, colours);
					if (colours <= 50) Fail("Blank " + view + " screenshot");
					var path = Path.Combine(_options.OutputFolder, "smoke-" + view.ToString().ToLowerInvariant() + ".png");
					File.WriteAllBytes(path, texture.EncodeToPNG()); _screenshots.Add(path);
				}
				catch (Exception error) { Fail("Screenshot failed: " + error.Message); }
				finally { if (texture != null) Destroy(texture); }
			}
			runner.EndRun();
			_report.recording = runner.LastRecordingPath;
			_report.recording_error = runner.RecordingError;
			if (!File.Exists(_report.recording) || runner.RecordingError != null) Fail("Recording missing or failed: " + runner.RecordingError);
			else _report.recording_bytes = new FileInfo(_report.recording).Length;
			if (_report.recording_bytes == 0) Fail("Empty recording");
			Finish();
		}

		private IEnumerator Wait(Func<bool> ready, double seconds)
		{
			var deadline = Time.realtimeSinceStartupAsDouble + seconds;
			while (Time.realtimeSinceStartupAsDouble < deadline)
			{
				try { if (ready()) { _met = true; yield break; } }
				catch (Exception error) { Fail("Waiting failed: " + error.Message); break; }
				yield return null;
			}
			_met = false;
		}
		private void Fail(string problem) => _failures.Add(problem);
		private void Finish()
		{
			var fallbackAvailable = Directory.Exists(Path.Combine(SimPaths.RepoRoot, EnvironmentPacks.ProcessedFolder)) || Directory.Exists(Path.Combine(SimPaths.RepoRoot, "vessels"));
			if (fallbackAvailable) Fail("Repository fallback data is available to the installed smoke run");
			lock (_logLock)
			{
				if (_errorCount > 0) Fail("Player logged errors: " + _errors[0]);
				_report.errors = _errorCount; _report.error_messages = _errors.ToArray(); _report.warnings = _warnings;
			}
			Snapshot("end");
			_memory["peak"] = new JObject { ["mono_used_bytes"] = _peakMonoUsed, ["mono_heap_bytes"] = _peakMonoHeap, ["total_allocated_bytes"] = _peakAllocated, ["total_reserved_bytes"] = _peakReserved };
			_harbor["memory"] = _memory;
			_report.screenshots = _screenshots.ToArray(); _report.screenshot_views = new[] { "Wheelhouse", "Chase" };
			if (_screenshots.Count != 2) Fail("Both screenshots are required");
			_report.frames = Time.frameCount - _frame; _report.wall_s = Time.realtimeSinceStartupAsDouble - _started;
			_report.average_fps = _report.frames / _report.wall_s;
			_report.failures = _failures.ToArray(); _report.passed = _failures.Count == 0;
			var document = JObject.FromObject(_report);
			// A spatial harbor surface has no single drawn scalar level; do not report an unmeasured default zero.
			document.Remove("drawn_water_level_navd88_m");
			document["scene"] = "Harbor"; document["harbor"] = _harbor;
			document["working_directory"] = System.Environment.CurrentDirectory;
			document["repository_fallback_available"] = fallbackAvailable;
			File.WriteAllText(Path.Combine(_options.OutputFolder, "smoke.json"), document.ToString() + "\n");
			Debug.Log("Harbor smoke " + (_report.passed ? "passed" : "FAILED") + ": " + string.Join("; ", _failures));
			Application.Quit(_report.passed ? 0 : 1);
		}
	}
}
