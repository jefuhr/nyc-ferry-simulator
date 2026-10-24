using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.HID;
using UnityEngine.InputSystem.Layouts;
using FerrySim.Controls.Profiles;

namespace FerrySim.Controls.ProfileTests
{
	public class GenericLayoutTests : InputTestFixture
	{
		[Test]
		public void GenericHidDescriptor_ProvidesQuadrantAxesWithoutVendorCode()
		{
			var descriptor = new HID.HIDDeviceDescriptor
			{
				vendorId = 1234, productId = 5678, usagePage = HID.UsagePage.GenericDesktop,
				usage = (int)HID.GenericDesktop.Joystick, inputReportSize = 9,
				elements = new[]
				{
					Axis(HID.GenericDesktop.X, 0), Axis(HID.GenericDesktop.Y, 16),
					Axis(HID.GenericDesktop.Slider, 32), Axis(HID.GenericDesktop.Rz, 48),
					new HID.HIDElementDescriptor { usagePage = HID.UsagePage.Button, usage = 1, reportType = HID.HIDReportType.Input,
						reportOffsetInBits = 64, reportSizeInBits = 1, logicalMin = 0, logicalMax = 1, flags = HID.HIDElementFlags.Variable }
				}
			};
			var device = InputSystem.AddDevice(new InputDeviceDescription
			{
				interfaceName = "HID", manufacturer = "Fixture", product = "USB Control", serial = "hid-1", capabilities = descriptor.ToJson()
			});
			Assert.That(device, Is.InstanceOf<Joystick>());
			var profile = DeviceProfileDefaults.Create(device);
			Assert.That(profile.deviceClass, Is.EqualTo(DeviceProfileClass.ThrottleQuadrant));
			Assert.That(device.TryGetChildControl<AxisControl>(profile.axes[0].path), Is.Not.Null);
			Assert.That(profile.axes[0].target, Is.EqualTo(ProfileTarget.PortLever));
			Assert.That(profile.axes[1].target, Is.EqualTo(ProfileTarget.StarboardLever));
		}

		[Test]
		public void GenericDialLayout_IsRecognizedByItsControlPath()
		{
			InputSystem.RegisterLayout<InputDevice>("ProfileBaseDevice");
			InputSystem.RegisterLayout(@"{""name"":""TestHID"",""extend"":""ProfileBaseDevice"",""controls"":[{""name"":""dial"",""layout"":""Axis"",""offset"":0,""format"":""FLT""}]}");
			var device = InputSystem.AddDevice("TestHID");
			var profile = DeviceProfileDefaults.Create(device);
			Assert.That(profile.deviceClass, Is.EqualTo(DeviceProfileClass.WheelOrKnob));
			Assert.That(profile.axes[0].path, Is.EqualTo("dial"));
			Assert.That(profile.axes[0].target, Is.EqualTo(ProfileTarget.Helm));
		}

		[Test]
		public void DefaultProfileRouter_IsBitIdenticalToTheOriginalActionPath()
		{
			var json = File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions"));
			var originalActions = InputActionAsset.FromJson(json);
			InputActionAsset profiledActions = null;
			try
			{
				profiledActions = InputActionAsset.FromJson(json);
				using (var original = new HelmInputMapper(originalActions))
				using (var profiled = new HelmInputMapper(profiledActions))
				using (var router = new DeviceProfileRouter(profiled, new DeviceProfileStore("unused-profile-path")))
				{
					original.Enable(); profiled.Enable();
					var keyboard = InputSystem.AddDevice<Keyboard>();
					var gamepad = InputSystem.AddDevice<Gamepad>();
					var joystick = InputSystem.AddDevice<Joystick>();
					Press(keyboard.wKey); Release(keyboard.wKey); Same(original, profiled);
					Set(gamepad.rightTrigger, 0.01f); Same(original, profiled);
					Set(gamepad.leftStick, new Vector2(0, 0.64f)); Same(original, profiled);
					Set(gamepad.rightTrigger, 0.73f); Same(original, profiled);
					Set(joystick.stick, new Vector2(-0.3f, -0.7f)); Same(original, profiled);
					original.ResyncWithDevices(); profiled.ResyncWithDevices(); Same(original, profiled);
				}
			}
			finally { Object.DestroyImmediate(originalActions); Object.DestroyImmediate(profiledActions); }
		}
		[Test]
		public void GeneratedFourAxisHid_PreservesJoystickFallbackAndBitParity()
		{
			var descriptor = new HID.HIDDeviceDescriptor
			{
				vendorId = 1235, productId = 5679, usagePage = HID.UsagePage.GenericDesktop,
				usage = (int)HID.GenericDesktop.Joystick, inputReportSize = 9,
				elements = new[] { Axis(HID.GenericDesktop.X, 0), Axis(HID.GenericDesktop.Y, 16), Axis(HID.GenericDesktop.Z, 32), Axis(HID.GenericDesktop.Rz, 48),
					new HID.HIDElementDescriptor { usagePage = HID.UsagePage.Button, usage = 1, reportType = HID.HIDReportType.Input,
						reportOffsetInBits = 64, reportSizeInBits = 1, logicalMin = 0, logicalMax = 1, flags = HID.HIDElementFlags.Variable } }
			};
			var device = InputSystem.AddDevice(new InputDeviceDescription { interfaceName = "HID", manufacturer = "Fixture", product = "USB Joystick", capabilities = descriptor.ToJson() });
			Assert.That(device, Is.InstanceOf<Joystick>());
			var json = File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions"));
			var originalActions = InputActionAsset.FromJson(json);
			InputActionAsset profiledActions = null;
			try
			{
				profiledActions = InputActionAsset.FromJson(json);
				using (var original = new HelmInputMapper(originalActions))
				using (var profiled = new HelmInputMapper(profiledActions))
				using (var router = new DeviceProfileRouter(profiled, new DeviceProfileStore("unused-profile-path")))
				{
					original.Enable(); profiled.Enable();
					Set(device.GetChildControl<AxisControl>("stick/y"), 0.8f);
					Assert.That(original.PortLever, Is.Not.Zero);
					Same(original, profiled);
					Set(device.GetChildControl<AxisControl>("rz"), 0.9f);
					Assert.That(original.Helm, Is.Not.Zero);
					Same(original, profiled);
					Set(device.GetChildControl<AxisControl>("z"), 0.7f); Same(original, profiled);
					Assert.That(DeviceProfileDefaults.Create(device).deviceClass, Is.EqualTo(DeviceProfileClass.GenericJoystick));
				}
			}
			finally { Object.DestroyImmediate(originalActions); Object.DestroyImmediate(profiledActions); }
		}

		[Test]
		public void AnalogTriggers_AreListedButDigitalButtonsAreNot()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var axes = DeviceProfileDefaults.Axes(gamepad);
			Assert.That(axes, Does.Contain(gamepad.leftTrigger));
			Assert.That(axes, Does.Contain(gamepad.rightTrigger));
			CollectionAssert.DoesNotContain(axes, gamepad.buttonSouth);
			CollectionAssert.DoesNotContain(axes, gamepad.dpad.up);
		}

		private static void Same(HelmInputMapper a, HelmInputMapper b)
		{
			Assert.That(System.BitConverter.DoubleToInt64Bits(b.PortLever), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a.PortLever)));
			Assert.That(System.BitConverter.DoubleToInt64Bits(b.StarboardLever), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a.StarboardLever)));
			Assert.That(System.BitConverter.DoubleToInt64Bits(b.Helm), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a.Helm)));
		}
		private static HID.HIDElementDescriptor Axis(HID.GenericDesktop usage, int offset) => new HID.HIDElementDescriptor
		{
			usagePage = HID.UsagePage.GenericDesktop, usage = (int)usage, reportType = HID.HIDReportType.Input,
			reportOffsetInBits = offset, reportSizeInBits = 16, logicalMin = 0, logicalMax = 65535, flags = HID.HIDElementFlags.Variable
		};
	}
}
