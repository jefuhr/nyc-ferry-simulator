using System.Collections;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (c) Per-run UI state resets when the bridge starts a new run. Failure modes guarded: a depth alarm
	/// acknowledged in one run staying quiet in the next (the learner never sees the new shallow water), and the
	/// last run's contact latch or episode shown at the start of a new one.
	/// </summary>
	public class IntegratedRunResetTests
	{
		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator AcknowledgedAlarmAndContactLatch_ResetWhenANewRunStarts()
		{
			var view = _scene.View;
			// The calm scenario has 48 m under the keel, so raise the threshold above it to have an alarm to acknowledge.
			view.DepthAlarmThresholdMetres = 100.0;
			_scene.StepsPerFrame(25);
			var controls = new LandingApproachControls(_scene.Runner, 1.0);
			_scene.Restart(_scene.Approach(15.0, "run reset"), controls);
			var panel = _scene.Panel;

			yield return _scene.FramesUntil(() => panel.Alarm.State == AlarmState.Active, 20, "the depth alarm");
			view.AcknowledgeAlarms();
			yield return SliceScene.Frames(2);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged));
			Assert.That(panel.Depth.AlarmLit, Is.True, "acknowledged: steady, still lit");

			yield return _scene.FramesUntil(() => _scene.Runner.Contact.StepsWithContact > 0, 400, "the bow to touch South Williamsburg's face");
			yield return SliceScene.Frames(2);
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Live).Or.EqualTo(ContactIndication.Recent));
			Assert.That(panel.Contact.Value.text, Does.EndWith(" kN"));
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged), "the acknowledgement holds within the run");
			var oldRun = _scene.Runner.RunId;

			_scene.Runner.ResetRun();
			Assert.That(_scene.Runner.RunId, Is.Not.EqualTo(oldRun));
			Assert.That(_scene.Runner.Contact.StepsWithContact, Is.EqualTo(0), "the bridge's latch starts empty");
			yield return SliceScene.Frames(3);

			Assert.That(_scene.Runner.HasTelemetry, Is.True, "the new run is stepping");
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active), "a fresh alarm, not the old acknowledgement");
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.None));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"), "no contact carried over");
			Assert.That(panel.ContactMonitor.PeakNewtons, Is.EqualTo(0.0));
		}
	}
}
