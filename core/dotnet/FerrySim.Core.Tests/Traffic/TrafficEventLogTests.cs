using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// The traffic event log and the hull sizes. Failure modes guarded: a value that does not read back bit for bit; a name with
	/// a comma or quotes splitting the row; a malformed log accepted, or refused without naming it; the open-episode snapshot
	/// mixed into the events, or an older log without it misread; a hull sized from an implausible AIS dimension, or one without
	/// the class's documented default.
	/// </summary>
	[TestFixture]
	public class TrafficEventLogTests
	{
		[Test]
		public void ARow_ReadsBackEveryValue_BitForBit()
		{
			var vessel = new TrafficVessel(3, 366999001, "TUG \"BIG\", JR", 52, TrafficClass.TugTow, 28.9, 9.1, 3.3, 0, 100, 2, 1, true, false);
			var encounter = new Encounter(812.3456789012345, 1.2345678901234567, 45.678901234567891, 61.234567890123456, 7.0000000000000009);
			var e = new TrafficEvent(TrafficEventKind.AlarmCleared, 12345, 11000, 12145, 3, 366999001, encounter, 33.333333333333336, -0.0);
			var text = TrafficEventLog.FormatHeader(new[] { new KeyValuePair<string, string>("traffic_pack", "2025-07-14") }) +
				TrafficEventLog.FormatRow(e, vessel) + "\n" + TrafficEventLog.FormatTrailer(1, 13000, "fnv1a64:0123456789abcdef");
			var log = TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "test.traffic.csv");
			Assert.That(log.Header["traffic_pack"], Is.EqualTo("2025-07-14"));
			Assert.That(log.Digest, Is.EqualTo("fnv1a64:0123456789abcdef"));
			Assert.That(log.Trailer["steps"], Is.EqualTo("13000"));
			var row = log.Records[0];
			Assert.That((row.Kind, row.Step, row.StartStep, row.LastStep, row.Mmsi), Is.EqualTo((TrafficEventKind.AlarmCleared, 12345L, 11000L, 12145L, 366999001)));
			Assert.That(row.Time, Is.EqualTo(123.45));
			Assert.That(BitConverter.DoubleToInt64Bits(row.Range), Is.EqualTo(BitConverter.DoubleToInt64Bits(encounter.Range)));
			Assert.That(BitConverter.DoubleToInt64Bits(row.Bearing), Is.EqualTo(BitConverter.DoubleToInt64Bits(encounter.Bearing)));
			Assert.That(BitConverter.DoubleToInt64Bits(row.Cpa), Is.EqualTo(BitConverter.DoubleToInt64Bits(encounter.Cpa)));
			Assert.That(BitConverter.DoubleToInt64Bits(row.Tcpa), Is.EqualTo(BitConverter.DoubleToInt64Bits(encounter.Tcpa)));
			Assert.That(BitConverter.DoubleToInt64Bits(row.RelativeSpeed), Is.EqualTo(BitConverter.DoubleToInt64Bits(encounter.RelativeSpeed)));
			Assert.That(row.MinimumCpa, Is.EqualTo(0.0), "−0 is written as 0");
			Assert.That((row.ClassName, row.Length, row.Beam, row.Name), Is.EqualTo(("tug-tow", 28.9, 9.1, "TUG \"BIG\", JR")));
			Assert.That(row.Line, Is.EqualTo(TrafficEventLog.FormatRow(e, vessel)), "the row exactly as written");
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.CollisionStarted), Is.EqualTo("collision"));
		}

		[Test]
		public void AlongsideRows_AndTheMargin_ReadBack()
		{
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.AlongsideStarted), Is.EqualTo("alongside"));
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.AlongsideEnded), Is.EqualTo("alongside-end"));
			var limits = TrafficEventLog.LimitsHeader(TrafficAlarmLimits.Default);
			Assert.That(limits.ConvertAll(p => p.Key), Is.EqualTo(new[] { "cpa_alarm_m", "tcpa_alarm_s", "closing_speed_mps", "alarm_clear_hold_s", "collision_hold_s", "alongside_margin_m" }));
			Assert.That(limits[5].Value, Is.EqualTo("5"));
			var vessel = new TrafficVessel(0, 367434220, "BERTHED", 60, TrafficClass.FerryPassenger, 25.0, 8.0, 2.0, 0, 100, 2, 1, true, false);
			var encounter = new Encounter(16.5, 0.05, 16.4, 3.9, 0.31);
			var started = new TrafficEvent(TrafficEventKind.AlongsideStarted, 1, 1, 1, 0, 367434220, encounter, 16.5, 16.4);
			var ended = new TrafficEvent(TrafficEventKind.AlongsideEnded, 5216, 1, 5215, 0, 367434220, encounter, 15.2, 15.1);
			var text = TrafficEventLog.FormatHeader(limits) + TrafficEventLog.FormatRow(started, vessel) + "\n" + TrafficEventLog.FormatRow(ended, vessel) + "\n";
			var log = TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "alongside.traffic.csv");
			Assert.That(log.Header["alongside_margin_m"], Is.EqualTo("5"));
			Assert.That(log.Records.Select(r => (r.Kind, r.Step, r.StartStep, r.LastStep)).ToArray(), Is.EqualTo(new[] { (TrafficEventKind.AlongsideStarted, 1L, 1L, 1L), (TrafficEventKind.AlongsideEnded, 5216L, 1L, 5215L) }));
			Assert.That(log.Records[0].Line, Does.StartWith("1,0.01,alongside,367434220,"));
		}

		[Test]
		public void OpenEpisodes_ReadBackApartFromTheEvents_AndAnOlderLogHasNone()
		{
			var vessel = new TrafficVessel(0, 200000007, "TARGET", 0, TrafficClass.Other, 10.0, 3.0, 1.0, 0, 100, 2, 1, true, false);
			var raised = new TrafficEvent(TrafficEventKind.AlarmRaised, 1, 1, 1, 0, vessel.Mmsi, new Encounter(500.0, 0.16, 79.954982333162, 49.0, 10.2), 500.0, 79.954982333162);
			var open = new TrafficEvent(TrafficEventKind.AlarmOpen, 2000, 1, 2000, 0, vessel.Mmsi, new Encounter(120.0, 0.6, 7.095661385001285, 11.0, 10.0), 120.0, 7.095661385001285);
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.AlarmOpen), Is.EqualTo("alarm-open"));
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.CollisionOpen), Is.EqualTo("collision-open"));
			Assert.That(TrafficEventLog.KindName(TrafficEventKind.AlongsideOpen), Is.EqualTo("alongside-open"));
			var header = TrafficEventLog.FormatHeader(new KeyValuePair<string, string>[0]);
			var text = header + TrafficEventLog.FormatRow(raised, vessel) + "\n" + TrafficEventLog.FormatRow(open, vessel) + "\n" + TrafficEventLog.FormatTrailer(1, 1, 2000, "fnv1a64:0123456789abcdef");
			var log = TrafficEventLog.Parse(text.TrimEnd('\n').Split('\n'), "open.traffic.csv");
			Assert.That(log.Records.Select(r => r.Kind).ToArray(), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised }), "the events only");
			Assert.That(log.HasOpenSnapshot, Is.True);
			Assert.That(log.Trailer["open_episodes"], Is.EqualTo("1"));
			Assert.That(log.Open.Count, Is.EqualTo(1));
			Assert.That((log.Open[0].Kind, log.Open[0].Step, log.Open[0].StartStep, log.Open[0].MinimumCpa), Is.EqualTo((TrafficEventKind.AlarmOpen, 2000L, 1L, 7.095661385001285)));
			Assert.That(log.Open[0].Line, Is.EqualTo(TrafficEventLog.FormatRow(open, vessel)));
			// A log from before the snapshot: no open line in its trailer, no open rows.
			var older = TrafficEventLog.Parse((header + TrafficEventLog.FormatRow(raised, vessel) + "\n" + TrafficEventLog.FormatTrailer(1, 2000, "fnv1a64:0123456789abcdef")).TrimEnd('\n').Split('\n'), "older.traffic.csv");
			Assert.That((older.HasOpenSnapshot, older.Open.Count, older.Records.Count), Is.EqualTo((false, 0, 1)));
			// The snapshot closes the rows: an event after it is refused.
			var error = Assert.Throws<FormatException>(() => TrafficEventLog.Parse((header + TrafficEventLog.FormatRow(open, vessel) + "\n" + TrafficEventLog.FormatRow(raised, vessel)).Split('\n'), "late.traffic.csv"));
			Assert.That(error.Message, Does.Contain("late.traffic.csv").And.Contain("after the open episodes"));
		}

		[Test]
		public void MalformedLogs_AreRefused_NamingTheLog()
		{
			var good = TrafficEventLog.FormatHeader(new KeyValuePair<string, string>[0]).TrimEnd('\n').Split('\n');
			Assert.That(TrafficEventLog.Parse(good, "x").Records, Is.Empty);
			Assert.That(TrafficEventLog.Parse(good, "x").Digest, Is.Null, "a log cut short has no digest");
			var error = Assert.Throws<FormatException>(() => TrafficEventLog.Parse(new[] { "step,time_s" }, "run.traffic.csv"));
			Assert.That(error.Message, Does.StartWith("run.traffic.csv: not a traffic event log"));
			error = Assert.Throws<FormatException>(() => TrafficEventLog.Parse(new[] { good[0], good[1], "1,0.01,crash,1,1,1,1,1,1,1,1,1,1,other,1,1,X" }, "run.traffic.csv"));
			Assert.That(error.Message, Does.Contain("line 3").And.Contain("event 'crash'").And.Contain("alongside, alongside-end, alarm-open, collision-open or alongside-open"));
			error = Assert.Throws<FormatException>(() => TrafficEventLog.Parse(new[] { good[0], good[1], "1,0.01,alarm,1,1" }, "run.traffic.csv"));
			Assert.That(error.Message, Does.Contain("has 5 fields"));
			error = Assert.Throws<FormatException>(() => TrafficEventLog.Parse(new[] { good[0], good[1], "1,0.01,alarm,1,1,1,1,1,1,1,1,1,1,other,1,1,\"OPEN" }, "run.traffic.csv"));
			Assert.That(error.Message, Does.Contain("unclosed quote"));
		}

		[Test]
		public void HullSizes_UseAisWherePlausible_ElseTheClassDefaults()
		{
			var both = TrafficHullSize.For(TrafficClass.Cargo, 294.0, 32.0, 12.0);
			Assert.That((both.Length, both.Beam, both.Draft, both.LengthFromAis, both.BeamFromAis, both.DraftFromAis), Is.EqualTo((294.0, 32.0, 12.0, true, true, true)));
			var noBeam = TrafficHullSize.For(TrafficClass.TugTow, 28.0, double.NaN, double.NaN);
			Assert.That((noBeam.Length, noBeam.BeamFromAis), Is.EqualTo((28.0, false)));
			Assert.That(noBeam.Beam, Is.EqualTo(10.0).Within(1e-12), "beam from the tug's 2.8 length-to-beam ratio");
			Assert.That(noBeam.Draft, Is.EqualTo(3.5), "the tug's default draft");
			var noLength = TrafficHullSize.For(TrafficClass.Pleasure, double.NaN, 3.0, 1.0);
			Assert.That((noLength.Length, noLength.Beam), Is.EqualTo((9.0, 3.0)));
			var none = TrafficHullSize.For(TrafficClass.FerryPassenger, double.NaN, double.NaN, double.NaN);
			Assert.That((none.Length, none.Beam, none.LengthFromAis, none.BeamFromAis), Is.EqualTo((26.0, 8.0, false, false)), "the harbor ferry default");
			var silly = TrafficHullSize.For(TrafficClass.Other, 1.0, 60.0, 30.0);
			Assert.That((silly.Length, silly.LengthFromAis), Is.EqualTo((210.0, false)), "a 1 m length is implausible; the beam stands and the length follows from it");
			var wide = TrafficHullSize.For(TrafficClass.Other, 20.0, 25.0, 1.0);
			Assert.That((wide.Length, wide.BeamFromAis), Is.EqualTo((20.0, false)), "a beam wider than the length is refused");
			Assert.That(wide.Beam, Is.EqualTo(20.0 / 3.5).Within(1e-12));
			Assert.That(TrafficHullSize.Default(TrafficClass.Military).Length, Is.EqualTo(30.0));
			Assert.That(new TrafficHullSize(30, 40, 1, true, true, true).Radius, Is.EqualTo(25.0));
		}
	}
}
