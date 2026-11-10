using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Core.Tests.Instruments;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	// Failure modes: helm still fights jog, proportional jog changes the fixed rate, hold latches forever,
	// reset retains a latch, only one rudder moves, or ordinary rate-limited travel trips steering failure.
	public class NfuOverrideTests
	{
		[TestCase(null, false)]
		[TestCase("return-to-tiller", false)]
		[TestCase("hold-until-tiller-moves", true)]
		public void OverrideAndRelease(string mode, bool hold)
		{
			var p = ManeuverRunner.Load("t-class.json");
			if (mode != null) p.Controls.NfuRelease = new ParameterText { Value = mode, Source = "test", Confidence = "estimated" };
			var gear = new SteeringGear(p);
			gear.Step(-1, .1, 1);
			Assert.That(gear.Angle, Is.EqualTo(gear.SteeringRate));
			Assert.That(gear.NfuActive, Is.True);
			gear.Step(-1, 0, .1);
			Assert.That(gear.Angle, Is.EqualTo(gear.SteeringRate * (hold ? 1 : .9)).Within(1e-12));
			gear.Step(-.9, 0, .1);
			Assert.That(gear.Angle, Is.LessThan(gear.SteeringRate));
			gear.Reset(); gear.Step(-1, 0, 1);
			Assert.That(gear.Angle, Is.EqualTo(-gear.SteeringRate));
		}

		[Test]
		public void HeldJogClampsAtBothHardOvers_AndBothRuddersMoveTogether()
		{
			var sim = ManeuverRunner.Create(ManeuverRunner.Load("t-class.json"));
			ManeuverRunner.Run(sim, new ControlInputs(0, 0, -1, 1), 20);
			Assert.That(sim.Telemetry.PortRudderAngle, Is.EqualTo(sim.HardOverAngle));
			Assert.That(sim.Telemetry.StarboardRudderAngle, Is.EqualTo(sim.HardOverAngle));
			ManeuverRunner.Run(sim, new ControlInputs(0, 0, 1, -1), 20);
			Assert.That(sim.Telemetry.PortRudderAngle, Is.EqualTo(-sim.HardOverAngle));
			Assert.That(sim.NfuActive, Is.True);
		}

		[Test]
		public void ContinuouslyChangingTiller_NeverRaisesSteeringAlarm()
		{
			var sim = ManeuverRunner.Create(ManeuverRunner.Load("t-class.json"));
			var m = new InstrumentModel(InstrumentFixtures.Load());
			for (var i = 0; i < 5000; i++)
			{
				var inputs = new ControlInputs(0, 0, i / 200 % 2 == 0 ? 1 : -1);
				sim.Step(inputs); m.Step(sim.State, sim.Telemetry, inputs, sim);
				Assert.That(m.Phase(3), Is.EqualTo(AlarmPhase.Normal), $"step {i}");
			}
		}

		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void HealthyHardOverToHardOverSwing_NeverRaisesSteeringAlarm(string file)
		{
			var sim = ManeuverRunner.Create(ManeuverRunner.Load(file));
			var m = new InstrumentModel(InstrumentFixtures.Load(file.Replace(".json", "")));
			for (var i = 0; i < 5000; i++)
			{
				var controls = new ControlInputs(0, 0, i / 1000 % 2 == 0 ? 1 : -1);
				sim.Step(controls); m.Step(sim.State, sim.Telemetry, controls, sim);
				Assert.That(m.Phase(3), Is.EqualTo(AlarmPhase.Normal), $"step {i}");
			}
		}
	}
}
