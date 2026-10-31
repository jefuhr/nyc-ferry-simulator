using FerrySim.Core.Navigation;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: an alarm on a target that is past its closest point or that never comes near, a missed alarm on a
	// converging target, a list that is unsorted or that changes order between frames, and an alarm on a parallel course.
	public class TargetRankingTests
	{
		private static readonly CpaAlarmLimits Limits = CpaAlarmLimits.Default;

		[Test]
		public void ExactFormula_ConvergingTarget()
		{
			// Own ship northbound at 5 m/s; a target 1000 m ahead and 100 m to starboard, southbound at 5 m/s.
			var e = Encounter.Between(0, 0, 5, 0, 1000, 100, -5, 0);
			Assert.That(e.Tcpa, Is.EqualTo(100.0).Within(1e-9), "closing at 10 m/s over 1000 m");
			Assert.That(e.Cpa, Is.EqualTo(100.0).Within(1e-9), "passes 100 m abeam");
			Assert.That(Limits.IsAlarm(e), Is.True, "100 m within 0.1 nm, 100 s within 6 min");
			Assert.That(new CpaAlarmLimits(185.2, 60.0).IsAlarm(e), Is.False, "too far ahead for a 60 s limit");
			Assert.That(new CpaAlarmLimits(100.0, 360.0).IsAlarm(e), Is.False, "CPA exactly at the threshold is not under it");
		}

		[Test]
		public void ParallelCourses_NoRelativeMotion_NoAlarm()
		{
			var e = Encounter.Between(0, 0, 6, 0, 50, 50, 6, 0);
			Assert.That(e.Tcpa, Is.EqualTo(0.0));
			Assert.That(e.Cpa, Is.EqualTo(e.Range).Within(1e-9));
			Assert.That(Limits.IsAlarm(e), Is.False, "a vessel keeping station 70 m away never closes");
		}

		[Test]
		public void StationaryOwnShip_TargetApproaching_Alarms()
		{
			var e = Encounter.Between(0, 0, 0, 0, 600, 0, -4, 0);
			Assert.That(e.Tcpa, Is.EqualTo(150.0).Within(1e-9));
			Assert.That(e.Cpa, Is.EqualTo(0.0).Within(1e-9));
			Assert.That(Limits.IsAlarm(e), Is.True);
		}

		[Test]
		public void TargetAheadAndReceding_NegativeTcpa_NoAlarm()
		{
			var e = Encounter.Between(0, 0, 3, 0, 100, 0, 8, 0);
			Assert.That(e.Tcpa, Is.LessThan(0.0), "it is already drawing away");
			Assert.That(e.Cpa, Is.EqualTo(0.0).Within(1e-9), "its extrapolated line ran through own ship 20 s ago: a tiny CPA in the past");
			Assert.That(Limits.IsAlarm(e), Is.False, "a past CPA, however small, is no alarm");
		}

		[Test]
		public void TargetJustPassed_NegativeTcpa_NoAlarmHoweverClose()
		{
			var e = Encounter.Between(0, 0, 5, 0, -20, 30, -5, 0);
			Assert.That(e.Tcpa, Is.LessThan(0.0));
			Assert.That(e.Cpa, Is.EqualTo(30.0).Within(1e-9));
			Assert.That(Limits.IsAlarm(e), Is.False);
		}

		[Test]
		public void Ranking_ActiveThreatsFirstByCpa_ThenTheRestByCpa_Truncated()
		{
			var ranking = new TargetRanking(4);
			ranking.Begin();
			ranking.Add(0, Encounter.Between(0, 0, 5, 0, 5000, 10, 5, 0), Limits); // parallel, far: CPA 5000, TCPA 0 (active, no relative motion)
			ranking.Add(1, Encounter.Between(0, 0, 5, 0, 2000, 150, -5, 0), Limits); // active, CPA 150, TCPA 200: alarm
			ranking.Add(2, Encounter.Between(0, 0, 5, 0, -100, 20, -5, 0), Limits); // past: CPA 20 but TCPA negative
			ranking.Add(3, Encounter.Between(0, 0, 5, 0, 1000, 300, -5, 0), Limits); // active, CPA 300, TCPA 100: no alarm
			ranking.Add(4, Encounter.Between(0, 0, 5, 0, 9000, 60, -5, 0), Limits); // CPA 60 but TCPA 900, beyond the limit
			Assert.That(ranking.Considered, Is.EqualTo(5));
			Assert.That(ranking.AlarmCount, Is.EqualTo(1));
			Assert.That(ranking.Alarm, Is.True);
			Assert.That(ranking.Count, Is.EqualTo(4));
			Assert.That(ranking.Target(0), Is.EqualTo(1), "active threats first, the nearest CPA first: the alarm");
			Assert.That(ranking.Target(1), Is.EqualTo(3), "then the active non-alarm");
			Assert.That(ranking.Target(2), Is.EqualTo(0), "the parallel vessel is active (TCPA 0) but far");
			Assert.That(ranking.Target(3), Is.EqualTo(2), "a past target comes after every active one, however small its CPA");
			Assert.That(ranking.IsAlarm(0), Is.True);
			Assert.That(ranking.IsActive(1), Is.True);
			Assert.That(ranking.IsActive(3), Is.False);
			Assert.That(ranking.IsPast(3), Is.True);
			Assert.That(ranking.IsPast(0), Is.False);
			Assert.That(() => ranking.Target(4), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
		}

		/// <summary>Review item 10: six nearer parallel (TCPA 0, active) targets must not push a closing alarm out of the list.</summary>
		[Test]
		public void Ranking_AlarmsLead_EvenBehindSixNearerActiveTargets()
		{
			var ranking = new TargetRanking(6);
			ranking.Begin();
			for (var i = 0; i < 6; i++)
			{
				ranking.Add(i, Encounter.Between(0, 0, 5, 0, 0, 50 + 10 * i, 5, 0), Limits); // abeam on a parallel course: no relative motion, TCPA 0, CPA 50..100 m, no alarm
			}

			ranking.Add(9, Encounter.Between(0, 0, 5, 0, 2000, 150, -5, 0), Limits); // closing: CPA 150 m in 200 s, an alarm
			Assert.That(ranking.AlarmCount, Is.EqualTo(1));
			Assert.That(ranking.Count, Is.EqualTo(6));
			Assert.That(ranking.Target(0), Is.EqualTo(9), "the alarm leads the list whatever the others' CPA");
			Assert.That(ranking.IsAlarm(0), Is.True);
			for (var row = 1; row < 6; row++)
			{
				Assert.That(ranking.IsAlarm(row), Is.False);
				Assert.That(ranking.IsActive(row), Is.True);
				Assert.That(ranking.Target(row), Is.EqualTo(row - 1), "then the active targets by CPA");
			}
		}

		[Test]
		public void Ranking_TiesBreakByTargetIndex_AndRepeatIdentically()
		{
			var ranking = new TargetRanking(4);
			for (var pass = 0; pass < 2; pass++)
			{
				ranking.Begin();
				ranking.Add(7, Encounter.Between(0, 0, 0, 0, 100, 0, 0, 0), Limits);
				ranking.Add(2, Encounter.Between(0, 0, 0, 0, 0, 100, 0, 0), Limits);
				ranking.Add(5, Encounter.Between(0, 0, 0, 0, -100, 0, 0, 0), Limits);
				Assert.That(ranking.Count, Is.EqualTo(3));
				Assert.That(ranking.Target(0), Is.EqualTo(2));
				Assert.That(ranking.Target(1), Is.EqualTo(5));
				Assert.That(ranking.Target(2), Is.EqualTo(7));
			}
		}

		// Failure modes of the plotter's limit keys: a step that leaves the grid, a step past a bound, drift after many
		// steps, one limit's step touching the other, and a default off the grid.
		[Test]
		public void Limits_StepCpa_InStepsOfATwentiethOfAMile_ClampedToTheBounds()
		{
			var limits = CpaAlarmLimits.Default;
			Assert.That(CpaAlarmLimits.CpaStep, Is.EqualTo(92.6).Within(1e-12), "0.05 nautical mile");
			Assert.That(CpaAlarmLimits.MinimumCpa, Is.EqualTo(92.6).Within(1e-12));
			Assert.That(CpaAlarmLimits.MaximumCpa, Is.EqualTo(1852.0).Within(1e-12), "1 nautical mile");
			var up = limits.StepCpa(1);
			Assert.That(up.Cpa, Is.EqualTo(277.8).Within(1e-9), "0.10 up to 0.15 nm");
			Assert.That(up.Tcpa, Is.EqualTo(limits.Tcpa), "the TCPA limit is untouched");
			var down = limits.StepCpa(-1);
			Assert.That(down.Cpa, Is.EqualTo(92.6).Within(1e-9), "0.10 down to 0.05 nm");
			Assert.That(down.StepCpa(-1).Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "and it stays at the floor");
			var top = limits;
			for (var i = 0; i < 40; i++) top = top.StepCpa(1);
			Assert.That(top.Cpa, Is.EqualTo(CpaAlarmLimits.MaximumCpa), "and at the ceiling");
			Assert.That(limits.StepCpa(0).Cpa, Is.EqualTo(limits.Cpa));
			// Off the grid (a programmatic 0.07 nm): a step lands on the next grid value in that direction.
			var off = new CpaAlarmLimits(0.07 * 1852.0, 360.0);
			Assert.That(off.StepCpa(1).Cpa, Is.EqualTo(185.2).Within(1e-9), "0.07 up to 0.10");
			Assert.That(off.StepCpa(-1).Cpa, Is.EqualTo(92.6).Within(1e-9), "0.07 down to 0.05");
			// Many steps never drift: twenty up and twenty down is the same double again.
			var walked = limits;
			for (var i = 0; i < 20; i++) walked = walked.StepCpa(1);
			for (var i = 0; i < 20; i++) walked = walked.StepCpa(-1);
			for (var i = 0; i < 20; i++) walked = walked.StepCpa(-1);
			for (var i = 0; i < 1; i++) walked = walked.StepCpa(1);
			Assert.That(walked.Cpa, Is.EqualTo(limits.Cpa), "back on the default exactly");
		}

		[Test]
		public void Limits_StepTcpa_InMinutes_ClampedToTheBounds()
		{
			var limits = CpaAlarmLimits.Default;
			Assert.That(CpaAlarmLimits.TcpaStep, Is.EqualTo(60.0));
			Assert.That(CpaAlarmLimits.MinimumTcpa, Is.EqualTo(60.0), "1 minute");
			Assert.That(CpaAlarmLimits.MaximumTcpa, Is.EqualTo(1800.0), "30 minutes");
			Assert.That(limits.StepTcpa(1).Tcpa, Is.EqualTo(420.0), "6 up to 7 min");
			Assert.That(limits.StepTcpa(1).Cpa, Is.EqualTo(limits.Cpa), "the CPA limit is untouched");
			Assert.That(limits.StepTcpa(-1).Tcpa, Is.EqualTo(300.0));
			var floor = limits;
			for (var i = 0; i < 10; i++) floor = floor.StepTcpa(-1);
			Assert.That(floor.Tcpa, Is.EqualTo(CpaAlarmLimits.MinimumTcpa));
			var ceiling = limits;
			for (var i = 0; i < 40; i++) ceiling = ceiling.StepTcpa(1);
			Assert.That(ceiling.Tcpa, Is.EqualTo(CpaAlarmLimits.MaximumTcpa));
			Assert.That(new CpaAlarmLimits(185.2, 90.0).StepTcpa(1).Tcpa, Is.EqualTo(120.0), "1:30 up to 2 min");
			Assert.That(new CpaAlarmLimits(185.2, 90.0).StepTcpa(-1).Tcpa, Is.EqualTo(60.0), "1:30 down to 1 min");
			Assert.That(limits.StepTcpa(0).Tcpa, Is.EqualTo(limits.Tcpa));
		}

		[Test]
		public void Limits_StepsNeverChangeTheRankingPolicy_AlarmsStillLead()
		{
			// Under any limits an alarm leads: widen the CPA limit until the far target becomes an alarm too, and it still sorts by CPA among the alarms.
			var wide = CpaAlarmLimits.Default;
			for (var i = 0; i < 40; i++) wide = wide.StepCpa(1);
			var ranking = new TargetRanking(4);
			ranking.Begin();
			ranking.Add(0, Encounter.Between(0, 0, 5, 0, 0, 20, 5, 0), wide); // abeam on a parallel course, CPA 20: no alarm whatever the limits
			ranking.Add(1, Encounter.Between(0, 0, 5, 0, 2000, 1500, -5, 0), wide); // closing, CPA 1500 m: an alarm only under the wide limit
			ranking.Add(2, Encounter.Between(0, 0, 5, 0, 1000, 300, -5, 0), wide); // closing, CPA 300 m
			Assert.That(ranking.AlarmCount, Is.EqualTo(2));
			Assert.That(ranking.Target(0), Is.EqualTo(2), "the nearer alarm leads");
			Assert.That(ranking.Target(1), Is.EqualTo(1));
			Assert.That(ranking.Target(2), Is.EqualTo(0), "the parallel target, nearer than both, follows the alarms");
			ranking.Begin();
			ranking.Add(0, Encounter.Between(0, 0, 5, 0, 0, 20, 5, 0), CpaAlarmLimits.Default);
			ranking.Add(1, Encounter.Between(0, 0, 5, 0, 2000, 1500, -5, 0), CpaAlarmLimits.Default);
			ranking.Add(2, Encounter.Between(0, 0, 5, 0, 1000, 300, -5, 0), CpaAlarmLimits.Default);
			Assert.That(ranking.AlarmCount, Is.Zero, "under the default limits none is an alarm");
			Assert.That(ranking.Target(0), Is.EqualTo(0), "so the active targets sort by CPA alone");
		}

		[Test]
		public void Limits_RejectNonPositiveOrInfinite()
		{
			Assert.That(() => new CpaAlarmLimits(0, 10), Throws.ArgumentException);
			Assert.That(() => new CpaAlarmLimits(10, double.PositiveInfinity), Throws.ArgumentException);
			Assert.That(CpaAlarmLimits.Default.Cpa, Is.EqualTo(185.2).Within(1e-12), "0.1 nautical mile");
			Assert.That(CpaAlarmLimits.Default.Tcpa, Is.EqualTo(360.0), "6 minutes");
		}
	}
}
