using System;
using System.Collections;
using System.IO;
using System.Threading;
using FerrySim.Controls;
using FerrySim.Core.Navigation;
using FerrySim.Core.Radar;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Chartplotter.Tests.Keys
{
	/// <summary>
	/// The CPA alarm keys through the composition (the harbor displays component with a synthetic chart, a wheelhouse UI,
	/// a camera rig and a simulated keyboard), the pattern of the harbor displays rig tests. Failure modes: a key
	/// changing the plotter while it is hidden, while the picker's search field has the keyboard, or outside the
	/// wheelhouse view; a key not reaching the view while it is shown; the settings not surviving a hide and a new run;
	/// a key clash with a helm key.
	/// </summary>
	public class AlarmKeysRigTests : InputTestFixture
	{
		private Scene _scene;
		private ManualResetEventSlim _gate;
		private VesselSimulationRunner _runner;
		private HelmInputController _helm;
		private WheelhouseUI _ui;
		private CameraRig _rig;
		private HarborDisplays _displays;
		private PanelSettings _settings;
		private RenderTexture _target;
		private Keyboard _keyboard;

		private static string ActionsPath => Path.Combine(Application.dataPath, "FerrySim", "Input", "FerrySimControls.inputactions");

		public override void Setup()
		{
			base.Setup();
			_keyboard = InputSystem.AddDevice<Keyboard>();
			HarborChartLoad.ResetForTests();
			_gate = new ManualResetEventSlim();
			_scene = SceneManager.CreateScene("Alarm keys rig " + Guid.NewGuid().ToString("N"));
			var vessel = new GameObject("Vessel");
			vessel.SetActive(false);
			SceneManager.MoveGameObjectToScene(vessel, _scene);
			_runner = vessel.AddComponent<VesselSimulationRunner>();
			_helm = vessel.AddComponent<HelmInputController>();
			_helm.Actions = InputActionAsset.FromJson(File.ReadAllText(ActionsPath));
			vessel.SetActive(true);
			_target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_target.Create();
			_settings = WheelhousePanelSettings.Create(_target, Color.clear);
			var uiObject = new GameObject("Wheelhouse UI");
			uiObject.SetActive(false);
			SceneManager.MoveGameObjectToScene(uiObject, _scene);
			_ui = uiObject.AddComponent<WheelhouseUI>();
			_ui.Configure(_runner, _settings);
			uiObject.SetActive(true);
			var cameraObject = new GameObject("Camera rig");
			SceneManager.MoveGameObjectToScene(cameraObject, _scene);
			cameraObject.AddComponent<Camera>();
			_rig = cameraObject.AddComponent<CameraRig>();
			_rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			var runtime = SyntheticHarbor.Runtime();
			var chart = SyntheticHarbor.Chart(runtime);
			var gate = _gate;
			var load = HarborChartLoad.Begin("alarm-keys:" + _scene.name, () =>
			{
				gate.Wait();
				return new HarborChartLoad.Result { Pack = chart, RadarScene = RadarScene.FromPacks(chart, runtime), Tessellation = ChartTessellation.Build(chart, runtime) };
			});
			var host = new GameObject("Harbor displays");
			SceneManager.MoveGameObjectToScene(host, _scene);
			_displays = host.AddComponent<HarborDisplays>();
			_displays.Configure(_runner, load, _ui, _rig, null, _helm);
		}

		public override void TearDown()
		{
			_gate.Set();
			if (_scene.IsValid()) SceneManager.UnloadSceneAsync(_scene);
			if (_settings != null) Object.Destroy(_settings);
			if (_target != null) { _target.Release(); Object.Destroy(_target); }
			HarborChartLoad.ResetForTests();
			base.TearDown();
		}

		private IEnumerator Frames(int count)
		{
			for (var i = 0; i < count; i++) yield return null;
		}

		private IEnumerator Ready()
		{
			_gate.Set();
			var started = Time.realtimeSinceStartupAsDouble;
			while (!_displays.Connected)
			{
				Assert.That(_displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), _displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(30), "the synthetic chart did not connect");
				yield return null;
			}

			yield return null;
		}

		private IEnumerator Tap(Key key)
		{
			Press(_keyboard[key]);
			Release(_keyboard[key]);
			InputSystem.Update();
			yield return null;
		}

		private ChartplotterView View => _displays.Chartplotter.View;

		[UnityTest]
		public IEnumerator TheAlarmKeys_ActOnlyWhileThePlotterIsShown_AndNeverUnderThePickerOrOutsideTheWheelhouse()
		{
			yield return Ready();
			Assert.That(_displays.Mapper, Is.Not.Null, "the rig's helm supplies the actions asset");
			Assert.That(_displays.Mapper.Enabled, Is.True);
			Assert.That(_displays.ChartplotterVisible, Is.False, "hidden by default");
			Assert.That(View, Is.Not.Null, "the view exists while hidden, with its defaults");
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa));
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa));
			Assert.That(View.AlarmMuted, Is.False);

			// Hidden: every alarm key is ignored.
			yield return Tap(Key.Digit8);
			yield return Tap(Key.Digit0);
			yield return Tap(Key.G);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa), "8 on a hidden plotter");
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa), "0 on a hidden plotter");
			Assert.That(View.AlarmMuted, Is.False, "G on a hidden plotter");

			// Shown: each key does its one thing.
			yield return Tap(Key.Digit1);
			yield return Frames(2);
			Assert.That(_displays.ChartplotterVisible, Is.True);
			yield return Tap(Key.Digit8);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(277.8).Within(1e-9), "8 widens the CPA limit to 0.15 nm");
			yield return Tap(Key.Digit7);
			yield return Tap(Key.Digit7);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "7 twice: 0.05 nm");
			yield return Tap(Key.Digit0);
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(420.0), "0 lengthens the TCPA limit to 7 min");
			yield return Tap(Key.Digit9);
			yield return Tap(Key.Digit9);
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(300.0), "9 twice: 5 min");
			yield return Tap(Key.G);
			Assert.That(View.AlarmMuted, Is.True, "G mutes");
			Assert.That(View.LimitsText, Is.EqualTo("LIMITS 0.05 NM · 5:00"));
			Assert.That(View.List.BannerShown, Is.EqualTo(CpaList.MutedText));
			yield return Tap(Key.G);
			Assert.That(View.AlarmMuted, Is.False, "G again unmutes");
			yield return Tap(Key.G);
			Assert.That(View.AlarmMuted, Is.True);

			// The picker open: the search field gets every key, so digits and G typed there change nothing.
			_ui.View.PickerVisible = true;
			yield return Frames(2);
			Assert.That(_displays.Mapper.Enabled, Is.False, "the Displays map is off under the picker");
			yield return Tap(Key.Digit8);
			yield return Tap(Key.Digit0);
			yield return Tap(Key.G);
			yield return Tap(Key.Digit7);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "a digit typed into the picker");
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(300.0));
			Assert.That(View.AlarmMuted, Is.True, "G typed into the picker");
			_ui.View.PickerVisible = false;
			yield return Frames(2);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "nothing queued from under the picker");
			Assert.That(View.AlarmMuted, Is.True);

			// Outside the wheelhouse the displays hide and the map is off; back in the wheelhouse the settings are as they were.
			_rig.CurrentMode = CameraRig.Mode.Chase;
			yield return Frames(2);
			Assert.That(_displays.Mapper.Enabled, Is.False);
			yield return Tap(Key.Digit8);
			yield return Tap(Key.G);
			_rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			yield return Frames(2);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "8 in the chase view");
			Assert.That(View.AlarmMuted, Is.True, "G in the chase view");

			// Hidden and shown again: the settings survive the hide (a session setting).
			yield return Tap(Key.Digit1);
			yield return Frames(2);
			Assert.That(_displays.ChartplotterVisible, Is.False);
			yield return Tap(Key.Digit8);
			yield return Tap(Key.Digit1);
			yield return Frames(2);
			Assert.That(_displays.ChartplotterVisible, Is.True);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "8 while hidden did nothing; the limit held through the hide");
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(300.0));
			Assert.That(View.AlarmMuted, Is.True);
			Assert.That(View.LimitsText, Is.EqualTo("LIMITS 0.05 NM · 5:00"));
		}

		[UnityTest]
		public IEnumerator TheAlarmKeys_NeverReachTheHelm_AndHelmKeysNeverReachTheAlarm()
		{
			yield return Ready();
			yield return Tap(Key.Digit1);
			yield return Frames(2);
			Assert.That(_displays.ChartplotterVisible, Is.True);
			var helmEvents = 0;
			_helm.Mapper.PauseRequested += () => helmEvents++;
			_helm.Mapper.NewRunRequested += () => helmEvents++;
			var controlsBefore = _helm.Mapper.Current;
			yield return Tap(Key.Digit7);
			yield return Tap(Key.Digit8);
			yield return Tap(Key.Digit9);
			yield return Tap(Key.Digit0);
			yield return Tap(Key.G);
			Assert.That(helmEvents, Is.Zero, "no alarm key is a helm button");
			Assert.That(_helm.Mapper.Current.Helm, Is.EqualTo(controlsBefore.Helm), "no alarm key moved the helm");
			Assert.That(_helm.Mapper.Current.PortLever, Is.EqualTo(controlsBefore.PortLever));
			Assert.That(_helm.Mapper.Current.StarboardLever, Is.EqualTo(controlsBefore.StarboardLever));
			Assert.That(View.AlarmMuted, Is.True);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa), "7 then 8: back where it started");
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa), "9 then 0: back where it started");

			// Helm keys (M, W, Q) change no alarm setting.
			yield return Tap(Key.M);
			yield return Tap(Key.W);
			yield return Tap(Key.Q);
			Assert.That(View.AlarmMuted, Is.True);
			Assert.That(View.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa));
			Assert.That(View.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa));
		}
	}
}
