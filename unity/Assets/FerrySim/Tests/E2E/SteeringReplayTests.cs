using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.E2E
{
	public class SteeringReplayTests
	{
		// Old controls can load but must not silently verify against the changed steering physics.
		[Test]
		public void Former35DegreeRecording_DivergesAgainstCrew20DegreeParameters()
		{
			var folder = Path.Combine(TripData.Root, "artifacts/crew-fixes/replay");
			Directory.CreateDirectory(folder);
			var path = Path.Combine(TripData.Root, "vessels/t-class.json");
			var former = VesselParametersJson.Load(path);
			former.Controls.HardOverAngle.Value = .6108652;
			former.Rudders.Area.Value = .4;
			former.Rudders.SlipstreamSpanFraction.Value = .9;
			former.Hull.DerivativeNr.Value = -.09;
			former.Hull.DerivativeNv.Value = -.187;
			var current = VesselParametersJson.Load(path);
			var scenario = new ScenarioSettings { name = "former-35-degree-steering", vesselFile = "vessels/t-class.json", startSpeedKnots = 4 };
			var environment = scenario.environment.Create();
			var initial = scenario.InitialState(environment);
			var factory = new VesselDynamicsFactory();
			var simulation = factory.Create(former, environment, initial);
			var header = RunRecorder.BuildHeader(scenario, former, simulation, factory.GetType().FullName, initial, environment, 100);
			using (var recorder = RunRecorder.Create(folder, "former-35-degree", 100, header))
			{
				var driver = new FixedStepDriver(simulation, new ConstantControls(new ControlInputs(.4, .4, 1)));
				driver.AddObserver(recorder);
				for (var i = 0; i < 1000; i++) Assert.That(driver.StepOnce(), Is.True);
				recorder.Finish(driver.StepCount, simulation.State, simulation.Telemetry);
				var run = RecordedRun.Load(recorder.TelemetryPath);
				var original = ReplayPlayer.Verify(run, former, factory);
				var changed = ReplayPlayer.Verify(run, current, factory);
				File.WriteAllText(Path.Combine(folder, "verification.txt"), "Former parameters: " + original + "\nCrew parameters: " + changed + "\n");
				Assert.That(original.Matches, Is.True, original.ToString());
				Assert.That(changed.Matches, Is.False, changed.ToString());
				Assert.That(changed.DigestMatches, Is.False);
			}
		}
	}
}
