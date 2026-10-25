using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.Slice
{
	/// <summary>
	/// A scripted check of a built player, started by <c>-ferrysim-smoke &lt;folder&gt;</c> (<see cref="PlayerSmokeOptions"/>);
	/// scripts/release.sh runs it on the app installed from the disk image. It waits for the slice's own run to step for
	/// <see cref="NeutralSimulatedSeconds"/> (helm neutral, no device input), restarts the run from the scenario start with
	/// both levers at <see cref="DriveLever"/> and the helm amidships for the drive's simulated seconds, selects and
	/// captures the wheelhouse view and then the chase view while the run is live, ends the run so its recording closes,
	/// writes <see cref="ReportFileName"/> and quits: 0 when every check passed, 1 when one failed. The checks: a player
	/// read the vessel file from StreamingAssets; the landings loaded; the slice has its camera rig; the drive started
	/// clear of the faces and reached its simulated seconds; the vessel moved at least <see cref="MinimumDisplacementMetres"/>
	/// with a finite state; the drive's recording was written; both views were captured, each with more than
	/// <see cref="MinimumScreenshotColours"/> colours (a check against blank frames, not of what they show); and nothing
	/// logged an error, exception or assert while the run watched. On a pack scenario it also checks that a player read the
	/// pack from StreamingAssets and that the drawn water stood at the pack's water level at the vessel.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class PlayerSmokeRun : MonoBehaviour
	{
		public const string ReportFileName = "smoke.json";

		/// <summary>Both levers during the drive: astern with some throttle, which backs the boat off the landing it starts facing.</summary>
		public const double DriveLever = -0.3;

		public const double NeutralSimulatedSeconds = 2.0;

		public const double MinimumDisplacementMetres = 1.0;

		/// <summary>A blank or placeholder frame has a handful of colours; the rendered river, sky and panel have thousands.</summary>
		public const int MinimumScreenshotColours = 50;

		/// <summary>Wall-clock seconds for the scene to load and its own run to step.</summary>
		public const double StartupTimeoutSeconds = 120.0;

		private const int MaximumErrorMessages = 20;

		private const int ViewSettleFrames = 10;

		/// <summary>The views captured, in order: the wheelhouse with its panel, then the chase view of the vessel.</summary>
		private static readonly CameraRig.Mode[] Views = { CameraRig.Mode.Wheelhouse, CameraRig.Mode.Chase };

		private readonly object _logLock = new object();
		private readonly List<string> _errorMessages = new List<string>();
		private readonly List<string> _failures = new List<string>();
		private readonly List<string> _screenshots = new List<string>();
		private readonly List<string> _views = new List<string>();
		private int _errors;
		private int _warnings;
		private PlayerSmokeOptions _options;
		private Action<int> _quit;
		private Func<Camera, Texture2D> _capture;
		private bool _requirePackagedData;
		private string _recordingFolder;
		private PlayerSmokeReport _report;
		private double _wallStart;
		private int _frameStart;
		private bool _waitMet;

		/// <summary>The code the run quit with, or null while it runs.</summary>
		public int? ExitCode { get; private set; }

		/// <summary>The report as written, or null while the run is going.</summary>
		public PlayerSmokeReport Report { get; private set; }

		/// <summary>
		/// Sets the run up before its first frame. <paramref name="quit"/> receives the exit code (Application.Quit in a
		/// player). <paramref name="requirePackagedData"/> demands that the vessel file come from StreamingAssets, as it must
		/// in a player. <paramref name="capture"/> renders a camera to a readable texture; null captures the screen, UI
		/// included, once the frame is drawn. <paramref name="recordingFolder"/> overrides where the drive is recorded.
		/// </summary>
		public void Configure(PlayerSmokeOptions options, Action<int> quit, bool requirePackagedData, Func<Camera, Texture2D> capture = null, string recordingFolder = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_quit = quit ?? throw new ArgumentNullException(nameof(quit));
			_requirePackagedData = requirePackagedData;
			_capture = capture;
			_recordingFolder = recordingFolder;
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void StartFromCommandLine()
		{
			if (Application.isEditor)
			{
				return;
			}

			PlayerSmokeOptions options;
			try
			{
				options = PlayerSmokeOptions.Parse(Environment.GetCommandLineArgs());
			}
			catch (ArgumentException error)
			{
				Debug.LogError($"[FerrySim] Smoke run not started: {error.Message}");
				Application.Quit(2);
				return;
			}

			if (options == null)
			{
				return;
			}

			// A window started from a script may never get focus, and an unfocused player otherwise stops updating.
			Application.runInBackground = true;
			var host = new GameObject("FerrySim player smoke run");
			DontDestroyOnLoad(host);
			host.AddComponent<PlayerSmokeRun>().Configure(options, Application.Quit, requirePackagedData: true);
		}

		/// <summary>The number of distinct RGB colours in <paramref name="texture"/>.</summary>
		public static int DistinctColours(Texture2D texture)
		{
			var seen = new HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
			}

			return seen.Count;
		}

		private void OnEnable() => Application.logMessageReceivedThreaded += Record;

		private void OnDisable() => Application.logMessageReceivedThreaded -= Record;

		private void Record(string message, string stackTrace, LogType type)
		{
			lock (_logLock)
			{
				switch (type)
				{
					case LogType.Warning:
						_warnings++;
						break;
					case LogType.Error:
					case LogType.Exception:
					case LogType.Assert:
						_errors++;
						if (_errorMessages.Count < MaximumErrorMessages)
						{
							_errorMessages.Add($"{type}: {message}");
						}

						break;
				}
			}
		}

		private IEnumerator Start()
		{
			if (_options == null)
			{
				Debug.LogWarning("[FerrySim] PlayerSmokeRun needs Configure before its first frame; removing it.", this);
				Destroy(this);
				yield break;
			}

			_wallStart = Time.realtimeSinceStartupAsDouble;
			_frameStart = Time.frameCount;
			_report = NewReport();
			Directory.CreateDirectory(_options.OutputFolder);

			SliceBootstrap bootstrap = null;
			yield return Wait(() =>
			{
				if (bootstrap == null)
				{
					bootstrap = FindFirstObjectByType<SliceBootstrap>();
				}

				return bootstrap != null && bootstrap.Runner != null && (bootstrap.PlayerError != null || bootstrap.LoadError != null
					|| (bootstrap.Runner.HasTelemetry && bootstrap.Runner.Driver.SimulationTime >= NeutralSimulatedSeconds));
			}, StartupTimeoutSeconds);

			if (!_waitMet)
			{
				Fail(bootstrap == null
					? "the slice scene did not load (no SliceBootstrap)"
					: $"the slice's run did not step {NeutralSimulatedSeconds:0} simulated seconds within {StartupTimeoutSeconds:0} s");
				Finish();
				yield break;
			}

			var runner = bootstrap.Runner;
			CheckSlice(bootstrap, runner);
			if (bootstrap.PlayerError != null || bootstrap.LoadError != null)
			{
				Finish();
				yield break;
			}

			// The drive: a fresh run from the scenario start, both engines astern, helm amidships, recorded.
			var start = default(VesselState);
			var previousRecording = runner.LastRecordingPath;
			var restarted = false;
			try
			{
				if (_recordingFolder != null)
				{
					runner.RecordingDirectory = _recordingFolder;
				}

				runner.RecordRuns = true;
				runner.Initialize(runner.Scenario, new ConstantControls(new ControlInputs(DriveLever, DriveLever, 0.0)), runner.Parameters);
				start = runner.State;
				restarted = true;
			}
			catch (Exception error)
			{
				Fail($"restarting the run for the drive threw {error.GetType().Name}: {error.Message}");
			}

			if (!restarted)
			{
				Finish();
				yield break;
			}

			_report.start_refusal = bootstrap.StartRefusal;
			if (bootstrap.StartRefusal != null)
			{
				Fail($"the drive's start was refused: {bootstrap.StartRefusal}");
			}

			var maxSpeed = 0.0;
			var driveTimeout = Math.Max(60.0, _options.DriveSeconds * 6.0);
			yield return Wait(() => !runner.IsRunActive || (runner.HasTelemetry && runner.Driver.SimulationTime >= _options.DriveSeconds), driveTimeout, () =>
			{
				if (runner.HasTelemetry)
				{
					maxSpeed = Math.Max(maxSpeed, runner.Telemetry.SpeedOverGround);
				}
			});

			var reached = _waitMet && runner.IsRunActive;
			var end = runner.State;
			CheckEnvironment(bootstrap, runner);
			_report.drive_simulated_s = runner.Driver != null ? runner.Driver.SimulationTime : 0.0;
			_report.max_speed_over_ground_mps = maxSpeed;
			_report.state_finite = end.IsFinite();
			_report.displacement_m = Math.Sqrt((end.North - start.North) * (end.North - start.North) + (end.East - start.East) * (end.East - start.East));
			if (!reached)
			{
				Fail(runner.IsRunActive
					? $"the drive did not reach {_options.DriveSeconds:0.#} simulated seconds within {driveTimeout:0} s"
					: $"the drive's run ended after {_report.drive_simulated_s:0.0} simulated seconds");
			}

			if (!_report.state_finite)
			{
				Fail("the vessel state is not finite");
			}

			if (!(_report.displacement_m >= MinimumDisplacementMetres))
			{
				Fail($"the vessel moved {_report.displacement_m:0.00} m under power, less than {MinimumDisplacementMetres:0.#} m");
			}

			// The views while the run is live, so the instruments show data: each selected and settled before its capture.
			var rig = bootstrap.CameraRig;
			if (rig != null)
			{
				var camera = rig.GetComponent<Camera>();
				foreach (var view in Views)
				{
					rig.CurrentMode = view;
					for (var frame = 0; frame < ViewSettleFrames; frame++)
					{
						yield return null;
					}

					yield return Capture(camera, view);
				}
			}

			runner.EndRun();
			CheckRecording(runner, previousRecording);
			Finish();
		}

		/// <summary>Frames until <paramref name="condition"/> holds or the wall-clock timeout passes; <see cref="_waitMet"/> says which.</summary>
		private IEnumerator Wait(Func<bool> condition, double timeoutSeconds, Action eachFrame = null)
		{
			var deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
			while (true)
			{
				bool met;
				try
				{
					met = condition();
				}
				catch (Exception error)
				{
					Fail($"watching the run threw {error.GetType().Name}: {error.Message}");
					_waitMet = false;
					yield break;
				}

				if (met)
				{
					_waitMet = true;
					yield break;
				}

				if (Time.realtimeSinceStartupAsDouble > deadline)
				{
					_waitMet = false;
					yield break;
				}

				eachFrame?.Invoke();
				yield return null;
			}
		}

		private void CheckSlice(SliceBootstrap bootstrap, VesselSimulationRunner runner)
		{
			_report.player_error = bootstrap.PlayerError;
			_report.landing_error = bootstrap.LoadError;
			_report.landing_segments = bootstrap.Landings != null ? bootstrap.Landings.Segments.Count : 0;
			_report.neutral_simulated_s = runner.HasTelemetry ? runner.Driver.SimulationTime : 0.0;
			var resolved = SimPaths.Resolve(runner.Scenario != null ? runner.Scenario.vesselFile : bootstrap.VesselFile);
			_report.vessel_file = resolved;
			var streaming = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			_report.vessel_file_packaged = !string.IsNullOrEmpty(resolved) && File.Exists(resolved)
				&& Path.GetFullPath(resolved).StartsWith(streaming, StringComparison.OrdinalIgnoreCase);
			if (bootstrap.PlayerError != null)
			{
				Fail($"the slice refused to run: {bootstrap.PlayerError}");
			}

			if (bootstrap.LoadError != null)
			{
				Fail($"the landings did not load: {bootstrap.LoadError}");
			}
			else if (_report.landing_segments == 0)
			{
				Fail("no landing faces were loaded");
			}

			if (_requirePackagedData && !_report.vessel_file_packaged)
			{
				Fail($"the vessel file was not read from StreamingAssets ({resolved})");
			}

			if (bootstrap.CameraRig == null)
			{
				Fail("the slice has no camera rig, so its views cannot be captured");
			}
		}

		private void CheckEnvironment(SliceBootstrap bootstrap, VesselSimulationRunner runner)
		{
			var environment = runner.RunEnvironment;
			if (environment == null)
			{
				return;
			}

			_report.environment = environment.Kind.ToString().ToLowerInvariant();
			var water = bootstrap.Tide != null ? FindFirstObjectByType<WaterSurface>() : null;
			_report.drawn_water_level_navd88_m = water != null ? water.WaterLevelNavd88 : 0.0;
			if (!environment.HasTide)
			{
				return;
			}

			var readout = runner.Environment;
			_report.environment_pack = environment.Pack.Name;
			_report.environment_digest = environment.Pack.Digest;
			_report.environment_folder = environment.PackFolder;
			_report.water_level_navd88_m = readout.WaterLevelNavd88;
			var streaming = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			_report.environment_packaged = !string.IsNullOrEmpty(environment.PackFolder)
				&& Path.GetFullPath(environment.PackFolder).StartsWith(streaming, StringComparison.OrdinalIgnoreCase);
			if (_requirePackagedData && !_report.environment_packaged)
			{
				Fail($"the environment pack {environment.Pack.Name} was not read from StreamingAssets ({environment.PackFolder})");
			}

			if (water != null && !(Math.Abs(_report.drawn_water_level_navd88_m - readout.WaterLevelNavd88) <= 1e-6))
			{
				Fail($"the drawn water stands at {_report.drawn_water_level_navd88_m:0.000} m NAVD88, not at the pack's {readout.WaterLevelNavd88:0.000} m at the vessel");
			}
		}

		private IEnumerator Capture(Camera camera, CameraRig.Mode view)
		{
			if (_capture == null)
			{
				// The screen as the player shows it, UI included, once the frame is drawn.
				yield return new WaitForEndOfFrame();
			}

			var fileName = $"smoke-{view.ToString().ToLowerInvariant()}.png";
			Texture2D texture = null;
			try
			{
				texture = _capture != null ? _capture(camera) : ScreenCapture.CaptureScreenshotAsTexture();
				var path = Path.Combine(_options.OutputFolder, fileName);
				File.WriteAllBytes(path, texture.EncodeToPNG());
				var colours = DistinctColours(texture);
				_report.min_screenshot_colours = _screenshots.Count == 0 ? colours : Math.Min(_report.min_screenshot_colours, colours);
				_screenshots.Add(path);
				_views.Add(view.ToString());
				if (colours <= MinimumScreenshotColours)
				{
					Fail($"{fileName} has {colours} colours; a rendered view has more than {MinimumScreenshotColours}");
				}
			}
			catch (Exception error)
			{
				Fail($"capturing {fileName} threw {error.GetType().Name}: {error.Message}");
			}
			finally
			{
				if (texture != null)
				{
					Destroy(texture);
				}
			}
		}

		private void CheckRecording(VesselSimulationRunner runner, string previousRecording)
		{
			_report.recording = runner.LastRecordingPath;
			_report.recording_error = runner.RecordingError;
			if (runner.RecordingError != null)
			{
				Fail($"the drive's recording failed: {runner.RecordingError}");
				return;
			}

			var path = runner.LastRecordingPath;
			if (string.IsNullOrEmpty(path) || path == previousRecording || !File.Exists(path))
			{
				Fail("the drive left no recording");
				return;
			}

			_report.recording_bytes = new FileInfo(path).Length;
			if (_report.recording_bytes == 0)
			{
				Fail($"the drive's recording {path} is empty");
			}
		}

		private PlayerSmokeReport NewReport()
		{
			var build = Application.isEditor
				? "editor"
				: PlayerBuildInfo.TryLoad(Application.streamingAssetsPath, out var info, out var problem) ? info.Describe() : "no build record: " + problem;
			return new PlayerSmokeReport
			{
				product = Application.productName,
				version = Application.version,
				build = build,
				unity_version = Application.unityVersion,
				platform = Application.platform.ToString(),
				operating_system = SystemInfo.operatingSystem,
				processor = SystemInfo.processorType,
				graphics_device = $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVersion})",
				streaming_assets = Application.streamingAssetsPath,
				persistent_data = Application.persistentDataPath,
				drive_lever = DriveLever,
			};
		}

		private void Fail(string reason) => _failures.Add(reason);

		private void Finish()
		{
			lock (_logLock)
			{
				_report.errors = _errors;
				_report.warnings = _warnings;
				_report.error_messages = _errorMessages.ToArray();
			}

			if (_report.errors > 0)
			{
				Fail($"{_report.errors} error(s) logged, the first: {_report.error_messages[0]}");
			}

			// Only a run that got as far as the drive owes its views.
			if (_report.drive_simulated_s > 0.0 && _views.Count != Views.Length)
			{
				Fail($"captured {_views.Count} of the {Views.Length} views ({string.Join(", ", Views)})");
			}

			_report.screenshot_views = _views.ToArray();
			_report.frames = Time.frameCount - _frameStart;
			_report.wall_s = Time.realtimeSinceStartupAsDouble - _wallStart;
			_report.average_fps = _report.wall_s > 0.0 ? _report.frames / _report.wall_s : 0.0;
			_report.screenshots = _screenshots.ToArray();
			_report.failures = _failures.ToArray();
			_report.passed = _failures.Count == 0;
			var path = Path.Combine(_options.OutputFolder, ReportFileName);
			try
			{
				File.WriteAllText(path, JsonUtility.ToJson(_report, true) + "\n");
			}
			catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
			{
				_report.passed = false;
				Debug.LogWarning($"[FerrySim] The smoke run could not write {path}: {error.Message}");
			}

			Report = _report;
			ExitCode = _report.passed ? 0 : 1;
			var outcome = _report.passed
				? $"passed: moved {_report.displacement_m:0.0} m in {_report.drive_simulated_s:0.0} simulated seconds"
				: "failed: " + string.Join("; ", _report.failures);
			Debug.Log($"[FerrySim] Smoke run {outcome}. Report: {path}");
			_quit(ExitCode.Value);
		}
	}
}
