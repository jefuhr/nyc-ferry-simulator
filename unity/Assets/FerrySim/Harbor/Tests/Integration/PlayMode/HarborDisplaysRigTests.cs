using System;
using System.Collections;
using System.IO;
using System.Threading;
using FerrySim.Chartplotter;
using FerrySim.Chartplotter.Tests;
using FerrySim.Controls;
using FerrySim.Core.Radar;
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

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>
	/// The composition component alone, with a gated synthetic chart load, a wheelhouse UI, a camera rig and a simulated
	/// keyboard. Review item 6: the chartplotter follows the wheelhouse depth unit set before and after the chart is
	/// ready, and after the view is replaced. Review plausible item 1: a display key in the same input update as the
	/// picker opening or an external camera view does nothing, whichever came first in that update. The fixture queues
	/// key events; InputSystem.Update() processes them at once, inside the same frame as the state change beside them.
	/// </summary>
	public class HarborDisplaysRigTests : InputTestFixture
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
			_scene = SceneManager.CreateScene("Harbor displays rig " + Guid.NewGuid().ToString("N"));
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
			var load = HarborChartLoad.Begin("rig:" + _scene.name, () =>
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

		[UnityTest]
		public IEnumerator TheChartplotterFollowsTheWheelhouseDepthUnit_BeforeAndAfterTheChartIsReady_AndAcrossAReplacedView()
		{
			yield return Frames(2);
			Assert.That(_ui.View, Is.Not.Null);
			Assert.That(_displays.Connected, Is.False, "the gate holds the chart");
			_ui.View.DistanceUnit = DistanceUnit.Metres;
			_displays.ChartplotterShown = true;
			yield return Ready();
			Assert.That(_displays.Chartplotter.View, Is.Not.Null);
			Assert.That(_displays.Chartplotter.View.DistanceUnit, Is.EqualTo(DistanceUnit.Metres), "the unit set before the chart was ready");
			_ui.View.DistanceUnit = DistanceUnit.Feet;
			yield return null;
			Assert.That(_displays.Chartplotter.View.DistanceUnit, Is.EqualTo(DistanceUnit.Feet), "a change after the chart is ready");
			_ui.View.ToggleDistanceUnit();
			yield return null;
			Assert.That(_displays.Chartplotter.View.DistanceUnit, Is.EqualTo(DistanceUnit.Metres), "the toolbar's toggle");
			// The wheelhouse view is replaced (a test or a reconfiguration): the new view's unit is followed too.
			var oldView = _ui.View;
			_ui.enabled = false;
			_ui.Configure(_runner, _settings);
			_ui.enabled = true;
			yield return Frames(2);
			Assert.That(_ui.View, Is.Not.SameAs(oldView));
			_ui.View.DistanceUnit = DistanceUnit.Feet;
			yield return null;
			Assert.That(_displays.Chartplotter.View.DistanceUnit, Is.EqualTo(DistanceUnit.Feet), "the replaced view's unit");
			_ui.View.DistanceUnit = DistanceUnit.Metres;
			yield return null;
			Assert.That(_displays.Chartplotter.View.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
		}

		[UnityTest]
		public IEnumerator ADisplayKeyInTheSameUpdateAsThePickerOpeningOrAnExternalViewDoesNothing()
		{
			yield return Ready();
			Assert.That(_displays.Mapper, Is.Not.Null, "the rig's helm supplies the actions asset");
			Assert.That(_displays.Mapper.Enabled, Is.True);
			var fired = 0;
			_displays.Mapper.ChartplotterToggleRequested += () => fired++;
			var action = _displays.Mapper.Map.FindAction("ChartplotterToggle", throwIfNotFound: true);
			TestContext.WriteLine("ChartplotterToggle: enabled=" + action.enabled + " controls=" + action.controls.Count + " keyboard=" + (Keyboard.current == _keyboard) + " devices=" + InputSystem.devices.Count);
			// Sanity: a key alone shows the chartplotter and hides it again.
			Press(_keyboard.digit1Key);
			Release(_keyboard.digit1Key);
			InputSystem.Update();
			yield return null;
			TestContext.WriteLine("after press: fired=" + fired + " shown=" + _displays.ChartplotterShown + " visible=" + _displays.ChartplotterVisible + " allowed=" + _displays.DisplaysAllowed + " connected=" + _displays.Connected);
			Assert.That(fired, Is.EqualTo(1), "the mapper raised the toggle for the key");
			Assert.That(_displays.ChartplotterVisible, Is.True);
			Press(_keyboard.digit1Key);
			Release(_keyboard.digit1Key);
			InputSystem.Update();
			yield return null;
			Assert.That(_displays.ChartplotterVisible, Is.False);

			// The picker opens, then the radar key lands in the same update.
			_ui.View.PickerVisible = true;
			Press(_keyboard.digit2Key);
			Release(_keyboard.digit2Key);
			InputSystem.Update();
			yield return null;
			Assert.That(_displays.RadarVisible, Is.False);
			_ui.View.PickerVisible = false;
			yield return Frames(2);
			Assert.That(_displays.RadarShown, Is.False, "the key under the picker was not a request");
			Assert.That(_displays.RadarVisible, Is.False);

			// The radar key lands first, then the picker opens in the same update.
			Press(_keyboard.digit2Key);
			Release(_keyboard.digit2Key);
			InputSystem.Update();
			_ui.View.PickerVisible = true;
			yield return null;
			Assert.That(_displays.RadarVisible, Is.False);
			_ui.View.PickerVisible = false;
			yield return Frames(2);
			Assert.That(_displays.RadarShown, Is.False, "the key in the update the picker opened in was not a request");
			Assert.That(_displays.RadarVisible, Is.False);

			// The radar key, then the chase view in the same update; back in the wheelhouse the radar stays hidden.
			Press(_keyboard.digit2Key);
			Release(_keyboard.digit2Key);
			InputSystem.Update();
			_rig.CurrentMode = CameraRig.Mode.Chase;
			yield return null;
			Assert.That(_displays.RadarVisible, Is.False);
			_rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			yield return Frames(2);
			Assert.That(_displays.RadarShown, Is.False, "the key in the update the view left the wheelhouse in was not a request");

			// An adjustment key with the picker opening in the same update changes nothing on a shown display.
			_displays.ChartplotterShown = true;
			yield return Frames(2);
			Assert.That(_displays.ChartplotterVisible, Is.True);
			var span = _displays.Chartplotter.View.SpanNauticalMiles;
			Press(_keyboard.equalsKey);
			Release(_keyboard.equalsKey);
			InputSystem.Update();
			_ui.View.PickerVisible = true;
			yield return null;
			_ui.View.PickerVisible = false;
			yield return Frames(2);
			Assert.That(_displays.Chartplotter.View.SpanNauticalMiles, Is.EqualTo(span), "no zoom from the update the picker opened in");
			Press(_keyboard.equalsKey);
			Release(_keyboard.equalsKey);
			InputSystem.Update();
			yield return null;
			Assert.That(_displays.Chartplotter.View.SpanNauticalMiles, Is.LessThan(span), "the same key alone zooms in");
		}
	}
}
