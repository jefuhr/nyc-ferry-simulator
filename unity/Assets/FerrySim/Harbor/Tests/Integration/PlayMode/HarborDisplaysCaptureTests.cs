using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Chartplotter;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>
	/// Visual evidence of the wheelhouse with both displays on the integrated harbor: Pier 11 by day and by night (the
	/// palettes following the sun), and the Upper Bay with the day's traffic; the Pier 11 day view also at 2560 × 1440.
	/// Written under artifacts/m3w3c-integration/captures for review; not a performance or installed-player acceptance.
	/// </summary>
	public class HarborDisplaysCaptureTests
	{
		private RenderTexture _uiTarget;
		private PanelSettings _panel;
		private RenderTexture _frameTarget;
		private string _folder;
		private readonly StringBuilder _index = new StringBuilder("capture,scenario,width,height,north_m,east_m,heading_deg,start_utc,radar_palette,chart_palette,radar_range_nm,radar_echo_bins,chart_span_nm,chart_targets_drawn,traffic_active,chart_ready_s\n");

		[UnityTearDown]
		public IEnumerator UnloadHarborScene() => HarborSceneFixture.Unload();

		[UnityTest, Explicit("Needs the copied harbor data and chart; run by exact test name for visual review."), Category("HarborRealData")]
		public IEnumerator Pier11DayAndNight_UpperBayTraffic_WheelhouseWithBothDisplays()
		{
			HarborSceneFixture.RequireData();
			if (!Directory.Exists(Path.Combine(HarborSceneFixture.Repo, "data", "processed", "harbor", "chart"))) Assert.Ignore("The chart pack is not copied.");
			_folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/m3w3c-integration/captures");
			Directory.CreateDirectory(_folder);
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			var displays = services.Displays;
			var ui = bootstrap.WheelhouseUI;
			var camera = bootstrap.CameraRig.GetComponent<Camera>();
			try
			{
				var catalog = services.HarborCatalog;
				var vessel = catalog.Vessels[0].Id;
				var day = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, HarborSceneFixture.BusiestLocalMinute, vessel, traffic: true);
				var night = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, 3 * 60, vessel, traffic: true);
				Assert.That(catalog.TryCreate(day, out var daySettings, out var problem), Is.True, problem);
				Assert.That(catalog.TryCreate(night, out var nightSettings, out problem), Is.True, problem);
				var upperBay = HarborSceneFixture.FixtureStart("Upper Bay viewpoint", -5850, -4700, 246, "2025-07-16T16:18:00Z", true, 8);
				yield return Resize(bootstrap, ui, camera, 1280, 720);
				yield return CaptureView(bootstrap, services, ui, camera, "pier-11-day-wheelhouse-displays", daySettings, 1280, 720, radarRange: 3, chartSpan: 1.0);
				Assert.That(displays.Radar.View.Palette, Is.EqualTo(RadarPalette.Day));
				Assert.That(displays.Chartplotter.View.Palette, Is.EqualTo(ChartPaletteKind.Day));
				yield return CaptureView(bootstrap, services, ui, camera, "pier-11-night-wheelhouse-displays", nightSettings, 1280, 720, radarRange: 3, chartSpan: 1.0);
				Assert.That(displays.Radar.View.Palette, Is.EqualTo(RadarPalette.Night), "the radar follows the night");
				Assert.That(displays.Chartplotter.View.Palette, Is.EqualTo(ChartPaletteKind.Night), "the chartplotter follows the night");
				yield return CaptureView(bootstrap, services, ui, camera, "upper-bay-traffic-wheelhouse-displays", upperBay, 1280, 720, radarRange: 5, chartSpan: 4.0);
				yield return Resize(bootstrap, ui, camera, 2560, 1440);
				yield return CaptureView(bootstrap, services, ui, camera, "pier-11-day-wheelhouse-displays-1440p", daySettings, 2560, 1440, radarRange: 3, chartSpan: 1.0);
				File.WriteAllText(Path.Combine(_folder, "scope.txt"),
					"The checked-in Harbor.unity with its real services and the shared chart pack load, the wheelhouse view with the chartplotter (upper left) and the compact radar (upper right) shown by their keys.\n" +
					"Pier 11 is a catalog start (stop 87, " + HarborSceneFixture.TrafficDay + ", T-class, traffic on) at 12:18 and 03:00 local; the Upper Bay view is a fixture start on the water off St. George at 8 kn, not a berth start.\n" +
					"Each view took real physics steps so the instruments and the displays show a live run; the radar had swept at least one revolution. Night is the sun's position at 03:00 local, which both displays follow.\n" +
					"The 1440p capture is the same 1280x720 reference layout scaled 2x by the wheelhouse panel settings. Rendering captures for visual review; not start-safety, survey, performance or installed-player acceptance.\n");
			}
			finally
			{
				File.WriteAllText(Path.Combine(_folder, "index.csv"), _index.ToString());
				camera.targetTexture = null;
				if (_panel != null) Object.Destroy(_panel);
				if (_uiTarget != null) { _uiTarget.Release(); Object.Destroy(_uiTarget); }
				if (_frameTarget != null) { _frameTarget.Release(); Object.Destroy(_frameTarget); }
			}
		}

		private IEnumerator Resize(HarborBootstrap bootstrap, WheelhouseUI ui, Camera camera, int width, int height)
		{
			if (_panel != null) Object.Destroy(_panel);
			if (_uiTarget != null) { _uiTarget.Release(); Object.Destroy(_uiTarget); }
			if (_frameTarget != null) { _frameTarget.Release(); Object.Destroy(_frameTarget); }
			_uiTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_uiTarget.Create();
			_panel = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
			ui.enabled = false;
			ui.Configure(bootstrap.Runner, _panel);
			ui.enabled = true;
			ui.DaylightSource = bootstrap.Context.Sun;
			camera.aspect = (float)width / height;
			_frameTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_frameTarget.Create();
			yield return null;
			ui.View.PickerVisible = false;
			yield return null;
		}

		private IEnumerator CaptureView(HarborBootstrap bootstrap, HarborIntegrationServices services, WheelhouseUI ui, Camera camera, string key, ScenarioSettings settings, int width, int height, int radarRange = -1, double chartSpan = double.NaN)
		{
			var displays = services.Displays;
			var started = Time.realtimeSinceStartupAsDouble;
			Assert.That(bootstrap.TryStart(settings, out var problem), Is.True, key + ": " + problem);
			ui.View.PickerVisible = false;
			yield return HarborSceneFixture.WaitReady(bootstrap, key);
			while (!displays.Connected)
			{
				Assert.That(displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(180), key + ": the chart did not become ready");
				yield return null;
			}

			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			bootstrap.CameraRig.Wheelhouse.Snap();
			displays.RadarShown = true;
			displays.ChartplotterShown = true;
			yield return null;
			if (radarRange >= 0) displays.Radar.View.ChangeRange(radarRange - displays.Radar.Engine.RangeIndex);
			if (!double.IsNaN(chartSpan)) displays.Chartplotter.View.SpanNauticalMiles = chartSpan;
			// Under way for a few seconds so the radar sweeps a revolution and the plotter has a track, then frozen for the capture.
			bootstrap.Runner.Resume();
			var driveStarted = Time.realtimeSinceStartupAsDouble;
			while (bootstrap.Runner.Driver.SimulationTime < 4.0 || HarborDisplays.CountEchoBins(displays.Radar.Engine) < 200)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - driveStarted, Is.LessThan(60), key + ": no radar picture");
				yield return null;
			}

			bootstrap.Runner.Pause();
			Assert.That(bootstrap.Runner.HasTelemetry, Is.True, key);
			Assert.That(bootstrap.Runner.Hold, Is.EqualTo(RunHold.None), key);
			for (var i = 0; i < 20; i++) yield return null;
			camera.targetTexture = _frameTarget;
			for (var i = 0; i < 12; i++) { camera.Render(); yield return null; }
			camera.targetTexture = null;
			Capture(camera, Path.Combine(_folder, key + ".png"), width, height);
			var state = bootstrap.Runner.State;
			var view = displays.Chartplotter.View;
			_index.Append(key).Append(",\"").Append(settings.name.Replace("\"", "\"\"")).Append("\",").Append(width).Append(',').Append(height).Append(',').Append(Number(state.North)).Append(',').Append(Number(state.East)).Append(',')
				.Append(Number(settings.startHeadingDegrees)).Append(',').Append(settings.scenarioStartUtc).Append(',').Append(displays.Radar.View.Palette).Append(',').Append(view.Palette).Append(',')
				.Append(Number(displays.Radar.Engine.Scale.RangeMetres / 1852)).Append(',').Append(HarborDisplays.CountEchoBins(displays.Radar.Engine)).Append(',').Append(Number(view.SpanNauticalMiles)).Append(',')
				.Append(view.Targets.TargetsDrawn).Append(',').Append(services.Traffic.ActiveCount).Append(',').Append(Number(displays.ChartReadySeconds)).Append('\n');
			TestContext.WriteLine(key + ": radar " + displays.Radar.View.Palette + " " + Number(displays.Radar.Engine.Scale.RangeMetres / 1852) + " nm, chart " + view.Palette + " " + view.SpanNauticalMiles + " NM, targets drawn " + view.Targets.TargetsDrawn + ", traffic active " + services.Traffic.ActiveCount);
			displays.RadarShown = false;
			displays.ChartplotterShown = false;
			yield return null;
		}

		private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

		private void Capture(Camera camera, string path, int width, int height)
		{
			var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Texture2D world = null, overlay = null;
#if UNITY_EDITOR
			var asyncShaders = ShaderUtil.allowAsyncCompilation;
			ShaderUtil.allowAsyncCompilation = false;
#endif
			try
			{
				camera.targetTexture = target;
				camera.Render();
				world = Read(target);
				overlay = Read(_uiTarget);
				var pixels = world.GetPixels32();
				var top = overlay.GetPixels32();
				var overlayPixels = 0;
				for (var i = 0; i < pixels.Length; i++)
				{
					if (top[i].a > 0) overlayPixels++;
					var a = top[i].a / 255f;
					pixels[i] = new Color32((byte)Mathf.Min(255, top[i].r + pixels[i].r * (1 - a)),
						(byte)Mathf.Min(255, top[i].g + pixels[i].g * (1 - a)), (byte)Mathf.Min(255, top[i].b + pixels[i].b * (1 - a)), 255);
				}

				Assert.That(overlayPixels, Is.GreaterThan(width * height / 4), "the wheelhouse UI and both displays rendered");
				world.SetPixels32(pixels);
				world.Apply();
				File.WriteAllBytes(path, world.EncodeToPNG());
			}
			finally
			{
#if UNITY_EDITOR
				ShaderUtil.allowAsyncCompilation = asyncShaders;
#endif
				camera.targetTexture = previous;
				RenderTexture.active = active;
				if (world != null) Object.Destroy(world);
				if (overlay != null) Object.Destroy(overlay);
				RenderTexture.ReleaseTemporary(target);
			}
		}

		private static Texture2D Read(RenderTexture target)
		{
			RenderTexture.active = target;
			var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
			texture.Apply();
			return texture;
		}
	}
}
