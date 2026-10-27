using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: a maneuver started before the vessel is at the test speed, bisection
	// that never converges or silently accepts an unreachable speed, inputs of the wrong sign
	// (port/starboard, ahead/astern), runs that never end, a lead-in that breaks the time origin,
	// and the CLI depending on the concrete model (the real factory must work end to end).
	[TestFixture]
	public sealed class ManeuverRunTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		private static (int Code, string Out, string Err, TelemetryTable Table, FakeFactory Factory) Maneuver(
			TestSupport.ScratchDirectory scratch, double maxSpeed, string name, params string[] extra)
		{
			var factory = new FakeFactory(maxSpeed);
			var path = scratch.File(name + ".csv");
			var args = new[] { "maneuver", name, "--vessel", Vessel, "--out", path }.Concat(extra).ToArray();
			var result = TestSupport.Run(factory, args);
			var table = result.Code == 0 ? TelemetryTable.Load(path) : null;
			return (result.Code, result.Out, result.Err, table, factory);
		}

		[Test]
		public void Settle_BisectsOnLeverForTheRequestedSpeedThroughWater()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, _) = Maneuver(scratch, 15.0, "turning-circle", "--speed-kn", "12");

				Assert.That(code, Is.EqualTo(0), stderr);
				var target = Units.KnotsToMetresPerSecond(12.0);
				Assert.That(table.HeaderDouble("settle.lever", double.NaN), Is.EqualTo(target / 15.0).Within(0.002));
				Assert.That(table.HeaderDouble("settle.stw_kn", double.NaN), Is.EqualTo(12.0).Within(0.03));
				Assert.That(table.StwKn[0], Is.EqualTo(12.0).Within(0.03), "the maneuver starts at the test speed");
				Assert.That(table.PortLever[0], Is.EqualTo(target / 15.0).Within(0.002), "levers are held through the turn");
			}
		}

		[Test]
		public void Settle_FromRestingStartConvergesToo()
		{
			// The settle phase must not depend on starting at the target: even a poor start ends steady.
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, _) = Maneuver(scratch, 8.0, "crash-stop", "--speed-kn", "10");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(table.StwKn[0], Is.EqualTo(10.0).Within(0.03));
			}
		}

		[Test]
		public void Settle_UnreachableSpeedFailsWithExitCode1AndSaysWhy()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, _, _) = Maneuver(scratch, 8.0, "crash-stop", "--speed-kn", "20");

				Assert.That(code, Is.EqualTo(1));
				Assert.That(stderr, Does.Contain("cannot reach 20.00 kn").And.Contain("full ahead settles at"));
				Assert.That(File.Exists(scratch.File("crash-stop.csv")), Is.False);
			}
		}

		[Test]
		public void DefaultTestSpeedIsTheVesselsServiceSpeed()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var service = Units.MetresPerSecondToKnots(Core.Serialization.VesselParametersJson.Load(Vessel).Performance.ServiceSpeed.Value);

				var (code, _, stderr, table, _) = Maneuver(scratch, 15.0, "stopping");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(table.StwKn[0], Is.EqualTo(service).Within(0.05));
			}
		}

		[Test]
		public void LeadIn_IsRecordedBeforeTheManeuverStartAndTimeStaysOnTheSimClock()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (_, _, _, table, _) = Maneuver(scratch, 15.0, "turning-circle", "--speed-kn", "12", "--lead-in-s", "5", "--duration-s", "10");

				var start = table.HeaderDouble("maneuver_start_s", double.NaN);
				Assert.That(start % 1.0, Is.EqualTo(0.0), "the maneuver starts on a whole second");
				Assert.That(table.Time[0], Is.EqualTo(start - 5.0).Within(1e-9));
				Assert.That(table.Time[table.RowCount - 1], Is.EqualTo(start + 10.0).Within(1e-9));
				var firstManeuverRow = Array.FindIndex(table.Time, t => t >= start - 1e-9);
				Assert.That(table.Helm[firstManeuverRow - 1], Is.EqualTo(0.0), "no helm during the lead-in");
				Assert.That(table.Helm[firstManeuverRow], Is.EqualTo(1.0));
				Assert.That(table.Time.Zip(table.Time.Skip(1), (a, b) => b - a), Has.All.EqualTo(0.1).Within(1e-9), "no duplicated or missing row at the join");
			}
		}

		[Test]
		public void TurningCircle_StopsAfterFullCircleAndHonoursDirection()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var starboard = Maneuver(scratch, 15.0, "turning-circle", "--speed-kn", "12");
				var port = Maneuver(scratch, 15.0, "turning-circle", "--speed-kn", "12", "--direction", "port", "--helm", "0.5");

				Assert.That(starboard.Table.Helm.Last(), Is.EqualTo(1.0));
				Assert.That(port.Table.Helm.Last(), Is.EqualTo(-0.5));
				var report = ManeuverMetrics.Compute(port.Table);
				Assert.That(report.Find("time_to_360_deg_s").Value, Is.Not.Null, "ran past 360 degrees");
				Assert.That(report.Find("transfer_m").Value, Is.GreaterThan(0.0), "transfer is positive toward the turn");
				Assert.That(port.Table.Time.Last() - port.Table.HeaderDouble("maneuver_start_s", 0.0), Is.LessThan(TurningCircleController.DefaultCap));
			}
		}

		[Test]
		public void Zigzag_ReversesTheHelmAtTheCheckAngleAndEndsAfterTheThirdOrder()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, _) = Maneuver(scratch, 15.0, "zigzag", "--speed-kn", "12");

				Assert.That(code, Is.EqualTo(0), stderr);
				var helm = 10.0 / (FerrySim.Core.Serialization.VesselParametersJson.Load(Vessel).Controls.HardOverAngle.Value * 180.0 / Math.PI);
				var start = Array.FindIndex(table.Time, t => t >= table.HeaderDouble("maneuver_start_s", 0.0) - 1e-9);
				var signs = table.Helm.Skip(start).Select(h => Math.Sign(h)).ToArray();
				Assert.That(table.Helm[start], Is.EqualTo(helm).Within(1e-4), "10 deg divided by the vessel hard-over parameter");
				Assert.That(signs.Distinct().ToArray(), Is.EquivalentTo(new[] { 1, -1 }));
				var changes = Enumerable.Range(1, signs.Length - 1).Count(i => signs[i] != signs[i - 1]);
				Assert.That(changes, Is.EqualTo(3), "starboard, port, starboard, port");
				Assert.That(Math.Sign(table.Helm.Last()), Is.EqualTo(-1), "the row that ends the run already carries the third order");
				var report = ManeuverMetrics.Compute(table);
				Assert.That(report.Find("first_overshoot_deg").Value, Is.Not.Null);
				Assert.That(report.Find("second_overshoot_deg").Value, Is.Not.Null);
			}
		}

		[Test]
		public void Zigzag_PortFirstAndCustomAngles()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, _) = Maneuver(scratch, 15.0, "zigzag", "--speed-kn", "12", "--first", "port", "--angle-deg", "20", "--rudder-deg", "20", "--max-rudder-deg", "40");

				Assert.That(code, Is.EqualTo(0), stderr);
				var start = Array.FindIndex(table.Time, t => t >= table.HeaderDouble("maneuver_start_s", 0.0) - 1e-9);
				Assert.That(table.Helm[start], Is.EqualTo(-0.5).Within(1e-4));
				Assert.That(table.HeaderValue("param.check_deg"), Is.EqualTo("20"));
			}
		}

		[Test]
		public void CrashStop_AppliesFullAsternOnBothLeversAndEnds()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, factory) = Maneuver(scratch, 15.0, "crash-stop", "--speed-kn", "12");

				Assert.That(code, Is.EqualTo(0), stderr);
				var last = factory.Created.Last().Received.Last();
				Assert.That(last, Is.EqualTo(new ControlInputs(-1.0, -1.0, 0.0)));
				var report = ManeuverMetrics.Compute(table);
				Assert.That(report.Find("time_to_stop_s").Value, Is.Not.Null);
				Assert.That(report.Find("head_reach_m").Value, Is.GreaterThan(0.0));
			}
		}

		[Test]
		public void Stopping_PutsBothLeversToNeutral()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, factory) = Maneuver(scratch, 15.0, "stopping", "--speed-kn", "12");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(factory.Created.Last().Received.Last(), Is.EqualTo(ControlInputs.Neutral));
				Assert.That(ManeuverMetrics.Compute(table).Find("time_to_stop_s").Value, Is.Not.Null);
			}
		}

		[Test]
		public void Pivot_SplitsTheEnginesAtRest_PortAheadStarboardAsternByDefault()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var starboard = Maneuver(scratch, 15.0, "pivot");
				var port = Maneuver(scratch, 15.0, "pivot", "--direction", "port", "--lever", "0.8", "--duration-s", "20");

				Assert.That(starboard.Table.StwKn[0], Is.EqualTo(0.0).Within(1e-3), "starts dead in the water");
				var received = starboard.Factory.Created.Last().Received;
				Assert.That(received[0], Is.EqualTo(new ControlInputs(0.5, -0.5, 0.0)));
				Assert.That(starboard.Table.Time.Last(), Is.EqualTo(HoldController.DefaultPivotSeconds).Within(1e-9));
				Assert.That(port.Factory.Created.Last().Received[0], Is.EqualTo(new ControlInputs(-0.8, 0.8, 0.0)));
				Assert.That(port.Table.Time.Last(), Is.EqualTo(20.0).Within(1e-9));
			}
		}

		[Test]
		public void Acceleration_FromRestToFullAheadEndsWhenSpeedIsSteady()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr, table, factory) = Maneuver(scratch, 10.0, "acceleration");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(table.StwKn[0], Is.EqualTo(0.0).Within(1e-3));
				Assert.That(factory.Created.Last().Received[0], Is.EqualTo(new ControlInputs(1.0, 1.0, 0.0)));
				Assert.That(table.Time.Last(), Is.LessThan(HoldController.AccelerationCap), "ended on steadiness, not the cap");
				var report = ManeuverMetrics.Compute(table);
				Assert.That(report.Find("steady_speed_kn").Value, Is.EqualTo(Units.MetresPerSecondToKnots(10.0)).Within(0.1));
				Assert.That(report.Find("time_to_90_percent_speed_s").Value, Is.EqualTo(5.0 * Math.Log(10.0)).Within(0.5), "tau 5 s");
			}
		}

		[Test]
		public void Current_RestStartIsAtRestRelativeToTheWaterNotTheGround()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(
					factory, "maneuver", "acceleration", "--vessel", Vessel, "--out", scratch.File("o.csv"), "--duration-s", "1", "--env", "current=2kn@0", "--heading-deg", "90");

				Assert.That(code, Is.EqualTo(0), stderr);
				var initial = factory.Initials.Single();
				Assert.That(Angles.ToDegrees(initial.Heading), Is.EqualTo(90.0).Within(1e-9));
				Assert.That(initial.GroundVelocity.North, Is.EqualTo(Units.KnotsToMetresPerSecond(2.0)).Within(1e-9), "drifting with the current");
				Assert.That(initial.GroundVelocity.East, Is.EqualTo(0.0).Within(1e-9));
			}
		}

		[Test]
		public void Current_SettledStartAddsTheTestSpeedAlongTheHeadingToTheCurrent()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(
					factory, "maneuver", "stopping", "--vessel", Vessel, "--out", scratch.File("o.csv"), "--speed-kn", "10", "--duration-s", "1", "--env", "current=2kn@0", "--heading-deg", "90");

				Assert.That(code, Is.EqualTo(0), stderr);
				var initial = factory.Initials.First();
				Assert.That(initial.GroundVelocity.East, Is.EqualTo(Units.KnotsToMetresPerSecond(10.0)).Within(1e-9));
				Assert.That(initial.GroundVelocity.North, Is.EqualTo(Units.KnotsToMetresPerSecond(2.0)).Within(1e-9));
			}
		}

		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void EveryManeuverRunsAgainstTheRealFactoryAndSummarizes(string vesselFile)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				// The transit needs its route: two legs of a kilometre.
				var route = scratch.File("route.csv");
				File.WriteAllText(route, "north_m,east_m\n1000,0\n1000,1000\n");
				foreach (var spec in Maneuvers.All)
				{
					var csv = scratch.File(spec.Name + ".csv");
					var args = new List<string> { "maneuver", spec.Name, "--vessel", TestSupport.Vessel(vesselFile), "--out", csv };
					if (spec.Name == "transit")
					{
						args.AddRange(new[] { "--waypoints", route });
					}

					var run = TestSupport.Run(new VesselDynamicsFactory(), args.ToArray());
					Assert.That(run.Code, Is.EqualTo(0), spec.Name + ": " + run.Err);

					var summary = TestSupport.Run(new VesselDynamicsFactory(), "summarize", csv, "--json");
					Assert.That(summary.Code, Is.EqualTo(0), spec.Name + ": " + summary.Err);
					using (var document = JsonDocument.Parse(summary.Out))
					{
						Assert.That(document.RootElement.GetProperty("maneuver").GetString(), Is.EqualTo(spec.Name));
						var metrics = document.RootElement.GetProperty("metrics");
						Assert.That(metrics.EnumerateObject().Count(), Is.GreaterThan(0));
						foreach (var metric in metrics.EnumerateObject())
						{
							Assert.That(metric.Value.ValueKind, Is.EqualTo(JsonValueKind.Number), $"{spec.Name}: {metric.Name} was not reached");
						}
					}
				}
			}
		}

		[Test]
		public void RealFactory_SameRunTwiceIsByteIdentical()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var args = new[] { "maneuver", "zigzag", "--vessel", Vessel, "--out", scratch.File("a.csv"), "--env", "current=1kn@90,wind=10kn@from45" };
				TestSupport.Run(new VesselDynamicsFactory(), args);
				args[5] = scratch.File("b.csv");
				TestSupport.Run(new VesselDynamicsFactory(), args);

				Assert.That(File.ReadAllBytes(scratch.File("b.csv")), Is.EqualTo(File.ReadAllBytes(scratch.File("a.csv"))));
			}
		}
	}
}
