using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Controls;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class LeanInInputTests : InputTestFixture
	{
		public override void Setup()
		{
			base.Setup();
			var defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.DestroyImmediate(defaults);
		}

		public override void TearDown()
		{
			InputBindingService.ResetSession();
			base.TearDown();
		}

		[Test]
		public void ActionsCycleRecentreAndBow_RunStartedClearsFocus_AutomaticBowCannotTakeOver()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>(); var mouse = InputSystem.AddDevice<Mouse>();
			var owner = new GameObject("Input vessel"); var camera = new GameObject("Input camera");
			var oldAutomatic = WheelhouseCameraController.AutomaticBowEnabled;
			try
			{
				var runner = owner.AddComponent<VesselSimulationRunner>(); runner.RecordRuns = false;
				var parameters = VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../vessels/k-class.json")));
				var scenario = new ScenarioSettings { vesselFile = "vessels/k-class.json", environment = new ScenarioEnvironment { calm = true } };
				runner.Initialize(scenario, new ConstantControls(ControlInputs.Neutral), parameters); runner.Pause();
				var mount = new GameObject("Mount").AddComponent<WheelhouseCameraMount>(); mount.transform.SetParent(owner.transform, false);
				WheelhouseControlsView.PlaceEye(owner.transform, mount, parameters, runner);
				var c = camera.AddComponent<WheelhouseCameraController>(); c.Mount = mount; c.Snap();
				var events = new List<string>(); c.FocusChanged += events.Add;
				Press(keyboard.f5Key); c.SendMessage("LateUpdate"); Release(keyboard.f5Key);

				Assert.That(c.FocusedScreen, Is.EqualTo("RadarScreen"));
				// Force an eligible bow-facing wall just ahead using a small query fixture.
				mount.ApproachFaces = new NearbyFace(runner.State, GreyboxDimensions.From(parameters).LengthOverall * .45);
				WheelhouseCameraController.AutomaticBowEnabled = true;
				for (var i = 0; i < 5; i++) c.SendMessage("LateUpdate");
				Assert.That(c.FocusedScreen, Is.EqualTo("RadarScreen")); Assert.That(c.ShowingBow, Is.False);
				Press(mouse.middleButton); c.SendMessage("LateUpdate"); Release(mouse.middleButton);

				Assert.That(c.FocusedScreen, Is.Null);
				Press(keyboard.f5Key); c.SendMessage("LateUpdate"); Release(keyboard.f5Key);
				Press(keyboard.bKey); c.SendMessage("LateUpdate"); Release(keyboard.bKey);

				Assert.That(c.FocusedScreen, Is.Null); Assert.That(c.ShowingBow, Is.True);
				c.CycleFocus(); Assert.That(c.FocusedScreen, Is.EqualTo("RadarScreen"));
				runner.Initialize(scenario, new ConstantControls(ControlInputs.Neutral), parameters); runner.Pause();
				Assert.That(c.FocusedScreen, Is.Null); Assert.That(events[events.Count - 1], Is.Null);
				Assert.That(events, Is.EqualTo(new[] { "RadarScreen", null, "RadarScreen", null, "RadarScreen", null }));
			}
			finally
			{
				WheelhouseCameraController.AutomaticBowEnabled = oldAutomatic;
				Object.DestroyImmediate(camera); Object.DestroyImmediate(owner);
			}
		}

		private sealed class NearbyFace : IWorldGeometry
		{
			private readonly ContactSegment _face;
			public NearbyFace(VesselState state, double bow)
			{
				var forward = new HorizontalVector(System.Math.Cos(state.Heading), System.Math.Sin(state.Heading));
				var right = new HorizontalVector(-forward.East, forward.North);
				var centre = state.Position + forward * (bow + 5);
				_face = new ContactSegment(centre + right * 30, centre - right * 30, new ContactSurface(1e6, 1e5, 0, 1), 1);
			}
			public int QuerySegments(double north, double east, double radius, ContactSegment[] destination) { destination[0] = _face; return 1; }
		}
	}
}
