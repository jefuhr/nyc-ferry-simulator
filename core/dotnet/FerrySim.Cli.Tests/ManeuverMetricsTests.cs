using System;
using System.Text.Json;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: metrics measured from the wrong origin (the lead-in instead of the
	// maneuver start), advance and transfer swapped, port turns reporting negative transfer, heading
	// wrap at north breaking the unwrapped heading change, overshoot taken from the wrong helm
	// window, a record that ends early reported as zero instead of "not reached", and the stop
	// criterion using ground speed or the wrong axis. Every expectation below is a closed-form answer.
	[TestFixture]
	public sealed class ManeuverMetricsTests
	{
		private const double Dt = 0.1;

		/// <summary>A circle of radius <paramref name="radius"/> flown at <paramref name="speed"/>, preceded by a straight lead-in.</summary>
		private static SyntheticCsv Circle(double radius, double speed, double startHeadingDeg, double turnSign, double leadIn, double turnSeconds)
		{
			var csv = new SyntheticCsv("turning-circle", leadIn);
			var h0 = Angles.ToRadians(startHeadingDeg);
			var omega = speed / radius;
			var cos = Math.Cos(h0);
			var sin = Math.Sin(h0);
			var rows = (int)Math.Round((leadIn + turnSeconds) / Dt);
			for (var i = 0; i <= rows; i++)
			{
				var t = i * Dt;
				double along, across, turned;
				if (t < leadIn)
				{
					// Straight run up to the origin of the maneuver, which is at (0, 0) when the helm goes over.
					along = (t - leadIn) * speed;
					across = 0.0;
					turned = 0.0;
				}
				else
				{
					turned = omega * (t - leadIn);
					along = radius * Math.Sin(turned);
					across = turnSign * radius * (1.0 - Math.Cos(turned));
				}

				var north = along * cos - across * sin;
				var east = along * sin + across * cos;
				var heading = startHeadingDeg + turnSign * Angles.ToDegrees(turned);
				csv.Row(t, north, east, heading, speed, yawRateDegps: turnSign * Angles.ToDegrees(omega), helm: t >= leadIn ? turnSign : 0.0, lever: 1.0);
			}

			return csv;
		}

		[Test]
		public void TurningCircle_PerfectCircleToStarboard()
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 0.0, turnSeconds: 140.0).Metrics();

			Assert.That(report.Find("advance_m").Value, Is.EqualTo(100.0).Within(0.1));
			Assert.That(report.Find("transfer_m").Value, Is.EqualTo(100.0).Within(0.1));
			Assert.That(report.Find("tactical_diameter_m").Value, Is.EqualTo(200.0).Within(0.1));
			var quarter = 100.0 / 5.0 * Math.PI / 2.0;
			Assert.That(report.Find("time_to_90_deg_s").Value, Is.EqualTo(quarter).Within(0.02));
			Assert.That(report.Find("time_to_180_deg_s").Value, Is.EqualTo(2.0 * quarter).Within(0.02));
			Assert.That(report.Find("time_to_360_deg_s").Value, Is.EqualTo(4.0 * quarter).Within(0.02));
		}

		[Test]
		public void TurningCircle_PortTurnReportsPositiveTransfer()
		{
			var report = Circle(radius: 60.0, speed: 6.0, startHeadingDeg: 0.0, turnSign: -1.0, leadIn: 0.0, turnSeconds: 70.0).Metrics();

			Assert.That(report.Find("advance_m").Value, Is.EqualTo(60.0).Within(0.1));
			Assert.That(report.Find("transfer_m").Value, Is.EqualTo(60.0).Within(0.1));
			Assert.That(report.Find("tactical_diameter_m").Value, Is.EqualTo(120.0).Within(0.1));
		}

		[Test]
		public void TurningCircle_UsesTheManeuverStartAsOriginNotTheLeadIn()
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 10.0, turnSeconds: 140.0).Metrics();

			Assert.That(report.Find("advance_m").Value, Is.EqualTo(100.0).Within(0.1));
			Assert.That(report.Find("tactical_diameter_m").Value, Is.EqualTo(200.0).Within(0.1));
			Assert.That(report.Find("time_to_90_deg_s").Value, Is.EqualTo(100.0 / 5.0 * Math.PI / 2.0).Within(0.02), "time counts from the helm order");
		}

		[TestCase(0.0)]
		[TestCase(40.0)]
		[TestCase(200.0)]
		[TestCase(350.0)]
		public void TurningCircle_IsIndependentOfInitialHeading_IncludingWrapAtNorth(double startHeading)
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: startHeading, turnSign: 1.0, leadIn: 0.0, turnSeconds: 140.0).Metrics();

			Assert.That(report.Find("advance_m").Value, Is.EqualTo(100.0).Within(0.1));
			Assert.That(report.Find("transfer_m").Value, Is.EqualTo(100.0).Within(0.1));
			Assert.That(report.Find("tactical_diameter_m").Value, Is.EqualTo(200.0).Within(0.1));
			Assert.That(report.Find("time_to_360_deg_s").Value, Is.Not.Null);
		}

		[Test]
		public void TurningCircle_EndingEarlyLeavesLaterMetricsUnreached()
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 0.0, turnSeconds: 45.0).Metrics();

			Assert.That(report.Find("advance_m").Value, Is.Not.Null, "90 degrees takes 31 s");
			Assert.That(report.Find("tactical_diameter_m").Value, Is.Null);
			Assert.That(report.Find("time_to_180_deg_s").Value, Is.Null);
			Assert.That(report.Find("time_to_360_deg_s").Value, Is.Null);
		}

		[Test]
		public void CrashStop_StraightLineStopGivesHeadReachAndTimeToStop()
		{
			// Heading east, speed 10 m/s falling 0.5 m/s each second: stopped (0.1 m/s) at t = 19.8 s.
			var csv = new SyntheticCsv("crash-stop").With("param.stop_speed_mps", "0.1");
			for (var i = 0; i <= 250; i++)
			{
				var t = i * Dt;
				var speed = Math.Max(0.0, 10.0 - 0.5 * t);
				var travelled = 10.0 * t - 0.25 * t * t;
				if (t > 20.0)
				{
					travelled = 100.0;
				}

				csv.Row(t, 0.0, travelled, 90.0, speed, lever: -1.0);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("head_reach_m").Value, Is.EqualTo(10.0 * 19.8 - 0.25 * 19.8 * 19.8).Within(0.05));
			Assert.That(report.Find("time_to_stop_s").Value, Is.EqualTo(19.8).Within(0.02));
			Assert.That(report.Find("track_reach_m").Value, Is.EqualTo(report.Find("head_reach_m").Value).Within(0.05));
			Assert.That(report.Find("lateral_deviation_m").Value, Is.EqualTo(0.0).Within(1e-6));
			Assert.That(report.Find("max_lateral_deviation_m").Value, Is.EqualTo(0.0).Within(1e-6));
		}

		[Test]
		public void CrashStop_LateralDeviationIsPositiveToStarboard_AndTrackReachIsPathLength()
		{
			// Heading east; drifting 0.5 m/s to the south, which is to starboard of an eastbound vessel.
			var csv = new SyntheticCsv("crash-stop");
			for (var i = 0; i <= 250; i++)
			{
				var t = i * Dt;
				var speed = Math.Max(0.0, 10.0 - 0.5 * t);
				var travelled = t > 20.0 ? 100.0 : 10.0 * t - 0.25 * t * t;
				csv.Row(t, -0.5 * Math.Min(t, 20.0), travelled, 90.0, speed, lever: -1.0);
			}

			var report = csv.Metrics();

			var head = report.Find("head_reach_m").Value.Value;
			Assert.That(report.Find("lateral_deviation_m").Value, Is.EqualTo(0.5 * 19.8).Within(0.05));
			Assert.That(report.Find("max_lateral_deviation_m").Value, Is.EqualTo(0.5 * 19.8).Within(0.05));
			Assert.That(report.Find("track_reach_m").Value, Is.GreaterThan(head));

			// Path length is the integral of the speed over the ground, sqrt((10 - 0.5 t)^2 + 0.5^2), up to the stop.
			var expected = 0.0;
			for (var t = 0.0005; t < 19.8; t += 0.001)
			{
				expected += Math.Sqrt((10.0 - 0.5 * t) * (10.0 - 0.5 * t) + 0.25) * 0.001;
			}

			Assert.That(report.Find("track_reach_m").Value, Is.EqualTo(expected).Within(0.1));
		}

		[Test]
		public void CrashStop_HeadReachIsAlongTheInitialHeadingForAnyHeading()
		{
			var csv = new SyntheticCsv("stopping");
			var heading = Angles.ToRadians(300.0);
			for (var i = 0; i <= 250; i++)
			{
				var t = i * Dt;
				var speed = Math.Max(0.0, 10.0 - 0.5 * t);
				var travelled = t > 20.0 ? 100.0 : 10.0 * t - 0.25 * t * t;
				csv.Row(t, travelled * Math.Cos(heading), travelled * Math.Sin(heading), 300.0, speed);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("head_reach_m").Value, Is.EqualTo(99.99).Within(0.1));
			Assert.That(report.Find("lateral_deviation_m").Value, Is.EqualTo(0.0).Within(0.01));
		}

		[Test]
		public void CrashStop_JudgesStoppedBySpeedThroughWaterNotSpeedOverGround()
		{
			// A 1 m/s current keeps SOG at 1 m/s while STW falls to zero; the stop must still be found.
			var csv = new SyntheticCsv("crash-stop");
			for (var i = 0; i <= 250; i++)
			{
				var t = i * Dt;
				csv.Row(t, 0.0, 10.0 * t, 90.0, Math.Max(0.0, 10.0 - 0.5 * t));
			}

			Assert.That(csv.Metrics().Find("time_to_stop_s").Value, Is.EqualTo(19.8).Within(0.02));
		}

		[Test]
		public void CrashStop_NeverStoppingLeavesMetricsUnreached()
		{
			var csv = new SyntheticCsv("crash-stop");
			for (var i = 0; i <= 100; i++)
			{
				csv.Row(i * Dt, 0.0, 10.0 * i * Dt, 90.0, 10.0);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("head_reach_m").Value, Is.Null);
			Assert.That(report.Find("time_to_stop_s").Value, Is.Null);
		}

		/// <summary>Heading change rising 1 deg/s to 14, falling to -17 at t = 45, then rising; helm flips at t = 10, 38 and 70.</summary>
		private static SyntheticCsv ZigzagTrack(double startHeading, double check)
		{
			var csv = new SyntheticCsv("zigzag").With("param.check_deg", check.ToString(System.Globalization.CultureInfo.InvariantCulture));
			for (var i = 0; i <= 800; i++)
			{
				var t = i * Dt;
				double change;
				if (t <= 14.0)
				{
					change = t;
				}
				else if (t <= 45.0)
				{
					change = 14.0 - (t - 14.0);
				}
				else
				{
					change = -17.0 + (t - 45.0);
				}

				var helm = t < 10.0 ? 0.3 : t < 38.0 ? -0.3 : t < 70.0 ? 0.3 : -0.3;
				csv.Row(t, 0.0, 0.0, startHeading + change, 5.0, helm: helm);
			}

			return csv;
		}

		[TestCase(0.0)]
		[TestCase(355.0)]
		public void Zigzag_FirstAndSecondOvershootFromHelmReversals(double startHeading)
		{
			var report = ZigzagTrack(startHeading, 10.0).Metrics();

			Assert.That(report.Find("first_overshoot_deg").Value, Is.EqualTo(4.0).Within(0.01), "peak 14 minus check 10");
			Assert.That(report.Find("second_overshoot_deg").Value, Is.EqualTo(7.0).Within(0.01), "trough -17 minus check -10");
		}

		[Test]
		public void Zigzag_WithTooFewReversalsReportsUnreached()
		{
			var csv = new SyntheticCsv("zigzag").With("param.check_deg", "10");
			for (var i = 0; i <= 200; i++)
			{
				csv.Row(i * Dt, 0.0, 0.0, i * Dt, 5.0, helm: i * Dt < 10.0 ? 0.3 : -0.3);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("first_overshoot_deg").Value, Is.Null, "needs the second reversal");
			Assert.That(report.Find("second_overshoot_deg").Value, Is.Null);
		}

		[Test]
		public void Zigzag_PortFirstMirrorsStarboardFirst()
		{
			var csv = new SyntheticCsv("zigzag").With("param.check_deg", "10");
			var mirrored = ZigzagTrack(0.0, 10.0).Render().Split('\n');
			var table = TelemetryTable.Parse(mirrored);
			for (var i = 0; i < table.RowCount; i++)
			{
				csv.Row(table.Time[i], 0.0, 0.0, -table.HeadingDeg[i], 5.0, helm: -table.Helm[i]);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("first_overshoot_deg").Value, Is.EqualTo(4.0).Within(0.01));
			Assert.That(report.Find("second_overshoot_deg").Value, Is.EqualTo(7.0).Within(0.01));
		}

		[Test]
		public void Acceleration_FirstOrderRiseGivesTimeToNinetyPercent()
		{
			const double steady = 10.0;
			const double tau = 20.0;
			var csv = new SyntheticCsv("acceleration");
			var distance = 0.0;
			for (var i = 0; i <= 6000; i++)
			{
				var t = i * Dt;
				var speed = steady * (1.0 - Math.Exp(-t / tau));
				distance = steady * (t - tau * (1.0 - Math.Exp(-t / tau)));
				csv.Row(t, distance, 0.0, 0.0, speed, lever: 1.0);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("steady_speed_kn").Value, Is.EqualTo(Units.MetresPerSecondToKnots(steady)).Within(0.01));
			Assert.That(report.Find("time_to_90_percent_speed_s").Value, Is.EqualTo(tau * Math.Log(10.0)).Within(0.05));
			var t90 = tau * Math.Log(10.0);
			Assert.That(report.Find("distance_to_90_percent_speed_m").Value, Is.EqualTo(steady * (t90 - tau * 0.9)).Within(0.1));
			Assert.That(string.Join("\n", report.Notes), Does.Not.Contain("provisional"));
		}

		[Test]
		public void Acceleration_StillRisingAtTheEndIsFlaggedProvisional()
		{
			var csv = new SyntheticCsv("acceleration");
			for (var i = 0; i <= 400; i++)
			{
				var t = i * Dt;
				csv.Row(t, 0.0, 0.0, 0.0, 10.0 * (1.0 - Math.Exp(-t / 20.0)));
			}

			Assert.That(string.Join("\n", csv.Metrics().Notes), Does.Contain("provisional"));
		}

		[Test]
		public void Pivot_ConstantYawRateAndDrift()
		{
			var csv = new SyntheticCsv("pivot");
			for (var i = 0; i <= 600; i++)
			{
				var t = i * Dt;
				csv.Row(t, 0.2 * t, 0.0, 3.0 * t, 0.0, yawRateDegps: 3.0, lever: 0.5);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("yaw_rate_degps").Value, Is.EqualTo(3.0).Within(1e-3));
			Assert.That(report.Find("heading_change_deg").Value, Is.EqualTo(180.0).Within(0.1));
			Assert.That(report.Find("drift_m").Value, Is.EqualTo(12.0).Within(0.01));
			Assert.That(report.Find("net_displacement_m").Value, Is.EqualTo(12.0).Within(0.01));
		}

		[Test]
		public void Pivot_DriftIsLargestExcursionNotJustTheEnd()
		{
			var csv = new SyntheticCsv("pivot");
			for (var i = 0; i <= 600; i++)
			{
				var t = i * Dt;
				var north = t <= 30.0 ? t : 60.0 - t;
				csv.Row(t, north, 0.0, 0.0, 0.0, yawRateDegps: -2.0);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("yaw_rate_degps").Value, Is.EqualTo(-2.0).Within(1e-3));
			Assert.That(report.Find("drift_m").Value, Is.EqualTo(30.0).Within(0.01));
			Assert.That(report.Find("net_displacement_m").Value, Is.EqualTo(0.0).Within(0.01));
		}

		[Test]
		public void UnknownManeuver_GetsGeneralFiguresAndANote()
		{
			var csv = new SyntheticCsv("trace-replay");
			for (var i = 0; i <= 100; i++)
			{
				csv.Row(i * Dt, 5.0 * i * Dt, 0.0, 0.0, 5.0);
			}

			var report = csv.Metrics();

			Assert.That(report.Find("distance_m").Value, Is.EqualTo(50.0).Within(0.01));
			Assert.That(report.Find("duration_s").Value, Is.EqualTo(10.0).Within(1e-6));
			Assert.That(report.Notes, Has.Some.Contain("trace-replay"));
		}

		[Test]
		public void JsonOutput_IsValidJsonWithNullsForUnreachedMetrics()
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 0.0, turnSeconds: 45.0).Metrics();

			using (var document = JsonDocument.Parse(SummaryFormatter.Json(report)))
			{
				var root = document.RootElement;
				Assert.That(root.GetProperty("maneuver").GetString(), Is.EqualTo("turning-circle"));
				Assert.That(root.GetProperty("metrics").GetProperty("advance_m").GetDouble(), Is.EqualTo(100.0).Within(0.1));
				Assert.That(root.GetProperty("metrics").GetProperty("tactical_diameter_m").ValueKind, Is.EqualTo(JsonValueKind.Null));
			}
		}

		[Test]
		public void TextOutput_ListsEveryMetricAndMarksUnreachedOnes()
		{
			var report = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 0.0, turnSeconds: 45.0).Metrics();

			var text = SummaryFormatter.Text(report);

			Assert.That(text, Does.Contain("Tactical diameter").And.Contain("not reached").And.Contain("Advance (at 90 deg)"));
			Assert.That(text, Does.Contain("100.0").Or.Contain("99.9"));
		}

		[Test]
		public void Summarize_ThroughTheCommandLine()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var path = Circle(radius: 100.0, speed: 5.0, startHeadingDeg: 0.0, turnSign: 1.0, leadIn: 0.0, turnSeconds: 140.0).WriteTo(scratch, "circle.csv");

				var human = TestSupport.Run(new FakeFactory(), "summarize", path);
				var json = TestSupport.Run(new FakeFactory(), "summarize", path, "--json");

				Assert.That(human.Code, Is.EqualTo(0), human.Err);
				Assert.That(human.Out, Does.Contain("Tactical diameter").And.Contain("200."));
				Assert.That(json.Code, Is.EqualTo(0), json.Err);
				using (var document = JsonDocument.Parse(json.Out))
				{
					Assert.That(document.RootElement.GetProperty("metrics").GetProperty("tactical_diameter_m").GetDouble(), Is.EqualTo(200.0).Within(0.1));
				}
			}
		}
	}
}
