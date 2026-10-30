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
