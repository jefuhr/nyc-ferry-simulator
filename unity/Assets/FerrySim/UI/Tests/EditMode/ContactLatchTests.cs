using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The contact indicator is driven by the per-step <see cref="ContactLatch"/>, not by the force sampled at
	/// frame time (review findings 2 and 4). Guards against: a contact on a physics step between two rendered
	/// frames never reaching the indicator; an episode peak that misses the strongest between-frame step;
	/// the fake source's latch missing contact rows that one <c>Advance</c> skips over; and a missing
	/// (non-finite) contact reading shown as a confident NONE on the panel or in the debrief.
	/// </summary>
	public class ContactLatchTests
	{
		private const double Threshold = DepthAlarm.DefaultThresholdMetres;

		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		[Test]
		public void Panel_ShowsAOneRowContactThatHappenedBetweenTwoRefreshes()
		{
			var source = FakeSource();
			source.Load(Rows(0.0, 6.0, (1.1, 20000.0)), null);
			var panel = new WheelhousePanel();
			source.Seek(1.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"));

			// One refresh interval passes the rows at 1.1 s (contact), 1.2 s and 1.3 s (none).
			source.Advance(0.35);
			Assert.That(source.Telemetry.ContactForce, Is.EqualTo(0.0), "the latest row has no contact: what the panel used to sample");
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.1);

			Assert.That(panel.Contact.Indication, Is.Not.EqualTo(ContactIndication.None), "a contact between refreshes is shown");
			Assert.That(panel.Contact.Value.text, Does.Contain("20 kN"));

			source.Advance(0.1);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.2);
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("PEAK 20 kN"));

			source.Seek(4.2);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.3);
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"), "the hold ends 3 s after the touch");
		}

		[Test]
		public void Panel_EpisodePeak_IncludesTheStrongestStepBetweenRefreshes()
		{
			var source = FakeSource();
			source.Load(Rows(0.0, 6.0, (1.1, 20000.0), (1.2, 45000.0), (1.3, 10000.0)), null);
			var panel = new WheelhousePanel();
			source.Seek(1.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);

			source.Advance(0.35);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.1);
			source.Advance(0.1);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.2);

			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("PEAK 45 kN"), "the 45 kN step fell between two refreshes");
		}

		[Test]
		public void FakeSource_LatchCountsEveryContactRow_EvenWhenOneAdvanceSkipsSeveral()
		{
			var source = FakeSource();
			source.Load(Rows(0.0, 6.0, (1.1, 20000.0), (1.2, 45000.0), (1.3, 10000.0), (2.5, 5000.0)), null);
			Assert.That(source.Contact.StepsWithContact, Is.EqualTo(0));
			Assert.That(source.Contact.LastContactStep, Is.EqualTo(-1));

			source.Seek(1.0);
			source.Advance(0.35);

			var latch = source.Contact;
			Assert.That(latch.StepsWithContact, Is.EqualTo(3), "rows 1.1, 1.2 and 1.3 s");
			Assert.That(latch.LastContactForce, Is.EqualTo(10000.0));
			Assert.That(latch.LastContactStep, Is.EqualTo(129), "a row at t is the state after physics step round(100 t) - 1, as the bridge counts");
			Assert.That(latch.PeakContactForce, Is.EqualTo(45000.0));
			Assert.That(latch.PeakContactStep, Is.EqualTo(119));

			source.Advance(1.5);
			latch = source.Contact;
			Assert.That(latch.StepsWithContact, Is.EqualTo(4));
			Assert.That(latch.LastContactForce, Is.EqualTo(5000.0));
			Assert.That(latch.LastContactStep, Is.EqualTo(249));
			Assert.That(latch.PeakContactForce, Is.EqualTo(45000.0), "the run's peak is kept");
			Assert.That(latch.PeakContactStep, Is.EqualTo(119));
		}

		[Test]
		public void Monitor_IsLiveOnlyWhileTheLatestStepTouches()
		{
			var monitor = new ContactMonitor();
			monitor.Update(ContactLatch.None, 1.00, true);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.None));

			// Steps 100 to 104 pass in one frame; 102 and 104 (the latest) touch, the peak on 102.
			var latch = new ContactLatch(2, 104, 900.0, 1200.0, 102);
			monitor.Update(latch, 1.05, true);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Live));
			Assert.That(monitor.ForceNewtons, Is.EqualTo(900.0), "the latest step's force");
			Assert.That(monitor.PeakNewtons, Is.EqualTo(1200.0), "the episode's peak, from a step between frames");

			monitor.Update(latch, 1.10, true);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Recent), "no contact in the steps since");
			Assert.That(monitor.ForceNewtons, Is.EqualTo(0.0));
			Assert.That(monitor.PeakNewtons, Is.EqualTo(1200.0));
		}

		[Test]
		public void Monitor_ShowsATouchThatEndedInsideOneFrame_AsRecent()
		{
			var monitor = new ContactMonitor();
			monitor.Update(ContactLatch.None, 2.00, true);

			// Only step 202 touched; the frame ends after step 204.
			monitor.Update(new ContactLatch(1, 202, 5000.0, 5000.0, 202), 2.05, true);

			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Recent));
			Assert.That(monitor.PeakNewtons, Is.EqualTo(5000.0));
		}

		[Test]
		public void Monitor_AttachedMidRun_HoldsOnlyATouchFromTheLastFewSeconds()
		{
			var late = new ContactMonitor();
			late.Update(new ContactLatch(3, 999, 4000.0, 4000.0, 999), 30.0, true);
			Assert.That(late.Indication, Is.EqualTo(ContactIndication.None), "a touch at 10 s is not news at 30 s");

			var recent = new ContactMonitor();
			recent.Update(new ContactLatch(3, 2899, 4000.0, 4000.0, 2899), 30.0, true);
			Assert.That(recent.Indication, Is.EqualTo(ContactIndication.Recent), "a touch at 29 s is");
		}

		[Test]
		public void Monitor_KeepsAKnownTouch_AndIsUnknownOnlyWhenNothingIsKnown()
		{
			var monitor = new ContactMonitor();
			monitor.Update(ContactLatch.None, 1.00, false);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Unknown));

			var latch = new ContactLatch(1, 104, 800.0, 800.0, 104);
			monitor.Update(latch, 1.06, false);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Recent), "the latch recorded the touch even though the latest reading is missing");

			monitor.Update(latch, 4.2, false);
			Assert.That(monitor.Indication, Is.EqualTo(ContactIndication.Unknown), "after the hold, nothing is known");
		}

		[Test]
		public void Panel_ShowsContactAsUnknown_WhenTheContactReadingIsNotFinite()
		{
			var panel = new WheelhousePanel();
			foreach (var missing in new[] { double.NaN, double.PositiveInfinity })
			{
				var source = StubTelemetrySource.Underway(contactForce: missing);

				panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);

				Assert.That(panel.ShowingNoData, Is.False, "the rest of the sample is valid");
				Assert.That(panel.Contact.Value.text, Is.EqualTo(InstrumentFormat.NoValue), $"a contact force of {missing} is not NONE");
				Assert.That(panel.Contact.Indication, Is.Not.EqualTo(ContactIndication.None));
			}

			panel.Refresh(StubTelemetrySource.Underway(time: 11.0), DistanceUnit.Feet, Threshold, 0.1);
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"), "a valid zero is a real NONE");
		}

		[Test]
		public void Debrief_ShowsUnknownContact_ForANonFiniteSample()
		{
			var builder = new TelemetryRecordingBuilder("in memory", new[] { new KeyValuePair<string, string>("ferrysim-csv", "1") });
			var valid = StubTelemetrySource.Underway(time: 1.0);
			builder.Add(valid.State, valid.Telemetry, valid.Controls);
			var missing = StubTelemetrySource.Underway(time: 2.0, contactForce: double.NaN);
			builder.Add(missing.State, missing.Telemetry, missing.Controls);
			var debrief = new DebriefScreen();
			debrief.Show(builder.Build(), null);

			debrief.SetCursorTime(1.0);
			Assert.That(debrief.ReadoutText("contact"), Is.EqualTo("none"));
			debrief.SetCursorTime(2.0);
			Assert.That(debrief.ReadoutText("contact"), Is.EqualTo(InstrumentFormat.NoValue), "an unknown force is not 'none'");
		}

		private FakeTelemetrySource FakeSource()
		{
			_host = new GameObject("ContactLatchTests");
			return _host.AddComponent<FakeTelemetrySource>();
		}

		/// <summary>Rows every 0.1 s from <paramref name="start"/> to <paramref name="end"/> in deep water, contact only at the given times.</summary>
		internal static TelemetryRecording Rows(double start, double end, params (double time, double force)[] contacts)
		{
			var header = new[] { new KeyValuePair<string, string>("ferrysim-csv", "1") };
			var builder = new TelemetryRecordingBuilder("synthetic contact rows (test only)", header);
			var count = (int)Math.Round((end - start) * 10.0);
			for (var i = 0; i <= count; i++)
			{
				var time = Math.Round(start + i * 0.1, 3);
				var force = 0.0;
				foreach (var (contactTime, contactForce) in contacts)
				{
					if (Math.Abs(contactTime - time) < 1e-6)
					{
						force = contactForce;
					}
				}

				var sample = StubTelemetrySource.Underway(time: time, contactForce: force);
				builder.Add(sample.State, sample.Telemetry, sample.Controls);
			}

			return builder.Build();
		}
	}
}
