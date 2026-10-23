using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Speed calibration and the standard manoeuvres (ITTC 7.5-04-02-01 definitions) for both classes,
	/// asserted against provisional plausibility bands and written to
	/// <c>artifacts/core/dynamics-report.md</c>. The bands are set a priori from the sources listed in
	/// core/FerrySim.Core/Dynamics/README.md (IMO MSC.137(76) criteria for conventional ships, the Delft 372
	/// catamaran results of Duman and Bal 2022, and first-principles bounds). No public full-scale trials of
	/// comparable 25 to 35 m twin-screw catamarans were found, so every band is provisional until the model
	/// is calibrated against AIS tracks and crew figures.
	/// </summary>
	public class StandardManeuverTests
	{
		private static readonly string[] Classes = { "t-class.json", "k-class.json" };
		private readonly Dictionary<string, Results> _results = new Dictionary<string, Results>();

		[OneTimeSetUp]
		public void RunManeuvers()
		{
			foreach (var file in Classes)
			{
				var parameters = ManeuverRunner.Load(file);
				var results = new Results { Parameters = parameters, Length = parameters.Particulars.LengthWaterline.Value };
				for (var i = 0; i <= 10; i++)
				{
					var lever = i / 10.0;
					results.SpeedByLever[i] = ManeuverRunner.SteadySpeed(parameters, lever, out var simulation);
					results.EngineRpmByLever[i] = Units.RadiansPerSecondToRpm(simulation.Telemetry.Port.EngineSpeed);
				}

				var serviceSpeed = parameters.Performance.ServiceSpeed.Value;
				results.ServiceLever = ManeuverRunner.LeverForSpeed(parameters, serviceSpeed);
				results.Starboard = ManeuverRunner.TurningCircle(parameters, results.ServiceLever, 1.0);
				results.Port = ManeuverRunner.TurningCircle(parameters, results.ServiceLever, -1.0);
				var hardOver = parameters.Controls.HardOverAngle.Value;
				results.ZigZag = ManeuverRunner.ZigZag(parameters, results.ServiceLever, Angles.ToRadians(10.0), Angles.ToRadians(10.0));
				results.ZigZag20 = ManeuverRunner.ZigZag(parameters, results.ServiceLever, Math.Min(hardOver, Angles.ToRadians(20.0)), Angles.ToRadians(20.0));
				results.CrashStop = ManeuverRunner.CrashStop(parameters, results.ServiceLever);
				var idle = parameters.Controls.ClutchInLever.Value + 0.5 * (parameters.Controls.ThrottleStartLever.Value - parameters.Controls.ClutchInLever.Value);
				results.IdleSplit = ManeuverRunner.Pivot(parameters, idle, -idle, 60.0);
				results.ModerateSplit = ManeuverRunner.Pivot(parameters, 0.35, -0.35, 60.0);
				results.TimeToService = ManeuverRunner.TimeToSpeed(parameters, serviceSpeed);
				_results[file] = results;
			}

			WriteReport();
		}

		[TestCase("k-class.json")]
		public void KClass_FullAhead_IsThePublishedMaximum(string file)
		{
			var max = _results[file].Parameters.Performance.MaxSpeed.Value;
			Assert.That(_results[file].SpeedByLever[10], Is.EqualTo(max).Within(0.05 * max), "within 5% of 30 kn in calm deep water");
		}

		[TestCase("t-class.json")]
		public void TClass_FullAhead_ReachesTheRunningSpeed_AndTheAssumedMaximum(string file)
		{
			var results = _results[file];
			Assert.That(results.SpeedByLever[10], Is.GreaterThanOrEqualTo(results.Parameters.Performance.ServiceSpeed.Value), "25 kn running speed is reachable");
			var assumed = results.Parameters.Performance.MaxSpeed.Value;
			Assert.That(results.SpeedByLever[10], Is.EqualTo(assumed).Within(0.03 * assumed), "calibrated to the assumed 26 kn maximum");
		}

		[TestCaseSource(nameof(Classes))]
		public void SpeedRisesWithLever_AndIsZeroInTheNeutralBand(string file)
		{
			var results = _results[file];
			var clutchIn = results.Parameters.Controls.ClutchInLever.Value;
			for (var i = 0; i <= 10; i++)
			{
				if (i / 10.0 < clutchIn)
				{
					Assert.That(results.SpeedByLever[i], Is.EqualTo(0.0).Within(1e-9), $"lever {i / 10.0:F1} is in neutral");
				}

				if (i > 0)
				{
					Assert.That(results.SpeedByLever[i], Is.GreaterThanOrEqualTo(results.SpeedByLever[i - 1]), $"lever {i / 10.0:F1}");
				}
			}
		}

		[TestCaseSource(nameof(Classes))]
		public void TurningCircle_HardOverAtServiceSpeed_IsPlausible(string file)
		{
			var results = _results[file];
			var turn = results.Starboard;
			var length = results.Length;
			Assert.That(turn.Advance / length, Is.InRange(2.0, 6.0), "advance, ship lengths");
			Assert.That(turn.TacticalDiameter / length, Is.InRange(2.0, 7.0), "tactical diameter, ship lengths");
			Assert.That(turn.Transfer / length, Is.InRange(0.8, 3.5), "transfer, ship lengths");
			Assert.That(turn.TimeTo180, Is.InRange(10.0, 45.0), "time to 180°, s");
			Assert.That(1.0 - turn.SteadySpeed / turn.ApproachSpeed, Is.InRange(0.15, 0.45), "speed loss in the steady turn");
			Assert.That(-Angles.ToDegrees(turn.SteadyRoll), Is.InRange(0.2, 6.0), "steady heel outward (port side down in a starboard turn), degrees");
		}

		[TestCaseSource(nameof(Classes))]
		public void TurningCircle_IsSymmetric(string file)
		{
			var results = _results[file];
			Assert.That(results.Port.TacticalDiameter, Is.EqualTo(results.Starboard.TacticalDiameter).Within(0.01 * results.Starboard.TacticalDiameter));
			Assert.That(results.Port.SteadyRoll, Is.EqualTo(-results.Starboard.SteadyRoll).Within(1e-3));
		}

		[TestCaseSource(nameof(Classes))]
		public void ZigZag10_Overshoots_ArePlausible(string file)
		{
			var zigZag = _results[file].ZigZag;
			Assert.That(Angles.ToDegrees(zigZag.FirstOvershoot), Is.InRange(1.0, 15.0), "first overshoot, degrees");
			Assert.That(Angles.ToDegrees(zigZag.SecondOvershoot), Is.InRange(1.0, 25.0), "second overshoot, degrees");
		}

		[TestCaseSource(nameof(Classes))]
		public void CrashStop_FromServiceSpeed_IsPlausible(string file)
		{
			var results = _results[file];
			var stop = results.CrashStop;
			var controls = results.Parameters.Controls;
			var transmission = results.Parameters.Transmission;
			Assert.That(stop.HeadReach / results.Length, Is.InRange(1.5, 10.0), "head reach, ship lengths");
			Assert.That(stop.TimeToStop, Is.InRange(10.0, 60.0), "time to stop, s");
			Assert.That(stop.TimeToAsternEngaged, Is.GreaterThanOrEqualTo(controls.NeutralDwell.Value + transmission.ClutchFillTime.Value),
				"astern only after the release, the neutral dwell and the clutch fill");
		}

		[TestCaseSource(nameof(Classes))]
		public void IdleSplit_PivotsBriskly_WithLittleWalk(string file)
		{
			var pivot = _results[file].IdleSplit;
			Assert.That(Angles.ToDegrees(pivot.YawRate), Is.InRange(1.0, 8.0), "pivot rate with one engine ahead idle and one astern idle, degrees per second");
			Assert.That(pivot.DriftSpeed, Is.LessThan(2.0), "forward walk from the weaker astern thrust, m/s");
		}

		[TestCaseSource(nameof(Classes))]
		public void Acceleration_ToServiceSpeed_IsPlausible(string file)
		{
			Assert.That(_results[file].TimeToService, Is.InRange(10.0, 90.0), "seconds from rest at full ahead");
		}

		private void WriteReport()
		{
			var text = new StringBuilder();
			var c = CultureInfo.InvariantCulture;
			text.AppendLine("# Vessel dynamics report");
			text.AppendLine();
			text.AppendLine("Generated by `StandardManeuverTests` (calm deep water, 50 m, no wind or current; runs start from rest). Bands are provisional until calibration.");
			foreach (var file in Classes)
			{
				var r = _results[file];
				var length = r.Length;
				text.AppendLine();
				text.AppendLine(string.Format(c, "## {0} (LWL {1:F1} m)", r.Parameters.DisplayName, length));
				text.AppendLine();
				text.AppendLine("Steady speed in calm deep water, both levers together:");
				text.AppendLine();
				text.AppendLine("| lever | speed (kn) | engine (rpm) |");
				text.AppendLine("|---|---|---|");
				for (var i = 0; i <= 10; i++)
				{
					text.AppendLine(string.Format(c, "| {0:F1} | {1:F2} | {2:F0} |", i / 10.0, Units.MetresPerSecondToKnots(r.SpeedByLever[i]), r.EngineRpmByLever[i]));
				}

				text.AppendLine();
				text.AppendLine(string.Format(c, "Service speed {0:F1} kn at lever {1:F3}. Time from rest at full ahead to service speed: {2:F1} s.",
					Units.MetresPerSecondToKnots(r.Parameters.Performance.ServiceSpeed.Value), r.ServiceLever, r.TimeToService));
				text.AppendLine();
				text.AppendLine("| manoeuvre | result | band |");
				text.AppendLine("|---|---|---|");
				var t = r.Starboard;
				Row(text, c, "turn, hard over to starboard: advance", "{0:F0} m ({1:F2} L)", t.Advance, t.Advance / length, "2.0 to 6.0 L");
				Row(text, c, "transfer", "{0:F0} m ({1:F2} L)", t.Transfer, t.Transfer / length, "0.8 to 3.5 L");
				Row(text, c, "tactical diameter", "{0:F0} m ({1:F2} L)", t.TacticalDiameter, t.TacticalDiameter / length, "2.0 to 7.0 L");
				Row(text, c, "time to 90° / 180°", "{0:F1} s / {1:F1} s", t.TimeTo90, t.TimeTo180, "180° in 10 to 45 s");
				Row(text, c, "steady turn: diameter, yaw rate", "{0:F0} m, {1:F2} °/s", t.SteadyTurningDiameter, Angles.ToDegrees(t.SteadyYawRate), "-");
				Row(text, c, "steady turn: speed, loss", "{0:F1} kn, {1:F0}%", Units.MetresPerSecondToKnots(t.SteadySpeed), 100.0 * (1.0 - t.SteadySpeed / t.ApproachSpeed), "15% to 45%");
				Row(text, c, "steady turn: heel (outward), peak", "{0:F2}°, {1:F2}°", -Angles.ToDegrees(t.SteadyRoll), Angles.ToDegrees(t.MaximumRoll), "0.2° to 6° outward");
				Row(text, c, "turn to port: tactical diameter", "{0:F0} m ({1:F2} L)", r.Port.TacticalDiameter, r.Port.TacticalDiameter / length, "symmetric");
				Row(text, c, "10/10 zig-zag: first / second overshoot", "{0:F1}° / {1:F1}°", Angles.ToDegrees(r.ZigZag.FirstOvershoot), Angles.ToDegrees(r.ZigZag.SecondOvershoot), "1° to 15° / 1° to 25°");
				Row(text, c, "20/20 zig-zag: first / second overshoot", "{0:F1}° / {1:F1}°", Angles.ToDegrees(r.ZigZag20.FirstOvershoot), Angles.ToDegrees(r.ZigZag20.SecondOvershoot), "reported");
				var s = r.CrashStop;
				Row(text, c, "crash stop: head reach", "{0:F0} m ({1:F2} L)", s.HeadReach, s.HeadReach / length, "1.5 to 10 L");
				Row(text, c, "crash stop: time to stop", "{0:F1} s", s.TimeToStop, 0.0, "10 to 60 s");
				Row(text, c, "crash stop: astern engaged / shafts turning astern after", "{0:F1} s / {1:F1} s", s.TimeToAsternEngaged, s.TimeToShaftsReversed, "after release, dwell and fill");
				Row(text, c, "crash stop: lateral deviation, heading change", "{0:F1} m, {1:F1}°", s.LateralDeviation, Angles.ToDegrees(s.HeadingChange), "reported");
				var p = r.IdleSplit;
				Row(text, c, "pivot, ahead idle / astern idle: yaw rate", "{0:F2} °/s", Angles.ToDegrees(p.YawRate), 0.0, "1 to 8 °/s");
				Row(text, c, "pivot, idle split: walk", "{0:F2} m/s at {1:F0}° off the bow", p.DriftSpeed, Angles.ToDegrees(p.DriftDirectionRelativeToHeading), "below 2 m/s");
				var m = r.ModerateSplit;
				Row(text, c, "pivot, levers ±0.35: yaw rate, walk", "{0:F2} °/s, {1:F2} m/s", Angles.ToDegrees(m.YawRate), m.DriftSpeed, "reported");
				Row(text, c, "pivot, levers ±0.35: shaft rpm port / starboard", "{0:F0} / {1:F0}", m.PortShaftRpm, m.StarboardShaftRpm, "reported");
			}

			var directory = Path.Combine(RepoPaths.Root, "artifacts", "core");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "dynamics-report.md"), text.ToString());
			TestContext.Out.Write(text.ToString());
		}

		private static void Row(StringBuilder text, CultureInfo culture, string name, string format, double a, double b, string band) =>
			text.AppendLine("| " + name + " | " + string.Format(culture, format, a, b) + " | " + band + " |");

		private sealed class Results
		{
			public VesselParameters Parameters;
			public double Length;
			public readonly double[] SpeedByLever = new double[11];
			public readonly double[] EngineRpmByLever = new double[11];
			public double ServiceLever;
			public TurningCircleResult Starboard;
			public TurningCircleResult Port;
			public ZigZagResult ZigZag;
			public ZigZagResult ZigZag20;
			public CrashStopResult CrashStop;
			public PivotResult IdleSplit;
			public PivotResult ModerateSplit;
			public double TimeToService;
		}
	}
}
