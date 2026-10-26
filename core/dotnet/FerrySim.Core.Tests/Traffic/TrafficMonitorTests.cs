using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Tests.Dynamics;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// The per-step monitor: CPA alarms and collisions as events. Failure modes guarded: no alarm before a head-on collision;
	/// a collision missed (one step of overlap between two others) or doubled (a graze that touches, parts for under a second and
	/// touches again); an alarm that chatters at its edge; the left-out vessel alarming; events out of order; a digest that does
	/// not follow what the monitor saw; garbage per step. Alongside at start: a vessel the learner starts beside counted as a
	/// collision or an alarm; alongside decided after the first step, or started again after the two parted; a vessel that parts
	/// and comes back not counted as a collision; alongside ended while the vessel is merely absent. The open snapshot: an episode
	/// still open at the end reported with its starting value instead of its minimum so far, missed, or ended by the snapshot.
	/// </summary>
	[TestFixture]
	public class TrafficMonitorTests
	{
		private sealed class Collect : ITrafficEventSink
		{
			public readonly List<TrafficEvent> Events = new List<TrafficEvent>();

			public void OnTrafficEvent(in TrafficEvent trafficEvent) => Events.Add(trafficEvent);
		}

		/// <summary>The learner north at 5 m/s from the origin; one vessel heading south at 5 m/s from 1000 m north, from pack time 0.</summary>
		private static TrafficPack HeadOn() => TrafficFixtures.Pack(TrafficFixtures.Straight(200000001, 0.0, 1000.0, 0.0, 5.0, Math.PI, 5));

		private static VesselState Learner(long step) => VesselState.Planar(SimulationClock.TimeAtStep(step), 5.0 * SimulationClock.TimeAtStep(step), 0.0, 0.0, 5.0);

		[Test]
		public void AHeadOnApproach_RaisesTheAlarm_LongBeforeTheCollision_AndEachHappensOnce()
		{
			var pack = HeadOn();
			var outline = HullOutline.FromContact(ManeuverRunner.Load("t-class.json"));
			var monitor = new TrafficMonitor(new TrafficReplay(pack), 0.0, -1, outline, TrafficAlarmLimits.Default);
			var sink = new Collect();
			for (long step = 1; step <= 12000; step++)
			{
				monitor.Update(step, Learner(step), sink);
			}

			var kinds = sink.Events.ConvertAll(e => e.Kind);
			Assert.That(kinds, Is.EqualTo(new[] { TrafficEventKind.AlarmRaised, TrafficEventKind.CollisionStarted, TrafficEventKind.AlarmCleared, TrafficEventKind.CollisionEnded }));
			var alarm = sink.Events[0];
			var collision = sink.Events[1];
			var cleared = sink.Events[2];
			var ended = sink.Events[3];
			// Closing at 10 m/s from 1000 m: TCPA 100 s, inside the 120 s look-ahead from the first step.
			Assert.That(alarm.Step, Is.EqualTo(1));
			Assert.That(alarm.Tcpa, Is.EqualTo(99.99).Within(1e-6));
			Assert.That(alarm.Cpa, Is.EqualTo(0.0).Within(1e-3), "a float32 COG of π leaves 4e-7 m/s across the line: 4e-5 m at the CPA");
			Assert.That(Angles.WrapPi(alarm.Bearing), Is.EqualTo(0.0).Within(1e-9), "dead ahead (a hair to port reads just under 2π)");
			Assert.That(alarm.Bearing, Is.GreaterThanOrEqualTo(0.0).And.LessThan(2.0 * Math.PI));
			Assert.That(alarm.Mmsi, Is.EqualTo(200000001));
			// The T-class bow fender is 13.76 m forward and the tug's rectangle 15 m: the hulls meet with 28.76 m between centres.
			Assert.That(collision.Time, Is.EqualTo((1000.0 - 28.76) / 10.0).Within(0.011));
			Assert.That(collision.RelativeSpeed, Is.EqualTo(10.0).Within(1e-6), "the closing speed at impact");
			Assert.That(collision.Time - alarm.Time, Is.GreaterThan(90.0), "the alarm comes a minute and a half before the collision");
			// Traffic never pushes the hull: the overlap lasts until the stern (12.25 m aft) clears, 27.25 m past.
			Assert.That(SimulationClock.TimeAtStep(ended.LastStep), Is.EqualTo((1000.0 + 27.25) / 10.0).Within(0.011));
			Assert.That(ended.StartStep, Is.EqualTo(collision.Step));
			Assert.That(ended.Step, Is.EqualTo(ended.LastStep + 100), "ended a second after the last overlap");
			Assert.That(ended.MinimumRange, Is.LessThan(0.01), "they passed through each other");
			// The alarm holds 2 s after the last threatening step (TCPA reaches 0 at 100 s).
			Assert.That(SimulationClock.TimeAtStep(cleared.LastStep), Is.EqualTo(99.99).Within(1e-9));
			Assert.That(cleared.Step, Is.EqualTo(cleared.LastStep + 200));
			Assert.That(cleared.StartStep, Is.EqualTo(alarm.Step));
			Assert.That(monitor.AlarmsRaised, Is.EqualTo(1));
			Assert.That(monitor.CollisionsStarted, Is.EqualTo(1));
			Assert.That(monitor.EventCount, Is.EqualTo(4));
		}

		[Test]
		public void TheReadout_FollowsTheEncounter()
		{
			var pack = HeadOn();
			var monitor = new TrafficMonitor(new TrafficReplay(pack), 0.0, -1, HullOutline.FromContact(ManeuverRunner.Load("t-class.json")), TrafficAlarmLimits.Default);
			Assert.That(monitor.Readout.State, Is.EqualTo(TrafficAlarmState.None), "before the first step");
			monitor.Update(1, Learner(1), null);
			var readout = monitor.Readout;
			Assert.That(readout.State, Is.EqualTo(TrafficAlarmState.Alarm));
			Assert.That(readout.Target.Mmsi, Is.EqualTo(200000001));
			Assert.That(readout.Alarms, Is.EqualTo(1));
			Assert.That(readout.Range, Is.EqualTo(1000.0 - 0.1).Within(1e-6));
			Assert.That(readout.Tcpa, Is.EqualTo(99.99).Within(1e-6));
			Assert.That(readout.Limits.Cpa, Is.EqualTo(100.0));
			monitor.Update(9800, Learner(9800), null);
			Assert.That(monitor.Readout.State, Is.EqualTo(TrafficAlarmState.Collision));
			monitor.Update(12000, Learner(12000), null);
			Assert.That(monitor.Readout.State, Is.EqualTo(TrafficAlarmState.Clear), "past and opening, 200 m off: named but no threat");
			Assert.That(monitor.Readout.Tcpa, Is.LessThan(0.0));
			var far = new TrafficMonitor(new TrafficReplay(pack), 0.0, -1, HullOutline.FromContact(ManeuverRunner.Load("t-class.json")), TrafficAlarmLimits.Default);
			far.Update(1, VesselState.Planar(0.01, -5000.0, 0.0, 0.0), null);
			Assert.That(far.Readout.State, Is.EqualTo(TrafficAlarmState.None), "6 km off: nothing within a mile");
			Assert.That(far.Readout.Target, Is.Null);
			Assert.That(double.IsNaN(far.Readout.Cpa), Is.True);
		}

		[Test]
		public void AOneStepOverlap_IsACollision_AndAGrazeThatPartsBriefly_IsOne()
		{
			// A moored barge (static) east of the learner's track, and the learner's states scripted to touch it.
			var barge = new TrafficFixtures.Track { Mmsi = 200000002, Moving = false, Length = 40.0, Beam = 10.0 };
			for (var k = 0; k < 10; k++)
			{
				barge.Reports.Add(new TrafficFixtures.Report(60.0 * k, 0.0, 30.0, 0.0, double.NaN, 0.0));
			}

			var pack = TrafficFixtures.Pack(barge);
			var outline = TrafficFixtures.Box(20.0, 6.0);
			// Clear: the learner's side at east 3, the barge's at 25. Touching: the learner 22.5 m east, its side at 25.5.
			var monitor = new TrafficMonitor(new TrafficReplay(pack), 0.0, -1, outline, TrafficAlarmLimits.Default);
			var sink = new Collect();
			long step = 1;
			void Hold(double east, int steps)
			{
				for (var i = 0; i < steps; i++, step++)
				{
					monitor.Update(step, VesselState.Planar(SimulationClock.TimeAtStep(step), 0.0, east, 0.0), sink);
				}
			}

			Hold(0.0, 100);
			Hold(22.5, 1);
			Hold(0.0, 300);
			Assert.That(sink.Events.FindAll(e => e.Kind == TrafficEventKind.CollisionStarted).Count, Is.EqualTo(1), "a single step of overlap is caught");
			Assert.That(sink.Events.FindAll(e => e.Kind == TrafficEventKind.CollisionEnded).Count, Is.EqualTo(1));
			var first = sink.Events.Find(e => e.Kind == TrafficEventKind.CollisionStarted);
			Assert.That(first.Step, Is.EqualTo(101));

			sink.Events.Clear();
			// A graze: touch, part for half a second, touch again: one collision.
			Hold(22.5, 20);
			Hold(0.0, 50);
			Hold(22.5, 20);
			Hold(0.0, 300);
			Assert.That(sink.Events.FindAll(e => e.Kind == TrafficEventKind.CollisionStarted).Count, Is.EqualTo(1), "parting for under a second is the same collision");
			var end = sink.Events.Find(e => e.Kind == TrafficEventKind.CollisionEnded);
			Assert.That(end.LastStep - end.StartStep, Is.EqualTo(89), "from the first touch to the last overlapping step");

			sink.Events.Clear();
			// Parting for two seconds makes two.
			Hold(22.5, 20);
			Hold(0.0, 200);
			Hold(22.5, 20);
			Hold(0.0, 300);
			Assert.That(sink.Events.FindAll(e => e.Kind == TrafficEventKind.CollisionStarted).Count, Is.EqualTo(2));
			Assert.That(sink.Events.FindAll(e => e.Kind == TrafficEventKind.AlarmRaised), Is.Empty, "the scripted states have no velocity, so nothing was closing");
		}

		[Test]
		public void TheLeftOutVessel_NeitherAlarmsNorCollides()
		{
			var pack = HeadOn();
			var monitor = new TrafficMonitor(new TrafficReplay(pack), 0.0, 0, HullOutline.FromContact(ManeuverRunner.Load("t-class.json")), TrafficAlarmLimits.Default);
			var sink = new Collect();
			for (long step = 1; step <= 12000; step += 7)
			{
				monitor.Update(step, Learner(step), sink);
			}

			Assert.That(sink.Events, Is.Empty);
			Assert.That(monitor.Readout.State, Is.EqualTo(TrafficAlarmState.None));
		}

		[Test]
		public void TheDigest_IsTheSameForTheSameSteps_AndDiffersWhenTheTrafficDoes()
		{
			string Run(int removed, double offset)
			{
				var monitor = new TrafficMonitor(new TrafficReplay(HeadOn()), offset, removed, HullOutline.FromContact(ManeuverRunner.Load("t-class.json")), TrafficAlarmLimits.Default);
				for (long step = 1; step <= 11000; step++)
				{
					monitor.Update(step, Learner(step), null);
				}

				return monitor.Digest;
			}

			var a = Run(-1, 0.0);
			Assert.That(a, Does.StartWith("fnv1a64:").And.Length.EqualTo(24));
			Assert.That(Run(-1, 0.0), Is.EqualTo(a), "deterministic");
			Assert.That(Run(0, 0.0), Is.Not.EqualTo(a), "without the vessel");
			Assert.That(Run(-1, 1.0), Is.Not.EqualTo(a), "a second later in the pack");
		}

		/// <summary>A vessel 30 by 10 m creeping north at 0.3 m/s along east 7 from 20 m south: beside a 20 by 6 m box at the origin.</summary>
		private static TrafficPack Creeper() => TrafficFixtures.Pack(TrafficFixtures.Straight(200000005, 0.0, -20.0, 7.0, 0.3, 0.0, 5));

		/// <summary>A static barge 40 by 10 m centred 10 m east of the origin, heading north, reporting from 0 to 600 s.</summary>
		private static TrafficFixtures.Track Barge(params double[] times)
		{
			var barge = new TrafficFixtures.Track { Mmsi = 200000006, Moving = false, Length = 40.0, Beam = 10.0 };
			foreach (var t in times.Length > 0 ? times : new[] { 0.0, 60.0, 120.0, 180.0, 240.0, 300.0, 360.0, 420.0, 480.0, 540.0, 600.0 })
			{
				barge.Reports.Add(new TrafficFixtures.Report(t, 0.0, 10.0, 0.0, double.NaN, 0.0));
			}

			return barge;
		}

		private static VesselState Still(long step, double north, double east) => VesselState.Planar(SimulationClock.TimeAtStep(step), north, east, 0.0);

		[Test]
		public void AVesselOverlappingAtTheFirstStep_IsAlongside_AndNeverAlarmsOrCollides_WhileTheyStayTogether()
		{
			var monitor = new TrafficMonitor(new TrafficReplay(Creeper()), 0.0, -1, TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			var sink = new Collect();
			for (long step = 1; step <= 10000; step++)
			{
				monitor.Update(step, Still(step, 0.0, 0.0), sink);
			}

			// At the first step its west side (east 2) overlaps the box's east side (east 3), and it is a threat by the alarm's
			// rule: closing at 0.3 m/s to a CPA of 7 m in 67 s.
			Assert.That(sink.Events.Count, Is.EqualTo(1), string.Join(", ", sink.Events.ConvertAll(e => e.Kind)));
			var alongside = sink.Events[0];
			Assert.That((alongside.Kind, alongside.Step, alongside.StartStep, alongside.LastStep, alongside.Mmsi), Is.EqualTo((TrafficEventKind.AlongsideStarted, 1L, 1L, 1L, 200000005)));
			Assert.That(alongside.Cpa, Is.EqualTo(7.0).Within(1e-3));
			Assert.That(TrafficAlarmLimits.Default.IsThreat(new Encounter(alongside.Range, alongside.Bearing, alongside.Cpa, alongside.Tcpa, alongside.RelativeSpeed)), Is.True,
				"without the alongside rule it would alarm from the first step");
			Assert.That(monitor.IsAlongside(0), Is.True, "100 s on it is still beside the box: they never parted");
			Assert.That((monitor.IsAlarmed(0), monitor.IsColliding(0)), Is.EqualTo((false, false)));
			Assert.That((monitor.AlongsideAtStart, monitor.AlarmsRaised, monitor.CollisionsStarted), Is.EqualTo((1L, 0L, 0L)));
			Assert.That(monitor.Readout.State, Is.EqualTo(TrafficAlarmState.Clear), "named as the nearest vessel, neither alarm nor collision");
			Assert.That(monitor.Readout.Target.Mmsi, Is.EqualTo(200000005));
		}

		[Test]
		public void OnlyTheFirstStepCounts_AVesselThatComesAlongsideLater_AlarmsAndCollides()
		{
			// The same vessel and box, but the first step is taken 100 m west, beyond the margin: from the second step on the box
			// sits where the first test kept it.
			var monitor = new TrafficMonitor(new TrafficReplay(Creeper()), 0.0, -1, TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			var sink = new Collect();
			monitor.Update(1, Still(1, 0.0, -100.0), sink);
			for (long step = 2; step <= 10000; step++)
			{
				monitor.Update(step, Still(step, 0.0, 0.0), sink);
			}

			Assert.That(sink.Events.Exists(e => e.Kind == TrafficEventKind.AlongsideStarted), Is.False);
			Assert.That(sink.Events[0].Kind, Is.EqualTo(TrafficEventKind.AlarmRaised));
			Assert.That(sink.Events[1].Kind, Is.EqualTo(TrafficEventKind.CollisionStarted));
			Assert.That(sink.Events[1].Step, Is.EqualTo(2), "overlapping from the second step: a collision as usual");
			Assert.That(monitor.AlongsideAtStart, Is.EqualTo(0));
		}

		[Test]
		public void WithinTheMarginAtTheFirstStep_IsAlongside_ItEndsForGoodWhenTheyPart_ThenAlarmsResumeAndAnOverlapIsACollision()
		{
			var monitor = new TrafficMonitor(new TrafficReplay(TrafficFixtures.Pack(Barge())), 0.0, -1, TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			var sink = new Collect();
			// Phase 1: the box creeps north at 0.5 m/s past the barge, 2 m off its side (east 3 against 5), starting with 4 m
			// between its bow and the barge's stern: within the 5 m margin, never overlapping. Closing to a CPA of 10 m in 68 s, it
			// would alarm from the first step. It parts when its stern passes 5 m beyond the barge's bow (north 25): at 138.75 s.
			const double start = -34.3725;
			long step = 1;
			for (; step <= 14000; step++)
			{
				monitor.Update(step, VesselState.Planar(SimulationClock.TimeAtStep(step), start + 0.5 * SimulationClock.TimeAtStep(step), 0.0, 0.0, 0.5), sink);
			}

			Assert.That(sink.Events.ConvertAll(e => e.Kind), Is.EqualTo(new[] { TrafficEventKind.AlongsideStarted, TrafficEventKind.AlongsideEnded }));
			var started = sink.Events[0];
			Assert.That(TrafficAlarmLimits.Default.IsThreat(new Encounter(started.Range, started.Bearing, started.Cpa, started.Tcpa, started.RelativeSpeed)), Is.True, "a threat it did not alarm for");
			var parted = sink.Events[1];
			Assert.That(parted.Time, Is.EqualTo(138.75).Within(1e-9), "the first step the grown rectangle no longer touches");
			Assert.That((parted.StartStep, parted.LastStep), Is.EqualTo((1L, parted.Step - 1)));
			Assert.That(parted.MinimumRange, Is.EqualTo(10.0).Within(1e-6), "abeam, centre to centre (the nearest step is 2.5 mm off abeam)");
			Assert.That(monitor.IsAlongside(0), Is.False);

			// Phase 2: it turns back south from 45 m north, the barge ahead: closing at 0.5 m/s to a CPA of 10 m: the alarm is up at
			// once. Its bow comes back within the margin at 20 s: alongside does not start again.
			sink.Events.Clear();
			var turn = step;
			for (; step <= turn + 3000; step++)
			{
				var t = SimulationClock.TimeAtStep(step - turn);
				monitor.Update(step, VesselState.Planar(SimulationClock.TimeAtStep(step), 45.0 - 0.5 * t, 0.0, Math.PI, 0.5), sink);
			}

			Assert.That(sink.Events.ConvertAll(e => e.Kind), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised }), "alarms resume; alongside does not");
			Assert.That(sink.Events[0].Step, Is.EqualTo(turn));

			// Phase 3: it steps 6 m east into the barge's side: a collision.
			sink.Events.Clear();
			for (var k = 0; k < 10; k++, step++)
			{
				monitor.Update(step, VesselState.Planar(SimulationClock.TimeAtStep(step), 25.0, 6.0, Math.PI, 0.5), sink);
			}

			Assert.That(sink.Events.Exists(e => e.Kind == TrafficEventKind.CollisionStarted), Is.True, "after parting, any overlap is a collision");
			Assert.That((monitor.AlongsideAtStart, monitor.CollisionsStarted), Is.EqualTo((1L, 1L)));
		}

		[Test]
		public void AlongsideHolds_WhileTheVesselIsAbsent_AndEndsAtTheFirstStepItIsSeenApart()
		{
			// Reports to 120 s, then none until 500 s: two segments, absent in between.
			var monitor = new TrafficMonitor(new TrafficReplay(TrafficFixtures.Pack(Barge(0.0, 60.0, 120.0, 500.0, 560.0, 620.0))), 0.0, -1, TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			var sink = new Collect();
			long step = 1;
			for (; step <= 51000; step++)
			{
				// Beside it, 2 m off, until 510 s; then 20 m off from 510 s.
				monitor.Update(step, Still(step, 0.0, SimulationClock.TimeAtStep(step) < 510.0 ? 0.0 : -20.0), sink);
				if (step == 30000)
				{
					Assert.That(monitor.IsAlongside(0), Is.True, "absent at 300 s, and still alongside");
				}
			}

			Assert.That(sink.Events.ConvertAll(e => e.Kind), Is.EqualTo(new[] { TrafficEventKind.AlongsideStarted, TrafficEventKind.AlongsideEnded }));
			Assert.That(sink.Events[1].Time, Is.EqualTo(510.0).Within(1e-9), "seen apart at 510 s, not when it vanished at 120 s");
			Assert.That(SimulationClock.TimeAtStep(sink.Events[1].LastStep), Is.EqualTo(509.99).Within(1e-9));
		}

		[Test]
		public void TheOpenSnapshot_GivesAnActiveAlarmsMinimumSoFar_WithoutEndingIt()
		{
			// The wave 2b review's case: a target 500 m north and 80 m east comes south at 5 m/s; the learner, a 10 by 4 m box,
			// makes 5 m/s turning to starboard at 0.018 rad/s. The alarm rises at a CPA of 79.95 m and is still up at 7.10 m when
			// the recording ends after 20 s.
			var target = new TrafficFixtures.Track { Mmsi = 200000007, Class = TrafficClass.Other, Length = 10.0, Beam = 3.0 };
			target.Reports.Add(new TrafficFixtures.Report(0.0, 500.0, 80.0, 5.0, Math.PI, double.NaN));
			target.Reports.Add(new TrafficFixtures.Report(100.0, 0.0, 80.0, 5.0, Math.PI, double.NaN));
			var monitor = new TrafficMonitor(new TrafficReplay(TrafficFixtures.Pack(target)), 0.0, -1, TrafficFixtures.Box(10.0, 4.0), TrafficAlarmLimits.Default);
			var sink = new Collect();
			const double turn = 0.018;
			var least = double.PositiveInfinity;
			for (long step = 1; step <= 2000; step++)
			{
				var t = SimulationClock.TimeAtStep(step);
				var heading = turn * t;
				monitor.Update(step, VesselState.Planar(t, 5.0 / turn * Math.Sin(heading), 5.0 / turn * (1.0 - Math.Cos(heading)), heading, 5.0, 0.0, turn), sink);
				least = Math.Min(least, monitor.Readout.Cpa);
			}

			Assert.That(sink.Events.ConvertAll(e => e.Kind), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised }));
			Assert.That(sink.Events[0].Cpa, Is.EqualTo(79.95).Within(0.01));
			Assert.That(monitor.Readout.Cpa, Is.EqualTo(7.10).Within(0.01), "still up, far closer");
			var digest = monitor.Digest;
			var events = monitor.EventCount;
			var open = new Collect();
			Assert.That(monitor.ReportOpen(open), Is.EqualTo(1));
			var snapshot = open.Events[0];
			Assert.That((snapshot.Kind, snapshot.Step, snapshot.StartStep, snapshot.LastStep, snapshot.Mmsi), Is.EqualTo((TrafficEventKind.AlarmOpen, 2000L, 1L, 2000L, 200000007)));
			Assert.That(snapshot.MinimumCpa, Is.EqualTo(least), "the least CPA while it was up, not the 79.95 m it rose at");
			Assert.That(snapshot.Cpa, Is.EqualTo(monitor.Readout.Cpa), "the encounter at the last step");
			Assert.That((monitor.IsAlarmed(0), monitor.EventCount, monitor.Digest), Is.EqualTo((true, events, digest)), "a snapshot, not an end: nothing changes");
			Assert.That(sink.Events.Count, Is.EqualTo(1), "and nothing goes to the event sink");
		}

		[Test]
		public void TheOpenSnapshot_ListsEveryOpenEpisode_AlongsideAndCollisionsToo()
		{
			// The creeper stays alongside; the barge is hit at step 2 and still overlapping at the end.
			var monitor = new TrafficMonitor(new TrafficReplay(TrafficFixtures.Pack(TrafficFixtures.Straight(200000005, 0.0, -20.0, 7.0, 0.3, 0.0, 5), Barge())), 0.0, -1,
				TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			monitor.Update(1, Still(1, 0.0, -100.0), null);
			Assert.That(monitor.ReportOpen(null), Is.Zero, "nothing open yet");
			for (long step = 2; step <= 500; step++)
			{
				monitor.Update(step, Still(step, 0.0, 6.0), null);
			}

			var open = new Collect();
			monitor.ReportOpen(open);
			Assert.That(open.Events.ConvertAll(e => (e.Kind, e.Mmsi)), Is.EqualTo(new[]
			{
				(TrafficEventKind.AlarmOpen, 200000005), (TrafficEventKind.CollisionOpen, 200000005), (TrafficEventKind.CollisionOpen, 200000006),
			}), "in vessel order, alarm before collision; the creeper was not within the margin at the first step, so not alongside");
			Assert.That(open.Events.TrueForAll(e => e.Step == 500 && e.StartStep == 2), Is.True);

			var alongside = new TrafficMonitor(new TrafficReplay(Creeper()), 0.0, -1, TrafficFixtures.Box(20.0, 6.0), TrafficAlarmLimits.Default);
			// 70 s: past abeam at 66.7 s, still beside the box.
			for (long step = 1; step <= 7000; step++)
			{
				alongside.Update(step, Still(step, 0.0, 0.0), null);
			}

			open.Events.Clear();
			alongside.ReportOpen(open);
			Assert.That((open.Events[0].Kind, open.Events[0].StartStep, open.Events[0].LastStep), Is.EqualTo((TrafficEventKind.AlongsideOpen, 1L, 7000L)));
			Assert.That(open.Events[0].MinimumRange, Is.EqualTo(7.0).Within(1e-3), "abeam at 7 m, the least range so far");
		}

		[Test]
		public void Update_AllocatesNothing()
		{
			var pack = TrafficFixtures.Pack(
				TrafficFixtures.Straight(200000001, 0.0, 1000.0, 0.0, 5.0, Math.PI, 5),
				TrafficFixtures.Straight(200000003, 0.0, 500.0, 300.0, 3.0, 4.0, 5),
				TrafficFixtures.Straight(200000004, 0.0, -800.0, -50.0, 6.0, 0.2, 5),
				Barge());
			var outline = HullOutline.FromContact(ManeuverRunner.Load("t-class.json"));
			// An allocation per step or per event shows in every run. The runtime's tiered JIT can allocate once while it
			// recompiles hot methods early in a test run (seen as about 5 kB under load, never with tiering off), so the same
			// run is measured up to three times, each with a fresh monitor, and one must be clean.
			var allocated = 0L;
			var events = 0;
			for (var run = 0; run < 3; run++)
			{
				var monitor = new TrafficMonitor(new TrafficReplay(pack), 0.0, -1, outline, TrafficAlarmLimits.Default);
				var sink = new Counting();
				for (long step = 1; step <= 100; step++)
				{
					monitor.Update(step, Learner(step), sink);
				}

				var before = GC.GetAllocatedBytesForCurrentThread();
				for (long step = 101; step <= 20000; step++)
				{
					monitor.Update(step, Learner(step), sink);
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
				events = sink.Count;
				if (allocated == 0)
				{
					break;
				}
			}

			Assert.That(allocated, Is.EqualTo(0), "no garbage per step, events included, once the runtime has settled");
			Assert.That(events, Is.GreaterThan(0));
		}

		private sealed class Counting : ITrafficEventSink
		{
			public int Count;

			public void OnTrafficEvent(in TrafficEvent trafficEvent) => Count++;
		}
	}
}
