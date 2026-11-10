using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests.PlayMode
{
	public class RebindingDigestTests : InputTestFixture
	{
		[Test]
		public void RebindHalfwayThroughARun_IsNotASimulationEvent()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var baseline = Run(keyboard, false);
			var rebound = Run(keyboard, true);
			Assert.That(rebound, Is.EqualTo(baseline));
			var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/input-rebinding"));
			Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, "binding-state-digest.txt"), "baseline=" + baseline + "\nrebound=" + rebound + "\nsteps=4000\n");
		}
		private string Run(Keyboard keyboard, bool rebind)
		{
			var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var vessel = VesselParametersJson.Load(Path.Combine(root, "vessels/t-class.json"));
			var simulation = new VesselDynamicsFactory().Create(vessel, UniformEnvironment.Calm, VesselState.Planar(0, 0, 0, 0));
			var digest = new StateDigest();
			using (var service = new InputBindingService(asset))
			using (var mapper = new HelmInputMapper(asset))
			{
				service.Register(asset); mapper.Enable();
				Press(keyboard.wKey); Release(keyboard.wKey);
				var smoother = new ControlInputSmoother();
				for (var step = 0; step < 4000; step++)
				{
					if (step == 2000)
					{
						if (rebind)
						{
							var before = mapper.Current;
							Assert.That(service.TryApply(ActionIds.PortLeverStep, 1, "Keyboard", "<Keyboard>/f11", out _), Is.True);
							Assert.That(mapper.Current, Is.EqualTo(before));
						}
						var key = rebind ? keyboard.f11Key : keyboard.qKey;
						Press(key); Release(key);
					}
					simulation.Step(smoother.Step(mapper.Current)); digest.Add(simulation.State);
				}
			}
			Object.DestroyImmediate(asset);
			return digest.Text;
		}
	}
}
