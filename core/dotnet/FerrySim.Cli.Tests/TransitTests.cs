using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: a waypoint file not checked (missing, wrong header, no rows, a non-finite number); the transit
	// not steering to each waypoint in turn, or not ending at the last; a cross current carrying the vessel off its legs
	// with nothing correcting it; the levers not at the lever that gives the test speed in calm deep water; the run not
	// starting at rest at the start; a transit that never arrives reported as arriving; and the summary missing the
	// arrival, the closest approach to each waypoint or the least depth under the keel.
	[TestFixture]
	public sealed class TransitTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		// A dog-leg with legs from 1.2 to 1.6 km: east, north-east, then north.
		private const string Route = "north_m,east_m\n0,1500\n1000,2500\n2400,2500\n";

		[Test]
		public void ATransit_SteersEachWaypointInTurn_AndEndsAtTheLast()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var table = Transit(scratch, "calm", Route);
				var north = table.Column("north_m");
				var east = table.Column("east_m");
				var waypoints = new[] { (0.0, 1500.0), (1000.0, 2500.0), (2400.0, 2500.0) };
				var previous = -1;
				foreach (var (n, e) in waypoints)
				{
					var closest = Enumerable.Range(0, table.RowCount).OrderBy(i => Math.Pow(north[i] - n, 2) + Math.Pow(east[i] - e, 2)).First();
					Assert.That(Math.Sqrt(Math.Pow(north[closest] - n, 2) + Math.Pow(east[closest] - e, 2)), Is.LessThan(60.0), $"passes waypoint ({n}, {e})");
					Assert.That(closest, Is.GreaterThan(previous), "in order");
					previous = closest;
				}

				var last = table.RowCount - 1;
				Assert.That(Math.Sqrt(Math.Pow(north[last] - 2400.0, 2) + Math.Pow(east[last] - 2500.0, 2)), Is.LessThanOrEqualTo(50.0), "ends within the arrival radius of the last");
				Assert.That(table.HeaderValue("param.waypoints"), Is.EqualTo("0,1500;1000,2500;2400,2500"));
				Assert.That(table.HeaderValue("param.arrive_m"), Is.EqualTo("50"));
			}
		}

		[Test]
		public void ATransit_StartsAtRestAtTheStart_WithBothLeversAtTheCalmWaterServiceSpeedLever()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var calm = Transit(scratch, "calm", Route);
				var current = Transit(scratch, "current=2kn@0", Route);
				Assert.That(calm.Column("north_m")[0], Is.EqualTo(0.0));
				Assert.That(calm.Column("east_m")[0], Is.EqualTo(0.0));
				Assert.That(calm.Column("stw_kn")[0], Is.EqualTo(0.0));
				Assert.That(calm.Column("sog_kn")[0], Is.EqualTo(0.0));
				Assert.That(current.Column("sog_kn")[0], Is.EqualTo(2.0).Within(1e-3), "at rest in the water, drifting with it");

				var lever = calm.HeaderValue("settle.lever");
				Assert.That(current.HeaderValue("settle.lever"), Is.EqualTo(lever), "the same lever whatever the water");
				Assert.That(double.Parse(calm.HeaderValue("settle.stw_kn"), System.Globalization.CultureInfo.InvariantCulture),
					Is.EqualTo(Units.MetresPerSecondToKnots(12.8611)).Within(0.02), "the T-class service speed, 25 kn");
				var port = calm.Column("port_lever");
				var starboard = calm.Column("starboard_lever");
				var value = double.Parse(lever, System.Globalization.CultureInfo.InvariantCulture);
				Assert.That(port.Concat(starboard).Skip(1), Has.All.EqualTo(value).Within(5e-5));
			}
		}

		[Test]
		public void ATransitInACrossCurrent_StaysNearItsLegs()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				// A 2 kn current across the first leg (flowing north while the leg runs east) and along the last.
				var table = Transit(scratch, "current=2kn@0", Route);
				var summary = TestSupport.Run(new VesselDynamicsFactory(), "summarize", scratch.File("current=2kn@0.csv"), "--json");
				Assert.That(summary.Code, Is.EqualTo(0), summary.Err);
				using (var document = JsonDocument.Parse(summary.Out))
				{
					var metrics = document.RootElement.GetProperty("metrics");
					Assert.That(metrics.GetProperty("arrived").GetDouble(), Is.EqualTo(1.0));
					Assert.That(metrics.GetProperty("max_cross_track_m").GetDouble(), Is.LessThan(60.0));
				}
			}
		}

		[Test]
		public void TheSummary_ReportsTheArrivalTheClosestApproachToEachWaypointAndTheLeastDepthUnderTheKeel()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				Transit(scratch, "calm", Route);
				var summary = TestSupport.Run(new VesselDynamicsFactory(), "summarize", scratch.File("calm.csv"), "--json");
				Assert.That(summary.Code, Is.EqualTo(0), summary.Err);
				using (var document = JsonDocument.Parse(summary.Out))
				{
					Assert.That(document.RootElement.GetProperty("maneuver").GetString(), Is.EqualTo("transit"));
					var metrics = document.RootElement.GetProperty("metrics");
					Assert.That(metrics.GetProperty("arrived").GetDouble(), Is.EqualTo(1.0));
					Assert.That(metrics.GetProperty("waypoints").GetDouble(), Is.EqualTo(3.0));
					for (var k = 1; k <= 3; k++)
					{
						Assert.That(metrics.GetProperty($"waypoint_{k}_closest_m").GetDouble(), Is.LessThan(60.0));
					}

					Assert.That(metrics.GetProperty("min_depth_under_keel_m").GetDouble(), Is.EqualTo(50.0 - 1.9).Within(0.01), "50 m of calm water less the keel");
					Assert.That(metrics.GetProperty("distance_m").GetDouble(), Is.GreaterThan(4000.0));
				}
			}
		}

		[Test]
		public void ATransitThatCannotArrive_SaysSo()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var table = Transit(scratch, "calm", Route, "--duration-s", "60");
				var summary = TestSupport.Run(new VesselDynamicsFactory(), "summarize", scratch.File("calm.csv"), "--json");
				using (var document = JsonDocument.Parse(summary.Out))
				{
					Assert.That(document.RootElement.GetProperty("metrics").GetProperty("arrived").GetDouble(), Is.EqualTo(0.0));
				}

				Assert.That(table.Column("time_s").Last(), Is.EqualTo(60.0).Within(1e-9));
			}
		}

		[TestCase("north_m,east_m\n", TestName = "ABadWaypointFile_FailsWithExitCode1(no rows)")]
		[TestCase("north,east\n0,100\n", TestName = "ABadWaypointFile_FailsWithExitCode1(wrong header)")]
		[TestCase("north_m,east_m\n0,nan\n", TestName = "ABadWaypointFile_FailsWithExitCode1(not a number)")]
		[TestCase("north_m,east_m\n0,100,5\n", TestName = "ABadWaypointFile_FailsWithExitCode1(three fields)")]
		[TestCase(null, TestName = "ABadWaypointFile_FailsWithExitCode1(missing)")]
		public void ABadWaypointFile_FailsWithExitCode1(string text)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var path = scratch.File("route.csv");
				if (text != null)
				{
					File.WriteAllText(path, text);
				}

				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "transit", "--vessel", Vessel, "--out", scratch.File("t.csv"), "--waypoints", path);
				Assert.That(code, Is.EqualTo(1), stderr);
				Assert.That(stderr, Does.StartWith("ferrysim: error: ").And.Contain("route.csv"));
			}
		}

		[Test]
		public void ATransitWithoutWaypoints_IsAUsageError()
		{
			var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "transit", "--vessel", Vessel, "--out", "t.csv");
			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.Contain("--waypoints"));
		}

		private static TelemetryTable Transit(TestSupport.ScratchDirectory scratch, string environment, string route, params string[] extra)
		{
			var path = scratch.File("route.csv");
			File.WriteAllText(path, route);
			var csv = scratch.File(environment + ".csv");
			var args = new[] { "maneuver", "transit", "--vessel", Vessel, "--out", csv, "--waypoints", path, "--env", environment, "--start-heading-deg", "90" }.Concat(extra).ToArray();
			var (code, _, stderr) = TestSupport.Run(new VesselDynamicsFactory(), args);
			Assert.That(code, Is.EqualTo(0), stderr);
			return TelemetryTable.Load(csv);
		}
	}
}
