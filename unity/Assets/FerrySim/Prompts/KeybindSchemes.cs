using UnityEngine.InputSystem;

namespace FerrySim.Prompts
{
	/// <summary>The control schemes of the actions asset (C4) and which one a device belongs to.</summary>
	public static class KeybindSchemes
	{
		public const string Keyboard = "Keyboard";
		public const string Gamepad = "Gamepad";
		public const string Joystick = "Joystick";

		/// <summary>The scheme a device's bindings belong to; mouse buttons share the keyboard's. Null for any other device.</summary>
		public static string ForDevice(InputDevice device)
		{
			switch (device)
			{
				case UnityEngine.InputSystem.Keyboard _:
				case Mouse _:
					return Keyboard;
				case UnityEngine.InputSystem.Gamepad _:
					return Gamepad;
				case UnityEngine.InputSystem.Joystick _:
					return Joystick;
				default:
					return null;
			}
		}
	}
}
