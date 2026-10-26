using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// CPA and TCPA. Failure modes guarded: the sign of TCPA (positive while closing, negative once past), the CPA taken at the
	/// wrong time or negative, the units (metres, seconds, metres per second), the bearing's convention (true, clockwise from
	/// north, from the learner to the other vessel), and a divide by zero with no relative motion.
	/// </summary>
	[TestFixture]
	public class EncounterTests
	{
		[Test]
		public void HeadOn_ClosesToZero_AtRangeOverClosingSpeed()
		{
			// The learner at the origin making 5 m/s north; the other 1000 m north making 5 m/s south.
			var e = Encounter.Between(0, 0, 5, 0, 1000, 0, -5, 0);
			Assert.That(e.Range, Is.EqualTo(1000.0));
			Assert.That(e.Bearing, Is.EqualTo(0.0), "dead ahead, true north");
			Assert.That(e.RelativeSpeed, Is.EqualTo(10.0));
			Assert.That(e.Tcpa, Is.EqualTo(100.0), "1000 m at 10 m/s");
			Assert.That(e.Cpa, Is.EqualTo(0.0));
			Assert.That(e.Closing, Is.True);
		}

		[Test]
		public void Crossing_FromStarboard_HasTheCpaOfTheMissDistance()
		{
			// The learner 5 m/s north from the origin; the other 600 m east and 500 m north making 6 m/s west: it crosses ahead
			// at t = 100 s, when the learner is 500 m north, so the closest point is ahead of the learner.
			var e = Encounter.Between(0, 0, 5, 0, 500, 600, 0, -6);
			Assert.That(Angles.ToDegrees(e.Bearing), Is.EqualTo(Angles.ToDegrees(Math.Atan2(600, 500))).Within(1e-12), "bearing clockwise from north");
			var t = e.Tcpa;
			var dn = 500 - 5 * t;
			var de = 600 - 6 * t;
			Assert.That(e.Cpa, Is.EqualTo(Math.Sqrt(dn * dn + de * de)).Within(1e-9), "the distance at TCPA");
			// The relative motion is (-5, -6) m/s; the closest point is where it is perpendicular to the line of sight.
			Assert.That(t, Is.EqualTo((500.0 * 5 + 600.0 * 6) / (25 + 36)).Within(1e-12));
			Assert.That(e.Cpa, Is.EqualTo(Math.Abs(500.0 * -6 - 600.0 * -5) / Math.Sqrt(61)).Within(1e-9));
		}

		[Test]
		public void Opening_HasANegativeTcpa_AndThePastCpa()
		{
			// Head-on, but they passed 30 s ago with a 50 m miss.
			var e = Encounter.Between(0, 0, 5, 0, -300, 50, -5, 0);
			Assert.That(e.Tcpa, Is.EqualTo(-30.0).Within(1e-12), "negative once the closest point is past");
			Assert.That(e.Cpa, Is.EqualTo(50.0).Within(1e-9));
			Assert.That(e.Closing, Is.False);
			Assert.That(e.Range, Is.GreaterThan(e.Cpa));
		}

		[Test]
		public void Overtaking_FromAstern_ClosesAtTheSpeedDifference()
		{
			var e = Encounter.Between(0, 0, 4, 0, -200, -20, 6, 0);
			Assert.That(e.Tcpa, Is.EqualTo(100.0).Within(1e-12));
			Assert.That(e.Cpa, Is.EqualTo(20.0).Within(1e-9));
			Assert.That(Angles.ToDegrees(e.Bearing), Is.EqualTo(180.0 + Angles.ToDegrees(Math.Atan2(20, 200))).Within(1e-9), "astern, a little to port");
		}

		[Test]
		public void WithNoRelativeMotion_TheRangeStays()
		{
			var e = Encounter.Between(10, 10, 3, 4, 70, 90, 3, 4);
			Assert.That(e.RelativeSpeed, Is.EqualTo(0.0));
			Assert.That(e.Tcpa, Is.EqualTo(0.0));
			Assert.That(e.Cpa, Is.EqualTo(100.0), "the range, 60 by 80 m");
			Assert.That(e.Closing, Is.False);
			var still = Encounter.Between(0, 0, 0, 0, 0, 0, 0, 0);
			Assert.That((still.Range, still.Cpa, still.Tcpa, still.Bearing), Is.EqualTo((0.0, 0.0, 0.0, 0.0)), "on top of each other, still: no NaN");
		}

		[Test]
		public void TheThreatRule_NeedsClosing_WithinBothLimits()
		{
			var limits = TrafficAlarmLimits.Default;
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 5, 0, 1000, 0, -5, 0)), Is.True, "head-on, 100 s, CPA 0");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 5, 0, 1300, 0, -5, 0)), Is.False, "TCPA 130 s, past the 120 s look-ahead");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 5, 0, 1000, 101, -5, 0)), Is.False, "CPA 101 m");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 5, 0, 1000, 100, -5, 0)), Is.True, "CPA 100 m, at the limit");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 5, 0, -300, 50, -5, 0)), Is.False, "opening");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 0, 0, 0, 20, 0, -0.2)), Is.False, "closing at 0.2 m/s (TCPA 100 s, CPA 0), under the 0.25 m/s floor");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 0, 0, 0, 30, 0, -0.3)), Is.True, "closing at 0.3 m/s");
			Assert.That(limits.IsThreat(Encounter.Between(0, 0, 0, 0, 0, 30, 0, 0)), Is.False, "two vessels at rest side by side never alarm");
		}
	}
}
