using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.HID;

namespace FerrySim.Controls.Profiles
{
	/// <summary>Layout/control paths, never a vendor SDK. The original Helm bindings are the fallback.</summary>
	public static class DeviceProfileDefaults
	{
		public static List<AxisControl> Axes(InputDevice device) => device.allControls.OfType<AxisControl>()
			.Where(IsAnalogAxis)
			.OrderBy(axis => RelativePath(axis), StringComparer.Ordinal).ToList();
		/// <summary>Float/byte trigger buttons are analog; bit buttons and stick/d-pad directions are digital.</summary>
		public static bool IsAnalogAxis(AxisControl axis) => axis != null && !(axis.parent is DpadControl)
			&& (!(axis is ButtonControl) || (axis.stateBlock.sizeInBits > 1 && !(axis.parent is StickControl)));

		public static string RelativePath(InputControl control) => control.path.Substring(control.device.path.Length + 1);

		public static DeviceProfile Create(InputDevice device, bool useQuadrant = false)
		{
			var result = new DeviceProfile { identity = DeviceIdentity.Key(device), deviceClass = DeviceProfileClass.GenericJoystick };
			// These classes retain the exact existing composite actions, deadzones and notch semantics.
			if (device is Keyboard) { result.deviceClass = DeviceProfileClass.Keyboard; return result; }
			if (device is Gamepad) { result.deviceClass = DeviceProfileClass.Gamepad; return result; }
			if (device is Pointer || device is Sensor) return result;
			var axes = Axes(device);
			var sliders = axes.Where(axis => IsLever(axis.name)).OrderBy(axis => axis.name.StartsWith("slider", StringComparison.OrdinalIgnoreCase) ? 0 : axis.name == "z" ? 1 : 2).ThenBy(axis => axis.name, StringComparer.Ordinal).ToList();
			var name = (device.layout + " " + device.description.product).ToLowerInvariant();
			if (!useQuadrant && (name.Contains("wheel") || name.Contains("knob") || name.Contains("rotary") || name.Contains("dial")
				|| (sliders.Count < 2 && axes.Any(axis => axis.name == "dial" || axis.name == "wheel" || axis.name == "steering"))))
			{
				result.deviceClass = DeviceProfileClass.WheelOrKnob;
				var helm = axes.FirstOrDefault(axis => axis.name == "x" || axis.name == "steering" || axis.name == "wheel")
					?? axes.FirstOrDefault(axis => axis.name == "dial") ?? axes.FirstOrDefault();
				if (helm != null) Add(result, helm, ProfileTarget.Helm, IsRelative(helm));
			}
			else if (sliders.Count >= 2 && (useQuadrant || name.Contains("quadrant") || name.Contains("dual throttle") || name.Contains("twin throttle")
				|| sliders.Any(axis => axis.name.StartsWith("slider", StringComparison.OrdinalIgnoreCase))))
			{
				result.deviceClass = DeviceProfileClass.ThrottleQuadrant;
				Add(result, sliders[0], ProfileTarget.PortLever);
				Add(result, sliders[1], ProfileTarget.StarboardLever);
				var helm = axes.FirstOrDefault(axis => axis.name == "x");
				if (helm != null) Add(result, helm, ProfileTarget.Helm);
			}
			return result;
		}
		private static bool IsRelative(AxisControl axis)
		{
			if (axis.parent is DeltaControl) return true;
			var description = axis.device.description;
			if (description.interfaceName != "HID" || string.IsNullOrEmpty(description.capabilities)
				|| !Enum.TryParse<HID.GenericDesktop>(axis.name, true, out var usage)) return false;
			try
			{
				var descriptor = HID.HIDDeviceDescriptor.FromJson(description.capabilities);
				return descriptor.elements != null && descriptor.elements.Any(element => element.usagePage == HID.UsagePage.GenericDesktop
					&& element.usage == (int)usage && element.isRelative);
			}
			catch (ArgumentException) { return false; }
		}

		private static bool IsLever(string name) => name.StartsWith("slider", StringComparison.OrdinalIgnoreCase)
			|| name.Equals("z", StringComparison.OrdinalIgnoreCase) || name.Equals("rz", StringComparison.OrdinalIgnoreCase);
		private static void Add(DeviceProfile profile, AxisControl axis, ProfileTarget target, bool relative = false) => profile.axes.Add(new AxisAssignment
		{
			path = RelativePath(axis), target = target, relative = relative,
			calibration = new AxisCalibration(-1f, 0f, 1f, 0.05f)
		});
	}
}
