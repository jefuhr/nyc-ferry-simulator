using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CesiumForUnity;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// The Windows probe for decision 0004: does a player load the harbor's local 3D Tiles over file:// from its
	/// StreamingAssets? On start it checks the packaged files against harbor3d/payload.json, points the scene's
	/// Cesium3DTileset at the file:// URL of StreamingAssets/harbor3d/tileset.json (no Cesium ion, no token, physics meshes
	/// off, tile cache capped at <see cref="MaximumCachedBytes"/>), waits for the first view to load, flies
	/// <see cref="ProbePath"/>, waits again until Cesium reports the view loaded or <see cref="WaitTimeoutSeconds"/> pass,
	/// takes a screenshot at both ends, writes probe.json (<see cref="ProbeReport"/>) and quits: 0 when every check passed,
	/// 1 when one failed, 2 when the command line was unusable.
	/// </summary>
	[DefaultExecutionOrder(-100)]
	[DisallowMultipleComponent]
	public sealed class TilesetProbe : MonoBehaviour
	{
		public const string ReportFileName = "probe.json";
		public const double WaitTimeoutSeconds = 60.0;
		public const long MaximumCachedBytes = 256L * 1024 * 1024;

		/// <summary>Cesium's load progress must read 100% this many frames running, with a tile loaded, to count as loaded.</summary>
		public const int LoadedFrames = 10;

		private static readonly Regex Stats = new Regex(
			@"Visited (\d+), Culled Visited (\d+), Rendered (\d+), Culled (\d+), Max Depth Visited (\d+), Loading-Worker (\d+), Loading-Main (\d+) Total Tiles Resident (\d+)",
			RegexOptions.CultureInvariant);

		// What Cesium logs about loading: a status code for a request, a failed load or parse, glTF trouble.
		private static readonly Regex CesiumMessage = new Regex(@"status code|tile|tileset|gltf|glb|cesium|failed to load|unable to|uri|url",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
		private static readonly Regex TileFailure = new Regex(@"status code \d+ for tile content|failed to load|tile content|error loading|gltf",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		[SerializeField] private Cesium3DTileset tileset;
		[SerializeField] private CesiumGlobeAnchor cameraAnchor;
		[SerializeField] private Shader tileShader;

		private readonly object _logLock = new object();
		private readonly HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);
		private readonly List<string> _cesiumErrors = new List<string>();
		private readonly List<string> _errorMessages = new List<string>();
		private readonly List<double> _frameMs = new List<double>();
		private ProbeReport _report;
		private ProbeOptions _options;
		private Action<int> _quit;
		private ProfilerRecorder _systemUsed;
		private string _lastStats;
		private int _errors;
		private int _warnings;
		private int _tileFailures;
		private int _tilesetFailures;
		private double _flightStart = -1;
		private double _wallStart;

		public Cesium3DTileset Tileset => tileset;

		public CesiumGlobeAnchor CameraAnchor => cameraAnchor;

		public Shader TileShader => tileShader;

		/// <summary>The report as written, or null while the run is going.</summary>
		public ProbeReport Report { get; private set; }

		/// <summary>The code the run quit with, or null while it runs.</summary>
		public int? ExitCode { get; private set; }

		/// <summary>Wires the scene's objects (the scene builder does this once, for the saved scene).</summary>
		public void Wire(Cesium3DTileset harborTileset, CesiumGlobeAnchor anchor, Shader shader)
		{
			tileset = harborTileset;
			cameraAnchor = anchor;
			tileShader = shader;
		}

		/// <summary>Sets the run up before its first frame; <paramref name="quit"/> receives the exit code.</summary>
		public void Configure(ProbeOptions options, Action<int> quit)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_quit = quit ?? throw new ArgumentNullException(nameof(quit));
		}

		private void Awake()
		{
			if (_options != null)
			{
				return;
			}

			try
			{
				Configure(ProbeOptions.Parse(Environment.GetCommandLineArgs(), ProbeOptions.DefaultFolder(Application.dataPath, Application.platform)), Quit);
			}
			catch (ArgumentException error)
			{
				Debug.LogError($"[FerrySim] Tiles3D probe not started: {error.Message}");
				ExitCode = 2;
				Quit(2);
				enabled = false;
			}
		}

		private void OnEnable()
		{
			Application.logMessageReceivedThreaded += Record;
			Cesium3DTileset.OnCesium3DTilesetLoadFailure += OnTilesetFailure;
			_systemUsed = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
		}

		private void OnDisable()
		{
			Application.logMessageReceivedThreaded -= Record;
			Cesium3DTileset.OnCesium3DTilesetLoadFailure -= OnTilesetFailure;
			if (tileset != null)
			{
				tileset.OnTileGameObjectCreated -= OnTileCreated;
			}

			_systemUsed.Dispose();
		}

		private IEnumerator Start()
		{
			if (_options == null)
			{
				yield break;
			}

			_wallStart = Time.realtimeSinceStartupAsDouble;
			// A window started from a script may never get focus, and an unfocused player otherwise stops updating.
			Application.runInBackground = true;
			// Frame times measure the work, not the display: vSync off and no cap, as the world-streaming spike measured.
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			_report = Describe();
			Directory.CreateDirectory(_options.OutputFolder);
			CheckPayload();
			if (tileset == null || cameraAnchor == null)
			{
				Finish("the probe scene lost its tileset or camera");
				yield break;
			}

			PlaceCamera(ProbePath.Sample(0.0));
			tileset.tilesetSource = CesiumDataSource.FromUrl;
			tileset.url = _report.tileset_url;
			tileset.createPhysicsMeshes = false;
			tileset.showCreditsOnScreen = false;
			tileset.maximumScreenSpaceError = 16f;
			tileset.maximumCachedBytes = MaximumCachedBytes;
			_report.maximum_cached_bytes = tileset.maximumCachedBytes;
			if (tileShader != null && tileShader.isSupported)
			{
				tileset.opaqueMaterial = new Material(tileShader) { name = "Tiles3D probe" };
			}

			tileset.OnTileGameObjectCreated += OnTileCreated;
			tileset.gameObject.SetActive(true);
			Debug.Log($"[FerrySim] Tiles3D probe: loading {tileset.url}");

			var started = Time.realtimeSinceStartupAsDouble;
			var met = false;
			yield return WaitForLoaded(value => met = value);
			_report.first_view_loaded = met;
			_report.first_view_s = Time.realtimeSinceStartupAsDouble - started;
			yield return Capture("start");

			_flightStart = Time.realtimeSinceStartupAsDouble;
			while (Time.realtimeSinceStartupAsDouble - _flightStart < ProbePath.DurationSeconds)
			{
				yield return null;
				_frameMs.Add(Time.unscaledDeltaTime * 1000.0);
				SampleMemory();
			}

			_report.flight_s = Time.realtimeSinceStartupAsDouble - _flightStart;
			_report.flight_m = ProbePath.LengthMetres();
			_flightStart = -1;
			PlaceCamera(ProbePath.Sample(ProbePath.DurationSeconds));

			tileset.logSelectionStats = true;
			started = Time.realtimeSinceStartupAsDouble;
			yield return WaitForLoaded(value => met = value);
			_report.final_view_loaded = met;
			_report.final_wait_s = Time.realtimeSinceStartupAsDouble - started;
			yield return Capture("end");
			Finish(null);
		}

		private void Update()
		{
			if (_flightStart >= 0)
			{
				PlaceCamera(ProbePath.Sample(Time.realtimeSinceStartupAsDouble - _flightStart));
			}
		}

		private void PlaceCamera(ProbePath.Waypoint pose)
		{
			var position = ProbePath.Geodetic(pose);
			cameraAnchor.longitudeLatitudeHeight = new double3(position.LongitudeDegrees, position.LatitudeDegrees, pose.Height);
			// East-up-north is Unity's (x, y, z): the heading turns about up, clockwise seen from above; pitch down is a
			// positive turn about x. ZXY is Unity's own Euler order, so this is Quaternion.Euler(-pitch, heading, 0).
			cameraAnchor.rotationEastUpNorth = quaternion.Euler(math.radians((float)-pose.PitchDegrees), math.radians((float)pose.HeadingDegrees), 0f,
				math.RotationOrder.ZXY);
		}

		private IEnumerator WaitForLoaded(Action<bool> result)
		{
			var deadline = Time.realtimeSinceStartupAsDouble + WaitTimeoutSeconds;
			var frames = 0;
			while (Time.realtimeSinceStartupAsDouble < deadline)
			{
				yield return null;
				SampleMemory();
				var progress = tileset.ComputeLoadProgress();
				_report.load_progress_final = progress;
				int loaded;
				lock (_logLock)
				{
					loaded = _loaded.Count;
				}

				frames = progress >= 100f && loaded > 0 ? frames + 1 : 0;
				if (frames >= LoadedFrames)
				{
					result(true);
					yield break;
				}
			}

			result(false);
		}

		private IEnumerator Capture(string name)
		{
			yield return new WaitForEndOfFrame();
			Texture2D texture = null;
			try
			{
				texture = ScreenCapture.CaptureScreenshotAsTexture();
				var fileName = $"probe-{name}.png";
				File.WriteAllBytes(Path.Combine(_options.OutputFolder, fileName), texture.EncodeToPNG());
				_report.screenshots = _report.screenshots.Append(fileName).ToArray();
				_report.screenshot_colours = _report.screenshot_colours.Append(DistinctColours(texture)).ToArray();
			}
			catch (Exception error)
			{
				Debug.LogError($"[FerrySim] Tiles3D probe could not capture the {name} view: {error.Message}");
			}
			finally
			{
				if (texture != null)
				{
					Destroy(texture);
				}
			}
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

		private ProbeReport Describe()
		{
			var report = new ProbeReport
			{
				product = Application.productName,
				unity_version = Application.unityVersion,
				platform = Application.platform.ToString(),
				operating_system = SystemInfo.operatingSystem,
				processor = SystemInfo.processorType,
				system_memory_mb = SystemInfo.systemMemorySize,
				graphics_device = SystemInfo.graphicsDeviceName,
				graphics_api = SystemInfo.graphicsDeviceType.ToString(),
				development_build = Debug.isDebugBuild,
				streaming_assets = Application.streamingAssetsPath,
				wait_timeout_s = WaitTimeoutSeconds,
				build = "editor",
			};
			var buildInfo = Path.Combine(Application.streamingAssetsPath, "build-info.json");
			if (File.Exists(buildInfo))
			{
				var info = JsonUtility.FromJson<BuildInfo>(File.ReadAllText(buildInfo));
				report.build = $"{info.version} at {info.commit}{(info.dirty ? " (dirty)" : string.Empty)}, built {info.built_utc}";
			}

			report.tileset_path = TilesetPath(out _);
			report.tileset_url = TilesetUrl.FromAbsolutePath(report.tileset_path);
			try
			{
				report.tileset_url_system_uri = new Uri(report.tileset_path).AbsoluteUri;
			}
			catch (UriFormatException error)
			{
				report.tileset_url_system_uri = "System.Uri refused the path: " + error.Message;
			}

			return report;
		}

		/// <summary>The packaged tileset in a player; in the editor, the pipeline's output under the repository.</summary>
		private static string TilesetPath(out string manifestPath)
		{
			var packaged = TilesetUrl.PathIn(Application.streamingAssetsPath);
			if (File.Exists(packaged) || !Application.isEditor)
			{
				manifestPath = Path.Combine(Path.GetDirectoryName(packaged) ?? string.Empty, TilesetPayloadManifest.FileName);
				return Path.GetFullPath(packaged);
			}

			var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "processed", "harbor", "3dtiles"));
			manifestPath = Path.Combine(folder, "index.json");
			return Path.Combine(folder, TilesetUrl.FileName);
		}

		private void CheckPayload()
		{
			TilesetPath(out var manifestPath);
			if (!File.Exists(manifestPath))
			{
				_report.payload_problems = new[] { "missing: " + manifestPath };
				return;
			}

			try
			{
				var text = File.ReadAllText(manifestPath);
				// In the editor the pipeline's index.json stands in: its files list has the same fields.
				var manifest = Path.GetFileName(manifestPath) == TilesetPayloadManifest.FileName
					? JsonUtility.FromJson<TilesetPayloadManifest>(text)
					: new TilesetPayloadManifest { scope = "editor (the pipeline's index.json)", files = JsonUtility.FromJson<IndexFiles>(text).files };
				_report.payload_scope = manifest.scope;
				_report.payload_tiles = manifest.tiles?.Length ?? 0;
				_report.payload_files = manifest.files?.Length ?? 0;
				_report.payload_bytes = manifest.TotalBytes;
				var problems = manifest.Check(Path.GetDirectoryName(manifestPath), out var missing, out var mismatched);
				_report.payload_missing = missing;
				_report.payload_mismatched = mismatched;
				_report.payload_problems = problems.Take(20).ToArray();
			}
			catch (Exception error)
			{
				_report.payload_problems = new[] { $"{manifestPath} could not be read: {error.Message}" };
			}
		}

		private void OnTileCreated(GameObject tile)
		{
			lock (_logLock)
			{
				_loaded.Add(tile.name);
			}

			foreach (var renderer in tile.GetComponentsInChildren<MeshRenderer>(true))
			{
				renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				renderer.receiveShadows = false;
			}
		}

		private void OnTilesetFailure(Cesium3DTilesetLoadFailureDetails details)
		{
			if (details.tileset != tileset)
			{
				return;
			}

			lock (_logLock)
			{
				_tilesetFailures++;
				_cesiumErrors.Add($"tileset load failure {details.type} (HTTP {details.httpStatusCode}): {details.message}");
			}
		}

		private void Record(string message, string stackTrace, LogType type)
		{
			lock (_logLock)
			{
				if (type == LogType.Log)
				{
					var match = Stats.Match(message);
					if (match.Success)
					{
						_lastStats = match.Value;
					}

					return;
				}

				var error = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
				if (error)
				{
					_errors++;
					if (_errorMessages.Count < 20)
					{
						_errorMessages.Add($"{type}: {message}");
					}
				}
				else
				{
					_warnings++;
				}

				if (CesiumMessage.IsMatch(message) && !message.StartsWith("[FerrySim]", StringComparison.Ordinal))
				{
					if (_cesiumErrors.Count < 50)
					{
						_cesiumErrors.Add($"{type}: {message}");
					}

					if (TileFailure.IsMatch(message))
					{
						_tileFailures++;
					}
				}
			}
		}

		private void SampleMemory()
		{
			if (!_systemUsed.Valid)
			{
				return;
			}

			var used = _systemUsed.LastValue;
			_report.system_used_memory_peak_bytes = Math.Max(_report.system_used_memory_peak_bytes, used);
			_report.system_used_memory_final_bytes = used;
		}

		private void Finish(string failure)
		{
			lock (_logLock)
			{
				_report.tiles_loaded = _loaded.Count;
				_report.tiles_loaded_l0 = _loaded.Count(n => n.EndsWith("_l0.glb", StringComparison.Ordinal));
				_report.tiles_loaded_l1 = _loaded.Count(n => n.EndsWith("_l1.glb", StringComparison.Ordinal));
				_report.tiles_loaded_l2 = _loaded.Count(n => n.EndsWith("l2.glb", StringComparison.Ordinal));
				_report.loaded_examples = _loaded.OrderBy(n => n, StringComparer.Ordinal).Take(3).ToArray();
				_report.tiles_failed = _tileFailures;
				_report.tileset_load_failures = _tilesetFailures;
				_report.cesium_errors = _cesiumErrors.ToArray();
				_report.errors = _errors;
				_report.warnings = _warnings;
				_report.error_messages = _errorMessages.ToArray();
				_report.selection_stats_final = _lastStats;
			}

			var stats = _lastStats == null ? null : Stats.Match(_lastStats);
			if (stats != null && stats.Success)
			{
				_report.tiles_rendered_final = int.Parse(stats.Groups[3].Value);
				_report.tiles_loading_final = int.Parse(stats.Groups[6].Value) + int.Parse(stats.Groups[7].Value);
				_report.tiles_resident_final = int.Parse(stats.Groups[8].Value);
			}

			_report.tiles_requested = _report.tiles_loaded + _report.tiles_failed + _report.tiles_loading_final;
			var sorted = _frameMs.OrderBy(v => v).ToList();
			_report.frames = sorted.Count;
			_report.frame_ms_p50 = ProbeReport.Percentile(sorted, 0.50);
			_report.frame_ms_p95 = ProbeReport.Percentile(sorted, 0.95);
			_report.frame_ms_p99 = ProbeReport.Percentile(sorted, 0.99);
			_report.frame_ms_max = sorted.Count > 0 ? sorted[sorted.Count - 1] : 0.0;
			try
			{
				using (var process = System.Diagnostics.Process.GetCurrentProcess())
				{
					process.Refresh();
					_report.resident_memory_bytes = process.WorkingSet64;
				}
			}
			catch (Exception error)
			{
				_report.memory_note = "the OS resident set is not readable here: " + error.GetType().Name;
			}

			if (string.IsNullOrEmpty(_report.memory_note))
			{
				_report.memory_note = "system_used_memory is Unity's System Used Memory (the process footprint, cesium-native included); "
					+ "resident_memory is the process working set the runtime reports (0 if it cannot read it)";
			}

			_report.wall_s = Time.realtimeSinceStartupAsDouble - _wallStart;
			var failures = ProbeReport.Verdict(_report).ToList();
			if (failure != null)
			{
				failures.Insert(0, failure);
			}

			_report.failures = failures.ToArray();
			_report.passed = failures.Count == 0;
			Report = _report;
			var path = Path.Combine(_options.OutputFolder, ReportFileName);
			File.WriteAllText(path, JsonUtility.ToJson(_report, true) + "\n");
			Debug.Log($"[FerrySim] Tiles3D probe {(_report.passed ? "passed" : "FAILED")}: {_report.tiles_loaded} tiles loaded "
				+ $"({_report.tiles_loaded_l0} L0, {_report.tiles_loaded_l1} L1, {_report.tiles_loaded_l2} L2), {_report.tiles_failed} failed; report {path}"
				+ (_report.passed ? string.Empty : "; " + string.Join("; ", _report.failures)));
			ExitCode = _report.passed ? 0 : 1;
			_quit(ExitCode.Value);
		}

		private static void Quit(int code)
		{
#if UNITY_EDITOR
			UnityEditor.EditorApplication.isPlaying = false;
#else
			Application.Quit(code);
#endif
		}

		[Serializable]
		private sealed class IndexFiles
		{
			public PayloadFile[] files = Array.Empty<PayloadFile>();
		}

		[Serializable]
		private sealed class BuildInfo
		{
			public string version = string.Empty;
			public string commit = string.Empty;
			public bool dirty = false;
			public string built_utc = string.Empty;
		}
	}
}
