using FerrySim.Controls;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Prompts.Tests.EditMode
{
	/// <summary>
	/// The <see cref="KeybindPopups"/> driver with the real binding service (no saved file) and virtual devices.
	/// Failure modes guarded: the hold key read from a copy the service never rebinds, the hold key working in
	/// another mode, popups that follow the wrong device's scheme, popups in the chase or free-fly view, and a
	/// layer that is not added to the document root it is given.
	/// </summary>
	public class KeybindPopupsDriverTests : InputTestFixture
	{
		private StandInInterior _interior;
		private InputBindingService _service;
		private GameObject _host;
		private KeybindPopups _popups;
		private Keyboard _keyboard;

		public override void Setup()
		{
			base.Setup();
			_keyboard = InputSystem.AddDevice<Keyboard>();
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			_service = new InputBindingService(defaults);
			Object.DestroyImmediate(defaults);
			InputBindingService.SetSession(new InputBindingService(InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text)));
			_interior = new StandInInterior();
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			_interior.Add("AlarmSilence", new Vector3(0.3f, 0f, 1f), "Silence alarms", ActionIds.AlarmSilence);
			_host = new GameObject("keybind popups (test)");
			_popups = _host.AddComponent<KeybindPopups>();
		}

		public override void TearDown()
		{
			_popups.Unbind();
			Object.DestroyImmediate(_host);
			_interior.Dispose();
			_service.Dispose();
			InputBindingService.ResetSession();
			base.TearDown();
		}

		private void Bind(Camera camera = null) => _popups.Bind(camera != null ? camera : _interior.Camera, _interior.Root.transform, _interior.Map(), _service);

		private string HornText()
		{
			Assert.That(_popups.Layer.TryGetState("HornButton", out var state), Is.True);
			return state.KeyText;
		}

		[Test]
		public void Hold_ShowsOnlyWhileTheHintsKeyIsHeld_ReadFromItsOwnRegisteredCopy()
		{
			Bind();
			_popups.Mode = KeybindHintMode.Hold;
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "not held");

			Press(_keyboard.backquoteKey);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.True, "backquote held");
			Assert.That(_popups.Layer.VisibleCount, Is.EqualTo(2));
			Release(_keyboard.backquoteKey);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "released");

			// A rebind through the service reaches the driver's registered copy.
			Assert.That(_service.TryApply(ActionIds.KeybindHints, 0, "Keyboard", "<Keyboard>/f12", out var holder), Is.True, holder);
			Press(_keyboard.backquoteKey);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "backquote no longer shows them");
			Release(_keyboard.backquoteKey);
			Press(_keyboard.f12Key);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.True, "the new key does");
			Release(_keyboard.f12Key);

			_popups.Mode = KeybindHintMode.Off;
			Press(_keyboard.f12Key);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "off ignores the key");
			_popups.Mode = KeybindHintMode.Always;
			Release(_keyboard.f12Key);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.True, "always needs no key");
			_popups.Suppressed = true;
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "suppressed");
		}

		[Test]
		public void TheSchemeFollowsTheDeviceLastUsed()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			var mouse = InputSystem.AddDevice<Mouse>();
			Bind();
			_popups.Mode = KeybindHintMode.Always;
			Assert.That(_popups.ActiveScheme, Is.EqualTo(KeybindSchemes.Keyboard), "before any input");
			Assert.That(HornText(), Is.EqualTo(_service.DisplayString(ActionIds.Horn, "Keyboard")));
			var gamepadHorn = _service.DisplayString(ActionIds.Horn, "Gamepad");
			Assert.That(gamepadHorn, Is.Not.Empty.And.Not.EqualTo(HornText()));

			Press(gamepad.buttonEast);
			Release(gamepad.buttonEast);
			Assert.That(_popups.ActiveScheme, Is.EqualTo(KeybindSchemes.Gamepad));
			Assert.That(HornText(), Is.EqualTo(gamepadHorn), "rebuilt for the gamepad at once");
			_popups.Tick();
			Assert.That(_popups.Layer.TryGetState("AlarmSilence", out var silence), Is.True);
			Assert.That(silence.Visible, Is.False, "silence has no gamepad binding: nothing shown");

			Press(mouse.leftButton);
			Release(mouse.leftButton);
			Assert.That(_popups.ActiveScheme, Is.EqualTo(KeybindSchemes.Keyboard), "mouse buttons share the keyboard scheme");
			Press(gamepad.buttonSouth);
			Release(gamepad.buttonSouth);
			Press(_keyboard.spaceKey);
			Release(_keyboard.spaceKey);
			Assert.That(_popups.ActiveScheme, Is.EqualTo(KeybindSchemes.Keyboard));
			Assert.That(HornText(), Is.EqualTo(_service.DisplayString(ActionIds.Horn, "Keyboard")));
		}

		[Test]
		public void ARebindThroughTheService_ChangesTheHornLabelAtOnce()
		{
			Bind();
			Assert.That(HornText(), Is.EqualTo("H"));
			Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out var holder), Is.True, holder);
			Assert.That(HornText(), Is.EqualTo("F11"));
		}

		[Test]
		public void OnlyTheWheelhouseViewShowsPopups()
		{
			var rig = _interior.Camera.gameObject.AddComponent<CameraRig>();
			Bind();
			_popups.Mode = KeybindHintMode.Always;
			rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.True, "wheelhouse");
			rig.CurrentMode = CameraRig.Mode.Chase;
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "chase");
			rig.CurrentMode = CameraRig.Mode.FreeFly;
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "free-fly");
		}

		[Test]
		public void AttachTo_AddsTheLayerToTheGivenRoot_AndBindWithoutAMapShowsNothing()
		{
			var root = new VisualElement();
			_popups.AttachTo(root);
			Assert.That(_popups.Layer.parent, Is.SameAs(root));
			var other = new VisualElement();
			_popups.AttachTo(other);
			Assert.That(_popups.Layer.parent, Is.SameAs(other), "moved, not duplicated");
			Assert.That(root.childCount, Is.Zero);

			_popups.Mode = KeybindHintMode.Always;
			_popups.Bind(_interior.Camera, _interior.Root.transform, null, _service);
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.False, "no control map: nothing");
			Bind();
			_popups.Tick();
			Assert.That(_popups.Layer.IsShowing, Is.True);
		}
	}
}
