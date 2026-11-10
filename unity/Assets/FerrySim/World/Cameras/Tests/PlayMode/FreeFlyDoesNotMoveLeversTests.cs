using System.IO;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.World.Tests
{
	public class FreeFlyDoesNotMoveLeversTests : InputTestFixture
	{
		public override void Setup()
		{
			base.Setup();
			var defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.DestroyImmediate(defaults);
		}
		public override void TearDown() { InputBindingService.ResetSession(); base.TearDown(); }
		[Test]
		public void ReturningWithAFlyKeyHeldRequiresANewPressToStepTheLevers()
		{
			var asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var vessel = new GameObject("held fly input"); vessel.SetActive(false);
			var camera = new GameObject("camera", typeof(Camera));
			try
			{
				var helm = vessel.AddComponent<HelmInputController>(); helm.Actions = asset; vessel.SetActive(true);
				var fly = camera.AddComponent<FreeFlyCameraController>();
				var rig = camera.AddComponent<CameraRig>(); rig.Configure(fly, null, null);
				var keyboard = InputSystem.AddDevice<Keyboard>(); Press(keyboard.wKey);
				rig.CurrentMode = CameraRig.Mode.Wheelhouse; InputSystem.Update();
				Assert.That(helm.Mapper.Current.PortLever, Is.Zero, "a key held while flying is not a new lever command");
				Release(keyboard.wKey); Press(keyboard.wKey);
				Assert.That(helm.Mapper.Current.PortLever, Is.EqualTo(.2));
				helm.Mapper.Disable(); rig.CurrentMode = CameraRig.Mode.FreeFly; rig.CurrentMode = CameraRig.Mode.Wheelhouse;
				Assert.That(helm.Mapper.Enabled, Is.False, "a picker-disabled helm must remain disabled");
			}
			finally { Object.DestroyImmediate(camera); Object.DestroyImmediate(vessel); Object.DestroyImmediate(asset); }
		}

		[Test]
		public void FlyingDoesNotStepAnyLever_ThenWheelhouseRestoresHelm()
		{
			var asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var vessel = new GameObject("input context test"); vessel.SetActive(false);
			var camera = new GameObject("camera", typeof(Camera));
			try
			{
				var helm = vessel.AddComponent<HelmInputController>(); helm.Actions = asset; vessel.SetActive(true);
				var fly = camera.AddComponent<FreeFlyCameraController>();
				var rig = camera.AddComponent<CameraRig>(); rig.Configure(fly, null, null);
				rig.CurrentMode = CameraRig.Mode.FreeFly;
				var keyboard = InputSystem.AddDevice<Keyboard>();
				Press(keyboard.wKey); Press(keyboard.qKey); Press(keyboard.eKey);
				Assert.That(helm.Mapper.Enabled, Is.False);
				Assert.That(helm.Mapper.Current.PortLever, Is.Zero);
				Assert.That(helm.Mapper.Current.StarboardLever, Is.Zero);
				Release(keyboard.wKey); Release(keyboard.qKey); Release(keyboard.eKey);
				rig.CurrentMode = CameraRig.Mode.Wheelhouse;
				Assert.That(helm.Mapper.Enabled, Is.True);
				Press(keyboard.wKey);
				Assert.That(helm.Mapper.Current.PortLever, Is.EqualTo(.2));
				Assert.That(helm.Mapper.Current.StarboardLever, Is.EqualTo(.2));
			}
			finally { Object.DestroyImmediate(camera); Object.DestroyImmediate(vessel); Object.DestroyImmediate(asset); }
		}
	}
}
