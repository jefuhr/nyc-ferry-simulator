using System.IO;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.World.Tests
{
	/// <summary>
	/// The camera rig's text-entry hold: while the player types (a HUD layout name), camera keys must do nothing.
	/// Failure modes guarded: a letter bound to a camera action acting while typing, including a rebound one; a key
	/// pressed during typing firing when typing ends; mouse look or scroll leaking through; a controller that is enabled
	/// after typing began missing the hold; the hold surviving its release.
	/// </summary>
	public class CameraTextEntryHoldTests : InputTestFixture
	{
		private GameObject _host;
		private GameObject _eye;
		private bool _automatic;
		private WheelhouseCameraController _camera;
		private CameraRig _rig;
		private Keyboard _keyboard;

		public override void Setup()
		{
			base.Setup();
			var defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.DestroyImmediate(defaults);
			_automatic = WheelhouseCameraController.AutomaticBowEnabled;
			WheelhouseCameraController.AutomaticBowEnabled = true;
			_host = new GameObject("camera hold test", typeof(Camera));
			_eye = new GameObject("eye", typeof(WheelhouseCameraMount));
			_camera = _host.AddComponent<WheelhouseCameraController>(); _camera.Mount = _eye.GetComponent<WheelhouseCameraMount>();
			_rig = _host.AddComponent<CameraRig>(); _rig.Configure(null, null, _camera); _rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			_keyboard = InputSystem.AddDevice<Keyboard>();
		}

		public override void TearDown()
		{
			Object.DestroyImmediate(_host); Object.DestroyImmediate(_eye);
			InputBindingService.ResetSession(); WheelhouseCameraController.AutomaticBowEnabled = _automatic; base.TearDown();
		}

		[Test]
		public void WhileHeld_CameraKeysDoNothing_AndWorkAgainAfterRelease()
		{
			_rig.SetTextEntryHeld(true);
			Assert.That(_rig.TextEntryHeld, Is.True);
			Press(_keyboard.bKey); _camera.SendMessage("LateUpdate");
			Assert.That(_camera.ShowingBow, Is.False, "the bow key types a letter, it does not move the camera");
			Release(_keyboard.bKey);
			Press(_keyboard.f2Key); _rig.SendMessage("Update"); Release(_keyboard.f2Key);
			Assert.That(_rig.CurrentMode, Is.EqualTo(CameraRig.Mode.Wheelhouse), "view selection is held too");

			_rig.SetTextEntryHeld(false);
			Assert.That(_rig.TextEntryHeld, Is.False);
			Press(_keyboard.bKey); _camera.SendMessage("LateUpdate"); Release(_keyboard.bKey);
			Assert.That(_camera.ShowingBow, Is.True, "a fresh press after typing works");
		}

		[Test]
		public void AKeyStillDownWhenTypingEnds_IsNotACommand()
		{
			_rig.SetTextEntryHeld(true);
			Press(_keyboard.bKey);
			_rig.SetTextEntryHeld(false);
			_camera.SendMessage("LateUpdate");
			Assert.That(_camera.ShowingBow, Is.False, "the press belonged to the text field");
			Release(_keyboard.bKey); _camera.SendMessage("LateUpdate");
			Assert.That(_camera.ShowingBow, Is.False);
			Press(_keyboard.bKey); _camera.SendMessage("LateUpdate"); Release(_keyboard.bKey);
			Assert.That(_camera.ShowingBow, Is.True);
		}

		[Test]
		public void AReboundKey_IsHeldToo()
		{
			Assert.That(InputBindingService.Instance.TryApply(ActionIds.BowView, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
			_rig.SetTextEntryHeld(true);
			Press(_keyboard.f11Key); _camera.SendMessage("LateUpdate"); Release(_keyboard.f11Key);
			Assert.That(_camera.ShowingBow, Is.False);
			_rig.SetTextEntryHeld(false);
			Press(_keyboard.f11Key); _camera.SendMessage("LateUpdate"); Release(_keyboard.f11Key);
			Assert.That(_camera.ShowingBow, Is.True);
		}

		[Test]
		public void MouseLookAndScroll_AreHeld()
		{
			var mouse = InputSystem.AddDevice<Mouse>();
			Assert.That(InputBindingService.Instance.TryApply(ActionIds.LookHold, 0, "Keyboard", "<Mouse>/leftButton", out _), Is.True);
			_rig.SetTextEntryHeld(true);
			Press(mouse.leftButton); Set(mouse.delta, new Vector2(20, -10)); _camera.SendMessage("LateUpdate");
			Assert.That(_camera.HeadLook, Is.EqualTo(Vector2.zero));
			Release(mouse.leftButton);
			var chase = _host.AddComponent<ChaseCameraController>(); chase.Distance = 100;
			// The rig enables only the controller of the mode in force, so make the chase camera the live one.
			_rig.Configure(null, chase, _camera); _rig.CurrentMode = CameraRig.Mode.Chase;
			Assert.That(chase.enabled, Is.True);
			Set(mouse.scroll, new Vector2(0, 120)); chase.SendMessage("LateUpdate");
			Assert.That(chase.Distance, Is.EqualTo(100).Within(.001), "a controller added while typing is held as well");
			_rig.SetTextEntryHeld(false);
			Set(mouse.scroll, new Vector2(0, 0)); Set(mouse.scroll, new Vector2(0, 120)); chase.SendMessage("LateUpdate");
			Assert.That(chase.Distance, Is.EqualTo(85).Within(.001));
		}

		[Test]
		public void AControllerEnabledWhileTyping_StartsHeld()
		{
			_rig.SetTextEntryHeld(true);
			_camera.enabled = false; _camera.enabled = true;
			Press(_keyboard.bKey); _camera.SendMessage("LateUpdate"); Release(_keyboard.bKey);
			Assert.That(_camera.ShowingBow, Is.False);
			_rig.enabled = false; _rig.enabled = true;
			Press(_keyboard.f2Key); _rig.SendMessage("Update"); Release(_keyboard.f2Key);
			Assert.That(_rig.CurrentMode, Is.EqualTo(CameraRig.Mode.Wheelhouse));
			_rig.SetTextEntryHeld(false);
			_rig.SetTextEntryHeld(false);
			Press(_keyboard.bKey); _camera.SendMessage("LateUpdate"); Release(_keyboard.bKey);
			Assert.That(_camera.ShowingBow, Is.True);
		}
	}
}
