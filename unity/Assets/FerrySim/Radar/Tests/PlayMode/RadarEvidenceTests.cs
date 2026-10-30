using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling.LowLevel.Unsafe;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FerrySim.Core.Radar;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using FerrySim.Core.Env;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Radar.Tests.PlayMode
{
	// Failure modes: known semantic features misplaced, texture inverted, controls clipped at 720p,
	// changing palette changes echoes, hot-path garbage, an expensive full sweep disguised as incremental work.
	public class RadarEvidenceTests
	{
		private Scene _scene;
		private GameObject _host;
		private RenderTexture _target;
		private PanelSettings _panel;
		private RadarView _view;
		private UIDocument _document;
		private int _previousFrameRate, _previousVsync;
		private bool _paced;
		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static string Folder => Path.Combine(Repo, "artifacts/radar");

		private void Setup(RadarEngine engine, int width = 1280, int height = 720, int rasterSize = RadarRaster.DefaultSize, bool compact = false)
		{
			_scene = SceneManager.CreateScene("Radar evidence additive"); _host = new GameObject("Radar evidence"); SceneManager.MoveGameObjectToScene(_host, _scene);
			_target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32); _target.Create();
			_panel = WheelhousePanelSettings.Create(_target, new Color(.025f, .035f, .045f));
			_document = _host.AddComponent<UIDocument>(); _document.panelSettings = _panel;
			_view = new RadarView(engine, rasterSize, compact); _view.style.marginLeft = 24; _view.style.marginTop = 24; _document.rootVisualElement.Add(_view);
		}

		/// <summary>The wheelhouse layout (wave 3c integration): 480 × 380 above the instrument panel and clear of its traffic readout band, nothing clipped or overlapping, no font under the default layout's 10 px.</summary>
		[UnityTest] public IEnumerator CompactLayoutFitsTheWheelhouseWithoutClippingOrOverlap()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>())); engine.Sea = 0; Setup(engine, compact: true);
			var target = new[] { new RadarTarget(350, 180, 25, 12) };
			for (var spoke = 0; spoke < engine.Configuration.Spokes; spoke++) engine.RenderSpoke(spoke, 0, new RadarFrame(0, 0, 0, 0, 9, 0), target, 1);
			_view.Raster.RefreshAll(); _view.Refresh(.4);
			for (var i = 0; i < 5; i++) yield return null;
			Assert.That(_view.Compact, Is.True); Assert.That(_view.PlotPixels, Is.EqualTo(RadarView.CompactPlotPixels));
			Assert.That(_view.layout.width, Is.EqualTo(RadarView.DefaultWidth).Within(.5)); Assert.That(_view.layout.height, Is.EqualTo(RadarView.CompactHeight).Within(.5));
			Assert.That(_view.Q("ppi").layout.width, Is.EqualTo(RadarView.CompactPlotPixels).Within(.5)); Assert.That(_view.Q("ppi").layout.height, Is.EqualTo(RadarView.CompactPlotPixels).Within(.5));
			Assert.That(_view.worldBound.yMax, Is.LessThanOrEqualTo(24 + 390), "clear of the wheelhouse's traffic readout band at 392 px");
			Assert.That(_view.Query<Button>().ToList(), Is.Empty, "the keys change range, orientation and palette in the wheelhouse");
			Assert.That(_view.Query<Slider>().ToList().Count, Is.EqualTo(5));
			CheckFits(_view);
			var compactFolder = Path.Combine(Repo, "artifacts/m3w3c-integration/captures"); Directory.CreateDirectory(compactFolder);
			Capture(Path.Combine(compactFolder, "radar-compact-720p.png"));
			_target.Release(); Object.Destroy(_target); _target = new RenderTexture(2560, 1440, 0); _target.Create(); _panel.targetTexture = _target;
			for (var i = 0; i < 5; i++) yield return null;
			Assert.That(_view.layout.height, Is.EqualTo(RadarView.CompactHeight).Within(.5), "reference pixels scale, the layout does not change");
			CheckFits(_view);
			Capture(Path.Combine(compactFolder, "radar-compact-1440p.png"));
		}
		private static void CheckFits(RadarView view)
		{
			var bounds = view.worldBound;
			var rows = new List<VisualElement>();
			foreach (var child in view.Children()) if (child.resolvedStyle.display != DisplayStyle.None) rows.Add(child);
			for (var i = 1; i < rows.Count; i++) Assert.That(rows[i].worldBound.yMin, Is.GreaterThanOrEqualTo(rows[i - 1].worldBound.yMax - .5f), rows[i].name + " overlaps the row above");
			view.Query<VisualElement>().ForEach(element =>
			{
				if (element.resolvedStyle.display == DisplayStyle.None || element == view) return;
				var rect = element.worldBound;
				if (float.IsNaN(rect.width) || rect.width <= 0 || rect.height <= 0) return;
				Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(bounds.xMin - .5f), element.GetType().Name + " " + element.name + " clipped left");
				Assert.That(rect.xMax, Is.LessThanOrEqualTo(bounds.xMax + .5f), element.GetType().Name + " " + element.name + " clipped right");
				Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(bounds.yMin - .5f), element.GetType().Name + " " + element.name + " clipped above");
				Assert.That(rect.yMax, Is.LessThanOrEqualTo(bounds.yMax + .5f), element.GetType().Name + " " + element.name + " clipped below");
				if (element is Label label) Assert.That(label.resolvedStyle.fontSize, Is.GreaterThanOrEqualTo(10), label.text + " font");
			});
			foreach (var row in rows)
			{
				var cells = new List<Rect>();
				// The PPI's overlay sits on its image by design (absolute); every other cell is laid out side by side.
				foreach (var cell in row.Children()) if (cell.resolvedStyle.display != DisplayStyle.None && cell.resolvedStyle.position != Position.Absolute && cell.worldBound.width > 0) cells.Add(cell.worldBound);
				cells.Sort((a, b) => a.xMin.CompareTo(b.xMin));
				for (var i = 1; i < cells.Count; i++) Assert.That(cells[i].xMin, Is.GreaterThanOrEqualTo(cells[i - 1].xMax - .5f), row.name + ": a control overlaps its neighbour");
			}
		}
		[UnityTearDown] public IEnumerator Teardown()
		{
			if (_paced) { Application.targetFrameRate = _previousFrameRate; QualitySettings.vSyncCount = _previousVsync; _paced = false; }
			_view?.Dispose(); _view = null;
			if (_scene.IsValid()) yield return SceneManager.UnloadSceneAsync(_scene);
			if (_panel != null) Object.Destroy(_panel);
			if (_target != null) { _target.Release(); Object.Destroy(_target); }
		}

		[UnityTest] public IEnumerator ControlsAndNoticeFitAt720And1440()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>())); Setup(engine);
			for (var i = 0; i < 5; i++) yield return null;
			Assert.That(_view.worldBound.yMax, Is.LessThanOrEqualTo(720));
			Assert.That(_view.Q("ppi").layout.height, Is.EqualTo(440));
			Assert.That(_view.Q<Label>().text, Is.EqualTo("RADAR"));
			Directory.CreateDirectory(Folder); Capture(Path.Combine(Folder, "fixture-720p.png"));
			_view.ChangeRange(1); Assert.That(engine.RangeIndex, Is.EqualTo(4));
			_view.SetHeadUp(true); Assert.That(_view.HeadUp, Is.True);
			_view.SetPalette(RadarPalette.Dusk); Assert.That(_view.Palette, Is.EqualTo(RadarPalette.Dusk));
			var before = _view.worldBound;
			_target.Release(); Object.Destroy(_target); _target = new RenderTexture(2560, 1440, 0); _target.Create(); _panel.targetTexture = _target;
			for (var i = 0; i < 5; i++) yield return null;
			Assert.That(_view.worldBound, Is.EqualTo(before));
			Directory.CreateDirectory(Folder); Capture(Path.Combine(Folder, "fixture-1440p.png"));
		}

		[UnityTest] public IEnumerator RenderedTargetHasNorthEastOrientationAndDarkBackground()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>())); engine.Sea = 0; Setup(engine);
			var target = new[] { new RadarTarget(350, 180, 25, 12) };
			for (var spoke = 0; spoke < engine.Configuration.Spokes; spoke++) engine.RenderSpoke(spoke, 0, new RadarFrame(0, 0, 0, 0, 9, 0), target, 1);
			_view.Raster.RefreshAll();
			for (var i = 0; i < 5; i++) yield return null;
			var previous = RenderTexture.active; RenderTexture.active = _target;
			var image = new Texture2D(1280, 720, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); image.Apply(); RenderTexture.active = previous;
			try
			{
				var plot = _view.Q("ppi").worldBound; var x = plot.center.x + (float)(180 / engine.Scale.RangeMetres * 220); var dy = (float)(350 / engine.Scale.RangeMetres * 220);
				float Peak(float y) { var peak = 0f; for (var a = -3; a <= 3; a++) for (var b = -3; b <= 3; b++) { var c = image.GetPixel((int)x + a, (int)y + b); peak = Mathf.Max(peak, c.g - c.r); } return peak; }
				Assert.That(Peak(720 - plot.center.y + dy), Is.GreaterThan(.15f), "north-east return appears above and right of origin");
				Assert.That(Peak(720 - plot.center.y - dy), Is.LessThan(.05f), "no mirrored southern return");
				var background = image.GetPixel((int)plot.xMin + 4, 720 - (int)plot.yMin - 4); Assert.That(background.maxColorComponent, Is.LessThan(.15f), "palette background is not gamma-brightened");
			}
			finally { Object.Destroy(image); }
		}

		[Test] public void KnownPierBridgeBuoyAndTrafficHaveCorrectRangeAndBearing()
		{
			// Literal semantic vertices from chart DIGEST 605680cd... and runtime 97d7ef25...
			// Pier 11 shore: radar:enc:0226359E630A1C82. Brooklyn span: enc:02260BBCC4881C82.
			CheckEdge(new RadarSegment(366.70, 721.34, 329.84, 750.97), null, true);
			CheckEdge(new RadarSegment(792.018, 1405.681, 808.889, 1423.830), 80, false);
			// Deep Water Channel Lighted Buoy 1, enc:02260220EB8D0032, literal runtime point.
			var buoy = new RadarTarget(-238.147, -519.550, 2, 3, .55);
			var traffic = new RadarTarget(-138.147, -419.550, 30, 8);
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>(), new[] { buoy })); engine.Sea = 0;
			var frame = new RadarFrame(-438.147, -519.550, 0, 0, 9, 0);
			engine.RenderSpoke(0, 0, frame); Assert.That(engine.Echo(0, engine.Scale.Bin(200)), Is.GreaterThan(.5));
			var bearing = RadarMath.Bearing(300, 100); var spoke = (int)Math.Round(bearing / RadarMath.Tau * engine.Configuration.Spokes);
			engine.RenderSpoke(spoke, 0, frame, new[] { traffic }, 1);
			Assert.That(engine.Echo(spoke, engine.Scale.Bin(Math.Sqrt(100000))), Is.GreaterThan(.6));
		}
		private static void CheckEdge(RadarSegment edge, double? roof, bool land)
		{
			var n = (edge.N0 + edge.N1) / 2; var e = (edge.E0 + edge.E1) / 2;
			var engine = new RadarEngine(new RadarScene(new[] { new RadarFeature(new[] { edge }, roof, land) })); engine.Sea = 0;
			engine.RenderSpoke(0, 0, new RadarFrame(n - 100, e, 0, 0, 9, 0));
			Assert.That(engine.Echo(0, engine.Scale.Bin(100)), Is.GreaterThan(.8));
			engine.RenderSpoke(engine.Configuration.Spokes / 2, 0, new RadarFrame(n - 100, e, 0, 0, 9, 0));
			Assert.That(engine.Echo(engine.Configuration.Spokes / 2, engine.Scale.Bin(100)), Is.LessThan(.1));
		}

		[UnityTest, Explicit("Copied resident harbor packs and rendered captures; select exact method."), Category("RadarRealData")]
		public IEnumerator RealHarborCapturesAndProfile()
		{
			var harbor = Path.Combine(Repo, "data/processed/harbor");
			Assert.That(File.Exists(Path.Combine(harbor, "chart/DIGEST")), Is.True, "copy adopted chart and harbor packs first");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			var memoryBefore = GC.GetTotalMemory(true); var scene = BuildScene(chart, runtime); var memoryAfter = GC.GetTotalMemory(true);
			var engine = new RadarEngine(scene); var memoryWithEngine = GC.GetTotalMemory(true);
			var rasterSize = int.TryParse(Environment.GetEnvironmentVariable("RADAR_RASTER_SIZE"), out var requestedSize) ? requestedSize : RadarRaster.DefaultSize;
			Assert.That(rasterSize, Is.EqualTo(384).Or.EqualTo(512)); Setup(engine, 1920, 1080, rasterSize);
			var perfStage = Environment.GetEnvironmentVariable("RADAR_PERF_STAGE");
			if (!string.IsNullOrEmpty(perfStage))
			{
				var output = Path.Combine(Folder, "perf", perfStage); Directory.CreateDirectory(output);
				File.WriteAllText(Path.Combine(output, "unity-memory.txt"), "Index managed delta=" + (memoryAfter - memoryBefore) + " bytes; engine delta=" + (memoryWithEngine - memoryAfter) + " bytes; cells=" + scene.SpatialCellCount + "; features=" + scene.FeatureCount + ".\nGC.GetTotalMemory(true), resident managed delta excluding shared chart/runtime; not peak construction memory.\n");
			}
			var trafficPack = TrafficPackJson.Load(Path.Combine(harbor, "traffic/2025-07-16"));
			var start = DateTimeOffset.Parse("2025-07-16T16:18:00Z", CultureInfo.InvariantCulture);
			var traffic = new TrafficRun(trafficPack, start, TrafficLeaveOut.Off, 0);
			var targets = new RadarTarget[trafficPack.Vessels.Count];
			var envPack = EnvironmentPackJson.Load(Path.Combine(harbor, "env/days/2025-07-16"), Path.Combine(harbor, "env/depth")); var environment = new DataEnvironment(envPack, start);
			Directory.CreateDirectory(Folder);
			var caption = new Label(); caption.style.position = Position.Absolute; caption.style.left = 540; caption.style.top = 60;
			caption.style.width = 680; caption.style.whiteSpace = WhiteSpace.Normal; caption.style.fontSize = 20; caption.style.color = InstrumentPalette.Day.Text;
			_document.rootVisualElement.Add(caption);
			var index = new StringBuilder("capture,north_m,east_m,range_nm,palette,traffic_present,tide_navd88_m,wind_mps\n");
			var views = new[]
			{
				new CaptureView("pier11-day", 351.061, 848.434, 3, RadarPalette.Day),
				new CaptureView("pier11-night", 351.061, 848.434, 3, RadarPalette.Night),
				new CaptureView("pier11-fog", 351.061, 848.434, 3, RadarPalette.Day),
				new CaptureView("brooklyn-manhattan-075", 520, 1420, 3, RadarPalette.Day),
				new CaptureView("brooklyn-manhattan-150", 520, 1420, 4, RadarPalette.Dusk),
				new CaptureView("upper-bay-traffic-300", -2400, -700, 5, RadarPalette.Day)
			};
			float[] dayPicture = null; var matchingPictures = 0;
			foreach (var capture in views)
			{
				engine.SetRange(capture.Scale); engine.Reset();
				var tide = environment.SampleWaterLevel(capture.North, capture.East, 0); var wind = environment.SampleWind(capture.North, capture.East, 0).Magnitude;
				var frame = new RadarFrame(capture.North, capture.East, .5, tide, 9, wind);
				var count = RadarController.SampleTraffic(traffic, 0, targets);
				engine.Advance(0, frame, targets, count);
				for (var i = 1; i <= 150; i++) engine.Advance(i / 60.0, frame, targets, count);
				if (!string.IsNullOrEmpty(perfStage)) VerifyPolar(engine, capture.Name, perfStage);
				if (capture.Name.StartsWith("pier11", StringComparison.Ordinal))
				{
					if (dayPicture == null) { dayPicture = new float[engine.Configuration.Spokes * engine.Scale.Bins]; for (var s = 0; s < engine.Configuration.Spokes; s++) for (var b = 0; b < engine.Scale.Bins; b++) dayPicture[s * engine.Scale.Bins + b] = engine.Echo(s, b); }
					else { var different = 0; for (var s = 0; s < engine.Configuration.Spokes; s++) for (var b = 0; b < engine.Scale.Bins; b++) if (dayPicture[s * engine.Scale.Bins + b] != engine.Echo(s, b)) different++; Assert.That(different, Is.Zero, "night/fog preserve every model amplitude"); matchingPictures++; }
				}
				_view.SetPalette(capture.Palette); _view.Raster.RefreshAll(); _view.Refresh(frame.Heading);
				caption.text = capture.Name.Replace('-', ' ').ToUpperInvariant() + "\n\nResident semantic radar\n2025-07-16 · 12:18 EDT\n" + count + " present AIS targets\n\n" +
					"Estimated X-band response · 24 rpm · " + rasterSize + " px PPI\nNo ARPA, acquisition or trails\nNot for navigation\n\n" +
					(capture.Name.Contains("fog") ? "100 m fog comparison\nOptical visibility has no radar input.\nFog does not create rain returns.\nThe radar data is identical to the day capture." : "Fixed observer on harbor water\nReal chart, bridge, aid and traffic packs\nNo render tiles loaded");
				for (var i = 0; i < 6; i++) yield return null;
				Capture(Path.Combine(Folder, capture.Name + ".png"));
				index.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7}\n", capture.Name, capture.North, capture.East, engine.Scale.RangeMetres / 1852, capture.Palette, count, tide, wind);
			}
			File.WriteAllText(Path.Combine(Folder, "captures.csv"), index.ToString());
			File.WriteAllText(Path.Combine(Folder, "palette-fog-equality.txt"), matchingPictures + " complete polar pictures match day exactly (night and fog comparison).\n");
			File.WriteAllText(Path.Combine(Folder, "data-digests.txt"), "chart " + chart.Digest + "\nruntime " + runtime.Digest + "\ntraffic " + trafficPack.Digest + "\n");
			// Profile the full engine + texture upload + readout update at the same 1080p target, separately from captures.
			engine.SetRange(5); engine.Reset(); var clock = 0.0;
			var profileFrame = new RadarFrame(-2400, -700, .5, environment.SampleWaterLevel(-2400, -700, 0), 9, environment.SampleWind(-2400, -700, 0).Magnitude);
			double trafficMs = 0, modelMs = 0, rasterMs = 0, trafficCpu = 0, modelCpu = 0, rasterCpu = 0;
			Action tick = () =>
			{
				var cpu = ThreadCpuMilliseconds(); var startTick = Stopwatch.GetTimestamp();
				var count = RadarController.SampleTraffic(traffic, clock, targets); trafficMs = Milliseconds(startTick); trafficCpu = ThreadCpuMilliseconds() - cpu;
				cpu = ThreadCpuMilliseconds(); startTick = Stopwatch.GetTimestamp();
				engine.Advance(clock += 1.0 / 60, new RadarFrame(profileFrame.North + clock * 4, profileFrame.East + clock * 2, profileFrame.Heading, profileFrame.Tide, 9, profileFrame.WindSpeed), targets, count);
				modelMs = Milliseconds(startTick); modelCpu = ThreadCpuMilliseconds() - cpu;
				cpu = ThreadCpuMilliseconds(); startTick = Stopwatch.GetTimestamp(); _view.Refresh(profileFrame.Heading);
				rasterMs = Milliseconds(startTick); rasterCpu = ThreadCpuMilliseconds() - cpu;
			};
			for (var i = 0; i < 200; i++) tick();
			var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
			var markerNames = new StringBuilder();
			foreach (var handle in handles) { var description = ProfilerRecorderHandle.GetDescription(handle); if (description.Name.Contains("UIElements") || description.Name.Contains("UI Toolkit")) markerNames.AppendLine(description.Name); }
			File.WriteAllText(Path.Combine(Folder, "ui-profiler-markers.txt"), markerNames.ToString());
			const int capacity = 100000; var sampleCount = 0; var profileStarted = 0.0;
			var uiNames = new[] { "PreLateUpdate.UIElementsUpdatePanels", "PostLateUpdate.UIElementsRepaintPanels", "PostLateUpdate.UIElementsRenderBatchModeOffscreen" };
			var uiRecorders = new Recorder[uiNames.Length];
			for (var i = 0; i < uiNames.Length; i++) { uiRecorders[i] = Recorder.Get(uiNames[i]); uiRecorders[i].FilterToCurrentThread(); uiRecorders[i].enabled = true; }
			var trafficTimes = new double[capacity]; var modelTimes = new double[capacity]; var rasterTimes = new double[capacity];
			var utcTicks = new long[capacity * 2]; var frameNumbers = new int[capacity * 2]; var spokeCounts = new int[capacity]; var firstSpokes = new int[capacity];
			var trafficCpuTimes = new double[capacity]; var modelCpuTimes = new double[capacity]; var rasterCpuTimes = new double[capacity];
			var tickCpuTimes = new double[capacity * 2];
			var samples = new double[capacity]; var control = new double[capacity]; var frameTimes = new double[capacity * 2]; var uiTimes = new double[capacity * 2]; long uiBlocks = 0;
			_previousFrameRate = Application.targetFrameRate; _previousVsync = QualitySettings.vSyncCount; _paced = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
			for (var warm = 0; warm < 240; warm++) { tick(); yield return null; }
			profileStarted = Time.realtimeSinceStartupAsDouble;
			for (var i = 0; i < capacity * 2 && (i < 3600 || (i & 1) != 0 || Time.realtimeSinceStartupAsDouble - profileStarted < 65); i++)
			{
				utcTicks[i] = DateTime.UtcNow.Ticks; frameNumbers[i] = Time.frameCount;
				var cpuStarted = ThreadCpuMilliseconds(); var started = Stopwatch.GetTimestamp();
				if ((i & 1) == 0) { tick(); samples[i / 2] = Milliseconds(started); trafficTimes[i / 2] = trafficMs; modelTimes[i / 2] = modelMs; rasterTimes[i / 2] = rasterMs; spokeCounts[i / 2] = engine.LastSpokeCount; firstSpokes[i / 2] = engine.LastFirstSpoke; trafficCpuTimes[i / 2] = trafficCpu; modelCpuTimes[i / 2] = modelCpu; rasterCpuTimes[i / 2] = rasterCpu; }
				else { engine.Advance(clock, profileFrame); _view.Refresh(profileFrame.Heading); control[i / 2] = Milliseconds(started); }
				tickCpuTimes[i] = ThreadCpuMilliseconds() - cpuStarted;
				if ((i & 1) != 0) sampleCount++;
				yield return null; frameTimes[i] = Time.unscaledDeltaTime * 1000;
				for (var r = 0; r < uiRecorders.Length; r++) { uiTimes[i] += uiRecorders[r].elapsedNanoseconds / 1000000.0; uiBlocks += uiRecorders[r].sampleBlockCount; }
			}
			for (var i = 0; i < uiRecorders.Length; i++) { uiRecorders[i].enabled = false; uiRecorders[i].CollectFromAllThreads(); }
			var allocations = MeasureAllocations(() => { for (var i = 0; i < 100; i++) tick(); });
			var profileDuration = Time.realtimeSinceStartupAsDouble - profileStarted;
			File.WriteAllText(Path.Combine(Folder, "pacing.txt"), "Requested Application.targetFrameRate=60, vSyncCount=0; measured frames per second=" + (sampleCount * 2 / profileDuration).ToString("F3", CultureInfo.InvariantCulture) + "; duration=" + profileDuration.ToString("F3", CultureInfo.InvariantCulture) + " seconds.\n");
			var stallSeconds = new HashSet<long>(); var stallFrames = new HashSet<int>(); var controlStalls = new bool[sampleCount];
			for (var i = 0; i < sampleCount; i++) controlStalls[i] = frameTimes[i * 2 + 1] > 21.67 || control[i] + uiTimes[i * 2 + 1] > 5;
			for (var i = 0; i < sampleCount; i++) if (controlStalls[i]) { stallSeconds.Add(utcTicks[i * 2 + 1] / TimeSpan.TicksPerSecond); stallFrames.Add(frameNumbers[i * 2 + 1]); }
			var unmatched = 0; var matched = 0; var overFive = new bool[sampleCount]; var stallMatch = new bool[sampleCount];
			for (var i = 0; i < sampleCount; i++)
			{
				overFive[i] = samples[i] + uiTimes[i * 2] > 5;
				stallMatch[i] = stallSeconds.Contains(utcTicks[i * 2] / TimeSpan.TicksPerSecond) || stallFrames.Contains(frameNumbers[i * 2] - 1) || stallFrames.Contains(frameNumbers[i * 2]) || stallFrames.Contains(frameNumbers[i * 2] + 1);
				if (overFive[i]) { if (stallMatch[i]) matched++; else unmatched++; }
			}
			var csv = new StringBuilder("sample,tick_ms,control_ms,active_frame_ms,control_frame_ms,active_ui_phases_ms,control_ui_phases_ms,traffic_ms,model_ms,raster_ms,active_utc_ticks,control_utc_ticks,active_unity_frame,control_unity_frame,control_stall,active_tick_ui_over_5ms,active_stall_matched,spokes,first_spoke,range_nm,first_after_setting_change,traffic_cpu_ms,model_cpu_ms,raster_cpu_ms,active_tick_cpu_ms,control_tick_cpu_ms\n");
			for (var i = 0; i < sampleCount; i++) csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10},{11},{12},{13},{14},{15},{16},{17},{18},3,False,{19:R},{20:R},{21:R},{22:R},{23:R}\n", i, samples[i], control[i], frameTimes[i * 2], frameTimes[i * 2 + 1], uiTimes[i * 2], uiTimes[i * 2 + 1], trafficTimes[i], modelTimes[i], rasterTimes[i], utcTicks[i * 2], utcTicks[i * 2 + 1], frameNumbers[i * 2], frameNumbers[i * 2 + 1], controlStalls[i], overFive[i], stallMatch[i], spokeCounts[i], firstSpokes[i], trafficCpuTimes[i], modelCpuTimes[i], rasterCpuTimes[i], tickCpuTimes[i * 2], tickCpuTimes[i * 2 + 1]);
			File.WriteAllText(Path.Combine(Folder, "profile.csv"), csv.ToString());
			var combined = new double[sampleCount]; for (var i = 0; i < sampleCount; i++) combined[i] = samples[i] + uiTimes[i * 2];
			var controlCombined = new double[sampleCount]; for (var i = 0; i < sampleCount; i++) controlCombined[i] = control[i] + uiTimes[i * 2 + 1]; Array.Sort(controlCombined);
			Array.Sort(combined); Array.Sort(samples, 0, sampleCount); Array.Sort(control, 0, sampleCount);
			File.WriteAllText(Path.Combine(Folder, "profile.txt"), string.Format(CultureInfo.InvariantCulture,
				"1080p editor, {8}px PPI, 512 spokes, 3 nm, 2025-07-16 12:18 EDT; {0} static features\n{7} interleaved active/control samples over at least 65 wall-clock seconds\ntick p50={1:F4} ms p95={2:F4} ms max={3:F4} ms\ncontrol p95={4:F4} ms max={5:F4} ms\nGC.Alloc positive control=1 measured samples={6}\nScope: sampling, model sweep, raster upload, UI readout update. UI Toolkit scheduled paint/GPU and installed players are not included in tick timing. Raw whole-frame times are retained without filtering.\n",
				scene.FeatureCount, samples[sampleCount / 2], samples[(int)(sampleCount * .95)], samples[sampleCount - 1], control[(int)(sampleCount * .95)], control[sampleCount - 1], allocations, sampleCount, rasterSize));
			File.WriteAllText(Path.Combine(Folder, "ui-phase-timing.txt"), "Markers: " + string.Join(", ", uiNames) + "\nObserved blocks=" + uiBlocks + "\nConservative tick + UI phase p95=" + combined[(int)(sampleCount * .95)].ToString("F4", CultureInfo.InvariantCulture) + " ms; max=" + combined[sampleCount - 1].ToString("F4", CultureInfo.InvariantCulture) + " ms\nIncludes the runtime UI panel update, repaint and batch-mode offscreen render parent markers on the main thread. GPU time and installed players remain unmeasured.\n");
			File.WriteAllText(Path.Combine(Folder, "timing-policy.txt"), string.Format(CultureInfo.InvariantCulture,
				"Coordinator policy: p95 <= 1.5 ms; p99 <= 2.5 ms; every >5 ms active tick+UI sample matched to control stall in same UTC second or +/-1 Unity frame.\nControl outlier marker: raw frame interval >21.67 ms or control tick+UI >5 ms at requested 60 Hz. Thread CPU columns cover the synchronous tick, not later UI phases. No rows removed.\nActive p50={0:F6} p95={1:F6} p99={2:F6} max={3:F6} ms\nControl tick+UI p50={4:F6} p95={5:F6} p99={6:F6} max={7:F6} ms\nActive >5 ms matched={8}, unexplained={9}\n",
				combined[sampleCount / 2], combined[(int)(sampleCount * .95)], combined[(int)(sampleCount * .99)], combined[sampleCount - 1],
				controlCombined[sampleCount / 2], controlCombined[(int)(sampleCount * .95)], controlCombined[(int)(sampleCount * .99)], controlCombined[sampleCount - 1], matched, unmatched));
			Assert.That(sampleCount * 2 / profileDuration, Is.InRange(50, 70), "60 Hz pacing must actually apply");
			Assert.That(combined[(int)(sampleCount * .99)], Is.LessThanOrEqualTo(2.5), "coordinator p99 limit");
			Assert.That(unmatched, Is.Zero, "every >5 ms frame must match the control stall criterion");
			Assert.That(uiBlocks, Is.GreaterThan(0), "UI timing has actual marker samples");
			Assert.That(combined[(int)(sampleCount * .95)], Is.LessThanOrEqualTo(1.5), "tick plus UI phase p95");
			Assert.That(allocations, Is.Zero); Assert.That(samples[(int)(sampleCount * .95)], Is.LessThanOrEqualTo(1.5), "steady-state tick p95; control and raw frames retained");
		}
		[MethodImpl(MethodImplOptions.NoInlining)] private static RadarScene BuildScene(HarborChartPack chart, HarborRuntimePack runtime) => RadarScene.FromPacks(chart, runtime);
		private static void VerifyPolar(RadarEngine engine, string name, string stage)
		{
			var output = Path.Combine(Folder, "perf", stage); var path = Path.Combine(output, "unity-" + name + ".polar");
			using (var writer = new BinaryWriter(File.Create(path))) for (var s = 0; s < engine.Configuration.Spokes; s++) for (var b = 0; b < engine.Scale.Bins; b++) writer.Write(engine.Echo(s, b));
			var bytes = File.ReadAllBytes(path); var different = 0;
			if (stage != "baseline")
			{
				var baseline = File.ReadAllBytes(Path.Combine(Folder, "perf/baseline", "unity-" + name + ".polar")); Assert.That(bytes.Length, Is.EqualTo(baseline.Length));
				for (var i = 0; i < bytes.Length; i += 4) if (BitConverter.ToInt32(bytes, i) != BitConverter.ToInt32(baseline, i)) different++;
			}
			using (var sha = SHA256.Create()) File.AppendAllText(Path.Combine(output, "unity-polar-identity.csv"), name + "," + bytes.Length / 4 + "," + different + "," + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") + "\n");
			Assert.That(different, Is.Zero, name + " polar amplitude bits must match the frozen reference");
		}
		// macOS SDK _time.h: CLOCK_THREAD_CPUTIME_ID = 16. Test-only CPU evidence; no player dependency.
#if UNITY_EDITOR_OSX
		[StructLayout(LayoutKind.Sequential)] private struct Timespec { public long Seconds, Nanoseconds; }
		[DllImport("/usr/lib/libSystem.B.dylib")] private static extern int clock_gettime(int clock, out Timespec value);
#endif
		private static double ThreadCpuMilliseconds()
		{
#if UNITY_EDITOR_OSX
			return clock_gettime(16, out var value) == 0 ? value.Seconds * 1000.0 + value.Nanoseconds / 1000000.0 : double.NaN;
#else
			return double.NaN;
#endif
		}
		private static double Milliseconds(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
		private static int MeasureAllocations(Action action)
		{
			var chars = new char[16]; var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true; var control = new string(chars); recorder.enabled = false; var calibration = recorder.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1)); recorder.enabled = true; action(); recorder.enabled = false; return recorder.sampleBlockCount;
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}
		private void Capture(string path)
		{
			var previous = RenderTexture.active; RenderTexture.active = _target;
			var image = new Texture2D(_target.width, _target.height, TextureFormat.RGB24, false);
			image.ReadPixels(new Rect(0, 0, _target.width, _target.height), 0, 0); image.Apply();
			File.WriteAllBytes(path, image.EncodeToPNG()); Object.Destroy(image); RenderTexture.active = previous;
		}
		private readonly struct CaptureView
		{
			public CaptureView(string name, double n, double e, int scale, RadarPalette palette) { Name = name; North = n; East = e; Scale = scale; Palette = palette; }
			public readonly string Name; public readonly double North, East; public readonly int Scale; public readonly RadarPalette Palette;
		}
	}
}
