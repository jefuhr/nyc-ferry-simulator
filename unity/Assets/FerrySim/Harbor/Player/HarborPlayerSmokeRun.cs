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
				host.AddComponent<HarborPlayerSmokeRun>()._options = options;
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
				initialized = true;
			}
			catch (Exception error) { Fail("Harbor start failed: " + error.Message); }
			if (!initialized) { Finish(); yield break; }
			var start = runner.State;
			yield return Wait(() => bootstrap.ViewState == HarborViewState.Ready || bootstrap.Problem != null, 90);
			if (!_met || bootstrap.ViewState != HarborViewState.Ready) { Fail(bootstrap.Problem ?? "Actual harbor tiles did not become ready"); runner.EndRun(); Finish(); yield break; }
			_harbor["tiles_ready"] = true;
			_harbor["start_to_ready_s"] = Time.realtimeSinceStartupAsDouble - _scenarioStart;
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
