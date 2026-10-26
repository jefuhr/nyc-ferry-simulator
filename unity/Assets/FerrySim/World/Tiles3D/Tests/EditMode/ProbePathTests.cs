using System;
using System.Linq;
using FerrySim.Core.Contracts.Geodesy;
using NUnit.Framework;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The probe's camera flight (<see cref="ProbePath"/>). Failure modes guarded: a flight that leaves the harbor's active
	/// tiles; a jump at a waypoint; a heading that swings the long way round; a pose outside the flight's ends.
	/// </summary>
	public sealed class ProbePathTests
	{
		[Test]
		public void TheFlightStaysOverTheSlicesTiles()
		{
			foreach (var waypoint in ProbePath.Waypoints)
			{
				var i = (int)Math.Floor(waypoint.North / 4000.0);
				var j = (int)Math.Floor(waypoint.East / 4000.0);
				Assert.That(i, Is.InRange(-1, 1), waypoint.Name);
				Assert.That(j, Is.InRange(-1, 1), waypoint.Name);
			}
		}

		[Test]
		public void PosesAreContinuousAndClamped()
		{
			var previous = ProbePath.Sample(0.0);
			for (var t = 0.1; t <= ProbePath.DurationSeconds; t += 0.1)
			{
				var pose = ProbePath.Sample(t);
				var step = Math.Sqrt(Math.Pow(pose.North - previous.North, 2) + Math.Pow(pose.East - previous.East, 2));
				Assert.That(step, Is.LessThan(40.0), $"t = {t}");
				var turn = Math.Abs(((pose.HeadingDegrees - previous.HeadingDegrees) % 360.0 + 540.0) % 360.0 - 180.0);
				Assert.That(turn, Is.LessThan(2.0), $"t = {t}");
				previous = pose;
			}

			Assert.That(ProbePath.Sample(-5.0).North, Is.EqualTo(ProbePath.Waypoints[0].North));
			Assert.That(ProbePath.Sample(1e6).East, Is.EqualTo(ProbePath.Waypoints.Last().East));
		}

		[Test]
		public void TheHeadingTurnsTheShortWayRound()
		{
			// From 20 to 220 degrees the short way is 160 degrees anticlockwise, through north: halfway is 300, not 120.
			var middle = ProbePath.Sample(ProbePath.LegSeconds * 3.5);
			Assert.That(middle.HeadingDegrees, Is.EqualTo(300.0).Within(1e-9));
		}

		[Test]
		public void WaypointsGoThroughTheCoresTangentPlane()
		{
			var pier = ProbePath.Waypoints[1];
			var position = ProbePath.Geodetic(pier);
			var back = LocalTangentPlane.World.ToLocal(position);

			Assert.That(back.North, Is.EqualTo(pier.North).Within(1e-6));
			Assert.That(back.East, Is.EqualTo(pier.East).Within(1e-6));
			Assert.That(position.LatitudeDegrees, Is.EqualTo(40.7032).Within(0.001));
		}
	}
}
