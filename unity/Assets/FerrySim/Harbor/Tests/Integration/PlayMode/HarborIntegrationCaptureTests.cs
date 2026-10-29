using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
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
	/// Visual evidence of the integrated harbor: the checked-in scene with the real water, navigation, skyline and
	/// traffic, from the wheelhouse and the chase view. Pier 11 is a catalog start on a traffic day; the Brooklyn Bridge
	/// and St. George views are fixture starts on the water (St. George's face is refused, so no berth start exists there).
	/// </summary>
	public class HarborIntegrationCaptureTests
	{
		private const int Width = 1600, Height = 900;
		private RenderTexture _uiTarget;
		private PanelSettings _panel;
		private RenderTexture _frameTarget;
		private string _folder;
		private readonly StringBuilder _index = new StringBuilder("view,scenario,north_m,east_m,heading_deg,start_utc,traffic_active,barges,aids_drawn,water_generation,ready_seconds\n");

		[UnityTearDown]
		public IEnumerator UnloadHarborScene() => HarborSceneFixture.Unload();

		[UnityTest, Explicit("Needs the copied harbor data; run by exact test name for visual review."), Category("HarborRealData")]
		public IEnumerator Pier11DayAndNight_BrooklynBridge_StGeorge_WheelhouseAndChase()
		{
			HarborSceneFixture.RequireData();
			_folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/harbor-integration/captures");
			Directory.CreateDirectory(_folder);
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			var ui = bootstrap.WheelhouseUI;
			_uiTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_uiTarget.Create();
			_panel = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
			ui.enabled = false;
			ui.Configure(runner, _panel);
			ui.enabled = true;
			ui.DaylightSource = bootstrap.Context.Sun;
			yield return null;
			var caption = new Label("INTEGRATED HARBOR · real water, navigation, skyline and traffic") { pickingMode = PickingMode.Ignore };
			caption.style.position = Position.Absolute;
			caption.style.top = 8;
			caption.style.left = 12;
			caption.style.fontSize = 15;
			caption.style.color = Color.white;
			caption.style.backgroundColor = new Color(0.02f, 0.04f, 0.06f, 0.9f);
			caption.style.paddingLeft = 10;
			caption.style.paddingRight = 10;
			ui.Document.rootVisualElement.Add(caption);
			var camera = bootstrap.CameraRig.GetComponent<Camera>();
			camera.aspect = (float)Width / Height;
			_frameTarget = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_frameTarget.Create();
			bootstrap.CameraRig.Chase.Distance = 65;
			bootstrap.CameraRig.Chase.Height = 24;
			try
			{
				var catalog = services.HarborCatalog;
				var vessel = catalog.Vessels[0].Id;
				foreach (var (key, minutes) in new[] { ("pier-11-day", HarborSceneFixture.BusiestLocalMinute), ("pier-11-night", 3 * 60) })
				{
					var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, minutes, vessel, traffic: true);
					Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
					yield return CaptureBoth(bootstrap, services, ui, camera, key, settings);
				}

				// Fixture viewpoints on the water (not berth starts): approaching the Brooklyn Bridge from the south-west, and St. George from the north-east.
				yield return CaptureBoth(bootstrap, services, ui, camera, "brooklyn-bridge-day",
					HarborSceneFixture.FixtureStart("Brooklyn Bridge viewpoint", 520, 1420, 45, "2025-07-16T16:18:00Z", true));
				yield return CaptureBoth(bootstrap, services, ui, camera, "st-george-day",
					HarborSceneFixture.FixtureStart("St. George viewpoint", -5850, -4700, 246, "2025-07-16T16:18:00Z", true));
				File.WriteAllText(Path.Combine(_folder, "scope.txt"),
					"The checked-in Harbor.unity with its real services: the harbor water service bound per run (canonical datum plus the day's station-blend tide), its 100 m visual patches,\n" +
					"the resident navigation world (barges, aids, bridges, labels), the terrain and policy-B skyline tilesets, and the day's AIS traffic through TrafficView.ConnectHarbor.\n" +
					"Pier 11 is a catalog start (stop 87, " + HarborSceneFixture.TrafficDay + ", T-class, traffic on) at 12:18 and 03:00 local; the Brooklyn Bridge and St. George views are fixture starts on the water, not berth starts.\n" +
					"Each view took two real physics steps before the capture so the wheelhouse instruments show a live sample. Night is the sun's position at 03:00 local; no fog.\n" +
					"These are rendering captures for visual review, not start-safety, survey, performance or installed-player acceptance.\n");
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

		private IEnumerator CaptureBoth(HarborBootstrap bootstrap, HarborIntegrationServices services, WheelhouseUI ui, Camera camera, string key, ScenarioSettings settings)
		{
			Assert.That(bootstrap.TryStart(settings, out var problem), Is.True, key + ": " + problem);
			ui.View.PickerVisible = false;
			var started = Time.realtimeSinceStartupAsDouble;
			yield return HarborSceneFixture.WaitReady(bootstrap, key);
			var ready = Time.realtimeSinceStartupAsDouble - started;
			// Two real steps so the instruments show a live sample, then freeze the view.
			bootstrap.Runner.Resume();
			bootstrap.Runner.AdvanceFrame(0.02);
			bootstrap.Runner.Pause();
			Assert.That(bootstrap.Runner.HasTelemetry, Is.True, key);
			Assert.That(bootstrap.Runner.Hold, Is.EqualTo(RunHold.None), key);
			// Let the tiles, traffic and water settle around the new start.
			for (var i = 0; i < 30; i++) yield return null;
			foreach (var mode in new[] { CameraRig.Mode.Wheelhouse, CameraRig.Mode.Chase })
			{
				bootstrap.CameraRig.CurrentMode = mode;
				if (mode == CameraRig.Mode.Wheelhouse) bootstrap.CameraRig.Wheelhouse.Snap();
				else bootstrap.CameraRig.Chase.Snap();
				yield return null;
				yield return null;
				var view = key + "-" + mode.ToString().ToLowerInvariant();
				camera.targetTexture = _frameTarget;
				for (var i = 0; i < 12; i++) { camera.Render(); yield return null; }
				camera.targetTexture = null;
				Capture(camera, Path.Combine(_folder, view + ".png"));
				var state = bootstrap.Runner.State;
				_index.Append(view).Append(',').Append(settings.name).Append(',').Append(Number(state.North)).Append(',').Append(Number(state.East)).Append(',')
					.Append(Number(settings.startHeadingDegrees)).Append(',').Append(settings.scenarioStartUtc).Append(',').Append(services.Traffic.ActiveCount).Append(',')
					.Append(services.Navigation.Barges.Count).Append(',').Append(services.Navigation.Aids.Count).Append(',').Append(services.Water.Generation).Append(',').Append(Number(ready)).Append('\n');
				TestContext.WriteLine(view + ": ready " + Number(ready) + " s, traffic active " + services.Traffic.ActiveCount);
			}
		}

		private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

		private void Capture(Camera camera, string path)
		{
			var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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

				Assert.That(overlayPixels, Is.GreaterThan(1000), "the wheelhouse UI rendered");
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
