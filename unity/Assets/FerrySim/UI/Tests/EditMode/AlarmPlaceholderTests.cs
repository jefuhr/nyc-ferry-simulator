using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The 5 ft under-keel alarm default and its feet unit are placeholders until crew say what they use
	/// (review finding 7). Guards against: the default threshold presented as if it were a confirmed
	/// operational setting on the panel or in the debrief, the mark disappearing in metres or while there
	/// is no data, and the mark staying on a threshold the user chose.
	/// </summary>
	public class AlarmPlaceholderTests
	{
		[Test]
		public void Panel_MarksTheDefaultThresholdAsAPlaceholder()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway();

			panel.Refresh(source, DistanceUnit.Feet, DepthAlarm.DefaultThresholdMetres, 0.0);
			Assert.That(panel.Depth.ThresholdText, Is.EqualTo("ALARM BELOW 5.0 ft · PLACEHOLDER"));

			panel.Refresh(source, DistanceUnit.Metres, DepthAlarm.DefaultThresholdMetres, 0.1);
			Assert.That(panel.Depth.ThresholdText, Is.EqualTo("ALARM BELOW 1.5 m · PLACEHOLDER"), "the same default, shown in metres");

			source.HasTelemetry = false;
			panel.Refresh(source, DistanceUnit.Feet, DepthAlarm.DefaultThresholdMetres, 0.2);
			Assert.That(panel.Depth.ThresholdText, Is.EqualTo("ALARM BELOW 5.0 ft · PLACEHOLDER"), "the setting is still shown without data");
		}

		[Test]
		public void Panel_DoesNotMarkAThresholdTheUserChose()
		{
			var panel = new WheelhousePanel();

			panel.Refresh(StubTelemetrySource.Underway(), DistanceUnit.Feet, DisplayUnits.DistanceToMetres(6.0, DistanceUnit.Feet), 0.0);

			Assert.That(panel.Depth.ThresholdText, Is.EqualTo("ALARM BELOW 6.0 ft"));
		}

		[Test]
		public void SteppingBackToFiveFeet_IsTheDefaultAgain()
		{
			var view = new WheelhouseView();
			view.StepDepthAlarmThreshold(1);
			view.StepDepthAlarmThreshold(-1);

			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			Assert.That(view.Panel.Depth.ThresholdText, Does.EndWith("· PLACEHOLDER"), "5 ft reached by stepping is still the unconfirmed default");
		}

		[Test]
		public void Debrief_SaysTheDefaultThresholdIsAPlaceholderPendingCrewInput()
		{
			var debrief = new DebriefScreen();

			debrief.Show(TelemetryCsv.Load(UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv)), null);

			var note = debrief.Q<Label>("debrief-events-note")?.text;
			Assert.That(note, Is.Not.Null, "the debrief notes where its depth alarms come from");
			Assert.That(note, Does.Contain("5.0 ft"));
			Assert.That(note, Does.Contain("placeholder").IgnoreCase);
			Assert.That(note, Does.Contain("pending crew input"));

			debrief.DepthAlarmThresholdMetres = 2.0;
			Assert.That(debrief.Q<Label>("debrief-events-note").text, Does.Not.Contain("placeholder").IgnoreCase);
		}
	}
}
