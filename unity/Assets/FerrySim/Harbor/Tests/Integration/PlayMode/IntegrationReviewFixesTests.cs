using System;
using System.Collections;
using System.Globalization;
using System.IO;
using FerrySim.Chartplotter;
using FerrySim.Console;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Core.Navigation;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.UI.Displays;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	// Failure modes: a picture has no pointer route, an off-page key wins a hit, a held key survives
	// navigation, session CPA choices vanish with a view, or text entry also drives independent maps.
	public class IntegrationReviewFixesTests : InputTestFixture
	{
		private Keyboard _keyboard;
		private float _volume;
		private string _folder;
		private SettingsStore _settings;
		private HarborBootstrap _bootstrap;
		private HarborDisplays _displays;
		private WheelhouseView View => _bootstrap.WheelhouseUI.View;
		private VesselSimulationRunner Runner => _bootstrap.Runner;

		public override void Setup()
		{
			base.Setup();
			_volume = AudioListener.volume; AudioListener.volume = 0;
			_keyboard = InputSystem.AddDevice<Keyboard>();
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.Destroy(defaults);
			_folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/integration-fixes/cases", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
			_settings = new SettingsStore(Path.Combine(_folder, SettingsStore.FileName));
			_settings.ConsoleScreens = true; Assert.That(_settings.Save(), Is.True);
			UserSettingsFiles.SetSession(_settings, HudLayoutStore.InMemory());
		}

		[UnityTearDown]
		public IEnumerator Unload()
		{
			Time.timeScale = 1;
			yield return HarborSceneFixture.Unload();
			AudioListener.volume = _volume;
			UserSettingsFiles.ResetSession(); InputBindingService.ResetSession();
		}

		private IEnumerator StartScene()
		{
			HarborSceneFixture.RequireData(); yield return HarborSceneFixture.Load();
			_bootstrap = HarborSceneFixture.Bootstrap();
			_displays = ((HarborIntegrationServices)_bootstrap.Services).Displays;
			Runner.RecordRuns = false;
			yield return StartVessel(0);
		}

		private IEnumerator StartVessel(int vessel)
		{
			var catalog = ((HarborIntegrationServices)_bootstrap.Services).HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay,
				HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[vessel].Id, traffic: false);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(_bootstrap.TryStart(settings, out problem), Is.True, problem);
			View.PickerVisible = false;
			yield return HarborSceneFixture.WaitReady(_bootstrap, "integration review");
			yield return ReadyScreens();
		}

		private IEnumerator ReadyScreens()
		{
			var began = Time.realtimeSinceStartupAsDouble;
			while ((_settings.ConsoleScreens && _displays.Screens?.Plotter == null) || !_displays.Connected)
			{
				Assert.That(_displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), _displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - began, Is.LessThan(180));
				yield return null;
			}
			for (var frame = 0; frame < 4; frame++) yield return null;
		}

		private static void Pointer(IPanel panel, EventType type, Vector2 position)
		{
			var input = new Event { type = type, mousePosition = position, button = 0, clickCount = 1 };
			using (EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(input) : (EventBase)PointerUpEvent.GetPooled(input))
				panel.visualTree.SendEvent(evt);
		}

		private static Vector2 KeyPoint(Image picture, SkinnedDisplayView display, string text)
		{
			var rect = default(Rect);
			foreach (var element in display.Elements)
				if (element is SoftkeyView key && key.LabelText == text && key.OnPage) rect = key.Spec.Rect;
			Assert.That(rect.width, Is.GreaterThan(0), text + " is on the visible page");
			var content = picture.contentRect; var reference = display.Skin.Reference;
			var scale = Mathf.Min(content.width / reference.x, content.height / reference.y);
			Assert.That(scale, Is.GreaterThan(0));
			return picture.LocalToWorld(content.center + (rect.center - reference * .5f) * scale);
		}

		private static void Click(Image picture, SkinnedDisplayView display, string key)
		{
			var point = KeyPoint(picture, display, key);
			Pointer(picture.panel, EventType.MouseDown, point);
			Pointer(picture.panel, EventType.MouseUp, point);
		}

		[UnityTest] public IEnumerator PortEnginePopout_PagesChangeOnTheSharedView() => EnginePopout("engine-port", true);
		[UnityTest] public IEnumerator StarboardEnginePopout_PagesChangeOnTheSharedView() => EnginePopout("engine-starboard", false);
		private IEnumerator EnginePopout(string id, bool port)
		{
			yield return StartScene();
			View.HudLayout.Choose(HudPresets.Console); View.HudLayout.SetSessionShown("screen:" + id, true);
			yield return ReadyScreens();
			var engine = port ? _displays.Screens.PortEngine : _displays.Screens.StarboardEngine;
			var other = port ? _displays.Screens.StarboardEngine : _displays.Screens.PortEngine;
			var picture = ((ScreenWidget)View.HudLayer.Widget("screen:" + id)).Picture;
			Assert.That(_displays.Screens.TryGetScreen(id, out var texture), Is.True);
			Assert.That(picture.image, Is.SameAs(texture));
			Click(picture, engine, "ALARMS");
			Assert.That(engine.CurrentPage, Is.EqualTo("alarms"), id + " pointer reaches the shared engine display");
			Assert.That(other.CurrentPage, Is.EqualTo("main"));
			Click(picture, engine, "MAIN"); Assert.That(engine.CurrentPage, Is.EqualTo("main"));
			Assert.That(_displays.Screens.TryGetScreen(id, out var after), Is.True);
			Assert.That(after, Is.SameAs(texture), "page navigation never replaces the render target");
			Assert.That(picture.image, Is.SameAs(texture));
		}

		private IEnumerator CustomAlarm(bool acknowledgeOnFirstPage, bool overlap = false)
		{
			yield return StartScene(); Runner.Pause(); View.DepthAlarmThresholdMetres = 1000; Runner.Driver.StepOnce();
			Assert.That(Runner.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			var folder = Path.Combine(_folder, "skins", "alarm-panel"); Directory.CreateDirectory(folder);
			var first = acknowledgeOnFirstPage ? "'action':'acknowledge'" : "'target':'two'";
			var second = acknowledgeOnFirstPage ? "'target':'one'" : "'action':'acknowledge'";
			var json = "{'version':1,'display':'alarm-panel','id':'generic-a','reference':[640,480],'refreshHz':10,'pages':['one','two'],'elements':[" +
				"{'type':'list','rect':[16,16,608,340],'text':'ALARMS'}," +
				"{'type':'softkey','page':'one','rect':[16,412,192,52],'text':'FIRST'," + first + "}," +
				"{'type':'softkey','page':'two','rect':[16,412,192,52],'text':'SECOND'," + second + "}]}";
			if (overlap) json = json.Replace("'page':'two'", "'page':'one'");
			File.WriteAllText(Path.Combine(folder, "generic-a.json"), json.Replace('\'', '"'));
			var library = new DisplaySkinLibrary(DisplaySkinAlarms.From(Runner.Instruments), Path.Combine(_folder, "skins"));
			_displays.Screens.Connect(Runner.transform.Find(WheelhouseControlsView.RootName), Runner, Runner,
				_displays.Load.Tessellation, _displays.Load.RadarScene, environment: Runner, skins: library);
			Assert.That(library.Problems, Is.Empty);
			View.HudLayout.Choose(HudPresets.Console); View.HudLayout.SetSessionShown("screen:alarms", true);
			yield return ReadyScreens();
		}

		[UnityTest]
		public IEnumerator HiddenPageKeyCannotTakeTheVisibleAcknowledgement()
		{
			yield return CustomAlarm(false);
			var alarm = _displays.Screens.Alarms;
			var picture = ((ScreenWidget)View.HudLayer.Widget("screen:alarms")).Picture;
			var acknowledges = 0; alarm.AcknowledgeRequested += () => acknowledges++;
			Click(picture, alarm, "FIRST"); Assert.That(alarm.CurrentPage, Is.EqualTo("two"));
			Click(picture, alarm, "SECOND");
			Assert.That(acknowledges, Is.EqualTo(1), "only the visible second-page ACK fires");
			Assert.That(Runner.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
		}

		[UnityTest]
		public IEnumerator TopmostVisibleSoftkeyWinsAnOverlappingHit()
		{
			yield return CustomAlarm(false, true);
			var alarm = _displays.Screens.Alarms;
			var picture = ((ScreenWidget)View.HudLayer.Widget("screen:alarms")).Picture;
			Click(picture, alarm, "SECOND");
			Assert.That(Runner.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged), "last drawn visible key wins");
			Assert.That(alarm.CurrentPage, Is.EqualTo("one"));
		}

		[UnityTest]
		public IEnumerator HeldSoftkeyIsCancelledWhenItsPageIsHidden()
		{
			yield return CustomAlarm(true);
			var alarm = _displays.Screens.Alarms;
			var picture = ((ScreenWidget)View.HudLayer.Widget("screen:alarms")).Picture;
			var point = KeyPoint(picture, alarm, "FIRST");
			var acknowledges = 0; alarm.AcknowledgeRequested += () => acknowledges++;
			Pointer(picture.panel, EventType.MouseDown, point);
			Assert.That(((SoftkeyView)alarm.Elements[1]).Pressed, Is.True);
			alarm.ShowPage("two");
			Pointer(picture.panel, EventType.MouseUp, point);
			Assert.That(acknowledges, Is.Zero, "a held key hidden by navigation must not fire on release");
			Assert.That(((SoftkeyView)alarm.Elements[1]).Pressed, Is.False);
			Assert.That(Runner.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
		}

		private static void Preferences(ChartplotterView plotter, CpaAlarmLimits limits, bool muted)
		{
			Assert.That(plotter, Is.Not.Null);
			Assert.That(plotter.AlarmLimits.Cpa, Is.EqualTo(limits.Cpa));
			Assert.That(plotter.AlarmLimits.Tcpa, Is.EqualTo(limits.Tcpa));
			Assert.That(plotter.AlarmMuted, Is.EqualTo(muted));
		}

		[UnityTest]
		public IEnumerator SessionCpaChoicesSurviveRunsClassSwitchAndConsoleToggle_WithoutSaving()
		{
			yield return StartScene();
			var file = Path.Combine(_folder, SettingsStore.FileName); var before = File.ReadAllBytes(file);
			var plotter = _displays.ActivePlotter;
			plotter.StepCpaLimit(1); plotter.StepTcpaLimit(1); plotter.ToggleAlarmMute();
			var limits = plotter.AlarmLimits; _displays.Tick();
			Assert.That(File.ReadAllBytes(file), Is.EqualTo(before));
			yield return StartVessel(0); Preferences(_displays.ActivePlotter, limits, true);
			yield return StartVessel(1); Preferences(_displays.ActivePlotter, limits, true);
			_settings.ConsoleScreens = false; _displays.ChartplotterShown = true;
			yield return ReadyScreens(); Preferences(_displays.ActivePlotter, limits, true);
			_settings.ConsoleScreens = true;
			yield return ReadyScreens(); Preferences(_displays.ActivePlotter, limits, true);
			Assert.That(File.ReadAllBytes(file), Is.EqualTo(before), "CPA choices are session-only; console choice returned to its starting value");
		}

		[UnityTest]
		public IEnumerator SessionCpaChoicesNeverChangeRecordingBytesOrStateDigest()
		{
			yield return StartScene(); Time.timeScale = 0;
			byte[] baseline = null; string baselineInputs = null, digest = null;
			var scenario = Runner.Scenario; var settingsBefore = File.ReadAllBytes(Path.Combine(_folder, SettingsStore.FileName));
			foreach (var change in new[] { false, true })
			{
				Assert.That(_bootstrap.TryStart(scenario, out var problem), Is.True, problem); View.PickerVisible = false;
				yield return HarborSceneFixture.WaitReady(_bootstrap, "CPA recording parity"); yield return ReadyScreens();
				Runner.Driver.Controls = new ConstantControls(new ControlInputs(.4, .4, .1));
				var telemetry = new StringWriter(CultureInfo.InvariantCulture); var inputs = new StringWriter(CultureInfo.InvariantCulture);
				var header = RunRecorder.BuildHeader(scenario, Runner.Parameters, Runner.Driver.Simulation, Runner.Factory.GetType().FullName,
					Runner.State, Runner.RunEnvironment, Runner.TelemetrySampleHz);
				using (var recorder = new RunRecorder(telemetry, inputs, Runner.TelemetrySampleHz, header))
				{
					Runner.Driver.AddObserver(recorder); Runner.Resume();
					for (var step = 0; step < 100; step++)
					{
						if (change && step == 50)
						{
							_displays.ActivePlotter.StepCpaLimit(1); _displays.ActivePlotter.StepTcpaLimit(1); _displays.ActivePlotter.ToggleAlarmMute();
						}
						Runner.Driver.StepOnce(); yield return null;
					}
					recorder.Finish(Runner.Driver.StepCount, Runner.State, Runner.Telemetry); Runner.Driver.RemoveObserver(recorder); Runner.Pause();
					Assert.That(recorder.Failure, Is.Null);
					var bytes = System.Text.Encoding.UTF8.GetBytes(telemetry.ToString());
					var id = change ? "changed" : "baseline";
					File.WriteAllBytes(Path.Combine(_folder, id + ".csv"), bytes); File.WriteAllText(Path.Combine(_folder, id + ".inputs.csv"), inputs.ToString());
					if (baseline == null) { baseline = bytes; baselineInputs = inputs.ToString(); digest = recorder.Digest.Text; }
					else { Assert.That(bytes, Is.EqualTo(baseline)); Assert.That(inputs.ToString(), Is.EqualTo(baselineInputs)); Assert.That(recorder.Digest.Text, Is.EqualTo(digest)); }
				}
			}
			Assert.That(File.ReadAllBytes(Path.Combine(_folder, SettingsStore.FileName)), Is.EqualTo(settingsBefore));
			File.WriteAllText(Path.Combine(_folder, "parity.txt"), "byte-identical telemetry, inputs and state digest: " + digest + "\n");
		}

		private void Key(KeyControl key) { PressAndRelease(key); InputSystem.Update(); }
		private static void Text(TextField field, char character, KeyCode code)
		{
			var target = field.panel.focusController.focusedElement as VisualElement;
			Assert.That(target, Is.Not.Null);
			using (var evt = KeyDownEvent.GetPooled(character, code, EventModifiers.None)) target.SendEvent(evt);
			using (var evt = KeyUpEvent.GetPooled(character, code, EventModifiers.None)) target.SendEvent(evt);
		}

		[UnityTest]
		public IEnumerator TypingNameHoldsDisplayAndCameraInput_WithRebindingAndNoLateCommands()
		{
			yield return StartScene(); View.HudLayout.Choose(HudPresets.Console); View.HudLayout.SetSessionShown("screen:plotter", true);
			yield return ReadyScreens();
			var plotter = _displays.ActivePlotter; var camera = _bootstrap.CameraRig.Wheelhouse;
			var muted = plotter.AlarmMuted; var centred = plotter.Centred; var bow = camera.ShowingBow;
			View.HudEditing = true; var field = View.HudLayer.EditBar.NameField; field.value = string.Empty; field.Focus();
			yield return null;
			Key(_keyboard.gKey); Text(field, 'g', KeyCode.G);
			Key(_keyboard.iKey); Text(field, 'i', KeyCode.I);
			Key(_keyboard.bKey); Text(field, 'b', KeyCode.B);
			yield return null;
			Assert.That(field.value, Is.EqualTo("gib"), "real text editing still receives the letters");
			Assert.That(_bootstrap.CameraRig.TextEntryHeld, Is.True, "the shared text-focus gate reaches the camera rig");
			Assert.That(new[] { plotter.AlarmMuted, plotter.Centred, camera.ShowingBow },
				Is.EqualTo(new[] { muted, centred, bow }), "mute, centred chart and bow view all stay unchanged");
			var service = InputBindingService.Instance;
			Assert.That(service.TryApply(ActionIds.BowView, 0, "Keyboard", "<Keyboard>/f12", out var conflict), Is.True, conflict);
			Assert.That(service.TryApply("Displays/ChartAlarmMute", 0, "Keyboard", "<Keyboard>/b", out conflict), Is.True, conflict);
			Key(_keyboard.bKey); Text(field, 'b', KeyCode.B); yield return null;
			Assert.That(field.value, Is.EqualTo("gibb")); Assert.That(plotter.AlarmMuted, Is.EqualTo(muted));
			field.Blur(); yield return null; yield return null;
			Assert.That(_bootstrap.CameraRig.TextEntryHeld, Is.False);
			Assert.That(plotter.AlarmMuted, Is.EqualTo(muted), "nothing typed fires after blur");
			Key(_keyboard.bKey); Key(_keyboard.iKey); Key(_keyboard.f12Key); yield return null;
			Assert.That(plotter.AlarmMuted, Is.EqualTo(!muted)); Assert.That(plotter.Centred, Is.False); Assert.That(camera.ShowingBow, Is.EqualTo(!bow));
		}

		[UnityTest]
		public IEnumerator EnteringTextDropsAlreadyQueuedDisplayCommands_EvenIfBlurredBeforeTick()
		{
			yield return StartScene(); View.HudLayout.Choose(HudPresets.Console); View.HudLayout.SetSessionShown("screen:plotter", true); yield return ReadyScreens();
			var plotter = _displays.ActivePlotter; var muted = plotter.AlarmMuted;
			Key(_keyboard.gKey); View.HudEditing = true; View.HudLayer.EditBar.NameField.Focus(); View.HudLayer.EditBar.NameField.Blur();
			_displays.Tick(); Assert.That(plotter.AlarmMuted, Is.EqualTo(muted), "focus entry discards commands queued earlier in that update");
		}
	}
}
