using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UIElements;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// The CPA alarm on the wheelhouse panel and traffic events in the debrief. Failure modes guarded: the strip shown on a run
	/// without traffic, or hidden on one with it; the wrong state, CPA, TCPA sign or bearing shown; an alarm not filled in the
	/// alarm colour, or flashing; garbage per refresh; the debrief missing a traffic alarm or collision, pairing an end with the
	/// wrong start, or inventing an end for an episode the run ended inside; a start alongside missing from the debrief, shown as
	/// an alarm or a collision, or given an end it never had; an alarm still up at the end shown with the CPA it rose at as its
	/// minimum.
	/// </summary>
	public class TrafficPanelTests
	{
		private sealed class Source : IVesselTelemetrySource, ITrafficReadoutSource
		{
			public VesselParameters Parameters => null;
			public VesselState State { get; set; } = VesselState.Planar(12.0, 0.0, 0.0, 0.0, 3.0);
			public VesselTelemetry Telemetry => default;
			public ControlInputs Controls => ControlInputs.Neutral;
			public DateTimeOffset ScenarioStartUtc => new DateTimeOffset(2025, 7, 14, 16, 0, 0, TimeSpan.Zero);
			public bool HasTelemetry { get; set; } = true;
			public long RunId => 7;
			public ContactLatch Contact => ContactLatch.None;
			public bool TrafficEnabled { get; set; } = true;
			public bool HasTraffic { get; set; } = true;
			public TrafficAlarmReadout TrafficAlarm { get; set; }
		}

		private sealed class Plain : IVesselTelemetrySource
		{
			public VesselParameters Parameters => null;
			public VesselState State => VesselState.Planar(12.0, 0.0, 0.0, 0.0, 3.0);
			public VesselTelemetry Telemetry => default;
			public ControlInputs Controls => ControlInputs.Neutral;
			public DateTimeOffset ScenarioStartUtc => new DateTimeOffset(2025, 7, 14, 16, 0, 0, TimeSpan.Zero);
			public bool HasTelemetry => true;
			public long RunId => 8;
			public ContactLatch Contact => ContactLatch.None;
		}

		private static readonly TrafficVessel Tug = new TrafficVessel(0, 366000001, "HARBOR TUG", 52, TrafficClass.TugTow, 28.0, 9.0, 3.0, 0.0, 100.0, 2, 1, true, false);

		private static TrafficAlarmReadout Readout(TrafficAlarmState state, double cpa, double tcpa, double bearingDegrees, int alarms = 1) =>
			new TrafficAlarmReadout(state, alarms, state == TrafficAlarmState.Collision ? 1 : 0, state == TrafficAlarmState.None ? null : Tug,
				new Encounter(400.0, Angles.ToRadians(bearingDegrees), cpa, tcpa, 5.0), TrafficAlarmLimits.Default);

		[Test]
		public void TheStrip_ShowsOnlyOnRunsWithTraffic()
		{
			var panel = new WheelhousePanel();
			panel.Refresh(new Plain(), null, DistanceUnit.Metres, 1.5, 0.0);
			Assert.That(panel.Traffic.Showing, Is.False, "a source with no traffic at all");
			Assert.That(panel.Traffic.style.display.value, Is.EqualTo(DisplayStyle.None));
			var source = new Source { TrafficEnabled = false };
			panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.0);
			Assert.That(panel.Traffic.Showing, Is.False, "a run without traffic");
			source.TrafficEnabled = true;
			source.TrafficAlarm = Readout(TrafficAlarmState.Clear, 350.0, 80.0, 45.0, 0);
			panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.0);
			Assert.That(panel.Traffic.Showing, Is.True);
			Assert.That(panel.Traffic.parent, Is.SameAs(panel), "docked to the panel itself, not inside an instrument column");
		}

		[Test]
		public void TheStrip_SaysTheStateCpaTcpaAndBearing()
		{
			var panel = new WheelhousePanel();
			panel.ApplyPalette(InstrumentPalette.Day);
			var source = new Source();
			void Show(TrafficAlarmReadout readout, DistanceUnit unit = DistanceUnit.Metres)
			{
				source.TrafficAlarm = readout;
				panel.Refresh(source, null, unit, 1.5, 0.0);
			}

			Show(Readout(TrafficAlarmState.Alarm, 45.2, 72.4, 359.6));
			Assert.That(panel.Traffic.StateText, Is.EqualTo("ALARM"));
			Assert.That(panel.Traffic.ValueText, Is.EqualTo("CPA 45.2 m  TCPA 1:12  BRG 000°T"));
			Assert.That(panel.Traffic.NameText, Is.EqualTo("HARBOR TUG"));
			Assert.That(panel.Traffic.style.backgroundColor.value, Is.EqualTo(InstrumentPalette.Day.Alarm), "filled in the alarm colour");
			Show(Readout(TrafficAlarmState.Alarm, 45.2, 72.4, 359.6, 3));
			Assert.That(panel.Traffic.StateText, Is.EqualTo("ALARM ×3"));
			Show(Readout(TrafficAlarmState.Collision, 2.0, 0.4, 90.0));
			Assert.That(panel.Traffic.StateText, Is.EqualTo("COLLISION"));
			Show(Readout(TrafficAlarmState.Clear, 250.0, -15.0, 180.0, 0), DistanceUnit.Feet);
			Assert.That(panel.Traffic.StateText, Is.EqualTo("CLEAR"));
			Assert.That(panel.Traffic.ValueText, Is.EqualTo("CPA 820 ft  TCPA PAST  BRG 180°T"), "the depth unit, and PAST once the closest point is behind");
			Assert.That(panel.Traffic.style.backgroundColor.value, Is.Not.EqualTo(InstrumentPalette.Day.Alarm));
			Show(Readout(TrafficAlarmState.None, double.NaN, double.NaN, 0.0, 0));
			Assert.That(panel.Traffic.StateText, Is.EqualTo("NO TRAFFIC NEAR"));
			source.HasTraffic = false;
			panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.0);
			Assert.That(panel.Traffic.ValueText, Does.Contain("---"), "no reading of the latest step: no values");
			Assert.That(TrafficReadout.Tcpa(6000.0), Is.EqualTo(">99 min"));
			Assert.That(TrafficReadout.Tcpa(59.5), Is.EqualTo("1:00"));
		}

		[Test]
		public void ARefresh_MakesNoGarbage_WhileWhatItShowsHolds()
		{
			var panel = new WheelhousePanel();
			var source = new Source { TrafficAlarm = Readout(TrafficAlarmState.Alarm, 45.2, 72.4, 30.0) };
			panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.0);
			panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.1);
			Assert.That(() => panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.2), Is.Not.AllocatingGCMemory());
			source.TrafficAlarm = Readout(TrafficAlarmState.Alarm, 45.21, 72.41, 30.01);
			Assert.That(() => panel.Refresh(source, null, DistanceUnit.Metres, 1.5, 0.3), Is.Not.AllocatingGCMemory(), "a change below the shown resolution");
		}

		private static TrafficEventLogFile Log()
		{
			var vessel = Tug;
			var other = new TrafficVessel(1, 366000002, "", 0, TrafficClass.Other, double.NaN, double.NaN, double.NaN, 0.0, 100.0, 2, 1, true, false);
			var rows = new List<string>
			{
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlarmRaised, 1000, 1000, 1000, 0, vessel.Mmsi, new Encounter(900, 0.1, 40, 90, 10), 900, 40), vessel),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlarmRaised, 1500, 1500, 1500, 1, other.Mmsi, new Encounter(700, 2.0, 80, 100, 6), 700, 80), other),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.CollisionStarted, 9700, 9700, 9700, 0, vessel.Mmsi, new Encounter(28, 0.1, 1, 2.8, 10), 28, 1), vessel),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlarmCleared, 10200, 1000, 10000, 0, vessel.Mmsi, new Encounter(20, 3.1, 1, -1, 10), 0.5, 0.25), vessel),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.CollisionEnded, 10372, 9700, 10272, 0, vessel.Mmsi, new Encounter(27, 3.1, 1, -2, 10), 0.5, 0.25), vessel),
			};
			var text = TrafficEventLog.FormatHeader(new[] { new KeyValuePair<string, string>("cpa_alarm_m", "100"), new KeyValuePair<string, string>("tcpa_alarm_s", "120") }) +
				string.Join("\n", rows) + "\n" + TrafficEventLog.FormatTrailer(rows.Count, 12000, "fnv1a64:0000000000000000");
			return TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "test.traffic.csv");
		}

		private static TelemetryRecording Recording()
		{
			var builder = new TelemetryRecordingBuilder("traffic test", new[] { new KeyValuePair<string, string>("vessel", "t-class") });
			for (var i = 0; i <= 120; i++)
			{
				builder.Add(VesselState.Planar(i, 5.0 * i, 0.0, 0.0, 5.0), default, ControlInputs.Neutral);
			}

			return builder.Build();
		}

		[Test]
		public void TheDebrief_ShowsTrafficAlarmsAndCollisions_FromTheLog()
		{
			var events = DebriefEvents.Detect(Recording(), 1.5, Log());
			var alarms = events.Where(e => e.Kind == DebriefEventKind.TrafficAlarm).ToList();
			var collisions = events.Where(e => e.Kind == DebriefEventKind.TrafficCollision).ToList();
			Assert.That(alarms.Count, Is.EqualTo(2));
			Assert.That(collisions.Count, Is.EqualTo(1));
			Assert.That((alarms[0].Mmsi, alarms[0].Time, alarms[0].EndTime, alarms[0].Value), Is.EqualTo((366000001, 10.0, 102.0, 0.25)), "raised to cleared, its least CPA");
			Assert.That(double.IsNaN(alarms[1].EndTime), Is.True, "an alarm still up when the run ended has no end");
			Assert.That(alarms[1].VesselName, Is.Empty);
			Assert.That((collisions[0].Time, collisions[0].EndTime, collisions[0].Value), Is.EqualTo((97.0, 102.73, 10.0)), "first overlap to the step after the last, at the closing speed");
			Assert.That(alarms[0].Describe(DistanceUnit.Metres), Is.EqualTo("CPA ALARM, HARBOR TUG (tug-tow, MMSI 366000001), min CPA 0.3 m"));
			Assert.That(alarms[1].Describe(DistanceUnit.Metres), Does.StartWith("CPA ALARM, unnamed (other, MMSI 366000002)"));
			Assert.That(collisions[0].Describe(DistanceUnit.Metres), Is.EqualTo("COLLISION with HARBOR TUG (tug-tow, MMSI 366000001) at 19.4 kn"));
			Assert.That(DebriefEvents.Detect(Recording(), 1.5).Any(e => e.Kind == DebriefEventKind.TrafficAlarm || e.Kind == DebriefEventKind.TrafficCollision), Is.False, "none without a log");
			var screen = new DebriefScreen();
			screen.Show(Recording(), null, Log());
			Assert.That(screen.Traffic, Is.Not.Null);
			Assert.That(screen.Events.Count(e => e.Kind == DebriefEventKind.TrafficAlarm), Is.EqualTo(2));
			Assert.That(screen.Timeline.Events.Count(e => e.Kind == DebriefEventKind.TrafficCollision), Is.EqualTo(1), "on the timeline");
		}

		[Test]
		public void TheDebrief_ShowsAStartAlongside_UntilTheyParted_AndNotAsAnAlarmOrCollision()
		{
			var berthed = new TrafficVessel(0, 367434220, "BERTHED FERRY", 60, TrafficClass.FerryPassenger, 25.0, 8.0, 2.0, 0.0, 100.0, 2, 1, true, false);
			var stays = new TrafficVessel(1, 367000009, "STAYS", 60, TrafficClass.FerryPassenger, 26.0, 8.0, 2.0, 0.0, 100.0, 2, 1, true, false);
			var rows = new List<string>
			{
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlongsideStarted, 1, 1, 1, 0, berthed.Mmsi, new Encounter(16.5, 0.05, 11.6, -150.0, 0.08), 16.5, 11.6), berthed),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlongsideStarted, 1, 1, 1, 1, stays.Mmsi, new Encounter(12.0, 1.5, 12.0, 0.0, 0.0), 12.0, 12.0), stays),
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlongsideEnded, 5221, 1, 5220, 0, berthed.Mmsi, new Encounter(21.0, 0.4, 20.0, -3.0, 0.5), 15.2, 11.6), berthed),
			};
			var text = TrafficEventLog.FormatHeader(TrafficEventLog.LimitsHeader(TrafficAlarmLimits.Default)) + string.Join("\n", rows) + "\n" +
				TrafficEventLog.FormatTrailer(rows.Count, 12000, "fnv1a64:0000000000000000");
			var log = TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "alongside.traffic.csv");
			var events = DebriefEvents.Detect(Recording(), 1.5, log);
			Assert.That(events.Any(e => e.Kind == DebriefEventKind.TrafficAlarm || e.Kind == DebriefEventKind.TrafficCollision), Is.False);
			var alongside = events.Where(e => e.Kind == DebriefEventKind.TrafficAlongside).ToDictionary(e => e.Mmsi);
			Assert.That(alongside.Count, Is.EqualTo(2));
			var parted = alongside[berthed.Mmsi];
			Assert.That((parted.Time, parted.EndTime, parted.Value), Is.EqualTo((0.01, 52.21, 15.2)), "from the first step to the step they parted, the least range between");
			Assert.That(parted.Describe(DistanceUnit.Metres), Is.EqualTo("ALONGSIDE AT START, BERTHED FERRY (ferry-passenger, MMSI 367434220), parted after 52.2 s"));
			Assert.That(double.IsNaN(alongside[stays.Mmsi].EndTime), Is.True, "still alongside when the run ended");
			Assert.That(alongside[stays.Mmsi].Describe(DistanceUnit.Metres), Is.EqualTo("ALONGSIDE AT START, STAYS (ferry-passenger, MMSI 367000009), never parted"));
			var screen = new DebriefScreen();
			screen.Show(Recording(), null, log);
			Assert.That(screen.Timeline.Events.Count(e => e.Kind == DebriefEventKind.TrafficAlongside), Is.EqualTo(2), "on the timeline");
		}

		/// <summary>
		/// The wave 2b review's case: an alarm rising at a CPA of 79.95 m and still up at 7.10 m when the recording ends; with the
		/// recorder's open snapshot, or as an older sidecar without one.
		/// </summary>
		private static TrafficEventLogFile UnfinishedAlarm(bool snapshot)
		{
			var rows = new List<string>
			{
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlarmRaised, 1, 1, 1, 0, Tug.Mmsi, new Encounter(500.0, 0.16, 79.954982333162, 49.0, 10.2), 500.0, 79.954982333162), Tug),
			};
			var open = new List<string>
			{
				TrafficEventLog.FormatRow(new TrafficEvent(TrafficEventKind.AlarmOpen, 2000, 1, 2000, 0, Tug.Mmsi, new Encounter(120.0, 0.6, 7.095661385001285, 11.0, 10.0), 120.0, 7.095661385001285), Tug),
			};
			var text = TrafficEventLog.FormatHeader(TrafficEventLog.LimitsHeader(TrafficAlarmLimits.Default)) + string.Join("\n", rows) + "\n" +
				(snapshot ? string.Join("\n", open) + "\n" + TrafficEventLog.FormatTrailer(rows.Count, open.Count, 2000, "fnv1a64:0000000000000000") : TrafficEventLog.FormatTrailer(rows.Count, 2000, "fnv1a64:0000000000000000"));
			return TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "unfinished.traffic.csv");
		}

		[Test]
		public void AnAlarmStillUpAtTheEnd_ShowsItsMinimumSoFar_OrSaysItsValueIsTheStartingCpa()
		{
			var alarm = DebriefEvents.Detect(Recording(), 1.5, UnfinishedAlarm(snapshot: true)).Single(e => e.Kind == DebriefEventKind.TrafficAlarm);
			Assert.That(double.IsNaN(alarm.EndTime), Is.True, "still up when the recording ended");
			Assert.That((alarm.Value, alarm.ValueAtStart), Is.EqualTo((7.095661385001285, false)), "the least CPA while it was up, from the open snapshot");
			Assert.That(alarm.Describe(DistanceUnit.Metres), Is.EqualTo("CPA ALARM, HARBOR TUG (tug-tow, MMSI 366000001), min CPA 7.1 m"));
			var older = DebriefEvents.Detect(Recording(), 1.5, UnfinishedAlarm(snapshot: false)).Single(e => e.Kind == DebriefEventKind.TrafficAlarm);
			Assert.That((older.Value, older.ValueAtStart), Is.EqualTo((79.954982333162, true)), "an older sidecar has no minimum: the CPA it rose at");
			Assert.That(older.Describe(DistanceUnit.Metres), Is.EqualTo("CPA ALARM, HARBOR TUG (tug-tow, MMSI 366000001), starting CPA 80.0 m"), "labelled as such, never as a minimum");
		}
	}
}
