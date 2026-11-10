using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using FerrySim.UI.Displays;
using NUnit.Framework;
using static FerrySim.UI.Tests.EditMode.Displays.SkinJson;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// The alarm panel and the engine monitor's alarm list. Failure modes guarded: an alarm the vessel defines that
	/// never appears (the list must come from the readout, not the skin); alarms filed under the wrong group; a button
	/// that does nothing, fires twice, or leaves the lamp test held after release; lamp test not lighting every lamp;
	/// and a list that says nothing when there is nothing to list.
	/// </summary>
	public class AlarmPanelViewTests
	{
		private static string PanelSkin() => Skin(
			"{'type': 'frame', 'rect': [0, 0, 640, 40], 'text': 'ALARMS'}," +
			"{'type': 'list', 'rect': [16, 52, 608, 340], 'text': 'NO ALARMS DEFINED'}," +
			"{'type': 'softkey', 'rect': [16, 412, 192, 52], 'text': 'ACK', 'action': 'acknowledge'}," +
			"{'type': 'softkey', 'rect': [224, 412, 192, 52], 'text': 'SILENCE', 'action': 'silence'}," +
			"{'type': 'softkey', 'rect': [432, 412, 192, 52], 'text': 'LAMP TEST', 'action': 'lamp-test'}",
			display: "alarm-panel", reference: "[640, 480]");

		private static AlarmPanelView Panel() => new AlarmPanelView(DisplaySkin.Parse(PanelSkin(), TClassAlarms));

		/// <summary>The four t-class alarms, deliberately not in group order.</summary>
		private static FakeInstrumentReadout Shuffled() => new FakeInstrumentReadout(
			("hull.grounding", AlarmGroup.Hull),
			("steering.failure", AlarmGroup.Steering),
			("navigation.shallow-water", AlarmGroup.Navigation),
			("hull.contact", AlarmGroup.Hull));

		[Test]
		public void TheList_ShowsEveryAlarmTheReadoutDefines_GroupedInGroupOrder()
		{
			var panel = Panel();
			panel.Refresh(Shuffled(), 0.1);

			var rows = panel.List.Rows.Select(row => (row.IsHeader, row.Text)).ToArray();
			Assert.That(rows, Is.EqualTo(new[]
			{
				(true, "STEERING"), (false, "FAILURE"),
				(true, "NAVIGATION"), (false, "SHALLOW WATER"),
				(true, "HULL"), (false, "GROUNDING"), (false, "CONTACT"),
			}));
			Assert.That(panel.List.Rows.Where(row => !row.IsHeader).Select(row => row.Alarm), Is.EqualTo(new[] { 1, 2, 0, 3 }));
			Assert.That(panel.List.EmptyTextVisible, Is.False);
		}

		[Test]
		public void TheList_FollowsTheReadout_WhenTheVesselChanges()
		{
			var panel = Panel();
			panel.Refresh(Shuffled(), 0.1);
			var other = new FakeInstrumentReadout(("steering.failure", AlarmGroup.Steering), ("engine.port.overspeed", AlarmGroup.Engine));
			panel.Refresh(other, 0.2);

			Assert.That(panel.List.Rows.Select(row => row.Text), Is.EqualTo(new[] { "STEERING", "FAILURE", "ENGINE", "PORT OVERSPEED" }));
		}

		[Test]
		public void AGroupFilter_ListsOnlyThatGroup_AndSaysSoWhenItIsEmpty()
		{
			var skin = DisplaySkin.Parse(EveryElementType(), TClassAlarms);
			var view = new SkinnedDisplayView(skin, DisplaySide.Port);
			view.ShowPage("alarms");
			var list = (AlarmListView)view.Elements[7];
			view.Refresh(FakeInstrumentReadout.TClass(), 0.1);

			Assert.That(list.Rows, Is.Empty, "t-class defines no engine alarms");
			Assert.That(list.EmptyTextVisible, Is.True);
			Assert.That(list.EmptyText, Is.EqualTo("NO ENGINE ALARMS"));

			var withEngine = new FakeInstrumentReadout(("steering.failure", AlarmGroup.Steering), ("engine.port.overspeed", AlarmGroup.Engine));
			withEngine.SetPhase("engine.port.overspeed", AlarmPhase.Acknowledged);
			view.Refresh(withEngine, 0.2);
			Assert.That(list.Rows.Select(row => row.Text), Is.EqualTo(new[] { "ENGINE", "PORT OVERSPEED" }));
			Assert.That(list.Rows[1].Lit, Is.True);
			Assert.That(list.EmptyTextVisible, Is.False);
		}

		[Test]
		public void EachRow_FollowsItsAlarm_ByLampAndByWord()
		{
			var panel = Panel();
			var readout = Shuffled();
			readout.SetPhase("steering.failure", AlarmPhase.Unacknowledged);
			readout.SetPhase("hull.contact", AlarmPhase.RingBack);
			readout.SetPhase("navigation.shallow-water", AlarmPhase.Acknowledged);
			panel.Refresh(readout, 0.6);

			var byText = panel.List.Rows.Where(row => !row.IsHeader).ToDictionary(row => row.Text);
			Assert.That(byText["FAILURE"].State, Is.EqualTo(LampState.Flashing));
			Assert.That(byText["FAILURE"].Lit, Is.False, "1 Hz flash is in its dark half at 0.6 s");
			Assert.That(byText["FAILURE"].Status, Is.EqualTo("ALARM"));
			Assert.That(byText["SHALLOW WATER"].State, Is.EqualTo(LampState.Steady));
			Assert.That(byText["SHALLOW WATER"].Lit, Is.True);
			Assert.That(byText["SHALLOW WATER"].Status, Is.EqualTo("ACK"));
			Assert.That(byText["CONTACT"].State, Is.EqualTo(LampState.SlowFlash));
			Assert.That(byText["CONTACT"].Lit, Is.True);
			Assert.That(byText["CONTACT"].Status, Is.EqualTo("CLEARED"));
			Assert.That(byText["GROUNDING"].State, Is.EqualTo(LampState.Off));
			Assert.That(byText["GROUNDING"].Lit, Is.False);
			Assert.That(byText["GROUNDING"].Status, Is.EqualTo(""));
		}

		[Test]
		public void LampTest_LightsEveryRow()
		{
			var panel = Panel();
			var readout = Shuffled();
			readout.LampTest = true;
			panel.Refresh(readout, 0.6);

			foreach (var row in panel.List.Rows.Where(row => !row.IsHeader))
			{
				Assert.That(row.Lit, Is.True, row.Text);
				Assert.That(row.State, Is.EqualTo(LampState.Test), row.Text);
				Assert.That(row.Status, Is.EqualTo("TEST"), row.Text);
			}
		}

		[Test]
		public void TheButtons_RaiseAcknowledgeSilenceAndAHeldLampTest()
		{
			var panel = Panel();
			var events = new List<string>();
			panel.AcknowledgeRequested += () => events.Add("ack");
			panel.SilenceRequested += () => events.Add("silence");
			panel.LampTestChanged += on => events.Add("lamp-test " + on);

			panel.PressSoftkey(2);
			panel.ReleaseSoftkey(2);
			panel.PressSoftkey(3);
			panel.ReleaseSoftkey(3);
			panel.PressSoftkey(4);
			Assert.That(events.Last(), Is.EqualTo("lamp-test True"), "lamp test holds while pressed");
			panel.ReleaseSoftkey(4);
			panel.ReleaseSoftkey(4);

			Assert.That(events, Is.EqualTo(new[] { "ack", "silence", "lamp-test True", "lamp-test False" }));
		}

		[Test]
		public void Connect_ForwardsTheButtonsToTheInstrumentSource()
		{
			var panel = Panel();
			var source = new RecordingSource();
			panel.Connect(source);
			panel.PressSoftkey(2);
			panel.ReleaseSoftkey(2);
			panel.PressSoftkey(3);
			panel.ReleaseSoftkey(3);
			panel.PressSoftkey(4);
			panel.ReleaseSoftkey(4);
			panel.Disconnect();
			panel.PressSoftkey(2);
			panel.ReleaseSoftkey(2);

			Assert.That(source.Calls, Is.EqualTo(new[] { "ack", "silence", "lamp-test True", "lamp-test False" }));
		}

		[Test]
		public void Disconnect_ReleasesAHeldLampTest()
		{
			var panel = Panel();
			var source = new RecordingSource();
			panel.Connect(source);
			panel.PressSoftkey(4);
			panel.Disconnect();
			Assert.That(source.Calls, Is.EqualTo(new[] { "lamp-test True", "lamp-test False" }));
		}

		[Test]
		public void AnAlarmPanel_NeedsAnAlarmPanelSkin()
		{
			var engine = DisplaySkin.Parse(EveryElementType(), TClassAlarms);
			Assert.Throws<System.ArgumentException>(() => new AlarmPanelView(engine));
			var panelSkin = DisplaySkin.Parse(PanelSkin(), TClassAlarms);
			Assert.Throws<System.ArgumentException>(() => new EngineMonitorView(DisplaySide.Port, panelSkin));
			Assert.Throws<System.ArgumentException>(() => new EngineMonitorView(DisplaySide.None, engine));
		}

		[Test]
		public void AnEngineMonitor_PicksItsSide()
		{
			var engine = DisplaySkin.Parse(EveryElementType(), TClassAlarms);
			var port = new EngineMonitorView(DisplaySide.Port, engine);
			var starboard = new EngineMonitorView(DisplaySide.Starboard, engine);
			Assert.That(port.Side, Is.EqualTo(DisplaySide.Port));
			Assert.That(starboard.Side, Is.EqualTo(DisplaySide.Starboard));
			Assert.That(port.name, Is.Not.EqualTo(starboard.name));
		}

		private sealed class RecordingSource : IInstrumentSource
		{
			public readonly List<string> Calls = new List<string>();

			public bool HasInstruments => true;

			public IInstrumentReadout Instruments { get; } = FakeInstrumentReadout.TClass();

			public void AcknowledgeAlarms() => Calls.Add("ack");

			public void SilenceAlarms() => Calls.Add("silence");

			public void SetLampTest(bool on) => Calls.Add("lamp-test " + on);
		}
	}
}
