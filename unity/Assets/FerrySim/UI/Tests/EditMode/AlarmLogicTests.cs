using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Depth alarm and contact indicator logic. Guards against: alarm chatter when the depth hovers at the
	/// threshold, an acknowledge that does not stop the flashing (or carries over to the next alarm), a
	/// lost sounder reading (NaN) silently clearing an alarm, a threshold change that is never
	/// re-evaluated, a flash rate fast enough to be a seizure risk (more than 3 per second), a contact
	/// shorter than a frame that the learner never sees, and a missing contact reading shown as no contact.
	/// The contact tests here feed one physics step per update; ContactLatchTests covers per-frame latches.
	/// </summary>
	public class AlarmLogicTests
	{
		[Test]
		public void DepthAlarm_RaisesOnlyBelowThreshold()
		{
			var alarm = new DepthAlarm(2.0);

			alarm.Update(2.5, 0.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal));
			alarm.Update(2.0, 1.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal), "exactly at the threshold is not below it");
			alarm.Update(1.99, 2.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active));
			Assert.That(alarm.RaisedAt, Is.EqualTo(2.0));
		}

		[Test]
		public void DepthAlarm_ClearsOnlyAboveThresholdPlusHysteresis()
		{
			var alarm = new DepthAlarm(2.0);
			Assert.That(alarm.HysteresisMetres, Is.EqualTo(0.2).Within(1e-12));

			alarm.Update(1.9, 0.0);
			alarm.Update(2.1, 1.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active), "inside the hysteresis band the alarm holds");
			alarm.Update(1.95, 2.0);
			alarm.Update(2.15, 3.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active));
			Assert.That(alarm.RaisedAt, Is.EqualTo(0.0), "hovering does not re-raise");
			alarm.Update(2.2, 4.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal));
		}

		[Test]
		public void DepthAlarm_HysteresisHasAFloorForSmallThresholds()
		{
			Assert.That(new DepthAlarm(0.5).HysteresisMetres, Is.EqualTo(0.1).Within(1e-12));
			Assert.That(new DepthAlarm(5.0).HysteresisMetres, Is.EqualTo(0.5).Within(1e-12));
		}

		[Test]
		public void DepthAlarm_AcknowledgeStopsFlashingButKeepsItLit()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.Update(1.0, 10.0);

			Assert.That(alarm.IsLit(10.0), Is.True);
			Assert.That(alarm.IsLit(10.7), Is.False, "unacknowledged alarms flash");
			alarm.Acknowledge();
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Acknowledged));
			Assert.That(alarm.IsLit(10.7), Is.True, "acknowledged alarms are steady");
			Assert.That(alarm.IsLit(11.7), Is.True);
		}

		[Test]
		public void DepthAlarm_NewAlarmNeedsANewAcknowledge()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.Update(1.0, 0.0);
			alarm.Acknowledge();
			alarm.Update(3.0, 1.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal));

			alarm.Update(1.0, 2.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active));
			Assert.That(alarm.RaisedAt, Is.EqualTo(2.0));
		}

		[Test]
		public void DepthAlarm_AcknowledgeWithoutAlarmDoesNothing()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.Acknowledge();
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal));
			Assert.That(alarm.IsLit(0.0), Is.False);
		}

		[Test]
		public void DepthAlarm_LostReadingHoldsTheCurrentState()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.Update(1.0, 0.0);
			alarm.Update(double.NaN, 1.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active), "NaN must not clear an alarm");

			var quiet = new DepthAlarm(2.0);
			quiet.Update(double.NaN, 0.0);
			Assert.That(quiet.State, Is.EqualTo(AlarmState.Normal), "NaN must not raise one either");
		}

		[Test]
		public void DepthAlarm_ThresholdChangeIsReevaluated()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.Update(2.5, 0.0);
			alarm.ThresholdMetres = 3.0;
			alarm.Update(2.5, 1.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Active));

			alarm.ThresholdMetres = 1.0;
			alarm.Update(2.5, 2.0);
			Assert.That(alarm.State, Is.EqualTo(AlarmState.Normal));
		}

		[Test]
		public void DepthAlarm_RejectsNonsenseThresholds()
		{
			var alarm = new DepthAlarm(2.0);
			alarm.ThresholdMetres = double.NaN;
			Assert.That(alarm.ThresholdMetres, Is.EqualTo(2.0));
			alarm.ThresholdMetres = -1.0;
			Assert.That(alarm.ThresholdMetres, Is.EqualTo(0.0));
		}

		[Test]
		public void AlarmFlash_IsSlowerThanThreePerSecondAndStartsLit()
		{
			Assert.That(1.0 / AlarmFlash.PeriodSeconds, Is.LessThanOrEqualTo(3.0));
			Assert.That(AlarmFlash.IsOn(0.0), Is.True, "an alarm is visible the moment it is raised");
			Assert.That(AlarmFlash.IsOn(AlarmFlash.PeriodSeconds * AlarmFlash.OnFraction + 0.01), Is.False);
			Assert.That(AlarmFlash.IsOn(AlarmFlash.PeriodSeconds + 0.01), Is.True);
			Assert.That(AlarmFlash.IsOn(-1.0), Is.True);
			Assert.That(AlarmFlash.IsOn(double.NaN), Is.True);
		}

		[Test]
		public void Contact_ShowsWhileForceIsNonZero()
		{
			var contact = new ContactMonitor();

			contact.Update(0.0, 0.0);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.None));
			contact.Update(1500.0, 0.1);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.Live));
			Assert.That(contact.ForceNewtons, Is.EqualTo(1500.0));
			Assert.That(contact.PeakNewtons, Is.EqualTo(1500.0));
		}

		[Test]
		public void Contact_StaysVisibleBrieflyAfterAOneFrameTouch()
		{
			var contact = new ContactMonitor(3.0);

			contact.Update(42000.0, 5.00);
			contact.Update(0.0, 5.02);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.Recent));
			Assert.That(contact.ForceNewtons, Is.EqualTo(0.0));
			Assert.That(contact.PeakNewtons, Is.EqualTo(42000.0));
			contact.Update(0.0, 7.99);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.Recent));
			contact.Update(0.0, 8.0);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.None));
		}

		[Test]
		public void Contact_PeakCoversTheWholeEpisodeAndResetsForTheNext()
		{
			var contact = new ContactMonitor(3.0);

			contact.Update(1000.0, 0.0);
			contact.Update(5000.0, 0.1);
			contact.Update(0.0, 0.2);
			contact.Update(2000.0, 1.0);
			Assert.That(contact.PeakNewtons, Is.EqualTo(5000.0), "a bounce inside the hold window is the same episode");

			contact.Update(0.0, 1.1);
			contact.Update(0.0, 5.0);
			contact.Update(800.0, 6.0);
			Assert.That(contact.PeakNewtons, Is.EqualTo(800.0), "a new episode starts a new peak");
		}

		[Test]
		public void Contact_NonFiniteForceIsUnknown_AndANegativeOneIsNoContact()
		{
			// Corrected per review finding 4: this used to accept NaN as "no contact".
			var contact = new ContactMonitor();

			contact.Update(double.NaN, 0.0);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.Unknown), "a missing reading is not a confident NONE");
			contact.Update(double.PositiveInfinity, 0.1);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.Unknown));
			contact.Update(-5.0, 0.2);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.None));
		}

		[Test]
		public void Contact_ResetsWhenTimeRunsBackwards()
		{
			var contact = new ContactMonitor(3.0);

			contact.Update(1000.0, 10.0);
			contact.Update(0.0, 2.0);
			Assert.That(contact.Indication, Is.EqualTo(ContactIndication.None), "a seek or loop restart is not a contact");
		}
	}
}
