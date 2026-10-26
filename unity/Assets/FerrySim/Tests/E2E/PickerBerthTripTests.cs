using System;
using System.Collections;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.E2E
{
	/// <summary>Picker-driven acceptance: actual berths, actual traffic, recorded fog and rendered night hulls.</summary>
	public class PickerBerthTripTests
	{
		private SliceBootstrap _bootstrap;
		private string Folder => Path.Combine(TripData.Root, "artifacts/pier11-and-picker");
		private VesselSimulationRunner Runner => _bootstrap.Runner;
		private ScenarioPicker Picker => _bootstrap.WheelhouseUI.View.Picker;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			Directory.CreateDirectory(Folder);
			ScenarioPicker.SessionChoice = null;
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TripData.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#endif
			yield return null; yield return null;
			_bootstrap = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(_bootstrap.LoadError, Is.Null);
			Runner.RecordingDirectory = Path.Combine(Folder, "recordings");
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			if (_bootstrap != null && _bootstrap.Runner != null) Runner.EndRun();
			ScenarioPicker.SessionChoice = null;
			var empty = SceneManager.CreateScene("picker acceptance cleanup");
			SceneManager.SetActiveScene(empty);
			for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
			{
				var scene = SceneManager.GetSceneAt(i);
				if (scene != empty) yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		private void Choose(string berth, int minute, bool traffic = false, int visibility = 0)
		{
			_bootstrap.WheelhouseUI.View.PickerVisible = true;
			var catalog = _bootstrap.ScenarioCatalog;
			Picker.Landings.SelectedIndex = catalog.Landings.ToList().FindIndex(l => l.StopId == "87");
			Picker.Days.SelectedIndex = catalog.Days.ToList().FindIndex(d => d.Date == "2025-07-14");
			Picker.Berths.SelectedIndex = catalog.Landings.Single(l => l.StopId == "87").Berths.ToList().FindIndex(b => b.Id == berth);
			Picker.Time.Minutes = minute;
			Picker.Traffic.Value = traffic;
			Picker.Visibility.SelectedIndex = visibility;
		}

		[UnityTest]
		public IEnumerator NorthBerth_StartsBowOnAndClear_FromThePicker()
		{
			Choose("enc:0226111D16AAB198", 12 * 60);
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Pause();
			var expected = LandingApproach.BowOn(_bootstrap.Landings.BerthFace("87", "enc:0226111D16AAB198"), Runner.Parameters,
				SliceScenarioCatalog.StartBowClearance, "expected", "vessels/t-class.json", Runner.Scenario.scenarioStartUtc);
			Assert.That((Runner.Scenario.StartPosition - expected.StartPosition).Magnitude, Is.LessThan(1e-6));
			Assert.That(Runner.State.Heading, Is.EqualTo(expected.StartHeading).Within(1e-9));
			Assert.That(FaceClearance.Check(Runner.Parameters, Runner.State.North, Runner.State.East, Runner.State.Heading, _bootstrap.Landings.Geometry.Segments).IsClear(), Is.True);
			yield return null;
		}

		[UnityTest]
		public IEnumerator TrafficLeaveOut_AndQuarterMileFog_AreDrivenByThePickerAndRecorded()
		{
			Choose("enc:0226015B0512B198", 18 * 60, true, 3);
			Assert.That(Picker.LeaveOut.Value, Is.True);
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Driver.Controls = new ConstantControls(ControlInputs.Neutral);
			for (var i = 0; i < 10; i++) yield return null;
			Assert.That(Runner.Traffic.Removed, Is.Not.Null, "the same real south-berth trip used by TrafficTripTests");
			Assert.That(_bootstrap.TrafficView.ShownCount, Is.GreaterThan(0));
			Assert.That(_bootstrap.TrafficView.HullFor(Runner.Traffic.Removed.Mmsi)?.Shown ?? false, Is.False);
			Assert.That(RenderSettings.fog, Is.True);
			Assert.That(RenderSettings.fogDensity, Is.EqualTo((float)MeteorologicalVisibility.ExtinctionCoefficient(463)).Within(1e-6));
			var atmosphere = Object.FindFirstObjectByType<Atmosphere>();
			Assert.That(atmosphere.FogSignal.IsSounding, Is.True);
			Assert.That(atmosphere.FogSignal.Source.isPlaying, Is.True);
			Runner.EndRun();
			Assert.That(File.ReadAllText(Runner.LastRecordingPath), Does.Contain("visibility_m"));
			Assert.That(RecordedRun.Load(Runner.LastRecordingPath).VisibilityMetres, Is.EqualTo(463));
			// A real catalog pointed at an empty traffic root models a player without traffic payloads.
			var empty = Path.Combine(Folder, "no-traffic"); Directory.CreateDirectory(empty);
			var original = _bootstrap.Scenarios.Catalog;
			var vessels = original.Vessels.Select(v => new System.Collections.Generic.KeyValuePair<string, VesselParameters>(v.VesselFile, original.Vessel(v.Id))).ToArray();
			var noTraffic = new SliceScenarioCatalog(_bootstrap.Landings, vessels, Runner.EnvironmentPacks, original.Region,
				trafficPacks: new FerrySim.Core.Serialization.TrafficPacks(empty));
			Picker.Bind(noTraffic, _bootstrap);
			Assert.That(Picker.Traffic.enabledSelf, Is.False);
			Assert.That(Picker.TrafficReason.text, Is.EqualTo(ScenarioPicker.NoTraffic));
		}

		[UnityTest]
		public IEnumerator PickerCaptures_DayAndNight_720pAnd1440p_AndNightHullLights()
		{
			Choose("enc:02261FA117EFB198", 22 * 60, true);
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Driver.Controls = new ConstantControls(ControlInputs.Neutral);
			for (var i = 0; i < 5; i++) yield return null;
			Assert.That(_bootstrap.TrafficView.Hulls.Any(h => h.Shown && h.NavigationLights.On && h.NavigationLights.Lights.Count > 0), Is.True);
			Runner.Pause();
			yield return Capture("night-traffic", 1280, 720, false, PaletteKind.Night);
			foreach (var size in new[] { (1280, 720), (2560, 1440) })
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
				yield return Capture("picker-" + palette.ToString().ToLowerInvariant() + "-" + size.Item2 + "p", size.Item1, size.Item2, true, palette);
		}

		[UnityTest]
		public IEnumerator Pier11Overview_ShowsThreeNamedBerthsAndTheirFenders()
		{
			Choose("enc:02261FA117EFB198", 12 * 60);
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Pause();
			var labels = Object.FindObjectsByType<TextMesh>(FindObjectsSortMode.None);
			foreach (var name in new[] { "Outer pontoon (east)", "North pontoon", "South pontoon" })
			{
				var label = labels.Single(l => l.text.Contains(name));
				Assert.That(label.text, Does.Contain("crew-reported berth"));
				var fenders = label.transform.parent.Find("Fenders");
				Assert.That(fenders, Is.Not.Null, name);
				Assert.That(fenders.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0), name);
			}
			yield return Capture("pier11-three-berths-fenders", 2560, 1440, false, PaletteKind.Day, 135, 90);
			Choose("enc:02261FA117EFB198", 22 * 60);
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Pause();
			yield return Capture("pier11-three-berths-night", 2560, 1440, false, PaletteKind.Night, 135, 90);
		}

		[UnityTest]
		public IEnumerator SouthWilliamsburgLabel_NamesTheReportedLandingSideWithoutClaimingDepth()
		{
			Choose("enc:0226015B0512B198", 18 * 60 + 48);
			Picker.Days.SelectedIndex = _bootstrap.ScenarioCatalog.Days.ToList().FindIndex(d => d.Date == "2025-07-20");
			Picker.Landings.SelectedIndex = _bootstrap.ScenarioCatalog.Landings.ToList().FindIndex(l => l.StopId == "8");
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Pause();
			var barge = Object.FindObjectsByType<LandingBarge>(FindObjectsSortMode.None).Single(b => b.StopId == "8");
			Assert.That(barge.GetComponentInChildren<TextMesh>().text, Does.Contain("crew-reported landing side"));
			Assert.That(barge.GetComponentInChildren<TextMesh>().text, Does.Not.Contain("needs-crew-verification"));
			yield return Capture("south-williamsburg-label-after", 1280, 720, false, PaletteKind.Day, 45, 16);
			Picker.Time.Minutes = 22 * 60;
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Pause();
			yield return Capture("south-williamsburg-label-night", 1280, 720, false, PaletteKind.Night, 45, 16);
		}

		[UnityTest]
		public IEnumerator BusiestNightHour_HullLightsHavePairedFrameCostAndNoStepOrPlacementGarbage()
		{
			var water = Object.FindFirstObjectByType<WaterSurface>();
			WaterSurface.SliceBounds(water.Datum, out var minN, out var maxN, out var minE, out var maxE);
			minN -= 250; maxN += 250; minE -= 250; maxE += 250;
			var catalog = _bootstrap.Scenarios.Catalog;
			string bestDay = null; var bestHour = 0; var most = -1;
			using (var inventory = new StreamWriter(Path.Combine(Folder, "night-traffic-hours.csv")))
			{
				inventory.WriteLine("day,local_hour,vessels_in_slice");
				foreach (var day in catalog.Days.Where(d => d.HasTraffic))
				{
					var pack = Runner.TrafficPacks.Load(day.Date);
					var replay = new FerrySim.Core.Traffic.TrafficReplay(pack);
					for (var hour = 0; hour < 24; hour++)
					{
						var solar = SolarPosition.Compute(pack.EpochUtc.AddHours(hour).UtcDateTime, 40.7, -74.015);
						if (!Daylight.ShowsNavigationLights(solar, 0)) continue;
						var count = 0;
						for (var v = 0; v < pack.Vessels.Count; v++)
							if (replay.TrySample(v, hour * 3600.0, out var sample) && sample.North >= minN && sample.North <= maxN && sample.East >= minE && sample.East <= maxE) count++;
						inventory.WriteLine(day.Date + "," + hour + "," + count);
						if (count > most) { most = count; bestDay = day.Date; bestHour = hour; }
					}
				}
			}
			Assert.That(bestDay, Is.Not.Null);
			Choose("enc:02261FA117EFB198", bestHour * 60, true);
			Picker.Days.SelectedIndex = catalog.Days.ToList().FindIndex(d => d.Date == bestDay);
			Runner.RecordRuns = false;
			Assert.That(Picker.TryStartRun(), Is.True, Picker.Problem);
			Runner.Driver.Controls = new ConstantControls(ControlInputs.Neutral);
			Runner.Pause();
			var view = _bootstrap.TrafficView; view.ViewRange = 12000;
			var result = new Newtonsoft.Json.Linq.JObject { ["day"] = bestDay, ["local_hour"] = bestHour, ["vessels_in_slice"] = most,
				["hulls_built"] = view.Hulls.Count, ["screen_width"] = Screen.width, ["screen_height"] = Screen.height,
				["graphics_device"] = SystemInfo.graphicsDeviceName, ["graphics_api"] = SystemInfo.graphicsDeviceType.ToString(), ["scope"] = "Rendered frame time includes Unity/editor overhead; GC.Alloc marker count covers runner stepping and hull placement, excluding NUnit and render-loop overhead; Mono byte counts are unavailable." };
			foreach (var lights in new[] { false, true })
			{
				Runner.ResetRun(); Runner.Pause(); Runner.Driver.Controls = new ConstantControls(ControlInputs.Neutral);
				view.NavigationLightsEnabled = lights;
				void Frame() { Runner.Resume(); Runner.AdvanceFrame(1.0 / 60.0); Runner.Pause(); view.Place(); }
				for (var i = 0; i < 60; i++) Frame();
				var recorder = Recorder.Get("GC.Alloc");
				recorder.enabled = false; recorder.FilterToCurrentThread(); recorder.enabled = true;
				for (var i = 0; i < 600; i++) Frame();
				recorder.enabled = false; recorder.CollectFromAllThreads();
				var allocated = recorder.sampleBlockCount;
				var values = new double[300]; var placement = new double[300];
				var capture = Time.captureDeltaTime; Time.captureDeltaTime = 1f / 60f; Runner.Resume();
				try
				{
					for (var warm = 0; warm < 30; warm++) yield return null;
					var last = Time.realtimeSinceStartupAsDouble;
					for (var i = 0; i < values.Length; i++)
					{
						yield return null;
						var now = Time.realtimeSinceStartupAsDouble;
						values[i] = (now - last) * 1000; last = now; placement[i] = view.LastMilliseconds;
					}
				}
				finally { Time.captureDeltaTime = capture; Runner.Pause(); }
				Array.Sort(values); Array.Sort(placement);
				result[lights ? "lights_on" : "lights_off"] = new Newtonsoft.Json.Linq.JObject {
					["frame_p50_ms"] = values[150], ["frame_p95_ms"] = values[285], ["frame_max_ms"] = values[299],
					["placement_p50_ms"] = placement[150], ["placement_p95_ms"] = placement[285], ["placement_max_ms"] = placement[299],
					["gc_allocations_per_frame"] = allocated / 600.0, ["hulls_shown"] = view.ShownCount };
				File.WriteAllText(Path.Combine(Folder, "traffic-light-cost.json"), result + "\n");
				Assert.That(allocated, Is.Zero, "runner stepping plus hull placement, 600 frames");
			}
		}

		private IEnumerator Capture(string name, int width, int height, bool picker, PaletteKind palette, float distance = 250, float heightAbove = 100)
		{
			var ui = _bootstrap.WheelhouseUI;
			var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); target.Create();
			var settings = WheelhousePanelSettings.Create(target, Color.clear);
			ui.enabled = false; ui.Configure(Runner, settings); ui.enabled = true;
			for (var i = 0; i < 3; i++) yield return null;
			ui.DaylightSource = null;
			ui.View.Palette = palette;
			ui.View.PickerVisible = picker;
			if (picker) Choose("enc:02261FA117EFB198", 22 * 60, true);
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			_bootstrap.CameraRig.Chase.Distance = distance; _bootstrap.CameraRig.Chase.Height = heightAbove; _bootstrap.CameraRig.Chase.Snap();
			for (var i = 0; i < 5; i++) yield return null;
			if (picker)
			{
				Assert.That(Picker.worldBound.xMin, Is.GreaterThanOrEqualTo(0));
				Assert.That(Picker.worldBound.xMax, Is.LessThanOrEqualTo(ui.View.worldBound.xMax));
				Assert.That(Picker.worldBound.yMax, Is.LessThanOrEqualTo(ui.View.worldBound.yMax));
			}
			var camera = _bootstrap.CameraRig.GetComponent<Camera>();
			var world = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			var old = camera.targetTexture; var active = RenderTexture.active; var aspect = camera.aspect;
			camera.targetTexture = world; camera.aspect = (float)width / height; camera.Render();
			var image = Read(world); var overlay = Read(target); var pixels = image.GetPixels32(); var top = overlay.GetPixels32();
			for (var i = 0; i < pixels.Length; i++)
			{
				var a = top[i].a / 255f;
				pixels[i] = new Color32((byte)Mathf.Min(255, top[i].r + pixels[i].r * (1-a)), (byte)Mathf.Min(255, top[i].g + pixels[i].g * (1-a)), (byte)Mathf.Min(255, top[i].b + pixels[i].b * (1-a)), 255);
			}
			image.SetPixels32(pixels); image.Apply(); File.WriteAllBytes(Path.Combine(Folder, name + ".png"), image.EncodeToPNG());
			camera.targetTexture = old; camera.aspect = aspect; RenderTexture.active = active;
			Object.Destroy(image); Object.Destroy(overlay); RenderTexture.ReleaseTemporary(world);
			ui.enabled = false; ui.Configure(Runner, null); ui.enabled = true;
			Object.Destroy(settings); target.Release(); Object.Destroy(target);
		}

		private static Texture2D Read(RenderTexture target)
		{
			RenderTexture.active = target;
			var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); return image;
		}
	}
}
