using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace FerrySim.Controls.Tests.PlayMode
{
	// Real Input System capture, not a mocked callback: parts, cancellation, conflicts and map lifetime.
	public class RebindingPlayModeTests : InputTestFixture
	{
		private InputActionAsset _asset;
		private InputBindingService _service;
		private Keyboard _keyboard;
		public override void Setup()
		{
			base.Setup();
			_keyboard = InputSystem.AddDevice<Keyboard>();
			_asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_service = new InputBindingService(_asset);
			_service.Register(_asset); _asset.FindActionMap("Helm").Enable();
		}
		public override void TearDown() { _service.Dispose(); UnityEngine.Object.DestroyImmediate(_asset); base.TearDown(); }
		[Test]
		public void RebindButton_CapturesKeyboard_ChangesDisplay_AndResetRestoresDefault()
		{
			var count = 0; var callbacks = 0; RebindOutcome outcome = default;
			_service.BindingsChanged += () => count++;
			_service.BeginRebind(ActionIds.Horn, 0, "Keyboard", value => { outcome = value; callbacks++; });
			Assert.That(_service.IsRebinding, Is.True);
			Press(_keyboard.f11Key);
			Assert.That(outcome.Result, Is.EqualTo(RebindResult.Applied)); Assert.That(callbacks, Is.EqualTo(1));
			Assert.That(_service.IsRebinding, Is.False);
			Assert.That(_asset.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/f11"));
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Does.Contain("F11"));
			Release(_keyboard.f11Key);
			Assert.That(_asset.FindActionMap("Helm").enabled, Is.True);
			_service.Reset(ActionIds.Horn);
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
			Assert.That(count, Is.EqualTo(2));
		}
		[Test]
		public void RebindCompositePart_OnlyChangesTheChosenSlot_AndReallySteps()
		{
			using (var mapper = new HelmInputMapper(_asset))
			{
				mapper.Enable(); var results = 0;
				_service.BeginRebind(ActionIds.PortLeverStep, 1, "Keyboard", value => { Assert.That(value.Result, Is.EqualTo(RebindResult.Applied)); results++; });
				Press(_keyboard.f11Key); Assert.That(results, Is.EqualTo(1));
				Assert.That(mapper.PortLever, Is.Zero, "capture itself must not order a lever step");
				Release(_keyboard.f11Key); Press(_keyboard.f11Key);
				Assert.That(mapper.PortLever, Is.EqualTo(.2));
				Release(_keyboard.f11Key); Press(_keyboard.aKey);
				Assert.That(mapper.PortLever, Is.Zero, "the negative part is unchanged");
				Assert.That(_service.Rows.Count(row => row.ActionId == ActionIds.PortLeverStep && row.Scheme == "Keyboard"), Is.EqualTo(2));
			}
		}
		[Test]
		public void CancelAndEscapeRunOneCallbackAndPreserveBindings()
		{
			var callbacks = 0;
			Action<RebindOutcome> completed = value => { Assert.That(value.Result, Is.EqualTo(RebindResult.Cancelled)); callbacks++; };
			_service.BeginRebind(ActionIds.Horn, 0, "Keyboard", completed);
			_service.CancelRebind(); _service.CancelRebind(); Assert.That(callbacks, Is.EqualTo(1));
			_service.BeginRebind(ActionIds.Horn, 0, "Keyboard", completed);
			Press(_keyboard.escapeKey); Assert.That(callbacks, Is.EqualTo(2));
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
		}
		[Test]
		public void ConflictIsRefusedByInteractiveAndDirectPaths()
		{
			RebindOutcome outcome = default;
			_service.BeginRebind(ActionIds.Horn, 0, "Keyboard", value => outcome = value);
			Press(_keyboard.pKey);
			Assert.That(outcome.Result, Is.EqualTo(RebindResult.Conflict));
			Assert.That(outcome.ConflictActionId, Is.EqualTo(ActionIds.Pause));
			Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/v", out var holder), Is.False);
			Assert.That(holder, Is.EqualTo(ActionIds.Mute));
			Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/escape", out holder), Is.False);
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
		}
		[Test]
		public void FreeFlyKeyMayEqualHelmKey_ButGlobalKeyCannot()
		{
			Assert.That(_service.TryApply(ActionIds.FreeFlyVertical, 0, "Keyboard", "<Keyboard>/h", out _), Is.True);
			Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/q", out _), Is.False);
			Assert.That(_service.TryApply(ActionIds.Options, 0, "Keyboard", "<Keyboard>/h", out _), Is.False);
			_service.ResetAll();
			Assert.That(_service.DisplayString(ActionIds.FreeFlyVertical, "Keyboard"), Does.Not.Contain("H"));
		}
		[Test]
		public void TryResetRefusesToReclaimAKeyNowHeldByAnotherAction()
		{
			Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
			Assert.That(_service.TryApply(ActionIds.Pause, 0, "Keyboard", "<Keyboard>/h", out _), Is.True);
			var changes = 0; _service.BindingsChanged += () => changes++;
			Assert.That(_service.TryReset(ActionIds.Horn, out var holder), Is.False);
			Assert.That(holder, Is.EqualTo(ActionIds.Pause));
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("F11"));
			Assert.That(_service.DisplayString(ActionIds.Pause, "Keyboard"), Is.EqualTo("H"));
			Assert.That(changes, Is.Zero);
			_service.ResetAll();
			Assert.That(_service.TryReset(ActionIds.Horn, out holder), Is.True); Assert.That(holder, Is.Null);
		}

		[Test]
		public void MouseAndGamepadButtonsCanBeCapturedInTheirSchemes()
		{
			var mouse = InputSystem.AddDevice<Mouse>();
			var gamepad = InputSystem.AddDevice<Gamepad>();
			RebindOutcome outcome = default;
			_service.BeginRebind(ActionIds.LookHold, 0, "Keyboard", value => outcome = value);
			Press(mouse.leftButton);
			Assert.That(outcome.Result, Is.EqualTo(RebindResult.Applied));
			Assert.That(_asset.FindAction(ActionIds.LookHold).bindings[0].effectivePath, Is.EqualTo("<Mouse>/leftButton"));
			Release(mouse.leftButton);
			_service.BeginRebind(ActionIds.Horn, 0, "Gamepad", value => outcome = value);
			Press(gamepad.leftStickButton);
			Assert.That(outcome.Result, Is.EqualTo(RebindResult.Applied));
			Assert.That(_asset.FindAction(ActionIds.Horn).bindings[1].effectivePath, Is.EqualTo("<Gamepad>/leftStickPress"));
		}

		[Test]
		public void ExtraJoystickButtonCanBeCapturedWithoutChangingAnalogAssignments()
		{
			InputSystem.RegisterLayout("{\"name\":\"BindingTestJoystick\",\"extend\":\"Joystick\",\"controls\":[{\"name\":\"extraButton\",\"layout\":\"Button\"}]}");
			var joystick = InputSystem.AddDevice("BindingTestJoystick");
			RebindOutcome outcome = default;
			_service.BeginRebind(ActionIds.Horn, 0, "Joystick", value => outcome = value);
			Press((ButtonControl)joystick["extraButton"]);
			Assert.That(outcome.Result, Is.EqualTo(RebindResult.Applied));
			Assert.That(_asset.FindAction(ActionIds.Horn).bindings[2].effectivePath, Does.EndWith("/extraButton"));
			Assert.That(_asset.FindAction(ActionIds.BothLevers).bindings[3].overridePath, Is.Null);
		}

		[Test]
		public void RebindingDoesNotChangeCurrentLeverOrders()
		{
			using (var mapper = new HelmInputMapper(_asset))
			{
				mapper.Enable(); Press(_keyboard.qKey); Release(_keyboard.qKey);
				var before = mapper.Current;
				_service.BeginRebind(ActionIds.Horn, 0, "Keyboard", _ => { });
				Press(_keyboard.f11Key); Release(_keyboard.f11Key);
				Assert.That(mapper.Current, Is.EqualTo(before));
			}
		}
	}
}
