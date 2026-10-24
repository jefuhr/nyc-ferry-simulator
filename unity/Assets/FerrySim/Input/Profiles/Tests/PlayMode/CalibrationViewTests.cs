using System;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using FerrySim.Controls.Profiles;

namespace FerrySim.Controls.ProfileTests
{
	public class CalibrationViewTests : InputTestFixture
	{
		private DeviceProfileStore _store;
		private CalibrationView _view;
		private SimulatedQuadrant _device;
		private string _path;
		public override void Setup()
		{
			base.Setup();
			InputSystem.RegisterLayout<SimulatedQuadrant>();
			_device = InputSystem.AddDevice<SimulatedQuadrant>();
			_path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
			_store = new DeviceProfileStore(_path);
			_view = new CalibrationView(_store);
			_view.SelectDevice(_device);
		}
		public override void TearDown()
		{
			_view.Dispose();
			if (File.Exists(_path)) File.Delete(_path);
			base.TearDown();
		}
		[Test]
		public void CaptureWizard_PreviewsAndSavesOnlyCompleteCalibration()
		{
			_view.BeginCalibration(ProfileTarget.PortLever, "slider");
			Set(_device.GetChildControl<AxisControl>("slider"), -0.8f); _view.CapturePoint();
			Set(_device.GetChildControl<AxisControl>("slider"), 0.2f); _view.CapturePoint();
			Assert.That(_view.CaptureCount, Is.EqualTo(2));
			Assert.That(_view.SaveDevice(), Is.False, "incomplete capture cannot be saved");
			Assert.That(File.Exists(_path), Is.False);
			Set(_device.GetChildControl<AxisControl>("slider"), 1.1f); _view.CapturePoint();
			Assert.That(_view.StageCalibration(0.04f), Is.True);
			Set(_device.GetChildControl<AxisControl>("slider"), 0.67f); _view.Refresh();
			Assert.That(_view.DraftOutput(ProfileTarget.PortLever), Is.EqualTo(0.5f).Within(1e-6));
			Assert.That(_view.SaveDevice(), Is.True);
			var loaded = new DeviceProfileStore(_path);
			Assert.That(loaded.Load(), Is.True);
			Assert.That(loaded.Find(DeviceIdentity.Key(_device)).axes.Find(axis => axis.target == ProfileTarget.PortLever).calibration.neutral, Is.EqualTo(0.2f));
		}
		[Test]
		public void CancelOrUnplug_NeverCommitsDraft()
		{
			_view.BeginCalibration(ProfileTarget.Helm, "rz");
			_view.CapturePoint();
			_view.CancelCalibration();
			Assert.That(_store.Find(DeviceIdentity.Key(_device)), Is.Null);
			_view.BeginCalibration(ProfileTarget.Helm, "rz");
			InputSystem.RemoveDevice(_device);
			_view.Refresh();
			Assert.That(_view.SaveDevice(), Is.False);
			Assert.That(File.Exists(_path), Is.False);
		}
		[Test]
		public void FailedFirstSave_RollsBackHeldRouteToNeutral()
		{
			InputSystem.RemoveDevice(_device);
			File.WriteAllText(_path, "block parent directory");
			var store = new DeviceProfileStore(Path.Combine(_path, "profile.json"));
			var actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var gamepad = InputSystem.AddDevice<Gamepad>();
			try
			{
				using (var mapper = new HelmInputMapper(actions))
				using (var router = new DeviceProfileRouter(mapper, store))
				using (var view = new CalibrationView(store))
				{
					mapper.Enable(); view.SelectDevice(gamepad);
					view.BeginCalibration(ProfileTarget.Helm, "leftStick/x");
					Set(gamepad.leftStick, Vector2.left); view.CapturePoint();
					Set(gamepad.leftStick, Vector2.zero); view.CapturePoint();
					Set(gamepad.leftStick, Vector2.right); view.CapturePoint();
					Assert.That(view.StageCalibration(0f), Is.True);
					Assert.That(view.SaveDevice(), Is.False);
					Assert.That(store.LastError, Is.Not.Empty);
					Assert.That(store.Find(DeviceIdentity.Key(gamepad)), Is.Null);
					Assert.That(mapper.Helm, Is.Zero);
				}
			}
			finally { UnityEngine.Object.DestroyImmediate(actions); }
		}

		[Test]
		public void FailedSave_PreservesKeyboardOwnerOfExistingAssignment()
		{
			File.WriteAllText(_path, "block parent directory");
			var store = new DeviceProfileStore(Path.Combine(_path, "profile.json"));
			var profile = DeviceProfileDefaults.Create(_device);
			profile.axes[0].calibration = new AxisCalibration(-1f, 0f, 1f, 0f); store.Set(profile);
			var actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			try
			{
				using (var mapper = new HelmInputMapper(actions))
				using (var router = new DeviceProfileRouter(mapper, store))
				using (var view = new CalibrationView(store))
				{
					mapper.Enable(); view.SelectDevice(_device);
					view.BeginCalibration(ProfileTarget.PortLever, "slider");
					var axis = _device.GetChildControl<AxisControl>("slider");
					Set(axis, -1f); view.CapturePoint(); Set(axis, 0.2f); view.CapturePoint(); Set(axis, 1f); view.CapturePoint();
					Assert.That(view.StageCalibration(0f), Is.True); Set(axis, 0.5f);
					var keyboard = InputSystem.AddDevice<Keyboard>(); Press(keyboard.qKey); Release(keyboard.qKey);
					var expected = mapper.PortLever;
					Assert.That(expected, Is.EqualTo(0.6));
					Assert.That(view.SaveDevice(), Is.False);
					Assert.That(mapper.PortLever, Is.EqualTo(expected), "failed save cannot take control away from the keyboard");
				}
			}
			finally { UnityEngine.Object.DestroyImmediate(actions); }
		}

		[TestCase(false)]
		[TestCase(true)]
		public void UnchangedRefresh_AllocatesNothingOutsideUIToolkit(bool duringCapture)
		{
			// Detached view matches UI RefreshAllocationTests: no panel layout/repaint internals measured.
			if (duringCapture) { _view.BeginCalibration(ProfileTarget.PortLever, "slider"); _view.CapturePoint(); }
			for (var i = 0; i < 12; i++) _view.Refresh();
			Assert.That(() => _view.Refresh(), UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void NoSerialNote_DoesNotClaimReconnectOrderSeparatesIdentities()
		{
			Assert.That(_view.Q<Label>(className: "cal-note").text, Does.Not.Contain("Reconnect in the same order"));
			var labels = _view.Query<Label>().ToList();
			Assert.That(labels.Exists(label => label.text.Contains("Reconnect in the same order")), Is.False);
		}

		[Test]
		public void DigitalButton_CannotStartAnAxisCapture()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>(); _view.SelectDevice(gamepad);
			_view.BeginCalibration(ProfileTarget.PortLever, "buttonSouth"); _view.CapturePoint();
			Assert.That(_view.CaptureCount, Is.Zero);
		}

		[Test]
		public void UserCanExplicitlyChooseQuadrantProfile()
		{
			var choice = _view.Q<Button>("use-quadrant");
			Assert.That(choice, Is.Not.Null);
			Assert.That(choice.enabledSelf, Is.True);
			_view.UseQuadrantProfile();
			Assert.That(_view.SaveDevice(), Is.True);
			Assert.That(_store.Find(DeviceIdentity.Key(_device)).deviceClass, Is.EqualTo(DeviceProfileClass.ThrottleQuadrant));
		}

		[Test]
		public void AnalogTrigger_CanBeCapturedPreviewedAndSaved()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>(); _view.SelectDevice(gamepad);
			Assert.That(_view.Q<DropdownField>("axis").choices, Does.Contain("leftTrigger"));
			_view.BeginCalibration(ProfileTarget.PortLever, "leftTrigger");
			Set(gamepad.leftTrigger, 0f); _view.CapturePoint();
			Set(gamepad.leftTrigger, 0.4f); _view.CapturePoint();
			Set(gamepad.leftTrigger, 1f); _view.CapturePoint();
			Assert.That(_view.StageCalibration(0.02f), Is.True);
			Assert.That(_view.DraftOutput(ProfileTarget.PortLever), Is.EqualTo(1));
			Assert.That(_view.SaveDevice(), Is.True);
		}

		[Test]
		public void ControllerReenable_KeepsStoreBoundToOpenView()
		{
			var actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var host = new GameObject("Controller lifecycle"); host.SetActive(false);
			try
			{
				var controller = host.AddComponent<HelmInputController>(); controller.Actions = actions;
				host.SetActive(true);
				var original = controller.Profiles;
				using (var view = new CalibrationView(original))
				{
					view.SelectDevice(_device);
					controller.enabled = false; controller.enabled = true;
					Assert.That(controller.Profiles, Is.SameAs(original));
					view.BeginCalibration(ProfileTarget.PortLever, "slider");
					Set(_device.GetChildControl<AxisControl>("slider"), -1f); view.CapturePoint();
					Set(_device.GetChildControl<AxisControl>("slider"), 0f); view.CapturePoint();
					Set(_device.GetChildControl<AxisControl>("slider"), 1f); view.CapturePoint();
					Assert.That(view.StageCalibration(0f), Is.True);
					// Publish the view's shared store without writing the real persistent user file.
					var profile = DeviceProfileDefaults.Create(_device); profile.axes[0].inverted = true;
					original.Set(profile);
					Assert.That(controller.Mapper.PortLever, Is.EqualTo(-1));
				}
			}
			finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(actions); }
		}

	}
}
