using System;
using System.IO;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	// Failure modes: file threshold wins over viewer setting, clearing/reset retains the override,
	// invalid values poison the condition, or viewer settings leak into deterministic physics.
	public class ShallowWaterOverrideTests
	{
		private static void Set(InstrumentModel model, double? metres) => model.SetShallowWaterThreshold(metres);

		private static void Observe(InstrumentModel model, double time, double depth) =>
			model.Step(InstrumentFixtures.State(time), InstrumentFixtures.Telemetry(70, depth), ControlInputs.Neutral, new InstrumentFixtures.Taps());

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void OverrideReplacesFileValue_AndClearingRestoresIt(string vessel)
		{
			var model = new InstrumentModel(InstrumentFixtures.Load(vessel));
			Observe(model, 0, 2);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Normal));
			Set(model, 3); Observe(model, .01, 2);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			Set(model, null); Observe(model, .02, 2);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.RingBack));
			model.Acknowledge(); Set(model, .5); Observe(model, .03, 1);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Normal), "below file threshold but above override");
			Set(model, null); Observe(model, .04, 1);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
		}

		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		[TestCase(double.NegativeInfinity)]
		[TestCase(-.01)]
		public void InvalidOverrideIsRefusedWithoutChangingTheLastSetting(double invalid)
		{
			var model = new InstrumentModel(InstrumentFixtures.Load());
			Set(model, .5);
			Assert.Throws<ArgumentOutOfRangeException>(() => Set(model, invalid));
			Observe(model, 0, 1);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Normal));
		}

		[Test]
		public void ZeroIsValid_AndResetReturnsToTheFileDefault()
		{
			var model = new InstrumentModel(InstrumentFixtures.Load());
			Set(model, 0); Observe(model, 0, 0);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Normal));
			model.Reset(); Observe(model, 0, 1);
			Assert.That(model.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
		}

		[Test]
		public void StateDigestIsIdenticalWithAndWithoutViewerOverrides() =>
			Assert.That(Digest(true), Is.EqualTo(Digest(false)));

		private static byte[] Digest(bool actions)
		{
			var simulation = ManeuverRunner.Create(ManeuverRunner.Load("t-class.json"));
			var model = new InstrumentModel(InstrumentFixtures.Load());
			using var bytes = new MemoryStream();
			using var writer = new BinaryWriter(bytes);
			for (var step = 0; step < 1000; step++)
			{
				var controls = new ControlInputs(.6, .4, .2, step < 500 ? .5 : 0);
				if (actions) Set(model, step % 3 == 0 ? (double?)null : step % 2 == 0 ? 100 : .5);
				simulation.Step(controls);
				model.Step(simulation.State, simulation.Telemetry, controls, simulation);
				if (actions) { model.Acknowledge(); model.Silence(); model.SetLampTest(step % 2 == 0); }
				var s = simulation.State;
				foreach (var value in new[] { s.Time, s.North, s.East, s.Down, s.Roll, s.Pitch, s.Heading, s.Surge, s.Sway, s.Heave, s.RollRate, s.PitchRate, s.YawRate }) writer.Write(value);
			}
			return SHA256.HashData(bytes.ToArray());
		}
	}
}
