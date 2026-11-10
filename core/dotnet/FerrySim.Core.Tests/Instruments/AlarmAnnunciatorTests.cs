using System;
using FerrySim.Core.Instruments;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	// Failure modes: silencing acknowledges, a cleared unacknowledged alarm disappears, repeats use wall
	// time, lamp testing mutates phases, or a slow consumer reads overwritten events under an old sequence.
	public class AlarmAnnunciatorTests
	{
		private static AlarmAnnunciator Panel(double repeat = 0) => new AlarmAnnunciator(new[]
		{
			new AlarmDefinition { Id = "test", Group = AlarmGroup.Navigation, ReRaiseSeconds = InstrumentFixtures.P(repeat, "s") },
		});

		// Every viewer (console lamps, display skins, the alarm panel) must flash in step, so the rhythm is
		// one pure function of phase, lamp test and readout time rather than a copy per consumer.
		[Test]
		public void LampRhythmIsOnePureFunctionSharedByEveryViewer()
		{
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Normal, false, 0.2), Is.False);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Normal, true, 0.7), Is.True, "lamp test lights every lamp");
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Acknowledged, false, 0.7), Is.True, "steady while acknowledged");
			// Unacknowledged: 1 Hz, half on. Ring-back: 0.5 Hz, half on.
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Unacknowledged, false, 0.25), Is.True);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Unacknowledged, false, 0.75), Is.False);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.Unacknowledged, false, 1.25), Is.True);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.RingBack, false, 0.5), Is.True);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.RingBack, false, 1.5), Is.False);
			Assert.That(AlarmAnnunciator.LampLit(AlarmPhase.RingBack, false, 2.5), Is.True);
			var p = Panel();
			p.Step(0, true, 1, 100);
			for (var time = 0.0; time < 6; time += 0.05)
				Assert.That(p.LampOn(0, time), Is.EqualTo(AlarmAnnunciator.LampLit(p.Phase(0), p.LampTest, time)));
		}

		[Test]
		public void RaiseFlashSilenceAcknowledgeAndClear()
		{
			var p = Panel();
			p.Step(0, true, 1, 100);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(p.SounderOn(AlarmGroup.Navigation), Is.True);
			Assert.That(p.LampOn(0, 1), Is.Not.EqualTo(p.LampOn(0, 1.5)));
			p.Silence(AlarmGroup.Navigation);
			Assert.That(p.SounderOn(AlarmGroup.Navigation), Is.False);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			p.Acknowledge(AlarmGroup.Navigation);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
			Assert.That(p.LampOn(0, 1.5), Is.True);
			p.Step(0, false, 2, 200);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Normal));
			Assert.That(p.NextEventSequence, Is.EqualTo(4));
			Assert.That(p.EventAt(0).Kind, Is.EqualTo(AlarmEventKind.Raised));
			Assert.That(p.EventAt(0).Step, Is.EqualTo(100));
			Assert.That(p.EventAt(1).Kind, Is.EqualTo(AlarmEventKind.Silenced));
			Assert.That(p.EventAt(2).Kind, Is.EqualTo(AlarmEventKind.Acknowledged));
			Assert.That(p.EventAt(3).Kind, Is.EqualTo(AlarmEventKind.Cleared));
		}

		[Test]
		public void RingBackFlashesSlowly_AndANewConditionSoundsAgain()
		{
			var p = Panel();
			p.Step(0, true, 0, 0); p.Silence(AlarmGroup.Navigation);
			p.Step(0, false, 1, 100);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.RingBack));
			Assert.That(p.SounderOn(AlarmGroup.Navigation), Is.False);
			Assert.That(p.LampOn(0, 2), Is.Not.EqualTo(p.LampOn(0, 3)));
			p.Step(0, true, 4, 400);
			Assert.That(p.SounderOn(AlarmGroup.Navigation), Is.True);
			p.Step(0, false, 5, 500); p.Acknowledge(AlarmGroup.Navigation);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Normal));
		}

		[TestCase(0, AlarmPhase.Acknowledged)]
		[TestCase(2, AlarmPhase.Unacknowledged)]
		public void RepeatUsesSimulationTime_ZeroMeansNever(double repeat, AlarmPhase expected)
		{
			var p = Panel(repeat);
			p.Step(0, true, 10, 1000); p.Acknowledge(AlarmGroup.Navigation);
			p.Step(0, true, 11.99, 1199);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
			p.Step(0, true, 12, 1200);
			Assert.That(p.Phase(0), Is.EqualTo(expected));
			if (repeat > 0) Assert.That(p.EventAt(2).Kind, Is.EqualTo(AlarmEventKind.ReRaised));
		}

		[Test]
		public void LampTestDoesNotRaiseOrAcknowledge_AndLogOverflowKeepsSequences()
		{
			var p = Panel();
			for (var i = 0; i < 300; i++) p.SetLampTest(i % 2 == 0, AlarmGroup.Navigation);
			Assert.That(p.FirstEventSequence, Is.EqualTo(44));
			Assert.That(p.NextEventSequence, Is.EqualTo(300));
			for (var i = p.FirstEventSequence; i < p.NextEventSequence; i++) Assert.That(p.EventAt(i).Sequence, Is.EqualTo(i));
			Assert.Throws<ArgumentOutOfRangeException>(() => p.EventAt(43));
			Assert.Throws<ArgumentOutOfRangeException>(() => p.EventAt(300));
			p.SetLampTest(true, AlarmGroup.Navigation);
			Assert.That(p.LampOn(0, 1.5), Is.True);
			Assert.That(p.Phase(0), Is.EqualTo(AlarmPhase.Normal));
			Assert.That(p.SounderOn(AlarmGroup.Navigation), Is.False);
		}
	}
}
