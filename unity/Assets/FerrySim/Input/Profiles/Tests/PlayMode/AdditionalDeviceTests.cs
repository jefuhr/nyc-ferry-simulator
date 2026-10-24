using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using FerrySim.Controls.Profiles;

namespace FerrySim.Controls.ProfileTests
{
	public class AdditionalDeviceTests : InputTestFixture
	{
		private InputActionAsset _actions;
		private HelmInputMapper _mapper;
		private DeviceProfileRouter _router;
		private CalibrationView _view;
		private GameObject _screen;
		private RenderTexture _renderTarget;
		private string _path;
		public override void Setup()
		{
			base.Setup();
			InputSystem.RegisterLayout<SimulatedQuadrant>();
			InputSystem.RegisterLayout<SimulatedKnob>();
			_path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
		}
		public override void TearDown()
		{
			if (_screen != null) UnityEngine.Object.DestroyImmediate(_screen);
			if (_renderTarget != null) { _renderTarget.Release(); UnityEngine.Object.DestroyImmediate(_renderTarget); }
			_view?.Dispose(); _router?.Dispose(); _mapper?.Dispose();
			if (_actions != null) UnityEngine.Object.DestroyImmediate(_actions);
			if (File.Exists(_path)) File.Delete(_path);
			base.TearDown();
		}
		private DeviceProfileStore StartMapper()
		{
			var store = new DeviceProfileStore(_path);
			_actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_mapper = new HelmInputMapper(_actions); _router = new DeviceProfileRouter(_mapper, store); _mapper.Enable();
			return store;
		}

		[Test]
		public void WheelDefault_UsesSteeringAxis_AndKeyboardStillDrivesLevers()
		{
			StartMapper();
			var wheel = InputSystem.AddDevice(new InputDeviceDescription { deviceClass = nameof(SimulatedQuadrant), product = "Test wheel", interfaceName = "HID" });
			Set(wheel.GetChildControl<AxisControl>("stick/x"), 1f);
			Assert.That(_mapper.Helm, Is.EqualTo(1));
			var keyboard = InputSystem.AddDevice<Keyboard>();
			Press(keyboard.wKey); Release(keyboard.wKey);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.2));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.2));
		}

		[Test]
		public void RelativeKnob_AccumulatesRepeatedPulses_AndDoesNotReturnToCentre()
		{
			StartMapper();
			var knob = InputSystem.AddDevice<SimulatedKnob>();
			var axis = knob.GetChildControl<AxisControl>("delta/x");
			InputSystem.QueueDeltaStateEvent(axis, 1f); InputSystem.Update();
			InputSystem.QueueDeltaStateEvent(axis, 1f); InputSystem.Update();
			InputSystem.Update();
			Assert.That(_mapper.Helm, Is.EqualTo(0.2f).Within(1e-6));
			InputSystem.QueueDeltaStateEvent(axis, -1f); InputSystem.Update();
			Assert.That(_mapper.Helm, Is.EqualTo(0.1f).Within(1e-6));
		}

		[Test]
		public void RelativeCapture_LatchesPulsesUntilTheMouseCanCaptureThem()
		{
			var knob = InputSystem.AddDevice<SimulatedKnob>();
			var axis = knob.GetChildControl<AxisControl>("delta/x");
			_view = new CalibrationView(new DeviceProfileStore(_path)); _view.SelectDevice(knob);
			_view.BeginCalibration(ProfileTarget.Helm, "delta/x", relative: true);
			InputSystem.QueueDeltaStateEvent(axis, -1f); InputSystem.Update(); InputSystem.Update(); _view.CapturePoint();
			_view.CapturePoint();
			InputSystem.QueueDeltaStateEvent(axis, 1f); InputSystem.Update(); InputSystem.Update(); _view.CapturePoint();
			Assert.That(_view.StageCalibration(0.05f), Is.True);
			InputSystem.QueueDeltaStateEvent(axis, 1f); InputSystem.Update();
			InputSystem.QueueDeltaStateEvent(axis, 1f); InputSystem.Update(); InputSystem.Update();
			Assert.That(_view.DraftOutput(ProfileTarget.Helm), Is.EqualTo(0.2f).Within(1e-6));
		}

		[Test]
		public void EventsInOneUpdate_RespectLastMovedDevice()
		{
			StartMapper();
			var device = InputSystem.AddDevice<SimulatedQuadrant>();
			var keyboard = InputSystem.AddDevice<Keyboard>();
			InputSystem.QueueDeltaStateEvent(device.GetChildControl<AxisControl>("slider"), 1f);
			InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Q));
			InputSystem.Update();
			Assert.That(_mapper.PortLever, Is.EqualTo(1));
			InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
			InputSystem.QueueDeltaStateEvent(device.GetChildControl<AxisControl>("slider"), 0.525f);
			InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Q)); InputSystem.Update();
			Assert.That(_mapper.PortLever, Is.EqualTo(0.6), "axis gives half ahead, then Q wins with the next notch");
		}

		[Test]
		public void CustomGamepadPort_PreservesUnassignedStarboardAndShoulders()
		{
			var store = StartMapper();
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var profile = DeviceProfileDefaults.Create(gamepad);
			profile.axes.Add(new AxisAssignment { path = "leftStick/y", target = ProfileTarget.PortLever });
			store.Set(profile);
			Set(gamepad.leftStick, new Vector2(0, 0.525f));
			Assert.That(_mapper.PortLever, Is.EqualTo(0.5).Within(1e-6));
			Set(gamepad.rightStick, new Vector2(0, 1f));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(1), "unassigned action keeps its original binding");
			Press(gamepad.rightShoulder);
			Assert.That(_mapper.Helm, Is.EqualTo(1));
			Set(gamepad.rightTrigger, 1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.5).Within(1e-6), "BothLevers must not overwrite this device's assigned port axis");
			Assert.That(_mapper.StarboardLever, Is.EqualTo(1));
		}

		[UnityTest]
		public IEnumerator CalibrationScreen_RendersMidCapture_WithSimulatedQuadrant()
		{
			var device = InputSystem.AddDevice<SimulatedQuadrant>();
			_screen = new GameObject("Calibration screenshot"); _screen.SetActive(false);
			_renderTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32); _renderTarget.Create();
			var screen = _screen.AddComponent<CalibrationScreen>(); screen.Configure(new DeviceProfileStore(_path), _renderTarget); _screen.SetActive(true);
			screen.View.SelectDevice(device);
			screen.View.BeginCalibration(ProfileTarget.PortLever, "slider");
			Set(device.GetChildControl<AxisControl>("slider"), -0.8f);
			yield return null; // UnityTest input events are applied on the next frame.
			screen.View.CapturePoint();
			Assert.That(screen.View.Q<Label>(className: "cal-step").text, Does.Contain("-0.800"));
			Set(device.GetChildControl<AxisControl>("slider"), 0.2f);
			Set(device.GetChildControl<AxisControl>("rz"), 0.7f);
			yield return null; yield return null;
			Assert.That(screen.View.CaptureCount, Is.EqualTo(1));
			Assert.That(screen.View.Q<Button>("capture").enabledSelf, Is.True);
			Assert.That(screen.View.Q<Button>("save").enabledSelf, Is.False);
			Assert.That(screen.View.Q<Button>("capture").worldBound.width, Is.GreaterThan(60));
			// An actual UI Toolkit mouse activation completes the neutral capture.
			var button = screen.View.Q<Button>("capture");
			var position = button.worldBound.center;
			using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = position, button = 0 })) button.SendEvent(down);
			using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = position, button = 0 })) button.SendEvent(up);
			Assert.That(screen.View.CaptureCount, Is.EqualTo(2), "mouse click captured neutral");
			yield return null; yield return null;
			var previous = RenderTexture.active;
			Texture2D texture = null;
			try
			{
				RenderTexture.active = _renderTarget;
				texture = new Texture2D(_renderTarget.width, _renderTarget.height, TextureFormat.RGBA32, false);
				texture.ReadPixels(new Rect(0, 0, _renderTarget.width, _renderTarget.height), 0, 0); texture.Apply();
				var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/unity"));
				Directory.CreateDirectory(directory);
				File.WriteAllBytes(Path.Combine(directory, "input-calibration-mid.png"), texture.EncodeToPNG());
				Assert.That(texture.width, Is.EqualTo(1280));
				var colours = new System.Collections.Generic.HashSet<Color32>(texture.GetPixels32());
				Assert.That(colours.Count, Is.GreaterThan(16), "calibration screen must actually render");
			}
			finally { RenderTexture.active = previous; if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
		}
	}

	[StructLayout(LayoutKind.Explicit, Size = 8)]
	public struct SimulatedKnobState : IInputStateTypeInfo
	{
		[InputControl(name = "delta", layout = "Delta")] [FieldOffset(0)] public Vector2 delta;
		public FourCC format => new FourCC('K', 'N', 'O', 'B');
	}
	[InputControlLayout(stateType = typeof(SimulatedKnobState))]
	public class SimulatedKnob : InputDevice { }
}
