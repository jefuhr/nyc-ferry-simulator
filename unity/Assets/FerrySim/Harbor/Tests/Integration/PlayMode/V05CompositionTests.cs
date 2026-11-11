using System;
using System.Collections;
using System.IO;
using FerrySim.Audio;
using FerrySim.Chartplotter;
using FerrySim.Console;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Prompts;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>Real-scene composition failures: competing pause owners, lost settings and absent interior consumers.</summary>
	public class V05CompositionTests : InputTestFixture
	{
		private Keyboard _keyboard;
		private float _listenerVolume;
		public override void Setup()
		{
			base.Setup();
			_listenerVolume = AudioListener.volume; AudioListener.volume = 0;
			_keyboard = InputSystem.AddDevice<Keyboard>();
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			InputBindingService.SetSession(new InputBindingService(defaults));
			Object.Destroy(defaults);
			UserSettingsFiles.ResetSession();
		}

		[UnityTearDown]
		public IEnumerator Unload()
		{
			Time.timeScale = 1;
			yield return HarborSceneFixture.Unload();
			AudioListener.volume = _listenerVolume;
			UserSettingsFiles.ResetSession();
			InputBindingService.ResetSession();
		}

		private static IEnumerator WaitScreens(HarborBootstrap bootstrap)
		{
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var began = Time.realtimeSinceStartupAsDouble;
			while (displays.Screens?.Plotter == null)
			{
				Assert.That(displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - began, Is.LessThan(180));
				yield return null;
			}
		}

		private static IEnumerator StartPier11(int vessel = 0)
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var catalog = ((HarborIntegrationServices)bootstrap.Services).HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay,
				HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[vessel].Id, traffic: false);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			bootstrap.Runner.RecordRuns = false;
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			bootstrap.WheelhouseUI.View.PickerVisible = false;
			yield return HarborSceneFixture.WaitReady(bootstrap, "v05-pier11");
			yield return null;
		}

		[UnityTest]
		public IEnumerator OptionsKey_PausesHoldsHelmAndResumesOnlyItsOwnPause()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var runner = bootstrap.Runner;
			var view = bootstrap.WheelhouseUI.View;
			Assert.That(runner.IsPaused, Is.False);
			PressAndRelease(_keyboard.f10Key);
			yield return null;
			yield return null;
			Assert.That(view.OptionsVisible, Is.True);
			Assert.That(runner.IsPaused, Is.True, "options owns a pause");
			Assert.That(((HarborIntegrationServices)bootstrap.Services).Displays.Presentation.Hints.Suppressed, Is.True);
			var helm = runner.GetComponent<HelmInputController>();
			var before = helm.Mapper.Current;
			PressAndRelease(_keyboard.qKey);
			yield return null;
			Assert.That(helm.Mapper.Current.PortLever, Is.EqualTo(before.PortLever));
			view.OptionsVisible = false;
			Assert.That(runner.IsPaused, Is.False);
			runner.Pause();
			view.OptionsVisible = true;
			view.OptionsVisible = false;
			Assert.That(runner.IsPaused, Is.True, "a prior pause belongs to the player");
		}

		[UnityTest]
		public IEnumerator PickerAndOptions_KeepThePauseUntilBothClose_InEitherOrder()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var view = bootstrap.WheelhouseUI.View;
			var runner = bootstrap.Runner;
			for (var order = 0; order < 2; order++)
			{
				runner.Resume();
				if (order == 0) { view.PickerVisible = true; view.OptionsVisible = true; view.PickerVisible = false; }
				else { view.OptionsVisible = true; view.PickerVisible = true; view.OptionsVisible = false; }
				Assert.That(runner.IsPaused, Is.True, "one dialog remains open");
				view.PickerVisible = view.OptionsVisible = false;
				Assert.That(runner.IsPaused, Is.False, "the last dialog releases the shared pause");
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator SavedSettings_ReachTheCameraAudioConsoleAndHints()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var settings = bootstrap.WheelhouseUI.View.Settings;
			settings.LookSensitivity = 0.73f;
			settings.AlarmVolume = 0.31f;
			settings.Muted = true;
			settings.KeybindHints = KeybindHintMode.Always;
			yield return null;
			Assert.That(bootstrap.CameraRig.GetComponent<WheelhouseCameraController>().LookSensitivity, Is.EqualTo(0.73f));
			var audio = Object.FindFirstObjectByType<FerryAudioRig>();
			Assert.That(audio.Bus.Alarms, Is.EqualTo(0.31f));
			Assert.That(audio.Bus.Muted, Is.True);
			var began = Time.realtimeSinceStartupAsDouble;
			while (!((HarborIntegrationServices)bootstrap.Services).Displays.Connected)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - began, Is.LessThan(180));
				yield return null;
			}
			yield return null;
			var screens = Object.FindFirstObjectByType<ConsoleScreens>();
			Assert.That(screens, Is.Not.Null);
			Assert.That(bootstrap.WheelhouseUI.View.ScreenSource, Is.SameAs(screens));
			Assert.That(screens.TargetColourBytes, Is.GreaterThan(0));
			var hints = Object.FindFirstObjectByType<KeybindPopups>();
			Assert.That(hints, Is.Not.Null);
			Assert.That(hints.Mode, Is.EqualTo(KeybindHintMode.Always));
			settings.ConsoleScreens = false;
			yield return null;
			Assert.That(screens.TargetColourBytes, Is.Zero);
		}

		[UnityTest]
		public IEnumerator PersistedSettings_ReapplyAfterDisposingTheWholeComposition_AndMuteKeySaves()
		{
			var file = Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/settings-restart.json");
			var saved = new SettingsStore(file);
			UserSettingsFiles.SetSession(saved, HudLayoutStore.InMemory());
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			saved.Palette = PaletteSetting.Night;
			saved.DistanceUnit = DistanceUnit.Metres;
			saved.DepthAlarmMetres = 2.8;
			saved.VectorMinutes = 4.5;
			saved.Predictor = PredictorKind.Curved;
			saved.MasterVolume = 0.61f;
			saved.AlarmVolume = 0.27f;
			saved.LookSensitivity = 0.63f;
			saved.KeybindHints = KeybindHintMode.Always;
			saved.Muted = true;
			PressAndRelease(_keyboard.vKey);
			yield return null;
			Assert.That(saved.Muted, Is.False, "the mute key publishes back into settings");
			for (var composition = 0; composition < 2; composition++)
			{
				bootstrap = HarborSceneFixture.Bootstrap();
				yield return WaitScreens(bootstrap);
				for (var frame = 0; frame < 40; frame++) yield return null;
				var screens = ((HarborIntegrationServices)bootstrap.Services).Displays.Screens;
				var view = bootstrap.WheelhouseUI.View;
				Assert.That(screens.Plotter.VectorMinutes, Is.EqualTo(4.5));
				Assert.That(screens.Plotter.Predictor, Is.EqualTo(PredictorMode.Curved));
				Assert.That(screens.Plotter.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
				Assert.That(view.DepthAlarmThresholdMetres, Is.EqualTo(2.8));
				Assert.That(screens.Radar.View.Palette, Is.EqualTo(RadarPalette.Night));
				Assert.That(screens.Plotter.Palette, Is.EqualTo(ChartPaletteKind.Night));
				Assert.That(bootstrap.CameraRig.Wheelhouse.LookSensitivity, Is.EqualTo(0.63f));
				var audio = bootstrap.Runner.GetComponentInChildren<FerryAudioRig>();
				Assert.That(audio.Bus.Alarms, Is.EqualTo(0.27f));
				Assert.That(audio.Bus.Muted, Is.False);
				Assert.That(audio.Bus.Master, Is.EqualTo(AudioLaunchOptions.MasterVolume(Environment.GetCommandLineArgs())), "test launch volume wins");
				Assert.That(view.Settings.MasterVolume, Is.EqualTo(0.61f), "the launch override was not saved");
				Assert.That(((HarborIntegrationServices)bootstrap.Services).Displays.Presentation.Hints.Mode, Is.EqualTo(KeybindHintMode.Always));
				if (composition == 0)
				{
					yield return HarborSceneFixture.Unload();
					var loaded = new SettingsStore(file); Assert.That(loaded.Load(), Is.True, loaded.LastError);
					UserSettingsFiles.SetSession(loaded, HudLayoutStore.InMemory());
					yield return StartPier11();
				}
			}
			bootstrap.WheelhouseUI.View.Settings.ConsoleScreens = false;
			yield return HarborSceneFixture.Unload();
			var disabled = new SettingsStore(file); Assert.That(disabled.Load(), Is.True, disabled.LastError);
			UserSettingsFiles.SetSession(disabled, HudLayoutStore.InMemory());
			yield return StartPier11();
			bootstrap = HarborSceneFixture.Bootstrap();
			var fallback = ((HarborIntegrationServices)bootstrap.Services).Displays;
			fallback.RadarShown = fallback.ChartplotterShown = true;
			yield return null;
			Assert.That(fallback.Screens.TargetColourBytes, Is.Zero, "disabled console survives a composition restart");
			Assert.That(fallback.RadarVisible && fallback.ChartplotterVisible, Is.True);
		}

		[UnityTest]
		public IEnumerator ScreensShareHudPictures_ReleaseOnDisable_StopInChase_AndRebuildOnClassSwitch()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var view = bootstrap.WheelhouseUI.View;
			view.HudLayout.Choose(HudPresets.Everything);
			for (var frame = 0; frame < 80; frame++) yield return null;
			var oldTargets = new Texture[4];
			for (var i = 0; i < 4; i++)
			{
				var id = HudWidgetIds.Screens[i];
				Assert.That(displays.Screens.TryGetScreen(id, out var picture), Is.True);
				oldTargets[i] = picture;
				Assert.That(((ScreenWidget)view.HudLayer.Widget(HudWidgetIds.Screen(id))).Picture.image, Is.SameAs(picture));
				var renderer = bootstrap.Runner.transform.Find(WheelhouseControlsView.RootName).GetComponentsInChildren<Renderer>();
				var mounted = false;
				foreach (var surface in renderer) if (surface.name == ConsoleScreens.MountNames[i] + "Surface") mounted = surface.sharedMaterial.mainTexture == picture;
				Assert.That(mounted, Is.True, "the mount and pop-out share " + id);
				AssertPicture((RenderTexture)picture);
			}
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			yield return null;
			yield return null;
			var before = displays.Screens.RefreshCount("plotter");
			for (var frame = 0; frame < 25; frame++) yield return null;
			Assert.That(displays.Screens.RefreshCount("plotter"), Is.EqualTo(before), "neither mounts nor hidden HUD demand refresh");
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			view.Settings.ConsoleScreens = false;
			yield return null;
			displays.RadarShown = displays.ChartplotterShown = true;
			for (var frame = 0; frame < 4; frame++) yield return null;
			Assert.That(displays.Screens.TargetColourBytes, Is.Zero);
			Assert.That(displays.RadarVisible && displays.ChartplotterVisible, Is.True);
			Assert.That(displays.Chartplotter.View, Is.Not.Null);
			foreach (var target in oldTargets) Assert.That(target == null, Is.True, "disabled targets destroyed");
			view.Settings.ConsoleScreens = true;
			yield return null;
			var catalog = ((HarborIntegrationServices)bootstrap.Services).HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay,
				HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[1].Id, traffic: false);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			yield return HarborSceneFixture.WaitReady(bootstrap, "class switch");
			yield return null;
			Assert.That(Object.FindObjectsByType<ConsoleScreens>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
			Assert.That(Object.FindObjectsByType<KeybindPopups>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
			Assert.That(displays.Screens.TargetColourBytes, Is.EqualTo(9019392));
		}

		private static void AssertPicture(RenderTexture target)
		{
			var previous = RenderTexture.active; RenderTexture.active = target;
			var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			try
			{
				image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
				var pixels = image.GetPixels32(); var changed = 0; var first = pixels[0];
				for (var i = 0; i < pixels.Length; i++) if (!pixels[i].Equals(first)) changed++;
				Assert.That(changed, Is.GreaterThan(100), target.name + " has a picture");
			}
			finally { RenderTexture.active = previous; Object.Destroy(image); }
		}

		[UnityTest]
		public IEnumerator ConsoleToggleKeys_ShowSharedPopoutsWithoutEditingOrSaving_AndLeaveProgrammaticRequestsAlone()
		{
			var folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/popout-toggle", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			var settingsFile = Path.Combine(folder, SettingsStore.FileName);
			var layoutFile = Path.Combine(folder, HudLayoutStore.FileName);
			var settings = new SettingsStore(settingsFile); Assert.That(settings.Save(), Is.True);
			var layouts = new HudLayoutStore(layoutFile);
			Assert.That(layouts.Put(new HudLayoutEntry { name = "fixture", basedOn = HudPresets.Classic }), Is.True, layouts.LastError);
			UserSettingsFiles.SetSession(settings, layouts);
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var view = bootstrap.WheelhouseUI.View;
			view.HudLayout.Choose(HudPresets.Classic);
			var beforeSettings = File.ReadAllBytes(settingsFile); var beforeLayouts = File.ReadAllBytes(layoutFile);
			displays.RadarShown = displays.ChartplotterShown = true;
			yield return null;
			Assert.That(view.HudLayout.IsShown("screen:radar"), Is.False, "programmatic overlay requests create no pop-out");
			Assert.That(view.HudLayout.IsShown("screen:plotter"), Is.False);
			foreach (var id in new[] { "radar", "plotter" })
			{
				var key = id == "radar" ? _keyboard.digit2Key : _keyboard.digit1Key;
				PressAndRelease(key); InputSystem.Update(); displays.Tick();
				yield return null;
				yield return null;
				Assert.That(view.HudLayout.IsShown("screen:" + id), Is.True, "the key shows a readable shared picture");
				Assert.That(displays.Screens.TryGetScreen(id, out var texture), Is.True);
				Assert.That(((ScreenWidget)view.HudLayer.Widget("screen:" + id)).Picture.image, Is.SameAs(texture));
				Assert.That(view.HudLayout.Modified, Is.False);
				Assert.That(File.ReadAllBytes(settingsFile), Is.EqualTo(beforeSettings));
				Assert.That(File.ReadAllBytes(layoutFile), Is.EqualTo(beforeLayouts));
				PressAndRelease(key); InputSystem.Update(); displays.Tick();
				yield return null;
				Assert.That(view.HudLayout.IsShown("screen:" + id), Is.False);
			}
			Assert.That(displays.RadarShown && displays.ChartplotterShown, Is.True, "console keys leave legacy requests alone");
			view.HudLayout.Choose(HudPresets.Everything);
			PressAndRelease(_keyboard.digit2Key); InputSystem.Update(); displays.Tick();
			Assert.That(view.HudLayout.IsShown("screen:radar"), Is.False, "a layout-provided pop-out can be hidden");
			PressAndRelease(_keyboard.digit2Key); InputSystem.Update(); displays.Tick();
			Assert.That(view.HudLayout.IsShown("screen:radar"), Is.True);
			view.HudLayout.Choose(HudPresets.Classic);
			Assert.That(view.HudLayout.IsShown("screen:radar"), Is.False, "choosing a preset clears temporary visibility");
			settings.ConsoleScreens = false;
			yield return null;
			displays.RadarShown = false;
			PressAndRelease(_keyboard.digit2Key); InputSystem.Update(); displays.Tick();
			yield return null;
			Assert.That(displays.RadarVisible, Is.True, "console off keeps the flat overlay");
			Assert.That(view.HudLayout.IsShown("screen:radar"), Is.False);
		}

		[UnityTest]
		public IEnumerator PaletteKeys_PersistAcrossRefreshesAndNewRuns_OnConsoleAndFlatDisplays()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var view = bootstrap.WheelhouseUI.View;
			foreach (var mounted in new[] { true, false })
			{
				view.Settings.ConsoleScreens = mounted;
				displays.RadarShown = displays.ChartplotterShown = true;
				for (var frame = 0; frame < 4; frame++) yield return null;
				var radar = (RadarPalette)(((int)displays.Radar.View.Palette + 1) % 3);
				var chart = (ChartPaletteKind)(((int)displays.ActivePlotter.Palette + 1) % 3);
				PressAndRelease(_keyboard.xKey); PressAndRelease(_keyboard.fKey); InputSystem.Update(); displays.Tick();
				for (var frame = 0; frame < 50; frame++) yield return null;
				Assert.That(displays.Radar.View.Palette, Is.EqualTo(radar));
				Assert.That(displays.ActivePlotter.Palette, Is.EqualTo(chart));
				Assert.That(bootstrap.TryStart(bootstrap.Runner.Scenario, out var problem), Is.True, problem);
				yield return HarborSceneFixture.WaitReady(bootstrap, "palette preference");
				for (var frame = 0; frame < 15; frame++) yield return null;
				Assert.That(displays.Radar.View.Palette, Is.EqualTo(radar));
				Assert.That(displays.ActivePlotter.Palette, Is.EqualTo(chart));
			}
			view.Settings.Palette = PaletteSetting.Night;
			yield return null;
			Assert.That(displays.Radar.View.Palette, Is.EqualTo(RadarPalette.Night));
			Assert.That(displays.ActivePlotter.Palette, Is.EqualTo(ChartPaletteKind.Night));
		}

		[UnityTest]
		public IEnumerator RebindingsReachTheDisplayCopyAndHornHint_AndTextEntryHoldsHelm()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var service = InputBindingService.Instance;
			Assert.That(service.TryApply("Displays/RadarToggle", 0, "Keyboard", "<Keyboard>/f11", out var conflict), Is.True, conflict);
			var toggle = displays.Mapper.Map.FindAction("RadarToggle");
			TestContext.WriteLine("radar action enabled=" + toggle.enabled + " path=" + toggle.bindings[0].effectivePath + " controls=" + toggle.controls.Count + " allowed=" + displays.DisplaysAllowed);
			var fired = 0; displays.Mapper.RadarToggleRequested += () => fired++;
			displays.RadarShown = false;
			PressAndRelease(_keyboard.digit2Key);
			yield return null;
			Assert.That(displays.RadarShown, Is.False);
			PressAndRelease(_keyboard.f11Key);
			InputSystem.Update();
			displays.Tick();
			yield return null;
			Assert.That(displays.RadarShown, Is.False, "console keys leave legacy requests alone");
			Assert.That(bootstrap.WheelhouseUI.View.HudLayout.IsShown("screen:radar"), Is.True, "mapper callbacks=" + fired);
			Assert.That(service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f12", out conflict), Is.True, conflict);
			var hints = displays.Presentation.Hints;
			hints.Layer.Refresh(bootstrap.CameraRig.GetComponent<Camera>());
			Assert.That(hints.Layer.TryGetState("HornButton", out var horn), Is.True);
			Assert.That(horn.KeyText, Does.Contain("F12"), "binding text changed on this frame's refresh");
			var view = bootstrap.WheelhouseUI.View;
			view.HudEditing = true;
			view.HudLayer.EditBar.NameField.Focus();
			yield return null;
			var helm = bootstrap.Runner.GetComponent<HelmInputController>();
			Assert.That(helm.Mapper.Enabled, Is.False);
			var before = helm.Mapper.Current;
			PressAndRelease(_keyboard.qKey);
			Assert.That(helm.Mapper.Current.PortLever, Is.EqualTo(before.PortLever));
			view.HudLayer.EditBar.NameField.Blur();
			yield return null;
			Assert.That(helm.Mapper.Enabled, Is.True);
		}

		[UnityTest]
		public IEnumerator EverythingOn_AndOffProduceIdenticalRecordingBytesAndStateDigest()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var runner = bootstrap.Runner;
			var scenario = runner.Scenario;
			var view = bootstrap.WheelhouseUI.View;
			var audio = runner.GetComponentInChildren<FerryAudioRig>();
			var outputVolume = AudioListener.volume;
			AudioListener.volume = 0;
			Time.timeScale = 0;
			byte[] baseline = null; string baselineInputs = null; string digest = null;
			try
			{
				foreach (var on in new[] { false, true })
				{
					view.Settings.ConsoleScreens = on;
					view.HudLayout.Choose(on ? HudPresets.Everything : HudPresets.Console);
					view.Settings.KeybindHints = on ? KeybindHintMode.Always : KeybindHintMode.Off;
					audio.GraphEnabled = on;
					runner.EndRun(); runner.RecordRuns = true;
					runner.RecordingDirectory = Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/recordings", on ? "on" : "off");
					Assert.That(bootstrap.TryStart(scenario, out var problem), Is.True, problem);
					view.PickerVisible = false;
					yield return HarborSceneFixture.WaitReady(bootstrap, "determinism");
					yield return null;
					runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.1));
					audio.Bus.Master = 1;
					var telemetry = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
					var inputs = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
					var header = RunRecorder.BuildHeader(scenario, runner.Parameters, runner.Driver.Simulation,
						runner.Factory.GetType().FullName, runner.State, runner.RunEnvironment, runner.TelemetrySampleHz);
					var recorder = new RunRecorder(telemetry, inputs, runner.TelemetrySampleHz, header);
					runner.Driver.AddObserver(recorder); runner.Resume();
					for (var frame = 0; frame < 300; frame++) { runner.AdvanceFrame(1.0 / 60); yield return null; }
					recorder.Finish(runner.Driver.StepCount, runner.State, runner.Telemetry);
					runner.Driver.RemoveObserver(recorder); runner.Pause();

					runner.EndRun();
					var recorded = RecordedRun.Load(runner.LastRecordingPath);
					var bytes = System.Text.Encoding.UTF8.GetBytes(telemetry.ToString());
					File.WriteAllBytes(Path.Combine(runner.RecordingDirectory, "canonical.csv"), bytes);
					File.WriteAllText(Path.Combine(runner.RecordingDirectory, "canonical.inputs.csv"), inputs.ToString());
					if (baseline == null) { baseline = bytes; baselineInputs = inputs.ToString(); digest = recorded.StateDigest; }
					else
					{
						Assert.That(bytes, Is.EqualTo(baseline));
						Assert.That(inputs.ToString(), Is.EqualTo(baselineInputs));
						Assert.That(recorded.StateDigest, Is.EqualTo(digest));
						File.WriteAllText(Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/state-digest.txt"), digest + "\nbyte-identical canonical recordings and inputs\n");
					}
				}
			}
			finally { AudioListener.volume = outputVolume; }
		}

		[UnityTest]
		public IEnumerator AlarmKeysAndPanel_ForwardWithoutChangingTheSimulation_AndNewRunReleasesLampTest()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var runner = bootstrap.Runner; runner.Pause();
			var model = runner.Instruments as InstrumentModel;
			Assert.That(model, Is.Not.Null);
			var alarmMap = ((HarborIntegrationServices)bootstrap.Services).Displays.Presentation.AlarmMap;
			var action = alarmMap.FindAction("Silence");
			TestContext.WriteLine("alarm enabled=" + action.enabled + " controls=" + action.controls.Count + " picker=" + bootstrap.WheelhouseUI.View.PickerVisible);
			RaiseShallow(model, runner);
			PressAndRelease(_keyboard.backslashKey);
			InputSystem.Update();
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(model.SounderOn(AlarmGroup.Navigation), Is.False);
			PressAndRelease(_keyboard.slashKey); InputSystem.Update();
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
			Press(_keyboard.f9Key); InputSystem.Update();
			Assert.That(model.LampTest, Is.True);
			Release(_keyboard.f9Key); InputSystem.Update();
			Assert.That(model.LampTest, Is.False);
			var alarms = ((HarborIntegrationServices)bootstrap.Services).Displays.Screens.Alarms;
			RaiseShallow(model, runner);
			for (var i = 0; i < alarms.Elements.Count; i++)
			{
				if (!(alarms.Elements[i] is FerrySim.UI.Displays.SoftkeyView key)) continue;
				alarms.PressSoftkey(i); alarms.ReleaseSoftkey(i);
			}
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
			Assert.That(model.LampTest, Is.False);
			Press(_keyboard.f9Key); InputSystem.Update();
			var scenario = runner.Scenario;
			Assert.That(bootstrap.TryStart(scenario, out var problem), Is.True, problem);
			yield return HarborSceneFixture.WaitReady(bootstrap, "new run lamp release");
			Assert.That(runner.Instruments.LampTest, Is.False);
			Release(_keyboard.f9Key); InputSystem.Update();
			runner.InstrumentParametersFile = "artifacts/v05-integration/unavailable-instruments.json";
			Assert.That(bootstrap.TryStart(scenario, out problem), Is.True, problem);
			yield return HarborSceneFixture.WaitReady(bootstrap, "no instruments");
			Assert.That(runner.HasInstruments, Is.False);
			PressAndRelease(_keyboard.slashKey); InputSystem.Update();
			PressAndRelease(_keyboard.backslashKey); InputSystem.Update();
			PressAndRelease(_keyboard.f9Key); InputSystem.Update();
		}

		private static void RaiseShallow(InstrumentModel model, VesselSimulationRunner runner)
		{
			model.Reset();
			var telemetry = new VesselTelemetry(default, default, 0, 0, 0, 0, 0, 0.1, 0);
			model.Step(runner.State, telemetry, ControlInputs.Neutral, new TestTaps());
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
		}

		[UnityTest, Explicit("Supplemental allocation attribution; does not replace the strict zero-allocation guard")]
		public IEnumerator Diagnostic_AllocationCalls_ConstantAndChangingTelemetry()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var runner = bootstrap.Runner;
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			Time.timeScale = 0; runner.Resume();
			runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.1));
			for (var frame = 0; frame < 1800; frame++)
			{
				runner.AdvanceFrame(1.0 / 60);
				if (frame % 60 == 0) yield return null;
			}
			var report = new System.Text.StringBuilder("# Supplemental allocation diagnosis\n\nReal Pier 11 runner after 30 simulated seconds. Editor diagnostic only. The strict guard is unchanged. GC.Alloc sample counts remain the primary measure; ProfilerRecorder values below are supplemental, scoped to the same synchronous call. The metric unit is reported explicitly; values are bytes only if Unity reports Bytes.\n\n");
			var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			DiagnosticMetric = new Unity.Profiling.ProfilerRecorder(Unity.Profiling.ProfilerCategory.Memory, "GC.Alloc", 1024,
				Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread | Unity.Profiling.ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
			report.Append("ProfilerRecorder GC.Alloc unit: ").Append(DiagnosticMetric.UnitType).AppendLine();
			var calibration = new AllocationProbe("one-string positive control", recorder);
			var chars = new char[16];
			calibration.Call = () => GC.KeepAlive(new string(chars));
			calibration.Measure(); calibration.Append(report);
			File.WriteAllText(Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/allocation-diagnosis.md"), report.ToString());
			Assert.That(calibration.Samples, Is.EqualTo(1));
			try
			{
				// Each variant has a new, isolated view so automatic scene updates cannot pre-consume its changes.
				// The classic variant is the v0.4 presentation path: console off, classic preset, no extra widgets.
				var engineSkin = displays.Screens.StarboardEngine.Skin;
				displays.Screens.Enabled = false;
				foreach (var definition in HudWidgetRegistry.Default.Widgets)
				{
					using (var isolated = new WheelhouseView())
					{
						isolated.EnvironmentSource = runner;
						isolated.HudLayout.Choose(definition.Id == HudWidgetIds.ClassicPanel ? HudPresets.Classic : HudPresets.Console);
						if (definition.Id != HudWidgetIds.ClassicPanel) isolated.HudLayout.SetSessionShown(definition.Id, true);
						isolated.Refresh(runner, runner.State.Time);
						var full = new AllocationProbe(definition.Id + " only: view.Refresh", recorder) { Call = () => isolated.Refresh(runner, runner.State.Time) };
						for (var frame = 0; frame < 120; frame++) full.Measure();
						full.Name += " / constant"; full.Append(report); full.Reset(definition.Id + " only / changing");
						for (var frame = 0; frame < 120; frame++) { runner.AdvanceFrame(1.0 / 60); full.Measure(); }
						full.Append(report);
					}
				}
				using (var isolated = new WheelhouseView())
				{
					isolated.EnvironmentSource = runner; isolated.HudLayout.Choose(HudPresets.Everything);
					isolated.Refresh(runner, runner.State.Time);
					var environment = new AllocationProbe("EnvironmentDisplay.Update", recorder) { Call = () => isolated.Panel.EnvironmentDisplay.Update(runner, runner, isolated.DistanceUnit) };
					var panel = new AllocationProbe("classic Panel.Refresh after environment", recorder) { Call = () => isolated.Panel.Refresh(runner, runner, isolated.DistanceUnit, isolated.DepthAlarmThresholdMetres, runner.State.Time) };
					var probes = new System.Collections.Generic.List<AllocationProbe>();
					foreach (var definition in HudWidgetRegistry.Default.Widgets)
					{
						var widget = isolated.HudLayer.Widget(definition.Id);
						if (widget == null) continue;
						probes.Add(new AllocationProbe("widget.Refresh " + definition.Id, recorder) { Call = () => widget.Refresh(new HudFrame(runner, isolated.Panel, isolated.DistanceUnit, isolated.DepthAlarmThresholdMetres, runner.State.Time, null)) });
					}
					var overlay = new AllocationProbe("HudOverlay.Refresh", recorder) { Call = () => isolated.Hud.Refresh(runner, isolated.Panel.EnvironmentDisplay) };
					overlay.Call();
					for (var frame = 0; frame < 240; frame++)
					{
						runner.AdvanceFrame(1.0 / 60); environment.Measure(); panel.Measure();
						foreach (var probe in probes) probe.Measure();
						overlay.Measure();
					}
					environment.Append(report); panel.Append(report); foreach (var probe in probes) probe.Append(report); overlay.Append(report);
				}
				var engine = new FerrySim.UI.Displays.EngineMonitorView(FerrySim.UI.Displays.DisplaySide.Starboard, engineSkin);
				var live = new AllocationProbe("starboard EngineMonitorView.Refresh / real model", recorder) { Call = () => engine.Refresh(runner.Instruments, runner.State.Time) };
				engine.Refresh(runner.Instruments, runner.State.Time);
				var before = new string[engine.Elements.Count];
				for (var frame = 0; frame < 1200; frame++)
				{
					for (var i = 0; i < before.Length; i++) before[i] = (engine.Elements[i] as FerrySim.UI.Displays.ValueView)?.ValueText;
					runner.AdvanceFrame(1.0 / 60); var priorSamples = live.Samples; var priorValue = live.MetricValue; live.Measure();
					if (live.Samples == priorSamples) continue;
					report.Append("allocating real-model engine refresh at ").Append(runner.State.Time).Append(" s: samples=").Append(live.Samples - priorSamples).Append(" metric value=").Append(live.MetricValue - priorValue).AppendLine();
					for (var i = 0; i < before.Length; i++)
					{
						var element = engine.Elements[i]; var after = (element as FerrySim.UI.Displays.ValueView)?.ValueText;
						if (before[i] != after) report.Append("  element ").Append(element.Spec.Number).Append(' ').Append(element.Spec.ChannelName).Append(" : ").Append(before[i]).Append(" -> ").Append(after).AppendLine();
					}
				}
				live.Append(report);
				var frozen = new FerrySim.UI.Displays.FakeInstrumentReadout();
				for (var channel = 0; channel < ChannelNames.Count; channel++) frozen.Set((Channel)channel, runner.Instruments.Value((Channel)channel));
				double time = runner.State.Time + 1;
				engine.Refresh(frozen, time);
				var controlled = new AllocationProbe("starboard engine / frozen channels", recorder) { Call = () => engine.Refresh(frozen, time) };
				for (var frame = 0; frame < 240; frame++) { time += 1.0 / 60; controlled.Measure(); }
				controlled.Append(report); controlled.Reset("starboard engine / only fuel-rate channel changing");
				for (var frame = 0; frame < 240; frame++)
				{
					time += 1.0 / 60; frozen.Set(Channel.EngineStarboardFuelRate, FerrySim.UI.Displays.SkinUnits.ToSi(FerrySim.UI.Displays.DisplayUnit.GallonsPerHour, 20 + frame * 0.1)); controlled.Measure();
				}
				controlled.Append(report); controlled.Reset("starboard engine / repeat the same fuel-rate values");
				for (var frame = 0; frame < 240; frame++)
				{
					time += 1.0 / 60; frozen.Set(Channel.EngineStarboardFuelRate, FerrySim.UI.Displays.SkinUnits.ToSi(FerrySim.UI.Displays.DisplayUnit.GallonsPerHour, 20 + frame * 0.1)); controlled.Measure();
				}
				controlled.Append(report);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); DiagnosticMetric.Dispose(); }
			File.WriteAllText(Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/allocation-diagnosis.md"), report.ToString());
		}

		private static Unity.Profiling.ProfilerRecorder DiagnosticMetric;

		private sealed class AllocationProbe
		{
			private readonly Recorder _recorder;
			public AllocationProbe(string name, Recorder recorder) { Name = name; _recorder = recorder; }
			public string Name; public Action Call; public long MetricValue; public int Samples, Calls, AllocatingCalls;
			public void Measure()
			{
				DiagnosticMetric.Reset(); DiagnosticMetric.Start(); _recorder.enabled = true;
				Call();
				_recorder.enabled = false; DiagnosticMetric.Stop(); long metricValue = 0;
				for (var i = 0; i < DiagnosticMetric.Count; i++) metricValue += DiagnosticMetric.GetSample(i).Value;
				var samples = _recorder.sampleBlockCount; MetricValue += metricValue; Samples += samples; Calls++; if (samples > 0) AllocatingCalls++;
			}
			public void Reset(string name) { Name = name; MetricValue = 0; Samples = Calls = AllocatingCalls = 0; }
			public void Append(System.Text.StringBuilder report) => report.Append(Name).Append(": calls=").Append(Calls).Append(" samples=").Append(Samples).Append(" metric value=").Append(MetricValue).Append(" allocating calls=").Append(AllocatingCalls).AppendLine();
		}

		/// <summary>
		/// UI/README.md:107 permits only one clock string per displayed second (date once per day).
		/// UI/Displays/README.md:24 documents per-readout caches: first-seen values cost one string.
		/// Shared machinery stays strictly zero; repeating already-shown engine values must stay zero.
		/// </summary>
		[UnityTest]
		public IEnumerator EverythingOn_UnderWay_AllocatesOnlyClockSecondsAndFirstSeenSkinValues_AndRepeatsAllocateNothing()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var displays = ((HarborIntegrationServices)bootstrap.Services).Displays;
			var screens = displays.Screens;
			var runner = bootstrap.Runner;
			var view = bootstrap.WheelhouseUI.View;
			var audio = runner.GetComponentInChildren<FerryAudioRig>();
			view.HudLayout.Choose(HudPresets.Everything);
			view.Settings.KeybindHints = KeybindHintMode.Always;
			bootstrap.CameraRig.Wheelhouse.HeadLook = new Vector2(0, 35);
			bootstrap.CameraRig.Wheelhouse.Snap();
			audio.GraphEnabled = true; audio.Bus.Master = 1;
			Time.timeScale = 0; runner.Resume();
			runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.1));
			screens.AutomaticRefresh = false;
			var histories = new SkinTextHistory[5];
			histories[2] = new SkinTextHistory(screens.PortEngine);
			histories[3] = new SkinTextHistory(screens.StarboardEngine);
			histories[4] = new SkinTextHistory(screens.Alarms);
			for (var frame = 0; frame < 1800; frame++)
			{
				runner.AdvanceFrame(1.0 / 60); displays.Tick(); screens.Tick(Time.unscaledTimeAsDouble, Time.frameCount);
				foreach (var history in histories) history?.Observe();
				view.Hud.Refresh(runner, view.Panel.EnvironmentDisplay);
				yield return null;
			}
			Assert.That(displays.Presentation.Hints.Layer.VisibleCount, Is.GreaterThan(0));
			var alloc = Recorder.Get("GC.Alloc"); alloc.enabled = false; alloc.FilterToCurrentThread();
			var chars = new char[16]; alloc.enabled = true; var control = new string(chars); alloc.enabled = false;
			GC.KeepAlive(control); Assert.That(alloc.sampleBlockCount, Is.EqualTo(1));
			var refreshed = screens.RefreshCount("plotter"); var began = runner.State.Time;
			var byCall = new int[7]; var firstSeen = 0; var clockSamples = 0; var clockChanges = 0; var badClockFrames = 0; var badConsoleFrames = 0;
			const int MeasuredFrames = 600;
			var trajectory = new double[MeasuredFrames, ChannelNames.Count]; var times = new double[MeasuredFrames]; var slots = new int[MeasuredFrames];
			var replay = new FerrySim.UI.Displays.FakeInstrumentReadout[2] { new FerrySim.UI.Displays.FakeInstrumentReadout(), new FerrySim.UI.Displays.FakeInstrumentReadout() };
			var repeatSamples = 0; var repeatCalls = 0; var stoppedClockSamples = 0;
			try
			{
				for (var frame = 0; frame < MeasuredFrames; frame++)
				{
					runner.AdvanceFrame(1.0 / 60);
					var oldClock = view.Panel.EnvironmentDisplay.Clock;
					alloc.enabled = true; view.Panel.EnvironmentDisplay.Update(runner, runner, view.DistanceUnit); alloc.enabled = false;
					var clockCount = alloc.sampleBlockCount; var changed = oldClock != view.Panel.EnvironmentDisplay.Clock;
					clockSamples += clockCount; if (changed) clockChanges++;
					if (clockCount > (changed ? 1 : 0)) badClockFrames++;
					alloc.enabled = true; displays.Presentation.Refresh(); alloc.enabled = false; byCall[0] += alloc.sampleBlockCount;
					alloc.enabled = true; displays.Tick(); alloc.enabled = false; byCall[1] += alloc.sampleBlockCount;
					alloc.enabled = true; screens.Tick(Time.unscaledTimeAsDouble, Time.frameCount); alloc.enabled = false;
					var consoleSamples = alloc.sampleBlockCount; byCall[2] += consoleSamples;
					var slot = screens.LastRefreshScreen; slots[frame] = slot; times[frame] = runner.State.Time;
					var unseen = slot >= 0 && histories[slot] != null ? histories[slot].Observe() : 0;
					firstSeen += unseen;
					if (consoleSamples != unseen) badConsoleFrames++;
					for (var channel = 0; channel < ChannelNames.Count; channel++) trajectory[frame, channel] = runner.Instruments.Value((Channel)channel);
					alloc.enabled = true; displays.Presentation.Hints.Layer.Refresh(bootstrap.CameraRig.GetComponent<Camera>()); alloc.enabled = false; byCall[3] += alloc.sampleBlockCount;
					// The actual panel's environment was updated above, so its second update and every HUD widget must be zero.
					alloc.enabled = true; view.Refresh(runner, Time.unscaledTimeAsDouble); alloc.enabled = false; byCall[4] += alloc.sampleBlockCount;
					alloc.enabled = true; audio.Tick(1.0 / 60); alloc.enabled = false; byCall[5] += alloc.sampleBlockCount;
					alloc.enabled = true; view.Hud.Refresh(runner, view.Panel.EnvironmentDisplay); alloc.enabled = false; byCall[6] += alloc.sampleBlockCount;
					yield return null;
				}
				for (var frame = 0; frame < MeasuredFrames; frame++)
				{
					alloc.enabled = true; view.Panel.EnvironmentDisplay.Update(runner, runner, view.DistanceUnit); alloc.enabled = false;
					stoppedClockSamples += alloc.sampleBlockCount;
				}
				// The scheduler may select a skin before its internal value cadence is due.
				// Warm every sampled state once, then measure the identical trajectory with all those values cached.
				for (var pass = 0; pass < 2; pass++)
				{
					for (var frame = 0; frame < MeasuredFrames; frame++)
					{
						if (slots[frame] != 2 && slots[frame] != 3) continue;
						var side = slots[frame] - 2; var readout = replay[side];
						for (var channel = 0; channel < ChannelNames.Count; channel++) readout.Set((Channel)channel, trajectory[frame, channel]);
						var engine = side == 0 ? screens.PortEngine : screens.StarboardEngine;
						if (pass == 0) engine.Refresh(readout, times[frame]);
						else
						{
							alloc.enabled = true; engine.Refresh(readout, times[frame]); alloc.enabled = false;
							repeatSamples += alloc.sampleBlockCount; repeatCalls++;
						}
						yield return null;
					}
				}
			}
			finally { alloc.enabled = false; alloc.CollectFromAllThreads(); screens.AutomaticRefresh = true; }
			File.WriteAllText(Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/allocations-contract.txt"),
				"positive control=1\nby call (presentation, displays, console, hints, HUD, audio, overlay)=" + string.Join(",", byCall) +
				"\nbad console frames=" + badConsoleFrames + "\nbad clock frames=" + badClockFrames + "\nfirst-seen skin values=" + firstSeen + "\nclock allocations=" + clockSamples + "\nclock second changes=" + clockChanges +
				"\nstopped clock allocations=" + stoppedClockSamples + "\nrepeated engine samples=" + repeatCalls + "\nrepeated engine allocations=" + repeatSamples +
				"\nplotter refreshes=" + (screens.RefreshCount("plotter") - refreshed) + "\nsimulation seconds=" + (runner.State.Time - began) + "\nvisible hints=" + displays.Presentation.Hints.Layer.VisibleCount + "\n");
			for (var call = 0; call < byCall.Length; call++) if (call != 2) Assert.That(byCall[call], Is.Zero, "shared machinery scope " + call);
			Assert.That(badClockFrames, Is.Zero); Assert.That(badConsoleFrames, Is.Zero, "only one string per first-seen displayed value, no console machinery garbage");
			Assert.That(byCall[2], Is.EqualTo(firstSeen)); Assert.That(clockSamples, Is.LessThanOrEqualTo(clockChanges));
			Assert.That(stoppedClockSamples, Is.Zero); Assert.That(repeatSamples, Is.Zero); Assert.That(repeatCalls, Is.GreaterThan(0));
			Assert.That(screens.RefreshCount("plotter"), Is.GreaterThan(refreshed)); Assert.That(runner.State.Time, Is.GreaterThan(began));
		}

		private sealed class SkinTextHistory
		{
			private readonly System.Collections.Generic.List<Label> _labels;
			private readonly System.Collections.Generic.HashSet<string>[] _seen;
			public SkinTextHistory(VisualElement view)
			{
				_labels = view.Query<Label>().ToList();
				_seen = new System.Collections.Generic.HashSet<string>[_labels.Count];
				for (var i = 0; i < _seen.Length; i++) _seen[i] = new System.Collections.Generic.HashSet<string>();
				Observe();
			}
			public int Observe()
			{
				var unseen = 0;
				for (var i = 0; i < _labels.Count; i++) if (_seen[i].Add(_labels[i].text)) unseen++;
				return unseen;
			}
		}

		[UnityTest]
		public IEnumerator PopoutToggles_AfterWarmupAllocateNothing()
		{
			yield return StartPier11(); var bootstrap = HarborSceneFixture.Bootstrap(); yield return WaitScreens(bootstrap);
			var view = bootstrap.WheelhouseUI.View; bootstrap.Runner.Pause(); view.HudLayout.Choose(HudPresets.Everything);
			for (var i = 0; i < 20; i++) { view.HudLayout.ToggleSessionShown("screen:radar"); yield return null; }
			var alloc = Recorder.Get("GC.Alloc"); alloc.enabled = false; alloc.FilterToCurrentThread();
			var chars = new char[16]; alloc.enabled = true; var control = new string(chars); alloc.enabled = false; GC.KeepAlive(control);
			Assert.That(alloc.sampleBlockCount, Is.EqualTo(1)); var samples = 0;
			try
			{
				for (var frame = 0; frame < 32; frame++)
				{
					alloc.enabled = true; view.HudLayout.ToggleSessionShown("screen:radar"); alloc.enabled = false; samples += alloc.sampleBlockCount;
					yield return null;
				}
			}
			finally { alloc.enabled = false; alloc.CollectFromAllThreads(); }
			File.WriteAllText(Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/allocations-popout-toggles.txt"), "positive control=1\ntoggles=32\nsamples=" + samples + "\n");
			Assert.That(samples, Is.Zero);
		}

		[UnityTest]
		public IEnumerator AlarmPopout_TheRenderedSoftkeysReceivePointerPresses()
		{
			yield return StartPier11();
			var bootstrap = HarborSceneFixture.Bootstrap();
			yield return WaitScreens(bootstrap);
			var view = bootstrap.WheelhouseUI.View;
			view.HudLayout.Choose(HudPresets.Console);
			view.HudLayout.SetSessionShown("screen:alarms", true);
			for (var frame = 0; frame < 20; frame++) yield return null;
			bootstrap.Runner.Pause();
			var model = (InstrumentModel)bootstrap.Runner.Instruments;
			var alarms = ((HarborIntegrationServices)bootstrap.Services).Displays.Screens.Alarms;
			var picture = ((ScreenWidget)view.HudLayer.Widget("screen:alarms")).Picture;
			foreach (var action in new[] { FerrySim.UI.Displays.SoftkeyAction.Silence, FerrySim.UI.Displays.SoftkeyAction.Acknowledge, FerrySim.UI.Displays.SoftkeyAction.LampTest })
			{
				RaiseShallow(model, bootstrap.Runner);
				var rect = default(Rect);
				foreach (var element in alarms.Elements) if (element.Spec.Action == action) rect = element.Spec.Rect;
				Assert.That(rect.width, Is.GreaterThan(0));
				var content = picture.contentRect;
				var reference = alarms.Skin.Reference;
				var scale = Mathf.Min(content.width / reference.x, content.height / reference.y);
				var local = content.center + (rect.center - reference * 0.5f) * scale;
				var point = picture.LocalToWorld(local);
				Pointer(view.panel, EventType.MouseDown, point);
				if (action == FerrySim.UI.Displays.SoftkeyAction.LampTest) Assert.That(model.LampTest, Is.True, "held through the HUD image");
				Pointer(view.panel, EventType.MouseUp, point);
				if (action == FerrySim.UI.Displays.SoftkeyAction.Silence)
				{
					Assert.That(model.SounderOn(AlarmGroup.Navigation), Is.False);
					Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
				}
				else if (action == FerrySim.UI.Displays.SoftkeyAction.Acknowledge) Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
				else Assert.That(model.LampTest, Is.False);
			}
		}
		private static void Pointer(IPanel panel, EventType type, Vector2 point)
		{
			var input = new Event { type = type, mousePosition = point, button = 0, clickCount = 1 };
			using (EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(input) : (EventBase)PointerUpEvent.GetPooled(input)) panel.visualTree.SendEvent(evt);
		}
		private sealed class TestTaps : IInstrumentTaps
		{
			public double RudderOrder => 0;
			public bool NfuActive => false;
			public double HardOverAngle => 0.35;
			public double SteeringRate => 0.1;
			public ShiftPhase PortShiftPhase => ShiftPhase.Neutral;
			public ShiftPhase StarboardShiftPhase => ShiftPhase.Neutral;
			public double PortEngineLoad => 0;
			public double StarboardEngineLoad => 0;
		}
	}
}
