using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Gear-state logic for the engine columns. Guards against: SHIFTING shown as engaged (the dangerous
	/// one: the learner thinks the boat has drive), shaft direction taken from engine speed instead of
	/// shaft speed, a shaft that rounds to 0 rpm still showing a direction, and an unknown enum value
	/// rendering as if it were a real state.
	/// </summary>
	public class GearReadoutTests
	{
		private static EngineTelemetry Engine(GearState gear, double engineRpm, double shaftRpm) =>
			new EngineTelemetry(Units.RpmToRadiansPerSecond(engineRpm), Units.RpmToRadiansPerSecond(shaftRpm), gear, double.NaN);

		[TestCase(GearState.Neutral, "NEUTRAL", GearTone.Idle)]
		[TestCase(GearState.Ahead, "AHEAD", GearTone.Engaged)]
		[TestCase(GearState.Astern, "ASTERN", GearTone.Engaged)]
		[TestCase(GearState.Shifting, "SHIFTING", GearTone.Transition)]
		public void EachGearState_HasItsOwnLabelAndTone(GearState gear, string label, GearTone tone)
		{
			var readout = GearReadout.From(Engine(gear, 700.0, 0.0));

			Assert.That(readout.Label, Is.EqualTo(label));
			Assert.That(readout.Tone, Is.EqualTo(tone));
		}

		[Test]
		public void Shifting_IsNeverPresentedAsEngaged()
		{
			var readout = GearReadout.From(Engine(GearState.Shifting, 1653.74, 656.51));

			Assert.That(readout.Tone, Is.Not.EqualTo(GearTone.Engaged));
			Assert.That(readout.Label, Is.EqualTo("SHIFTING"));
		}

		[Test]
		public void ShaftDirection_ComesFromShaftSpeedSign()
		{
			// Engine speed is always positive; only the shaft says which way the propeller turns.
			var ahead = GearReadout.From(Engine(GearState.Ahead, 2025.66, 804.15));
			var astern = GearReadout.From(Engine(GearState.Astern, 372.68, -14.27));
			var coasting = GearReadout.From(Engine(GearState.Shifting, 378.0, 25.26));

			Assert.That(ahead.Shaft, Is.EqualTo(ShaftRotation.Ahead));
			Assert.That(ahead.ShaftRpm, Is.EqualTo("804"));
			Assert.That(astern.Shaft, Is.EqualTo(ShaftRotation.Astern));
			Assert.That(astern.ShaftRpm, Is.EqualTo("14"));
			Assert.That(coasting.Shaft, Is.EqualTo(ShaftRotation.Ahead), "shaft still windmilling ahead while the clutch shifts");
		}

		[Test]
		public void ShaftThatReadsZero_ShowsNoDirection()
		{
			Assert.That(GearReadout.From(Engine(GearState.Neutral, 650.0, 0.4)).Shaft, Is.EqualTo(ShaftRotation.Stopped));
			Assert.That(GearReadout.From(Engine(GearState.Neutral, 650.0, -0.4)).Shaft, Is.EqualTo(ShaftRotation.Stopped));
			Assert.That(GearReadout.From(Engine(GearState.Neutral, 650.0, 0.6)).Shaft, Is.EqualTo(ShaftRotation.Ahead));
			Assert.That(GearReadout.From(Engine(GearState.Neutral, 650.0, 0.0)).ShaftRpm, Is.EqualTo("0"));
		}

		[Test]
		public void UnknownGearValue_IsShownAsUnknown()
		{
			var readout = GearReadout.From(Engine((GearState)42, 650.0, 0.0));

			Assert.That(readout.Label, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(readout.Tone, Is.EqualTo(GearTone.Unknown));
		}

		[Test]
		public void NonFiniteShaftSpeed_IsUnknownDirection()
		{
			var readout = GearReadout.From(new EngineTelemetry(70.0, double.NaN, GearState.Ahead, 0.0));

			Assert.That(readout.Shaft, Is.EqualTo(ShaftRotation.Unknown));
			Assert.That(readout.ShaftRpm, Is.EqualTo(InstrumentFormat.NoValue));
		}

		[TestCase(GearState.Neutral, "N")]
		[TestCase(GearState.Ahead, "AHD")]
		[TestCase(GearState.Astern, "AST")]
		[TestCase(GearState.Shifting, "SHIFT")]
		public void ShortLabels_ForTheHud(GearState gear, string expected)
		{
			Assert.That(GearReadout.From(Engine(gear, 700.0, 0.0)).ShortLabel, Is.EqualTo(expected));
		}
	}
}
