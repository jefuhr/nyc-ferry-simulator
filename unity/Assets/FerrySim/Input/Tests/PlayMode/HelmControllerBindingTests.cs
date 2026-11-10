using System.IO;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests.PlayMode
{
	public class HelmControllerBindingTests : InputTestFixture
	{
		[Test]
		public void ControllerIsNfuSource_AndHelpRefreshesAfterARebind()
		{
			var asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			InputBindingService.SetSession(new InputBindingService(asset));
			var host = new GameObject("controller bindings"); host.SetActive(false);
			try
			{
				var controller = host.AddComponent<HelmInputController>(); controller.Actions = asset; host.SetActive(true);
				Assert.That(controller, Is.InstanceOf<INfuControlSource>());
				var keyboard = InputSystem.AddDevice<Keyboard>(); Press(keyboard.f7Key);
				Assert.That(controller.ControlsForStep(0).Jog, Is.EqualTo(1));
				Release(keyboard.f7Key); Assert.That(controller.ControlsForStep(1).Jog, Is.Zero);
				Assert.That(InputBindingService.Instance.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
				Assert.That(controller.ReadoutLine, Does.Contain("F11 horn"));
				InputBindingService.Instance.Reset(ActionIds.Horn);
				Assert.That(controller.ReadoutLine, Does.Contain("H horn").And.Not.Contain("F11 horn"));
			}
			finally { Object.DestroyImmediate(host); Object.DestroyImmediate(asset); InputBindingService.ResetSession(); }
		}
	}
}
