using System;
using System.IO;
using System.Runtime.InteropServices;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// Device mapping of the "Helm" action map, driven through the Input System's test fixture with simulated
	/// devices. Failure modes guarded: sign errors (stick up must be +1 ahead, right must be +helm starboard),
	/// keyboard notches that miss exact neutral or overshoot ±1, a press counted twice, an idle device clobbering
	/// a lever another device set, buttons not firing (or firing on a short tap of the reset key), and the
	/// actions asset losing an action other systems rely on.
	/// </summary>
	public class HelmInputMapperTests : InputTestFixture
	{
		private InputActionAsset _actions;
		private HelmInputMapper _mapper;

		private static string ActionsPath => Path.Combine(Application.dataPath, "FerrySim", "Input", "FerrySimControls.inputactions");

		public override void Setup()
		{
			base.Setup();
			_actions = InputActionAsset.FromJson(File.ReadAllText(ActionsPath));
			_mapper = new HelmInputMapper(_actions);
			_mapper.Enable();
		}

		public override void TearDown()
		{
			_mapper.Dispose();
			UnityEngine.Object.DestroyImmediate(_actions);
			base.TearDown();
		}

		[Test]
		public void Asset_HasTheHelmMapWithItsAxesAndButtons()
		{
			var map = _actions.FindActionMap("Helm", throwIfNotFound: true);
			foreach (var axis in new[] { "PortLever", "StarboardLever", "Helm", "BothLevers" })
			{
				var action = map.FindAction(axis, throwIfNotFound: true);
				Assert.That(action.type, Is.EqualTo(InputActionType.Value), axis);
				Assert.That(action.expectedControlType, Is.EqualTo("Axis"), axis);
				Assert.That(action.bindings.Count, Is.GreaterThan(0), axis);
			}

			foreach (var button in new[] { "Horn", "Pause", "Reset", "CameraCycle" })
			{
				var action = map.FindAction(button, throwIfNotFound: true);
				Assert.That(action.type, Is.EqualTo(InputActionType.Button), button);
				Assert.That(action.bindings.Count, Is.GreaterThan(0), button);
			}

			foreach (var scheme in new[] { "Keyboard", "Gamepad", "Joystick" })
			{
				Assert.That(_actions.FindControlSchemeIndex(scheme), Is.GreaterThanOrEqualTo(0), scheme);
			}
		}

		[Test]
		public void Keyboard_StepSequence_ProducesTheExpectedLeverValues()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			// (key, expected port, expected starboard) after each press: Q/A port, E/D starboard, W/S both.
			var sequence = new (Key key, double port, double starboard)[]
			{
				(Key.Q, 0.2, 0.0),
				(Key.Q, 0.4, 0.0),
				(Key.E, 0.4, 0.2),
				(Key.A, 0.2, 0.2),
				(Key.A, 0.0, 0.2),
				(Key.A, -0.2, 0.2),
				(Key.W, 0.0, 0.4),
				(Key.S, -0.2, 0.2),
				(Key.S, -0.4, 0.0),
				(Key.D, -0.4, -0.2),
				(Key.W, -0.2, 0.0),
				(Key.W, 0.0, 0.2),
			};

			foreach (var (key, port, starboard) in sequence)
			{
				Tap(keyboard[key]);
				Assert.That(_mapper.PortLever, Is.EqualTo(port), $"port after {key}");
				Assert.That(_mapper.StarboardLever, Is.EqualTo(starboard), $"starboard after {key}");
			}

			Assert.That(BitConverter.DoubleToInt64Bits(_mapper.PortLever), Is.EqualTo(0L), "neutral is exactly +0");
		}

		[Test]
		public void Keyboard_LeversClickThroughNeutralToTheEndsAndClamp()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var expected = new[] { 0.2, 0.4, 0.6, 0.8, 1.0, 1.0, 1.0 };
			foreach (var value in expected)
			{
				Tap(keyboard.qKey);
				Assert.That(_mapper.PortLever, Is.EqualTo(value));
			}

			var down = new[] { 0.8, 0.6, 0.4, 0.2, 0.0, -0.2, -0.4, -0.6, -0.8, -1.0, -1.0 };
			foreach (var value in down)
			{
				Tap(keyboard.aKey);
				Assert.That(_mapper.PortLever, Is.EqualTo(value));
			}
		}

		[Test]
		public void Keyboard_HoldingAStepKeyStepsOnce()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();

			Press(keyboard.eKey);
			InputSystem.Update();
			InputSystem.Update();

			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.2));
			Release(keyboard.eKey);
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.2), "releasing does not step back");
		}

		[Test]
		public void Keyboard_ArrowKeysSteer_StarboardPositive()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();

			Press(keyboard.rightArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(1.0), "right arrow is starboard");
			Release(keyboard.rightArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(0.0));
			Press(keyboard.leftArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(-1.0), "left arrow is port");
			Release(keyboard.leftArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(0.0));
		}

		[Test]
		public void Gamepad_SticksMapWithAheadPositive()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();

			Set(gamepad.leftStick, new Vector2(0f, 1f));
			Assert.That(_mapper.PortLever, Is.EqualTo(1.0).Within(1e-6), "left stick up is port full ahead");
			Set(gamepad.rightStick, new Vector2(0f, -1f));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(-1.0).Within(1e-6), "right stick down is starboard full astern");
			Set(gamepad.leftStick, new Vector2(0f, 0.5f));
			Assert.That(_mapper.PortLever, Is.InRange(0.3, 0.6), "half stick is part ahead");

			Set(gamepad.leftStick, Vector2.zero);
			Set(gamepad.rightStick, Vector2.zero);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.0));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.0));
		}

		[Test]
		public void TriggerRestNoise_PreservesKeyboardOrders_AndReturnsToExactNeutral()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var gamepad = InputSystem.AddDevice<Gamepad>();
			for (var i = 0; i < 3; i++) Tap(keyboard.wKey);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.6), "keyboard precondition");
			Set(gamepad.rightTrigger, 0.01f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.6), "right trigger rest noise");
			Set(gamepad.leftTrigger, 0.02f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.6));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.6));
			Set(gamepad.rightTrigger, 1f);
			Assert.That(_mapper.PortLever, Is.GreaterThan(0.9));
			Set(gamepad.rightTrigger, 0.01f);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.0));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.0));
		}

		[Test]
		public void TwistRestNoise_IsExactlyNeutral_IncludingDeviceResync()
		{
			InputSystem.RegisterLayout<TwistJoystick>();
			var joystick = InputSystem.AddDevice<TwistJoystick>();
			var twist = joystick.GetChildControl<AxisControl>("twist");
			Set(twist, 0.01f);
			Assert.That(_mapper.Helm, Is.EqualTo(0.0));
			_mapper.ResyncWithDevices();
			Assert.That(_mapper.Helm, Is.EqualTo(0.0));
			Set(twist, -1f);
			Assert.That(_mapper.Helm, Is.EqualTo(-1.0));
			Set(twist, -0.02f);
			Assert.That(_mapper.Helm, Is.EqualTo(0.0));
		}

		[Test]
		public void Gamepad_TriggersDriveBothLevers()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();

			Set(gamepad.rightTrigger, 1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(1.0).Within(1e-6), "right trigger is both ahead");
			Assert.That(_mapper.StarboardLever, Is.EqualTo(1.0).Within(1e-6));
			Set(gamepad.rightTrigger, 0f);
			Set(gamepad.leftTrigger, 1f);
			Assert.That(_mapper.PortLever, Is.EqualTo(-1.0).Within(1e-6), "left trigger is both astern");
			Assert.That(_mapper.StarboardLever, Is.EqualTo(-1.0).Within(1e-6));
		}

		[Test]
		public void Gamepad_ShouldersSteer_StarboardPositive()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();

			Press(gamepad.rightShoulder);
			Assert.That(_mapper.Helm, Is.EqualTo(1.0), "right shoulder is starboard");
			Release(gamepad.rightShoulder);
			Press(gamepad.leftShoulder);
			Assert.That(_mapper.Helm, Is.EqualTo(-1.0), "left shoulder is port");
		}

		[Test]
		public void Gamepad_DpadStepsBothLevers()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();

			Tap(gamepad.dpad.up);
			Tap(gamepad.dpad.up);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.4));
			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.4));
			Tap(gamepad.dpad.down);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.2));
		}

		[Test]
		public void IdleGamepad_DoesNotOverrideKeyboardLevers_AndMovingItTakesOver()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var gamepad = InputSystem.AddDevice<Gamepad>();
			Tap(keyboard.qKey);
			Tap(keyboard.qKey);
			Tap(keyboard.qKey);

			for (var i = 0; i < 5; i++)
			{
				InputSystem.Update();
			}

			Assert.That(_mapper.PortLever, Is.EqualTo(0.6), "a gamepad at rest leaves the keyboard's lever alone");
			Set(gamepad.leftStick, new Vector2(0f, -1f));
			Assert.That(_mapper.PortLever, Is.EqualTo(-1.0).Within(1e-6), "the device moved last wins");
			Set(gamepad.leftStick, Vector2.zero);
			Tap(keyboard.qKey);
			Assert.That(_mapper.PortLever, Is.EqualTo(0.2), "stepping resumes from where the lever is");
		}

		[Test]
		public void Joystick_StickDrivesBothLeversAndTheHelm()
		{
			var joystick = InputSystem.AddDevice<Joystick>();

			Set(joystick.stick, new Vector2(0f, 1f));
			Assert.That(_mapper.PortLever, Is.EqualTo(1.0).Within(1e-6), "stick forward is both ahead");
			Assert.That(_mapper.StarboardLever, Is.EqualTo(1.0).Within(1e-6));
			Set(joystick.stick, new Vector2(1f, 0f));
			Assert.That(_mapper.Helm, Is.EqualTo(1.0).Within(1e-6), "stick right is starboard");
			Assert.That(_mapper.PortLever, Is.EqualTo(0.0), "stick centred fore and aft is neutral");
		}

		[Test]
		public void HidStyleTwistAxis_Steers_StarboardPositive()
		{
			InputSystem.RegisterLayout<TwistJoystick>();
			var joystick = InputSystem.AddDevice<TwistJoystick>();
			Assume.That(joystick.twist, Is.Not.Null, "the test device exposes a {Twist} axis");

			Set(joystick.twist, 1f);
			Assert.That(_mapper.Helm, Is.EqualTo(1.0).Within(1e-6), "twist right is starboard");
			Set(joystick.twist, -0.5f);
			Assert.That(_mapper.Helm, Is.EqualTo(-0.5).Within(1e-6));
		}

		[Test]
		public void Buttons_FireOncePerPress_AndResetNeedsAHold()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			int pauses = 0, resets = 0, cameras = 0;
			var horn = 0;
			_mapper.PauseRequested += () => pauses++;
			_mapper.ResetRequested += () => resets++;
			_mapper.CameraCycleRequested += () => cameras++;
			_mapper.HornChanged += active => horn += active ? 1 : -1;

			Tap(keyboard.pKey);
			Tap(keyboard.cKey);
			Tap(keyboard.cKey);
			Assert.That(pauses, Is.EqualTo(1));
			Assert.That(cameras, Is.EqualTo(2));

			Press(keyboard.hKey);
			Assert.That(_mapper.HornActive, Is.True);
			Release(keyboard.hKey);
			Assert.That(_mapper.HornActive, Is.False);
			Assert.That(horn, Is.EqualTo(0), "one on and one off");

			Press(keyboard.rKey);
			currentTime += 0.3;
			InputSystem.Update();
			Release(keyboard.rKey);
			Assert.That(resets, Is.EqualTo(0), "a tap of R does not reset the run");

			Press(keyboard.rKey);
			currentTime += 1.2;
			InputSystem.Update();
			Release(keyboard.rKey);
			Assert.That(resets, Is.EqualTo(1), "holding R for a second resets once");
		}

		[Test]
		public void ResyncWithDevices_ReturnsSteppedLeversToNeutralButKeepsHeldAxes()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var joystick = InputSystem.AddDevice<Joystick>();
			Tap(keyboard.eKey);
			Tap(keyboard.eKey);
			Set(joystick.stick, new Vector2(0.6f, 0f));

			_mapper.ResyncWithDevices();

			Assert.That(_mapper.StarboardLever, Is.EqualTo(0.0), "keyboard notches go back to neutral");
			Assert.That(_mapper.Helm, Is.GreaterThan(0.3), "a stick held over keeps its helm");
		}

		[TestCase(0.53, 1, 0.6)]
		[TestCase(0.53, -1, 0.4)]
		[TestCase(0.6, 1, 0.8)]
		[TestCase(0.6, -1, 0.4)]
		[TestCase(-0.01, 1, 0.0)]
		[TestCase(0.01, -1, 0.0)]
		[TestCase(1.0, 1, 1.0)]
		[TestCase(-1.0, -1, -1.0)]
		public void StepNotch_MovesToTheNextNotch(double from, int direction, double expected)
		{
			Assert.That(HelmInputMapper.StepNotch(from, direction, 5), Is.EqualTo(expected));
		}

		[Test]
		public void StepNotch_WorksForOtherNotchCounts()
		{
			Assert.That(HelmInputMapper.StepNotch(0.0, 1, 4), Is.EqualTo(0.25));
			Assert.That(HelmInputMapper.StepNotch(0.25, 1, 4), Is.EqualTo(0.5));
			Assert.That(HelmInputMapper.StepNotch(0.0, -1, 10), Is.EqualTo(-0.1));
		}

		private void Tap(ButtonControl button)
		{
			Press(button);
			Release(button);
		}

		/// <summary>A minimal HID-style joystick with a stick and a twist (Rz) axis tagged with the Twist usage.</summary>
		[StructLayout(LayoutKind.Explicit, Size = 16)]
		private struct TwistJoystickState : IInputStateTypeInfo
		{
			[InputControl(name = "trigger", layout = "Button", usages = new[] { "PrimaryTrigger", "PrimaryAction" }, bit = 0)]
			[FieldOffset(0)]
			public byte buttons;

			[InputControl(name = "stick", layout = "Stick", usage = "Primary2DMotion")]
			[FieldOffset(4)]
			public Vector2 stick;

			[InputControl(name = "twist", layout = "Axis", usage = "Twist")]
			[FieldOffset(12)]
			public float twist;

			public FourCC format => new FourCC('T', 'W', 'S', 'T');
		}

		[InputControlLayout(stateType = typeof(TwistJoystickState), displayName = "Test Twist Joystick")]
		private sealed class TwistJoystick : Joystick
		{
		}
	}
}
