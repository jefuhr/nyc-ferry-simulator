using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	public class InstrumentParameterTests
	{
		[TestCase("t-class")]
		[TestCase("k-class")]
		public void RatingsMatchTheExistingPublishedParticulars_WithoutChangingTheVessel(string id)
		{
			var p = InstrumentFixtures.Load(id); var vessel = ManeuverRunner.Load(id + ".json");
			Assert.That(p.RatedPower.Value, Is.EqualTo(vessel.Machinery.RatedPowerPerEngine.Value));
			Assert.That(p.RatedSpeed.Value, Is.EqualTo(vessel.Machinery.RatedEngineSpeed.Value));
			Assert.That(p.OilPressure.Idle.Confidence, Is.EqualTo("estimated"));
			Assert.That(p.RatedPower.Confidence, Is.EqualTo("published"));
			Assert.That(vessel.Controls.NfuRelease, Is.Null);
			Assert.That(VesselParametersJson.Serialize(vessel), Does.Not.Contain("nfuRelease"));
		}

		[Test]
		public void InvalidInstrumentData_IsRejectedAtTheBoundary()
		{
			var p = InstrumentFixtures.Load(); p.OilPressure.TimeConstant.Value = 0;
			Assert.Throws<ArgumentException>(() => new InstrumentModel(p));
			p = InstrumentFixtures.Load(); p.BusVoltage.Unit = "psi";
			Assert.Throws<ArgumentException>(() => p.Validate());
			p = InstrumentFixtures.Load(); p.RatedSpeed.Source = "unknown";
			Assert.Throws<ArgumentException>(() => p.Validate());
			p = InstrumentFixtures.Load(); p.Alarms[0].Condition = "engine-fault";
			Assert.Throws<ArgumentException>(() => p.Validate());
			p = InstrumentFixtures.Load(); p.Alarms[0].ReRaiseSeconds.Value = -1;
			Assert.Throws<ArgumentException>(() => p.Validate());
			var json = File.ReadAllText(Path.Combine(RepoPaths.Root, "vessels", "instruments", "t-class.json"));
			Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => InstrumentParametersJson.Parse(json.Replace("\"version\"", "\"versoin\"")));
		}

		[TestCase("return-to-tiller")]
		[TestCase("hold-until-tiller-moves")]
		public void ReleasePolicyRoundTripsAndInvalidChoicesAreRejected(string policy)
		{
			var p = ManeuverRunner.Load("t-class.json");
			p.Controls.NfuRelease = new ParameterText { Value = policy, Source = "simulator test option", Confidence = "estimated" };
			var copy = VesselParametersJson.Parse(VesselParametersJson.Serialize(p));
			Assert.That(copy.Controls.NfuRelease.Value, Is.EqualTo(policy));
			p.Controls.NfuRelease.Value = "silently-ignore-tiller";
			Assert.Throws<VesselParametersException>(() => VesselParametersJson.Parse(VesselParametersJson.Serialize(p)));
		}

		[Test]
		public void SteeringTimeoutIsDefined_AndResetDiscardsViewerStateAndLagHistory()
		{
			var p = InstrumentFixtures.Load(); var m = new InstrumentModel(p);
			var taps = new InstrumentFixtures.Taps { RudderOrder = .35 };
			m.Step(InstrumentFixtures.State(0), InstrumentFixtures.Telemetry(70), ControlInputs.Neutral, taps);
			m.Step(InstrumentFixtures.State(8), InstrumentFixtures.Telemetry(70), ControlInputs.Neutral, taps);
			Assert.That(m.Phase(3), Is.EqualTo(AlarmPhase.Unacknowledged), "synthetic stuck-gear observation, unreachable by the healthy simulator");
			m.SetLampTest(true); m.Acknowledge(); m.Silence(); m.Reset();
			Assert.That(m.NextEventSequence, Is.Zero); Assert.That(m.LampTest, Is.False);
			taps.RudderOrder = 0;
			m.Step(InstrumentFixtures.State(0), InstrumentFixtures.Telemetry(p.IdleSpeed.Value), ControlInputs.Neutral, taps);
			Assert.That(m.Phase(3), Is.EqualTo(AlarmPhase.Normal));
			Assert.That(m.Value(Channel.EnginePortOilPressure), Is.EqualTo(200000));
		}
	}
}
