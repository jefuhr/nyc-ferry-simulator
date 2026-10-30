using System;
using System.Collections;
using System.Globalization;
using System.IO;
using FerrySim.Chartplotter;
using FerrySim.Core.Contracts;
using FerrySim.Core.Radar;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>
	/// End to end on the checked-in harbor scene with the real services, data and chart pack. Failure modes: a start that
	/// waits on the chart load; a display drawing before the chart is ready without saying so; displays that stay
	/// connected to an old run's traffic; own ship or a target drawn where the runner is not; a radar without its heading
	/// line or without the echoes the geometry demands; a display visible outside the wheelhouse view or under the
	/// picker; keys that act on a hidden display; per-frame garbage with both displays on; a display that changes a
	/// recording.
	/// </summary>
	public class HarborDisplaysPlayTests
	{
		private const int Width = 1280, Height = 720;
		private RenderTexture _uiTarget;
		private PanelSettings _panel;

		private static string Folder => Path.Combine(HarborSceneFixture.Repo, "artifacts/m3w3c-integration");

		[UnityTearDown]
		public IEnumerator UnloadHarborScene()
		{
			Time.timeScale = 1f;
			yield return HarborSceneFixture.Unload();
			if (_panel != null) Object.Destroy(_panel);
			if (_uiTarget != null) { _uiTarget.Release(); Object.Destroy(_uiTarget); }
			_panel = null;
			_uiTarget = null;
		}

		private static void RequireChart()
		{
			HarborSceneFixture.RequireData();
			var chart = Path.Combine(HarborSceneFixture.Repo, "data", "processed", "harbor", "chart");
			if (!Directory.Exists(chart))
			{
				Assert.Ignore("The chart pack is not copied: missing " + chart + ".");
			}
		}

		/// <summary>The wheelhouse UI (and so the displays, which draw in its panel) into a texture the test can read.</summary>
		private IEnumerator RenderUiToTexture(HarborBootstrap bootstrap)
		{
			var ui = bootstrap.WheelhouseUI;
			_uiTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_uiTarget.Create();
			_panel = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
			ui.enabled = false;
			ui.Configure(bootstrap.Runner, _panel);
			ui.enabled = true;
			ui.DaylightSource = bootstrap.Context.Sun;
			yield return null;
			ui.View.PickerVisible = false;
			yield return null;
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator Pier11_StartBeforeTheChartIsReady_DisplaysConnectDrawTheRunAndCostNoGarbage()
		{
			RequireChart();
			Directory.CreateDirectory(Folder);
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			var displays = services.Displays;
			Assert.That(displays, Is.Not.Null, "the composition root created the displays");
			Assert.That(displays.Load, Is.Not.Null, "the chart load started when the scene composed");
			var composedState = displays.ChartState;
			TestContext.WriteLine("chart state when the scene had composed: " + composedState);
			yield return RenderUiToTexture(bootstrap);
			var ui = bootstrap.WheelhouseUI;

			// Hidden by default; shown, they announce the chart until it is ready.
			Assert.That(displays.RadarShown, Is.False);
			Assert.That(displays.ChartplotterShown, Is.False);
			Assert.That(displays.RadarVisible, Is.False);
			Assert.That(displays.ChartplotterVisible, Is.False);
			displays.ToggleRadar();
			displays.ToggleChartplotter();
			yield return null;
			Assert.That(displays.RadarVisible, Is.True);
			Assert.That(displays.ChartplotterVisible, Is.True);
			if (!displays.Connected)
			{
				Assert.That(displays.RadarStatusText, Is.EqualTo(HarborDisplays.LoadingText));
				Assert.That(displays.ChartplotterStatusText, Is.EqualTo(HarborDisplays.LoadingText));
				Assert.That(displays.Radar.View, Is.Null, "nothing radar-like is drawn before the chart");
			}

			// The start never waits on the chart.
			var catalog = services.HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[0].Id, traffic: true);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			var stateAtStart = displays.ChartState;
			var startWatch = System.Diagnostics.Stopwatch.StartNew();
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			startWatch.Stop();
			ui.View.PickerVisible = false;
			Assert.That(runner.IsRunActive, Is.True);
			TestContext.WriteLine("start took " + startWatch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms with the chart " + stateAtStart + " (" + displays.ChartState + " after)");
			if (stateAtStart == HarborChartLoadState.Loading)
			{
				Assert.That(startWatch.Elapsed.TotalSeconds, Is.LessThan(5), "the atomic start does not wait for the chart");
			}

			yield return HarborSceneFixture.WaitReady(bootstrap, "pier-11");
			var tilesReadyAt = Time.realtimeSinceStartupAsDouble;

			// The chart becomes ready and both displays connect to the runner, the runtime pack and the chart.
			var waited = Time.realtimeSinceStartupAsDouble;
			while (!displays.Connected)
			{
				Assert.That(displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180), "the chart did not become ready");
				yield return null;
			}

			var load = displays.Load;
			TestContext.WriteLine("chart ready " + displays.ChartReadySeconds.ToString("F2", CultureInfo.InvariantCulture) + " s after composition, " + (Time.realtimeSinceStartupAsDouble - tilesReadyAt).ToString("F2", CultureInfo.InvariantCulture) + " s after the tiles; load " +
				load.LoadMilliseconds.ToString("F0") + " ms, index " + load.IndexMilliseconds.ToString("F0") + " ms, tessellation " + load.TessellationMilliseconds.ToString("F0") + " ms; peak Mono used while loading " + displays.PeakMonoUsedBytes + " bytes");
			File.WriteAllText(Path.Combine(Folder, "chart-load-editor.txt"),
				"state when composed=" + composedState + "\nstate at start=" + stateAtStart + "\nstart_ms=" + startWatch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
				"\nchart_ready_s_after_composition=" + displays.ChartReadySeconds.ToString("F3", CultureInfo.InvariantCulture) + "\nload_ms=" + load.LoadMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
				"\nindex_ms=" + load.IndexMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "\ntessellation_ms=" + load.TessellationMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
				"\ntotal_ms=" + load.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "\nmanaged_before=" + load.ManagedBytesBefore + "\nmanaged_after=" + load.ManagedBytesAfter +
				"\npeak_mono_used_while_loading=" + displays.PeakMonoUsedBytes + "\n");
			Assert.That(displays.RadarStatusText, Is.Empty);
			Assert.That(displays.ChartplotterStatusText, Is.Empty);
			var radar = displays.Radar;
			var chartplotter = displays.Chartplotter;
			Assert.That(radar.View, Is.Not.Null);
			Assert.That(radar.View.Compact, Is.True);
			Assert.That(radar.Traffic, Is.SameAs(runner.Traffic), "the radar samples the run's traffic");
			Assert.That(chartplotter.Chart, Is.SameAs(load.Tessellation), "the chartplotter draws the shared tessellation");
			Assert.That(chartplotter.Chart.Chart, Is.SameAs(load.Pack));
			Assert.That(chartplotter.Chart.RuntimeDigest, Is.EqualTo(services.Pack.Digest), "the tessellation records the runtime it was built from, and holds no runtime pack (review item 3)");
			Assert.That(chartplotter.OwnShip, Is.SameAs(runner));
			Assert.That(chartplotter.View, Is.Not.Null);
			Assert.That(chartplotter.Visible, Is.True);
			Assert.That(radar.enabled, Is.True, "a visible radar sweeps");

			// Drive about 30 s ahead with both displays on.
			runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.0));
			runner.Resume();
			var driveStarted = Time.realtimeSinceStartupAsDouble;
			while (runner.Driver.SimulationTime < 30)
			{
				Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
				Assert.That(Time.realtimeSinceStartupAsDouble - driveStarted, Is.LessThan(150), "the drive did not finish");
				yield return null;
			}

			// Garbage: the calibrated recorder over the composition's tick and both displays' refresh, under way, 120 frames.
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			var chars = new char[16];
			recorder.enabled = true;
			var control = new string(chars);
			recorder.enabled = false;
			GC.KeepAlive(control);
			var calibration = recorder.sampleBlockCount;
			Assert.That(calibration, Is.EqualTo(1), "deliberate one-string positive control");
			var samples = 0;
			var sweptSpokes = 0;
			var uploadsBefore = radar.View.Raster.Uploads;
			// The radar's own Update would sweep this frame's spokes before the coroutine resumes and leave nothing for a second
			// Refresh at the same render time (review item 7): its automatic update is off for the window, so the measured
			// Refresh is the real scheduled sweep of each frame's new spokes and the raster upload.
			radar.enabled = false;
			yield return null;
			for (var i = 0; i < 120; i++)
			{
				recorder.enabled = true;
				displays.Tick();
				radar.Refresh();
				chartplotter.Refresh();
				recorder.enabled = false;
				samples += recorder.sampleBlockCount;
				sweptSpokes += radar.Engine.LastSpokeCount;
				yield return null;
			}

			radar.enabled = true;
			var uploads = radar.View.Raster.Uploads - uploadsBefore;
			recorder.CollectFromAllThreads();
			TestContext.WriteLine("GC.Alloc positive control=" + calibration + "; measured samples over 120 frames with both displays=" + samples + "; spokes swept inside the window=" + sweptSpokes + "; raster uploads inside the window=" + uploads);
			File.WriteAllText(Path.Combine(Folder, "gc-alloc-displays.txt"), "positive control=" + calibration + "\nmeasured samples (120 frames, Tick + radar Refresh + chartplotter Refresh, under way)=" + samples + "\nspokes swept inside the measured window=" + sweptSpokes + "\nraster uploads inside the measured window=" + uploads + "\n");
			Assert.That(sweptSpokes, Is.GreaterThan(0), "the measured window really swept the radar (review item 7)");
			Assert.That(uploads, Is.GreaterThan(0), "the measured window really uploaded the raster");
			Assert.That(samples, Is.Zero);

			// Freeze the run so a drawn frame and the runner agree exactly, then compare.
			runner.Pause();
			yield return null;
			yield return null;
			var view = chartplotter.View;
			var state = runner.State;
			Assert.That(view.HasView, Is.True);
			Assert.That(view.Targets.OwnShipShown, Is.True);
			view.CurrentView.ToScreen(state.North, state.East, out var ownX, out var ownY);
			Assert.That(view.Targets.OwnX, Is.EqualTo((float)ownX).Within(0.75f), "own ship x");
			Assert.That(view.Targets.OwnY, Is.EqualTo((float)ownY).Within(0.75f), "own ship y");
			var headingDegrees = (int)Math.Round(state.Heading * 180 / Math.PI) % 360;
			Assert.That(view.HeadingText, Does.StartWith("HDG " + headingDegrees.ToString("000", CultureInfo.InvariantCulture)), "the plotter's heading readout is the runner's");
			var traffic = runner.Traffic;
			var present = 0;
			for (var i = 0; i < traffic.Pack.Vessels.Count; i++)
			{
				if (!view.Targets.IsPresent(i))
				{
					continue;
				}

				Assert.That(traffic.Replay.TrySample(i, traffic.PackTime(state.Time), out var sample), Is.True, "a drawn target is in the replay at the run time");
				view.CurrentView.ToScreen(sample.North, sample.East, out var x, out var y);
				Assert.That(view.Targets.TargetX(i), Is.EqualTo((float)x).Within(0.75f), "target " + i + " x");
				Assert.That(view.Targets.TargetY(i), Is.EqualTo((float)y).Within(0.75f), "target " + i + " y");
				present++;
			}

			Assert.That(present, Is.GreaterThan(0), "the day's traffic is on the plotter");
			Assert.That(view.Ranking.Considered, Is.GreaterThan(100), "the day's roster was considered");
			TestContext.WriteLine("chartplotter: own ship at (" + view.Targets.OwnX.ToString("F1") + ", " + view.Targets.OwnY.ToString("F1") + "), " + present + " targets drawn of " + view.Ranking.Considered);

			// The radar: a whole revolution has passed; the heading line is drawn and known geometry echoes on its bearing.
			var engine = radar.Engine;
			Assert.That(engine.Cleared, Is.False);
			var echoBins = HarborDisplays.CountEchoBins(engine);
			Assert.That(echoBins, Is.GreaterThan(200), "the picture holds echoes");
			// Pier 11's own shore (radar:enc:0226359E630A1C82, literal vertices from chart 605680cd...) and a Brooklyn Bridge span (enc:02260BBCC4881C82).
			AssertEcho(engine, state, (366.70 + 329.84) / 2, (721.34 + 750.97) / 2, "Pier 11 shoreline");
			AssertEcho(engine, state, (792.018 + 808.889) / 2, (1405.681 + 1423.830) / 2, "Brooklyn Bridge span");
			AssertRasterEcho(radar.View, engine.Configuration.Spokes, state, (792.018 + 808.889) / 2, (1405.681 + 1423.830) / 2, "Brooklyn Bridge span");
			yield return AssertHeadingLine(radar.View, state.Heading);

			// Hidden in the chase view, under the picker and the debrief; the keys act only on a visible display.
			var rangeBefore = engine.RangeIndex;
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			yield return null;
			Assert.That(displays.RadarVisible, Is.False);
			Assert.That(displays.ChartplotterVisible, Is.False);
			Assert.That(displays.Mapper.Enabled, Is.False, "no display key works outside the wheelhouse");
			Assert.That(radar.enabled, Is.False, "a hidden radar does not sweep");
			Assert.That(chartplotter.Visible, Is.False);
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			yield return null;
			Assert.That(displays.RadarVisible, Is.True);
			Assert.That(displays.ChartplotterVisible, Is.True);
			Assert.That(displays.Mapper.Enabled, Is.True);
			ui.View.PickerVisible = true;
			yield return null;
			Assert.That(displays.RadarVisible, Is.False);
			Assert.That(displays.ChartplotterVisible, Is.False);
			Assert.That(displays.Mapper.Enabled, Is.False, "the picker's search field gets every key");
			ui.View.PickerVisible = false;
			yield return null;
			Assert.That(displays.RadarVisible, Is.True);
			Assert.That(engine.RangeIndex, Is.EqualTo(rangeBefore));
			displays.ToggleRadar();
			yield return null;
			Assert.That(displays.RadarVisible, Is.False);
			Assert.That(displays.ChartplotterVisible, Is.True, "toggled independently");
			displays.ToggleRadar();
			yield return null;
			Assert.That(displays.RadarVisible, Is.True);

			// A new run reconnects both to the new traffic, the way the water forwarder rebinds.
			var oldTraffic = runner.Traffic;
			var generation = chartplotter.Targets.Generation;
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			ui.View.PickerVisible = false;
			yield return null;
			yield return null;
			Assert.That(runner.Traffic, Is.Not.SameAs(oldTraffic));
			Assert.That(radar.Traffic, Is.SameAs(runner.Traffic), "the radar follows the new run's traffic");
			Assert.That(chartplotter.Targets.Generation, Is.GreaterThan(generation), "the chartplotter saw the new traffic");
			Assert.That(chartplotter.Targets.Count, Is.EqualTo(runner.Traffic.Pack.Vessels.Count));
			Assert.That(HarborChartLoad.StartedCount, Is.EqualTo(1), "the chart loaded once for both runs");
		}

		/// <summary>A target that the engine must echo at its bearing and range from own ship, within the current scale.</summary>
		private static void AssertEcho(RadarEngine engine, VesselState state, double north, double east, string what)
		{
			var dn = north - state.North;
			var de = east - state.East;
			var range = RadarMath.Range(dn, de);
			Assert.That(range, Is.LessThan(engine.Scale.RangeMetres), what + " is inside the range scale");
			var bearing = RadarMath.Bearing(dn, de);
			var spoke = (int)Math.Round(bearing / RadarMath.Tau * engine.Configuration.Spokes) % engine.Configuration.Spokes;
			var bin = engine.Scale.Bin(range);
			var peak = 0f;
			for (var s = -2; s <= 2; s++)
			{
				for (var b = -3; b <= 3; b++)
				{
					var at = Math.Max(0, Math.Min(engine.Scale.Bins - 1, bin + b));
					peak = Math.Max(peak, engine.Echo(((spoke + s) % engine.Configuration.Spokes + engine.Configuration.Spokes) % engine.Configuration.Spokes, at));
				}
			}

			TestContext.WriteLine(what + ": bearing " + (bearing * 180 / Math.PI).ToString("F1") + "°T, range " + range.ToString("F0") + " m, spoke " + spoke + ", bin " + bin + ", peak echo " + peak.ToString("F2"));
			Assert.That(peak, Is.GreaterThan(0.3f), what + " echoes on its bearing");
		}

		/// <summary>The same echo in the drawn texture: pixels of that spoke and bin carry the echo colour (day palette: green over the console).</summary>
		private static void AssertRasterEcho(RadarView view, int spokes, VesselState state, double north, double east, string what)
		{
			var raster = view.Raster;
			var dn = north - state.North;
			var de = east - state.East;
			var spoke = (int)Math.Round(RadarMath.Bearing(dn, de) / RadarMath.Tau * spokes) % spokes;
			var pixels = raster.Texture.GetPixels32();
			var size = raster.Size;
			var found = 0;
			var echo = 0;
			for (var y = 0; y < size; y++)
			{
				for (var x = 0; x < size; x++)
				{
					var at = raster.SpokeAt(x, y);
					if (at < 0)
					{
						continue;
					}

					var apart = Math.Abs(at - spoke);
					if (apart > 1 && apart < spokes - 1)
					{
						continue;
					}

					found++;
					var c = pixels[y * size + x];
					if (c.g > c.r + 40)
					{
						echo++;
					}
				}
			}

			TestContext.WriteLine(what + ": " + found + " raster pixels on the bearing, " + echo + " drawn in the echo colour");
			Assert.That(found, Is.GreaterThan(0));
			Assert.That(echo, Is.GreaterThan(0), what + " is drawn in the echo colour on its bearing");
		}

		/// <summary>The own-ship heading line in the drawn radar: pixels along the heading from the PPI centre carry the overlay's line colour.</summary>
		private IEnumerator AssertHeadingLine(RadarView view, double heading)
		{
			// Two whole frames so the panel has repainted into the texture (WaitForEndOfFrame never fires in a batchmode editor).
			yield return null;
			yield return null;
			var plot = view.Q("ppi").worldBound;
			var centre = plot.center;
			var radius = plot.width / 2f;
			var texture = Read(_uiTarget);
			try
			{
				var line = InstrumentPalette.Day.TextDim;
				var background = InstrumentPalette.Day.Console;
				var up = view.HeadUp ? heading : 0.0;
				var angle = heading - up;
				var dx = (float)Math.Sin(angle);
				var dy = -(float)Math.Cos(angle);
				var onLine = 0;
				var total = 0;
				for (var r = radius * 0.3f; r < radius * 0.9f; r += 4f)
				{
					var x = centre.x + dx * r;
					var y = centre.y + dy * r;
					var best = float.MaxValue;
					var brightest = 0f;
					for (var a = -1; a <= 1; a++)
					{
						for (var b = -1; b <= 1; b++)
						{
							var c = texture.GetPixel((int)x + a, Height - (int)y + b);
							best = Math.Min(best, Mathf.Abs(c.r - line.r) + Mathf.Abs(c.g - line.g) + Mathf.Abs(c.b - line.b));
							brightest = Math.Max(brightest, c.maxColorComponent);
						}
					}

					total++;
					if (best < 0.45f && brightest > background.maxColorComponent + 0.1f)
					{
						onLine++;
					}
				}

				TestContext.WriteLine("heading line: " + onLine + " of " + total + " samples along " + (heading * 180 / Math.PI).ToString("F1") + "°T carry the line colour");
				Assert.That(onLine, Is.GreaterThanOrEqualTo(total / 2), "the heading line is drawn from own ship");
			}
			finally
			{
				Object.Destroy(texture);
			}
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator RecordingDigestIsIdenticalWithAndWithoutTheDisplays_AndAReplayReconnectsThem()
		{
			RequireChart();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			var displays = services.Displays;
			var ui = bootstrap.WheelhouseUI;
			yield return null;
			ui.View.PickerVisible = false;
			var folder = Path.Combine(Folder, "recordings", Guid.NewGuid().ToString("N"));
			runner.RecordRuns = true;
			runner.RecordingDirectory = folder;
			var catalog = services.HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[0].Id, traffic: true);
			// The runner's own Update advances unscaled frame time times the time scale: zero, so only the test's exact frames step.
			Time.timeScale = 0f;
			string pathA = null, pathB = null;
			long stepsA = -1;
			foreach (var shown in new[] { false, true })
			{
				displays.RadarShown = shown;
				displays.ChartplotterShown = shown;
				Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
				Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
				ui.View.PickerVisible = false;
				yield return HarborSceneFixture.WaitReady(bootstrap, shown ? "with displays" : "without displays");
				if (shown)
				{
					var waited = Time.realtimeSinceStartupAsDouble;
					while (!displays.Connected)
					{
						Assert.That(displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), displays.Load.Error);
						Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180));
						yield return null;
					}

					yield return null;
					Assert.That(displays.RadarVisible, Is.True);
					Assert.That(displays.ChartplotterVisible, Is.True);
				}

				runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.1));
				runner.Resume();
				var stepsBefore = runner.Driver.StepCount;
				for (var frame = 0; frame < 600; frame++)
				{
					runner.AdvanceFrame(1.0 / 60);
					yield return null;
				}

				var steps = runner.Driver.StepCount - stepsBefore;
				Assert.That(steps, Is.InRange(990, 1000), "about ten simulated seconds");
				if (stepsA < 0) stepsA = steps; else Assert.That(steps, Is.EqualTo(stepsA), "the same frames step the same count with and without the displays");
				Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
				if (shown)
				{
					Assert.That(displays.Radar.PresentTargets, Is.GreaterThan(0), "the radar sampled the run's traffic while it recorded");
					Assert.That(displays.Chartplotter.View.Ranking.Considered, Is.GreaterThan(0));
				}

				runner.EndRun();
				Assert.That(runner.LastRecordingPath, Is.Not.Null, runner.RecordingError);
				if (shown) pathB = runner.LastRecordingPath; else pathA = runner.LastRecordingPath;
			}

			var a = RecordedRun.Load(pathA);
			var b = RecordedRun.Load(pathB);
			Assert.That(a.StateDigest, Is.Not.Null.And.Not.Empty);
			Assert.That(b.StateDigest, Is.EqualTo(a.StateDigest), "the displays never change a recording");
			Assert.That(b.Rows.Count, Is.EqualTo(a.Rows.Count));
			Assert.That(b.StepCount, Is.EqualTo(a.StepCount));
			File.WriteAllText(Path.Combine(Folder, "recording-digest.txt"), "without displays " + pathA + "\n  state_digest " + a.StateDigest + "\nwith displays " + pathB + "\n  state_digest " + b.StateDigest + "\nrows " + a.Rows.Count + " / " + b.Rows.Count + "\n");
			TestContext.WriteLine("state digests: " + a.StateDigest + " (without) / " + b.StateDigest + " (with)");

			// A replay of A, with the displays shown, reconnects both to the replay's traffic before the first frame draws.
			var oldTraffic = runner.Traffic;
			var generation = displays.Chartplotter.Targets.Generation;
			runner.RecordRuns = false;
			runner.InitializeReplay(a);
			Assert.That(runner.IsReplaying, Is.True);
			runner.Resume();
			runner.AdvanceFrame(0.05);
			yield return null;
			yield return null;
			Assert.That(runner.Traffic, Is.Not.SameAs(oldTraffic));
			Assert.That(displays.Radar.Traffic, Is.SameAs(runner.Traffic), "the radar follows the replay's traffic");
			Assert.That(displays.Chartplotter.Targets.Generation, Is.GreaterThan(generation));
			Assert.That(displays.Chartplotter.Targets.Count, Is.EqualTo(runner.Traffic.Pack.Vessels.Count));
			Assert.That(displays.Radar.PresentTargets, Is.GreaterThan(0));
		}

		/// <summary>Review item 3: reloading the scene composes again with its own runtime pack; the load is shared and the first scene's runtime pack is collectable.</summary>
		[UnityTest, Category("HarborRealData")]
		public IEnumerator ASecondComposition_SharesTheChartLoad_AndFreesTheFirstRuntimePack()
		{
			RequireChart();
			yield return HarborSceneFixture.Load();
			var first = (HarborIntegrationServices)HarborSceneFixture.Bootstrap().Services;
			var firstLoad = first.Displays.Load;
			var waited = Time.realtimeSinceStartupAsDouble;
			while (!first.Displays.Connected)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180));
				yield return null;
			}

			var firstRuntime = new WeakReference(first.Pack);
			Assert.That(first.Displays.Chartplotter.Chart.RuntimeDigest, Is.EqualTo(first.Pack.Digest));
			first = null;
			yield return HarborSceneFixture.Unload();
			yield return HarborSceneFixture.Load();
			var second = (HarborIntegrationServices)HarborSceneFixture.Bootstrap().Services;
			waited = Time.realtimeSinceStartupAsDouble;
			while (!second.Displays.Connected)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180));
				yield return null;
			}

			Assert.That(second.Displays.Load, Is.SameAs(firstLoad), "the second composition shares the one chart load");
			Assert.That(second.Displays.Chartplotter.Chart, Is.SameAs(firstLoad.Tessellation));
			Assert.That(second.Displays.Chartplotter.Chart.RuntimeDigest, Is.EqualTo(second.Pack.Digest));
			for (var i = 0; i < 10 && firstRuntime.IsAlive; i++)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
				yield return null;
			}

			Assert.That(firstRuntime.IsAlive, Is.False, "the first scene's runtime pack is not retained by the shared load");
			GC.KeepAlive(second);
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
