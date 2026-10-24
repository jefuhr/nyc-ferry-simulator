using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using FerrySim.Controls.Profiles;

namespace FerrySim.Controls.ProfileTests
{
	public class DeviceRoutingTests : InputTestFixture
	{
		private InputActionAsset _actions;
		private HelmInputMapper _mapper;
		private DeviceProfileStore _store;
		private DeviceProfileRouter _router;
		public override void Setup()
		{
			base.Setup();
			InputSystem.RegisterLayout<SimulatedQuadrant>();
			_actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_mapper = new HelmInputMapper(_actions);
			_store = new DeviceProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
			_router = new DeviceProfileRouter(_mapper, _store);
			_mapper.Enable();
		}
		public override void TearDown()
		{
			_router.Dispose();
			_mapper.Dispose();
			UnityEngine.Object.DestroyImmediate(_actions);
			base.TearDown();
		}

		[Test]
		public void AssignedOffCentreAxes_AreCalibratedBeforeMapperOutput()
		{
			var device = Add("A");
			Assign(device, "slider", ProfileTarget.PortLever);
			Set(device.GetChildControl<AxisControl>("slider"), 0.67f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.5).Within(1e-6));
			Set(device.GetChildControl<AxisControl>("slider"), 0.22f);
			Assert.That(_mapper.PortLever, Is.Zero);
			Set(device.GetChildControl<AxisControl>("slider"), -0.8f);
			Assert.That(_mapper.PortLever, Is.EqualTo(-1));
			_mapper.ResyncWithDevices();
			Assert.That(_mapper.PortLever, Is.EqualTo(-1));
		}

		[Test]
		public void SerialAssignments_DoNotCrossTalk()
		{
			var a = Add("A");
			var b = Add("B");
			Assign(a, "slider", ProfileTarget.PortLever);
			Assign(b, "slider", ProfileTarget.StarboardLever, true);
			Set(a.GetChildControl<AxisControl>("slider"), 1.1f);
			Set(b.GetChildControl<AxisControl>("slider"), 1.1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(1));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(-1));
		}

		[Test]
		public void HotPlug_RestoresAssignmentWithoutRestartingMapper()
		{
			var first = Add("A");
			Assign(first, "rz", ProfileTarget.Helm);
			InputSystem.RemoveDevice(first);
			var reconnected = Add("A");
			Set(reconnected.GetChildControl<AxisControl>("rz"), 1.1f);
			Assert.That(_mapper.Helm, Is.EqualTo(1));
		}

		[Test]
		public void IdleDetent_DoesNotStealKeyboard_AndMovingAxisTakesOver()
		{
			var device = Add("A");
			Assign(device, "slider", ProfileTarget.PortLever);
			Set(device.GetChildControl<AxisControl>("slider"), 0.2f);
			var keyboard = InputSystem.AddDevice<Keyboard>();
			Press(keyboard.qKey); Release(keyboard.qKey);
			Set(device.GetChildControl<AxisControl>("slider"), 0.22f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.2));
			Set(device.GetChildControl<AxisControl>("slider"), 0.67f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.5).Within(1e-6));
		}

		[Test]
		public void UncalibratedQuadrant_HasIndependentLevers()
		{
			var device = Add("A");
			Set(device.GetChildControl<AxisControl>("slider"), 1f);
			Set(device.GetChildControl<AxisControl>("rz"), -1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(1));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(-1));
		}

		[Test]
		public void Recalibration_ReevaluatesHeldAxisWithoutStealingUnchangedTargets()
		{
			var device = Add("A");
			Set(device.GetChildControl<AxisControl>("slider"), 0.2f);
			Assert.That(_mapper.PortLever, Is.GreaterThan(0.1));
			var keyboard = InputSystem.AddDevice<Keyboard>();
			for (var i = 0; i < 3; i++) { Press(keyboard.eKey); Release(keyboard.eKey); }
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.6));
			var profile = DeviceProfileDefaults.Create(device);
			profile.axes.Find(axis => axis.target == ProfileTarget.PortLever).calibration = new AxisCalibration(-0.8f, 0.2f, 1.1f, 0.04f);
			_store.Set(profile);
			Assert.That(_mapper.PortLever, Is.Zero, "saving a new neutral must immediately remove the old ahead order");
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.6), "unchanged axes cannot steal a keyboard order on save");
		}

		[TestCase(false)]
		[TestCase(true)]
		public void HeldRouteRemovalOrReassignment_ReleasesOldTarget(bool reassign)
		{
			var device = Add("A");
			var profile = new DeviceProfile { identity = DeviceIdentity.Key(device) };
			profile.axes.Add(new AxisAssignment { path = "slider", target = ProfileTarget.PortLever });
			_store.Set(profile);
			Set(device.GetChildControl<AxisControl>("slider"), 1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(1));
			if (reassign) profile.axes[0].target = ProfileTarget.Helm; else profile.axes.Clear();
			_store.Set(profile);
			Assert.That(_mapper.PortLever, Is.Zero, "removed route must return to available action fallback or neutral");
			if (reassign) Assert.That(_mapper.Helm, Is.EqualTo(1));
		}

		[Test]
		public void HeldRouteResetToNoDefaults_ReleasesTarget()
		{
			var device = InputSystem.AddDevice<Gamepad>();
			var profile = DeviceProfileDefaults.Create(device);
			profile.axes.Add(new AxisAssignment { path = "leftStick/x", target = ProfileTarget.Helm });
			_store.Set(profile); Set(device.leftStick, Vector2.right);
			Assert.That(_mapper.Helm, Is.EqualTo(1));
			_store.Reset(profile.identity);
			Assert.That(_mapper.Helm, Is.Zero);
		}

		[TestCase(false)]
		[TestCase(true)]
		public void RemovingHeldRoute_PreservesNewKeyboardOrDeviceOwner(bool anotherDevice)
		{
			var device = Add("A"); Assign(device, "slider", ProfileTarget.PortLever);
			Set(device.GetChildControl<AxisControl>("slider"), 1.1f);
			if (anotherDevice)
			{
				var other = Add("B"); Assign(other, "slider", ProfileTarget.PortLever);
				Set(other.GetChildControl<AxisControl>("slider"), 0.67f);
			}
			else
			{
				var keyboard = InputSystem.AddDevice<Keyboard>(); Press(keyboard.aKey); Release(keyboard.aKey);
			}
			var expected = _mapper.PortLever;
			_store.Set(new DeviceProfile { identity = DeviceIdentity.Key(device) });
			Assert.That(_mapper.PortLever, Is.EqualTo(expected));
		}

		[Test]
		public void PersistedAnalogTrigger_IsCalibratedAndRouted()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var profile = DeviceProfileDefaults.Create(gamepad);
			profile.axes.Add(new AxisAssignment { path = "leftTrigger", target = ProfileTarget.PortLever, calibration = new AxisCalibration(0f, 0.4f, 1f, 0.02f) });
			_store.Set(profile);
			Set(gamepad.leftTrigger, 1f); Assert.That(_mapper.PortLever, Is.EqualTo(1));
			Set(gamepad.leftTrigger, 0.4f); Assert.That(_mapper.PortLever, Is.Zero);
			Set(gamepad.leftTrigger, 0f); Assert.That(_mapper.PortLever, Is.EqualTo(-1));
		}

		[Test]
		public void UnsupportedPersistedButtonAssignment_ReportsRejection()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var profile = DeviceProfileDefaults.Create(gamepad);
			profile.axes.Add(new AxisAssignment { path = "buttonSouth", target = ProfileTarget.PortLever });
			_store.Set(profile);
			Assert.That(_store.LastError, Does.Contain("buttonSouth"));
			Assert.That(_store.LastError, Does.Contain("analog"));
			try
			{
				Assert.That(_store.Save(), Is.True);
				Assert.That(_store.LastError, Does.Contain("buttonSouth"));
				Assert.That(_store.Load(), Is.True);
				Assert.That(_store.LastError, Does.Contain("analog"));
			}
			finally { if (File.Exists(_store.Path)) File.Delete(_store.Path); }
		}

		[Test]
		public void RemovingHeldRoute_ReturnsToHeldFallback()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var profile = DeviceProfileDefaults.Create(gamepad);
			profile.axes.Add(new AxisAssignment { path = "leftStick/y", target = ProfileTarget.PortLever });
			_store.Set(profile); Set(gamepad.leftStick, Vector2.up);
			profile.axes.Clear(); _store.Set(profile);
			Assert.That(_mapper.PortLever, Is.EqualTo(1), "the held legacy action is available again");
			Set(gamepad.leftStick, Vector2.zero); Assert.That(_mapper.PortLever, Is.Zero);
		}

		private SimulatedQuadrant Add(string serial) => (SimulatedQuadrant)InputSystem.AddDevice(new InputDeviceDescription
		{
			deviceClass = nameof(SimulatedQuadrant), interfaceName = "HID", manufacturer = "Test", product = "Quadrant", serial = serial
		});
		private void Assign(InputDevice device, string path, ProfileTarget target, bool inverted = false)
		{
			_store.Set(new DeviceProfile
			{
				identity = DeviceIdentity.Key(device), deviceClass = DeviceProfileClass.ThrottleQuadrant,
				axes = new System.Collections.Generic.List<AxisAssignment>
				{
					new AxisAssignment { path = path, target = target, inverted = inverted,
						calibration = new AxisCalibration(-0.8f, 0.2f, 1.1f, 0.04f) }
				}
			});
			_router.Refresh();
		}
	}

	[StructLayout(LayoutKind.Explicit, Size = 24)]
	public struct SimulatedQuadrantState : IInputStateTypeInfo
	{
		[InputControl(name = "trigger", layout = "Button", bit = 0)] [FieldOffset(0)] public byte buttons;
		[InputControl(name = "stick", layout = "Stick")] [FieldOffset(4)] public Vector2 stick;
		[InputControl(name = "slider", layout = "Axis")] [FieldOffset(12)] public float slider;
		[InputControl(name = "rz", layout = "Axis")] [FieldOffset(16)] public float rz;
		[InputControl(name = "dial", layout = "Axis")] [FieldOffset(20)] public float dial;
		public FourCC format => new FourCC('Q', 'U', 'A', 'D');
	}
	[InputControlLayout(stateType = typeof(SimulatedQuadrantState), displayName = "Simulated twin throttle quadrant")]
	public class SimulatedQuadrant : Joystick { }
}
