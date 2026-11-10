using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	public static class InstrumentFixtures
	{
		public static ParameterValue P(double value, string unit = "1") => new ParameterValue { Value = value, Unit = unit, Source = "simulator estimate for tests", Confidence = "estimated" };
		public static InstrumentParameters Load(string id = "t-class") => InstrumentParametersJson.Load(Path.Combine(RepoPaths.Root, "vessels", "instruments", id + ".json"));
		public sealed class Taps : IInstrumentTaps
		{
			public double RudderOrder { get; set; }
			public bool NfuActive { get; set; }
			public double HardOverAngle => .35;
			public double SteeringRate => .12;
			public ShiftPhase PortShiftPhase => ShiftPhase.Engaged;
			public ShiftPhase StarboardShiftPhase => ShiftPhase.Engaged;
			public double PortEngineLoad { get; set; }
			public double StarboardEngineLoad { get; set; }
		}
		public static VesselTelemetry Telemetry(double speed, double depth = 10, double force = 0) => new VesselTelemetry(new EngineTelemetry(speed, speed / 2, GearState.Ahead, 1), new EngineTelemetry(speed, -speed / 2, GearState.Astern, 1), 0, 0, 5, 6, 1, depth, force);
		public static VesselState State(double time) => VesselState.Planar(time, 20, 30, .4, 5, 1, .01);
	}

	// Failure modes: units/ranges drift, the second engine aliases the first, lag overshoots, or a channel
	// is forgotten. The target coefficients below are independently specified by the estimated data table.
	public class EngineInstrumentModelTests
	{
		[TestCase("t-class", 0, 0)]
		[TestCase("t-class", .7, .6)]
		[TestCase("t-class", 1, 1)]
		[TestCase("k-class", 0, 0)]
		[TestCase("k-class", .7, .6)]
		[TestCase("k-class", 1, 1)]
		public void SteadyIdleCruiseAndFull_AreSIAndEveryChannelIsFinite(string id, double speedRatio, double load)
		{
			var data = InstrumentFixtures.Load(id);
			var m = new InstrumentModel(data);
			var taps = new InstrumentFixtures.Taps { PortEngineLoad = load, StarboardEngineLoad = load };
			var speed = data.IdleSpeed.Value + speedRatio * (data.RatedSpeed.Value - data.IdleSpeed.Value);
			m.Step(InstrumentFixtures.State(0), InstrumentFixtures.Telemetry(speed), new ControlInputs(.2, -.3, .4, .5), taps);
			Assert.That(m.Value(Channel.EnginePortOilPressure), Is.EqualTo(200000 + 200000 * speedRatio + 50000 * load).Within(1e-8));
			Assert.That(m.Value(Channel.EngineStarboardOilPressure), Is.EqualTo(m.Value(Channel.EnginePortOilPressure)));
			Assert.That(m.Value(Channel.EnginePortCoolantTemperature), Is.EqualTo(348.15 + 10 * speedRatio + 5 * load).Within(1e-9));
			Assert.That(m.Value(Channel.EnginePortExhaustTemperature), Is.EqualTo(423.15 + 100 * speedRatio + 300 * load).Within(1e-9));
			Assert.That(m.Value(Channel.EnginePortBoostPressure), Is.EqualTo(10000 * speedRatio + 180000 * load).Within(1e-9));
			Assert.That(m.Value(Channel.EnginePortFuelRate), Is.EqualTo(.001 + .001 * speedRatio + data.RatedPower.Value * .00000006 * load).Within(1e-12));
			Assert.That(m.Value(Channel.EnginePortGearOilPressure), Is.EqualTo(1500000 + 100000 * speedRatio + 100000 * load).Within(1e-9));
			foreach (Channel c in Enum.GetValues(typeof(Channel))) Assert.That(double.IsFinite(m.Value(c)), Is.True, c.ToString());
			Assert.That(m.Value(Channel.SteeringJog), Is.EqualTo(.5));
		}

		[Test]
		public void FirstOrderLag_UsesElapsedSimulationTime_AndIndependentSides()
		{
			var data = InstrumentFixtures.Load(); var m = new InstrumentModel(data); var taps = new InstrumentFixtures.Taps();
			m.Step(InstrumentFixtures.State(0), InstrumentFixtures.Telemetry(data.IdleSpeed.Value), ControlInputs.Neutral, taps);
			taps.PortEngineLoad = 1;
			m.Step(InstrumentFixtures.State(1), InstrumentFixtures.Telemetry(data.RatedSpeed.Value), ControlInputs.Neutral, taps);
			Assert.That(m.Value(Channel.EnginePortOilPressure), Is.EqualTo(200000 + 250000 * (1 - Math.Exp(-1 / data.OilPressure.TimeConstant.Value))).Within(1e-8));
			Assert.That(m.Value(Channel.EnginePortLoad), Is.EqualTo(1 - Math.Exp(-2)).Within(1e-12));
			Assert.That(m.Value(Channel.EngineStarboardLoad), Is.Zero);
			Assert.That(m.Value(Channel.EngineStarboardOilPressure), Is.LessThan(m.Value(Channel.EnginePortOilPressure)));
			Assert.That(m.Value(Channel.EnginePortCoolantTemperature), Is.InRange(348.15, 363.15));
		}

		[Test]
		public void OnlyModelledConditionsRaise_AndViewerActionsDoNotChangeSimulation()
		{
			var m = new InstrumentModel(InstrumentFixtures.Load()); var taps = new InstrumentFixtures.Taps();
			m.Step(InstrumentFixtures.State(0), InstrumentFixtures.Telemetry(70, -1, 100), ControlInputs.Neutral, taps);
			Assert.That(m.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(m.Phase(1), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(m.Phase(2), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(m.Phase(3), Is.EqualTo(AlarmPhase.Normal));
			m.Silence(); m.Acknowledge(); m.SetLampTest(true);
			Assert.That(m.LampTest, Is.True);
			Assert.That(m.SounderOn(AlarmGroup.Hull), Is.False);
		}
	}
}
