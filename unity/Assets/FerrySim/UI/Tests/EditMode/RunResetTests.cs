using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Per-run UI state starts over when <c>IVesselTelemetrySource.RunId</c> or the source changes (review
	/// finding 3). Guards against: an acknowledged, steady depth alarm carried into a new run that starts in
	/// shallow water (it must flash again until acknowledged), whether the new run is a reset or replay of
	/// the same source, keeps counting time forward, or comes from a different source; a contact episode
	/// from the previous run shown in the new one; and, the other way round, a transient no-data sample
	/// within a run clearing an acknowledgement.
	/// </summary>
	public class RunResetTests
	{
		private const double Threshold = DepthAlarm.DefaultThresholdMetres;
		private const double Shallow = 1.0;

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
		public void AcknowledgedAlarm_FlashesAgain_WhenTheSourceResetsToAShallowStart()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(time: 5.0, depthUnderKeel: Shallow, runId: 7);
			AcknowledgeShallowAlarm(panel, source);

			// Reset on the same runner instance: a new run id, time back near zero, still shallow.
			source.RunId = 8;
			source.Set(0.01, Shallow);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.3);

			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active), "a new run's alarm needs its own acknowledge");
			Assert.That(panel.Alarm.RaisedAt, Is.EqualTo(100.3), "and flashes from when it was raised");
		}

		[Test]
		public void AcknowledgedAlarm_FlashesAgain_WhenANewRunsTimeKeepsGoingForward()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(time: 5.0, depthUnderKeel: Shallow, runId: 7);
			AcknowledgeShallowAlarm(panel, source);

			// Only the run id says this is a new run: same object, time still increasing.
			source.RunId = 8;
			source.Set(6.0, Shallow);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.3);

			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active));
		}

		[Test]
		public void AcknowledgedAlarm_FlashesAgain_WhenTheSourceIsReplacedByAnotherShallowRun()
		{
			var panel = new WheelhousePanel();
			var first = StubTelemetrySource.Underway(time: 5.0, depthUnderKeel: Shallow, runId: 1);
			AcknowledgeShallowAlarm(panel, first);

			// Run ids come from each source's own counter, so a different source can reuse the number.
			var second = StubTelemetrySource.Underway(time: 5.1, depthUnderKeel: Shallow, runId: 1);
			panel.Refresh(second, DistanceUnit.Feet, Threshold, 100.3);

			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active));
		}

		[Test]
		public void AcknowledgedAlarm_FlashesAgain_WhenTheFakeSourceReplaysOrLoadsAShallowRun()
		{
			_host = new GameObject("RunResetTests");
			var source = _host.AddComponent<FakeTelemetrySource>();
			var shallow = ShallowRun();
			source.Load(shallow, null);
			source.Seek(2.0);
			var panel = new WheelhousePanel();
			AcknowledgeShallowAlarm(panel, source);

			source.Seek(shallow.StartTime);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.3);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active), "replaying from the start is a new run");

			panel.AcknowledgeAlarm();
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.4);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged));
			source.Load(shallow, null);
			source.Seek(2.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.5);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active), "loading a recording is a new run, even at a later time");
		}

		[Test]
		public void TransientNoData_KeepsTheAcknowledgement()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(time: 5.0, depthUnderKeel: Shallow, runId: 3);
			AcknowledgeShallowAlarm(panel, source);

			source.HasTelemetry = false;
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.3);
			source.HasTelemetry = true;
			source.Set(5.2, Shallow);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.4);

			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged), "a dropout inside one run is not a new run");
		}

		[Test]
		public void ContactEpisode_DoesNotCarryIntoANewRun()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(time: 5.0, contactForce: 30000.0, runId: 4);
			source.Contact = new ContactLatch(1, 499, 30000.0, 30000.0, 499);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);
			source.Set(5.1, 12.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.1);
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent), "the fixture holds a finished contact");

			source.RunId = 5;
			source.Contact = ContactLatch.None;
			source.Set(5.2, 12.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.2);

			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.None));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"));
		}

		private static void AcknowledgeShallowAlarm(WheelhousePanel panel, IVesselTelemetrySource source)
		{
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.0);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active), "the fixture starts in shallow water");
			panel.AcknowledgeAlarm();
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 100.1);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged));
			Assert.That(panel.Depth.AlarmLit, Is.True, "acknowledged: steady");
		}

		private static TelemetryRecording ShallowRun()
		{
			var builder = new TelemetryRecordingBuilder("shallow run (test only)");
			for (var i = 0; i <= 50; i++)
			{
				var sample = StubTelemetrySource.Underway(time: i * 0.1, depthUnderKeel: Shallow);
				builder.Add(sample.State, sample.Telemetry, sample.Controls);
			}

			return builder.Build();
		}
	}
}
