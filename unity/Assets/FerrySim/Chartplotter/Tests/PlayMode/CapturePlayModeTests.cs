using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Serialization;
using FerrySim.Core.World.Chart;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// Visual evidence on the real data: the checked-in harbor scene with its real services and the real chart pack,
	/// the plotter connected to the runner. Pier 11 at 0.5 NM north-up by day and by night and in fog; the East River at
	/// 2 NM course-up with the day's traffic; the Upper Bay at 4 NM with the CPA alarm on a synthetic target staged among
	/// the real traffic. Also the real-data timings: the tessellation, the static render at each span, the steady-state
	/// frame cost against a control run and the refresh's allocation samples.
	/// </summary>
	public class CapturePlayModeTests
	{
		private const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		private const string TrafficDay = "2025-07-16";
		private const int BusiestLocalMinute = 738;
		private const string Pier11StopId = "87";
		private const int Width = 1280, Height = 720;

		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

		private static string Folder => Path.Combine(Repo, "artifacts/chartplotter");

		private RenderTexture _target;
		private PanelSettings _settings;
		private GameObject _host;
		private ChartplotterDisplay _display;
		private readonly StringBuilder _index = new StringBuilder("capture,scenario,span_nm,orientation,palette,north_m,east_m,heading_deg,start_utc,visibility_m,targets_present,targets_drawn,alarm,soundings_shown,labels_refused,static_render_ms\n");
		private readonly StringBuilder _evidence = new StringBuilder();

		[UnityTearDown]
		public IEnumerator Unload()
		{
			if (_host != null) Object.Destroy(_host);
			if (_settings != null) Object.Destroy(_settings);
			if (_target != null) { _target.Release(); Object.Destroy(_target); }
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				foreach (var root in scene.GetRootGameObjects())
					foreach (var runner in root.GetComponentsInChildren<VesselSimulationRunner>(true))
						runner.EndRun();
				yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		[UnityTest, Explicit("Needs the copied harbor data and chart; run by exact test name for visual review."), Category("HarborRealData")]
		public IEnumerator Pier11_EastRiver_UpperBay_Fog_AndRealDataTimings()
		{
			var harbor = Path.Combine(Repo, "data", "processed", "harbor");
			foreach (var folder in new[] { "runtime", "env/days", "3dtiles", "3dtiles-buildings", "traffic/" + TrafficDay, "chart" })
			{
				if (!Directory.Exists(Path.Combine(harbor, folder))) Assert.Ignore("The harbor data is not copied: missing " + Path.Combine(harbor, folder) + ".");
			}

			Directory.CreateDirectory(Folder);
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			Assert.Fail("This test loads the checked-in editor scene.");
			yield break;
#endif
			yield return null;
			var bootstrap = Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(bootstrap, Is.Not.Null, "Harbor.unity has one HarborBootstrap");
			Assert.That(bootstrap.Problem, Is.Null, "the real services connected");
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			bootstrap.WheelhouseUI.View.PickerVisible = false;

			// The chart pack, loaded once against the scene's verified runtime pack.
			var loadWatch = System.Diagnostics.Stopwatch.StartNew();
			var before = GC.GetTotalMemory(true);
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), services.Pack);
			var afterLoad = GC.GetTotalMemory(true);
			loadWatch.Stop();
			var loadMilliseconds = loadWatch.Elapsed.TotalMilliseconds;

			_target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_target.Create();
			_settings = WheelhousePanelSettings.Create(_target, new Color(0.36f, 0.45f, 0.54f, 1f));
			_host = new GameObject("Chartplotter (capture)");
			_host.SetActive(false);
			_display = _host.AddComponent<ChartplotterDisplay>();
			_display.Configure(_settings);
			_display.SetPlacement(new Rect(8, 8, 1264, 704));
			var synthetic = new SyntheticTargets();
			var realTargets = new TrafficRunTargets(runner);
			_display.Connect(runner, chart, services.Pack, new CompositeTargets(realTargets, synthetic));
			_display.DaylightSource = bootstrap.Context.Sun;
			_host.SetActive(true);
			yield return null;
			var afterBuild = GC.GetTotalMemory(true);
			var tessellation = _display.Chart;
			_evidence.Append("{\n");
			_evidence.AppendFormat(CultureInfo.InvariantCulture, "\t\"chart_load_ms\": {0:F1},\n\t\"chart_managed_bytes\": {1},\n\t\"tessellation_ms\": {2:F1},\n\t\"tessellation_managed_bytes\": {3},\n\t\"triangles\": {4},\n\t\"line_vertices\": {5},\n\t\"soundings\": {6},\n\t\"aids\": {7},\n\t\"hazards\": {8},\n",
				loadMilliseconds, afterLoad - before, tessellation.BuildMilliseconds, afterBuild - afterLoad, tessellation.TriangleCount, tessellation.VertexCount, tessellation.Soundings.Count, tessellation.Aids.Count, tessellation.Hazards.Count);

			var catalog = services.HarborCatalog;
			var vessel = catalog.Vessels[0].Id;
			try
			{
				// Pier 11 by day: a catalog start on the traffic day at its busiest minute.
				Assert.That(catalog.TryCreate(new ScenarioChoice(Pier11StopId, TrafficDay, BusiestLocalMinute, vessel, traffic: true), out var day, out var problem), Is.True, problem);
				yield return Start(bootstrap, day, "pier-11-day", 2);
				yield return Capture("pier-11-0.5nm-north-up-day", day, 0.5, ChartOrientation.NorthUp, null);
				Assert.That(_display.View.Palette, Is.EqualTo(ChartPaletteKind.Day), "the daylight source gave the day palette");
				yield return RealDataTimings(runner, bootstrap, day);

				// Pier 11 at night: 03:00 local; the daylight source selects the night palette.
				Assert.That(catalog.TryCreate(new ScenarioChoice(Pier11StopId, TrafficDay, 3 * 60, vessel, traffic: true), out var night, out problem), Is.True, problem);
				yield return Start(bootstrap, night, "pier-11-night", 2);
				yield return Capture("pier-11-0.5nm-north-up-night", night, 0.5, ChartOrientation.NorthUp, null);
				Assert.That(_display.View.Palette, Is.EqualTo(ChartPaletteKind.Night), "the daylight source gave the night palette");

				// Pier 11 in fog: a quarter-mile visibility, the ring drawn.
				Assert.That(catalog.TryCreate(new ScenarioChoice(Pier11StopId, TrafficDay, BusiestLocalMinute, vessel, traffic: true, visibilityMetres: 463), out var fog, out problem), Is.True, problem);
				yield return Start(bootstrap, fog, "pier-11-fog", 2);
				_display.View.VisibilityMetres = fog.visibilityMetres;
				yield return Capture("pier-11-0.5nm-fog", fog, 0.5, ChartOrientation.NorthUp, null);
				_display.View.VisibilityMetres = 0;

				// The East River at 2 NM course-up, under way north-east below the Brooklyn Bridge with the day's traffic.
				var eastRiver = FixtureStart("East River viewpoint", 520, 1420, 45, "2025-07-16T16:18:00Z", true, 6);
				yield return Start(bootstrap, eastRiver, "east-river", 150);
				yield return Capture("east-river-2nm-course-up", eastRiver, 2.0, ChartOrientation.CourseUp, null);
				Assert.That(_display.View.Ranking.Considered, Is.GreaterThan(0), "the day's traffic is present");

				// The Upper Bay at 4 NM, off St. George, with a synthetic target staged head-on among the real traffic.
				var upperBay = FixtureStart("Upper Bay viewpoint", -5850, -4700, 246, "2025-07-16T16:18:00Z", true, 8);
				yield return Start(bootstrap, upperBay, "upper-bay", 2);
				var state = runner.State;
				var ahead = 1200.0;
				var ownSpeed = Units.KnotsToMetresPerSecond(8);
				var bearing = state.Heading;
				var targetNorth = state.North + ahead * Math.Cos(bearing) - 50.0 * Math.Sin(bearing);
				var targetEast = state.East + ahead * Math.Sin(bearing) + 50.0 * Math.Cos(bearing);
				// Reciprocal course at 8 kn: closing at 16 kn, CPA about 50 m, TCPA about 146 s. Its track starts at the run's time.
				synthetic.Add("SYNTHETIC TUG", 999999999, targetNorth + ownSpeed * Math.Cos(bearing) * state.Time, targetEast + ownSpeed * Math.Sin(bearing) * state.Time,
					-ownSpeed * Math.Cos(bearing), -ownSpeed * Math.Sin(bearing), 28, 9);
				yield return Capture("upper-bay-4nm-cpa-alarm", upperBay, 4.0, ChartOrientation.NorthUp, null);
				Assert.That(_display.View.Alarm, Is.True, "the staged target raised the CPA alarm");
				Assert.That(_display.View.List.RowName(0), Is.EqualTo("SYNTHETIC TUG"));
				synthetic.Clear();
				_evidence.Append("\t\"note\": \"Editor measurements in the integrated harbor scene (Cesium tiles, water, traffic and the wheelhouse UI rendering too); the display cost is the difference of the frame medians with and without it. Not installed-player acceptance.\"\n}\n");
				File.WriteAllText(Path.Combine(Folder, "real-data-evidence.json"), _evidence.ToString());
			}
			finally
			{
				File.WriteAllText(Path.Combine(Folder, "index.csv"), _index.ToString());
				File.WriteAllText(Path.Combine(Folder, "scope.txt"),
					"The checked-in Harbor.unity with its real services, the real chart pack (data/processed/harbor/chart) loaded against the scene's runtime pack, and the day's AIS traffic through TrafficRunTargets.\n" +
					"Pier 11 is a catalog start (stop 87, " + TrafficDay + ", T-class, traffic on) at 12:18 local (the busiest active-tile minute) and 03:00 local; the East River and Upper Bay views are fixture starts on the water, not berth starts.\n" +
					"The Upper Bay alarm is a synthetic target appended to the real traffic for the capture (CompositeTargets); the run's own traffic monitor never saw it.\n" +
					"Fog is the scenario's quarter-mile visibility; the plotter draws the visibility ring from it. Night is the sun's position at 03:00 local through the daylight source.\n" +
					"The plotter renders into its own 1280x720 panel at 1264x704; these are rendering captures for visual review, not start-safety, survey, performance or installed-player acceptance.\n");
			}
		}

		private IEnumerator Start(HarborBootstrap bootstrap, ScenarioSettings settings, string key, int frames)
		{
			Assert.That(bootstrap.TryStart(settings, out var problem), Is.True, key + ": " + problem);
			bootstrap.WheelhouseUI.View.PickerVisible = false;
			var layers = Object.FindObjectsByType<HarborTileset>(FindObjectsSortMode.None);
			var started = Time.realtimeSinceStartupAsDouble;
			while (bootstrap.ViewState != HarborViewState.Ready)
			{
				Assert.That(bootstrap.Problem, Is.Null, key);
				foreach (var layer in layers) Assert.That(layer.Failure, Is.Null, key + ": " + layer.LayerId);
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(120), key + ": the initial harbor view did not settle");
				yield return null;
			}

			// Real physics steps so the plotter shows a live run, then the run is frozen for the capture.
			bootstrap.Runner.Resume();
			for (var i = 0; i < frames; i++) bootstrap.Runner.AdvanceFrame(0.02);
			bootstrap.Runner.Pause();
			Assert.That(bootstrap.Runner.HasTelemetry, Is.True, key);
			Assert.That(bootstrap.Runner.Hold, Is.EqualTo(RunHold.None), key);
			for (var i = 0; i < 20; i++) yield return null;
		}

		private IEnumerator Capture(string key, ScenarioSettings settings, double span, ChartOrientation orientation, ChartPaletteKind? palette)
		{
			var view = _display.View;
			view.SpanNauticalMiles = span;
			view.Orientation = orientation;
			view.Centre();
			if (palette.HasValue) view.Palette = palette.Value;
			for (var i = 0; i < 6; i++) yield return null;
			var image = Read(_target);
			File.WriteAllBytes(Path.Combine(Folder, key + ".png"), image.EncodeToPNG());
			var distinct = PlotterRig.DistinctColours(image);
			var luminance = PlotterRig.MeanLuminance(image);
			Object.Destroy(image);
			Assert.That(distinct, Is.GreaterThan(32), key + " is not blank");
			var state = _display.OwnShip.State;
			var invariant = CultureInfo.InvariantCulture;
			_index.Append(key).Append(',').Append(settings.name).Append(',').Append(span.ToString(invariant)).Append(',').Append(orientation).Append(',').Append(view.Palette).Append(',')
				.Append(state.North.ToString("F1", invariant)).Append(',').Append(state.East.ToString("F1", invariant)).Append(',').Append(settings.startHeadingDegrees.ToString(invariant)).Append(',')
				.Append(settings.scenarioStartUtc).Append(',').Append(settings.visibilityMetres.ToString(invariant)).Append(',').Append(view.Ranking.Considered).Append(',').Append(view.Targets.TargetsDrawn).Append(',')
				.Append(view.Alarm).Append(',').Append(view.Canvas.SoundingsShown).Append(',').Append(view.Canvas.LabelsRefused).Append(',').Append(view.Canvas.LastRenderMilliseconds.ToString("F2", invariant)).Append('\n');
			TestContext.WriteLine(key + ": palette " + view.Palette + ", targets considered " + view.Ranking.Considered + ", drawn " + view.Targets.TargetsDrawn + ", alarm " + view.Alarm + ", soundings " + view.Canvas.SoundingsShown + ", static render " + view.Canvas.LastRenderMilliseconds.ToString("F2", invariant) + " ms, mean luminance " + luminance.ToString("F4", invariant));
		}

		private IEnumerator RealDataTimings(VesselSimulationRunner runner, HarborBootstrap bootstrap, ScenarioSettings settings)
		{
			var view = _display.View;
			var invariant = CultureInfo.InvariantCulture;
			// The static render at each span of the ladder, centred on Pier 11.
			_evidence.Append("\t\"static_render_ms_by_span\": {");
			var first = true;
			foreach (var span in ChartplotterView.SpanLadder)
			{
				view.SpanNauticalMiles = span;
				yield return null;
				yield return null;
				_evidence.AppendFormat(invariant, "{0}\"{1}\": {{ \"placement_ms\": {2:F2}, \"mesh_ms\": {3:F2}, \"vertices\": {4}, \"chunks\": {5}, \"soundings\": {6}, \"aid_labels\": {7}, \"stop_labels\": {8} }}",
					first ? " " : ", ", span, view.Canvas.LastPlacementMilliseconds, view.Canvas.LastRenderMilliseconds, view.Canvas.LastVertices, view.Canvas.LastChunks, view.Canvas.SoundingsShown, view.Canvas.AidLabelsShown, view.Canvas.StopLabelsShown);
				first = false;
			}

			_evidence.Append(" },\n");
			view.SpanNauticalMiles = 2.0;
			yield return null;

			// Steady-state frame cost at 1080p: the run under way (resumed, so the targets and own ship move) for 60 s with the
			// display, then the same 60 s again without it (the control), per the phase 2 budget definition.
			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			var with = new FrameSampler("with display");
			var tick = new FrameSampler("display refresh");
			var control = new FrameSampler("control");
			var big = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			big.Create();
			var previousTarget = _settings.targetTexture;
			_settings.targetTexture = big;
			var startState = runner.State;
			var staticBefore = view.Canvas.RequestCount;
			try
			{
				runner.Resume();
				for (var i = 0; i < 30; i++) yield return null;
				var started = Time.realtimeSinceStartupAsDouble;
				while (Time.realtimeSinceStartupAsDouble - started < 60.0)
				{
					yield return null;
					with.Sample();
					tick.Milliseconds.Add(_display.LastRefreshMilliseconds);
					tick.WallClockSeconds.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
				}

				var staticDuring = view.Canvas.RequestCount - staticBefore;
				var endState = runner.State;
				runner.Pause();
				// Allocation: the calibrated recorder around the refresh, with the run frozen (the state holds, the list holds).
				var recorder = Recorder.Get("GC.Alloc");
				recorder.enabled = false;
				recorder.FilterToCurrentThread();
				var chars = new char[16];
				recorder.enabled = true;
				var controlString = new string(chars);
				recorder.enabled = false;
				GC.KeepAlive(controlString);
				var calibration = recorder.sampleBlockCount;
				var samples = 0;
				for (var i = 0; i < 120; i++)
				{
					recorder.enabled = true;
					_display.Refresh();
					recorder.enabled = false;
					samples += recorder.sampleBlockCount;
					yield return null;
				}

				recorder.CollectFromAllThreads();
				// The control: the same start again, the same 60 s under way, without the display.
				_display.enabled = false;
				Assert.That(bootstrap.TryStart(settings, out var problem), Is.True, "control restart: " + problem);
				bootstrap.WheelhouseUI.View.PickerVisible = false;
				var waited = Time.realtimeSinceStartupAsDouble;
				while (bootstrap.ViewState != HarborViewState.Ready)
				{
					Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(120), "control: the initial harbor view did not settle");
					yield return null;
				}

				runner.Resume();
				for (var i = 0; i < 30; i++) yield return null;
				started = Time.realtimeSinceStartupAsDouble;
				while (Time.realtimeSinceStartupAsDouble - started < 60.0)
				{
					yield return null;
					control.Sample();
				}

				runner.Pause();
				_display.enabled = true;
				yield return null;
				var verdict = FrameSampler.Verdict(with, control, tick, out var pass);
				_evidence.AppendFormat(invariant, "\t\"frame_cost_1080p\": {{ \"targets_considered\": {0}, \"run_from_north_east\": [{1:F1}, {2:F1}], \"run_to_north_east\": [{3:F1}, {4:F1}], \"static_regenerations_during_run\": {5}, \"gate\": {6} }},\n",
					view.Ranking.Considered, startState.North, startState.East, endState.North, endState.East, staticDuring, verdict);
				_evidence.AppendFormat(invariant, "\t\"refresh_gc_alloc_samples\": {{ \"positive_control\": {0}, \"samples_over_120_frames\": {1} }},\n", calibration, samples);
				TestContext.WriteLine("real data frame cost: " + verdict + "; alloc samples " + samples + " (control " + calibration + ")");
				RealDataPass = pass;
			}
			finally
			{
				_settings.targetTexture = previousTarget;
				big.Release();
				Object.Destroy(big);
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}
		}

		/// <summary>The real-data budget verdict, reported in the evidence; the capture test does not fail on it (the integrated editor scene is not the gate's player).</summary>
		public bool RealDataPass { get; private set; }

		private static ScenarioSettings FixtureStart(string name, double north, double east, double heading, string utc, bool traffic, double speedKnots)
		{
			var geo = LocalTangentPlane.World.ToGeodetic(north, east);
			var settings = new ScenarioSettings
			{
				name = name,
				vesselFile = "vessels/t-class.json",
				startLatitude = geo.LatitudeDegrees,
				startLongitude = geo.LongitudeDegrees,
				startHeadingDegrees = heading,
				startSpeedKnots = speedKnots,
				scenarioStartUtc = utc,
				environment = new ScenarioEnvironment { calm = false, pack = "harbor/" + TrafficDay },
			};
			if (traffic) settings.traffic.day = TrafficDay;
			return settings;
		}

		private static Texture2D Read(RenderTexture target)
		{
			var active = RenderTexture.active;
			RenderTexture.active = target;
			var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
			texture.Apply();
			RenderTexture.active = active;
			return texture;
		}
	}
}
