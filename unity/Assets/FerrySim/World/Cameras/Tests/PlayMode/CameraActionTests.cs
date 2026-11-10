using System.IO;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.World.Tests
{
	public class CameraActionTests : InputTestFixture
	{
		private GameObject _host;
		private GameObject _eye;
		private bool _automatic;
		public override void Setup()
		{
			base.Setup();
			var defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.DestroyImmediate(defaults);
			_automatic = WheelhouseCameraController.AutomaticBowEnabled;
			WheelhouseCameraController.AutomaticBowEnabled = true;
			_host = new GameObject("camera action test", typeof(Camera));
			_eye = new GameObject("eye", typeof(WheelhouseCameraMount));
		}
		public override void TearDown()
		{
			Object.DestroyImmediate(_host); Object.DestroyImmediate(_eye);
			InputBindingService.ResetSession(); WheelhouseCameraController.AutomaticBowEnabled = _automatic; base.TearDown();
		}
		[Test]
		public void BowAndViewSelectionFollowOverrides()
		{
			var camera = _host.AddComponent<WheelhouseCameraController>(); camera.Mount = _eye.GetComponent<WheelhouseCameraMount>();
			var keyboard = InputSystem.AddDevice<Keyboard>();
			Assert.That(InputBindingService.Instance.TryApply(ActionIds.BowView, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
			Press(keyboard.bKey); camera.SendMessage("LateUpdate"); Assert.That(camera.ShowingBow, Is.False); Release(keyboard.bKey);
			Press(keyboard.f11Key); camera.SendMessage("LateUpdate"); Assert.That(camera.ShowingBow, Is.True); Release(keyboard.f11Key);
			var rig = _host.AddComponent<CameraRig>(); rig.Configure(null, null, camera); rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			Assert.That(InputBindingService.Instance.TryApply(ActionIds.ViewChase, 0, "Keyboard", "<Keyboard>/f12", out _), Is.True);
			Press(keyboard.f12Key); rig.SendMessage("Update"); Assert.That(rig.CurrentMode, Is.EqualTo(CameraRig.Mode.Chase));
		}
		[Test]
		public void MouseLookKeepsSensitivityAndLimits_AndScrollKeepsChaseZoom()
		{
			var camera = _host.AddComponent<WheelhouseCameraController>(); camera.Mount = _eye.GetComponent<WheelhouseCameraMount>();
			var mouse = InputSystem.AddDevice<Mouse>();
			Assert.That(InputBindingService.Instance.TryApply(ActionIds.LookHold, 0, "Keyboard", "<Mouse>/leftButton", out _), Is.True);
			Press(mouse.leftButton); Set(mouse.delta, new Vector2(20, -10)); camera.SendMessage("LateUpdate");
			Assert.That(camera.HeadLook.x, Is.EqualTo(3).Within(.001)); Assert.That(camera.HeadLook.y, Is.EqualTo(1.5).Within(.001));
			Set(mouse.delta, new Vector2(100000, 100000)); camera.SendMessage("LateUpdate");
			Assert.That(camera.HeadLook.x, Is.EqualTo(camera.Mount.YawLimits.y)); Assert.That(camera.HeadLook.y, Is.EqualTo(camera.Mount.PitchLimits.x));
			Release(mouse.leftButton); camera.enabled = false;
			var chase = _host.AddComponent<ChaseCameraController>(); chase.Distance = 100;
			Set(mouse.scroll, new Vector2(0, 120)); chase.SendMessage("LateUpdate"); Assert.That(chase.Distance, Is.EqualTo(85).Within(.001));
		}
	}
}
