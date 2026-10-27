using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Controls;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.World.Tests
{
	public class BowKeyTests : InputTestFixture
	{
		private GameObject _host;
		private GameObject _eye;
		private bool _automatic;

		public override void Setup()
		{
			base.Setup();
			_automatic = WheelhouseCameraController.AutomaticBowEnabled;
			WheelhouseCameraController.AutomaticBowEnabled = true;
			_host = new GameObject("bow key test", typeof(Camera));
			_eye = new GameObject("eye", typeof(WheelhouseCameraMount));
		}

		public override void TearDown()
		{
			Object.DestroyImmediate(_host); Object.DestroyImmediate(_eye);
			WheelhouseCameraController.AutomaticBowEnabled = _automatic;
			base.TearDown();
		}

		[Test]
		public void B_TogglesPreset_ShiftBOnlyTogglesSessionAutomatic()
		{
			var camera = _host.AddComponent<WheelhouseCameraController>();
			camera.Mount = _eye.GetComponent<WheelhouseCameraMount>();
			var keyboard = InputSystem.AddDevice<Keyboard>();
			Press(keyboard.bKey); camera.SendMessage("LateUpdate");
			Assert.That(camera.ShowingBow, Is.True);
			Release(keyboard.bKey); Press(keyboard.bKey); camera.SendMessage("LateUpdate");
			Assert.That(camera.ShowingBow, Is.False);
			Release(keyboard.bKey); Press(keyboard.leftShiftKey); Press(keyboard.bKey); camera.SendMessage("LateUpdate");
			Assert.That(WheelhouseCameraController.AutomaticBowEnabled, Is.False);
			Assert.That(camera.ShowingBow, Is.False);
		}

		[Test]
		public void AutomaticBow_BKeyAndHandLookRespectHeldHelm_AndRunReset()
		{
			// Guard a hidden manual latch, B/Shift+B stealing a held helm, and suppression leaking across runs.
			var vessel = new GameObject("bow approach test");
			vessel.SetActive(false);
			var actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			try
			{
				var runner = vessel.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				var helm = vessel.AddComponent<HelmInputController>();
				helm.Actions = actions;
				_eye.transform.SetParent(vessel.transform);
				vessel.SetActive(true);
				var camera = _host.AddComponent<WheelhouseCameraController>();
				var mount = _eye.GetComponent<WheelhouseCameraMount>();
				camera.Mount = mount;
				var face = new ContactSegment(new HorizontalVector(25, 10), new HorizontalVector(25, -10),
					new ContactSurface(1000, 100, .3, .1), 1);
				var geometry = new SegmentListGeometry(new[] { face });
				mount.ApproachFaces = geometry;
				runner.Initialize(new ScenarioSettings(), helm);
				camera.SendMessage("LateUpdate");
				Assert.That(camera.ShowingBow, Is.True, "eligible slow approach selects automatic bow");
				var keyboard = InputSystem.AddDevice<Keyboard>();
				var mouse = InputSystem.AddDevice<Mouse>();
				Press(keyboard.rightArrowKey);
				Assert.That(helm.Mapper.Helm, Is.EqualTo(.25));
				Press(keyboard.bKey); camera.SendMessage("LateUpdate");
				Assert.That(camera.ShowingBow, Is.False, "one B press must leave automatic bow");
				Release(keyboard.bKey);
				helm.Mapper.AdvanceHelmRepeat(.41);
				Assert.That(helm.Mapper.Helm, Is.EqualTo(.5), "B does not cancel held helm repeat");
				Press(keyboard.bKey); camera.SendMessage("LateUpdate");
				Assert.That(camera.ShowingBow, Is.True);
				Release(keyboard.bKey);
				Press(mouse.rightButton); camera.SendMessage("LateUpdate"); Release(mouse.rightButton);
				Assert.That(camera.ShowingBow, Is.False, "hand look still suppresses the approach");
				Press(keyboard.leftShiftKey); Press(keyboard.bKey); camera.SendMessage("LateUpdate");
				Assert.That(WheelhouseCameraController.AutomaticBowEnabled, Is.False);
				Assert.That(camera.ShowingBow, Is.False);
				Release(keyboard.bKey); Press(keyboard.bKey); camera.SendMessage("LateUpdate");
				Assert.That(WheelhouseCameraController.AutomaticBowEnabled, Is.True);
				Assert.That(camera.ShowingBow, Is.False, "preference toggles do not clear hand suppression");
				Release(keyboard.bKey); Release(keyboard.leftShiftKey);
				mount.ApproachFaces = EmptyWorldGeometry.Instance;
				runner.ResetRun();
				helm.Mapper.AdvanceHelmRepeat(1);
				camera.SendMessage("LateUpdate");
				Assert.That(helm.Mapper.Helm, Is.Zero, "reset cancels repeat even while the step key remains held");
				Assert.That(camera.ShowingBow, Is.False);
				Assert.That(WheelhouseCameraController.AutomaticBowEnabled, Is.True, "run reset preserves the session preference");
				Release(keyboard.rightArrowKey);
				mount.ApproachFaces = geometry;
				camera.SendMessage("LateUpdate");
				Assert.That(camera.ShowingBow, Is.True, "the new run has no inherited suppression");
			}
			finally
			{
				_eye.transform.SetParent(null);
				Object.DestroyImmediate(vessel);
				Object.DestroyImmediate(actions);
			}
		}

		[Test]
		public void CameraRig_EnablesBowControllerOnlyForWheelhouse()
		{
			var wheelhouse = _host.AddComponent<WheelhouseCameraController>();
			var rig = _host.AddComponent<CameraRig>();
			rig.Configure(null, null, wheelhouse);
			foreach (var mode in new[] { CameraRig.Mode.FreeFly, CameraRig.Mode.Chase, CameraRig.Mode.Wheelhouse })
			{
				rig.CurrentMode = mode;
				Assert.That(wheelhouse.enabled, Is.EqualTo(mode == CameraRig.Mode.Wheelhouse));
			}
		}
	}
}
