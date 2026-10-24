using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// Acceptance 1: the Unity-driven bridge and a headless loop agree on the same 120 s trace (both levers ahead,
	/// then hard over to starboard at 60 s). The runner steps from its own <c>Update</c> in the player loop at a
	/// simulated 30 fps; the reference is a plain loop over the same core in this process (Mono), since CoreCLR
	/// and Mono may differ in the last bits. The difference from a CLI CSV of the same trace is reported in
	/// artifacts/unity/bridge/trajectory-report.md when that CSV exists.
	/// Failure modes guarded: inputs applied a step early or late, steps lost or repeated across frames, the
	/// runner using Unity's FixedUpdate or a frame-dependent step, and the recording disagreeing with the physics.
	/// </summary>
	public class TrajectoryMatchTests
	{
		private const long Steps = 12000;
		private const double PositionTolerance = 1e-6;
		private const double HeadingTolerance = 1e-9;
		private const string CliCsvName = "cli-ahead-then-hard-over.csv";

		private float _savedCaptureDeltaTime;
		private VesselSimulationRunner _runner;

		[SetUp]
		public void SetUp() => _savedCaptureDeltaTime = Time.captureDeltaTime;

		[TearDown]
		public void TearDown()
		{
			Time.captureDeltaTime = _savedCaptureDeltaTime;
			if (_runner == null) return;
			try { _runner.EndRun(); }
			finally
			{
				UnityEngine.Object.DestroyImmediate(_runner.gameObject);
				_runner = null;
			}
		}

		[UnityTest]
		public IEnumerator UnityDrivenRun_MatchesAPlainLoop_AheadThenHardOver()
		{
			var vessel = PlayModeSupport.LoadTClass();
			var trace = ControlTrace.Load(PlayModeSupport.FixturePath("ahead-then-hard-over.csv"));
			Assume.That(trace.EndStep, Is.EqualTo(Steps));

			// Reference: a plain loop over the same core, in this process.
			var reference = new VesselState[Steps];
			var referenceDigest = new StateDigest();
			var plain = new VesselDynamicsFactory().Create(vessel, UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			for (long step = 0; step < Steps; step++)
			{
				plain.Step(trace.At(step));
				reference[step] = plain.State;
				referenceDigest.Add(plain.State);
			}

			// Unity-driven: the runner in the player loop, at the world origin heading north like the CLI's runs.
			var folder = PlayModeSupport.FreshArtifactFolder("trajectory");
			var scenario = new ScenarioSettings { name = "ahead-then-hard-over" };
			var runner = _runner = PlayModeSupport.NewRunner("TrajectoryVessel", scenario, trace, vessel, folder, sampleHz: 100);
			runner.Driver.StepLimit = Steps;
			var log = new StepLog();
			runner.Driver.AddObserver(log);
			Time.captureDeltaTime = 1f / 30f;
			var startFrame = Time.frameCount;

			yield return PlayModeSupport.Frames(() => runner.Driver.AtStepLimit, 20000);

			var frames = Time.frameCount - startFrame;
			Time.captureDeltaTime = _savedCaptureDeltaTime;
			Assert.That(runner.Driver.StepCount, Is.EqualTo(Steps), $"the runner reached {runner.Driver.StepCount} steps in {frames} frames");
			Assert.That(runner.Driver.TotalDroppedTime, Is.EqualTo(0.0), "no frame fell behind");
			var digest = runner.Recorder.Digest.Text;
			runner.EndRun();
			var recording = runner.LastRecordingPath;

			var maxPosition = 0.0;
			var maxHeading = 0.0;
			var identical = 0;
			for (var i = 0; i < Steps; i++)
			{
				var a = log.States[i];
				var b = reference[i];
				maxPosition = Math.Max(maxPosition, Math.Sqrt((a.North - b.North) * (a.North - b.North) + (a.East - b.East) * (a.East - b.East)));
				maxHeading = Math.Max(maxHeading, Math.Abs(Angles.WrapPi(a.Heading - b.Heading)));
				identical += a.Equals(b) ? 1 : 0;
			}

			var report = Report(log.States[(int)Steps - 1], frames, maxPosition, maxHeading, identical, digest, referenceDigest.Text, recording);
			File.WriteAllText(Path.Combine(PlayModeSupport.ArtifactsDir, "trajectory-report.md"), report);
			TestContext.WriteLine(report);

			Assert.That(log.States.Count, Is.EqualTo(Steps));
			Assert.That(maxPosition, Is.LessThanOrEqualTo(PositionTolerance), "position difference, metres");
			Assert.That(maxHeading, Is.LessThanOrEqualTo(HeadingTolerance), "heading difference, radians");
			Assert.That(log.States[(int)Steps - 1].Time, Is.EqualTo(120.0));
			Assert.That(Math.Abs(log.States[5999].YawRate), Is.LessThan(0.01), "straight while the helm is amidships");
			Assert.That(log.States[(int)Steps - 1].YawRate, Is.GreaterThan(0.05), "turning to starboard after hard over");
			Assert.That(digest, Is.EqualTo(referenceDigest.Text), "the recording's digest covers the same states");
		}

		private static string Report(VesselState end, int frames, double maxPosition, double maxHeading, int identical, string runnerDigest, string referenceDigest, string recording)
		{
			var invariant = CultureInfo.InvariantCulture;
			var builder = new StringBuilder();
			builder.AppendLine("# Trajectory match: ahead then hard over (120 s, t-class, calm)");
			builder.AppendLine();
			builder.AppendLine("Trace: `unity/Assets/FerrySim/Sim/Tests/Fixtures/ahead-then-hard-over.csv` (both levers 1.0, helm 0; helm +1 at 60 s).");
			builder.AppendLine();
			builder.AppendLine("## Unity-driven runner vs plain loop (same Unity process, Mono)");
			builder.AppendLine();
			builder.AppendLine($"- frames: {frames} at a simulated 30 fps (`Time.captureDeltaTime`), runner stepping from `Update`");
			builder.AppendLine($"- steps compared: {Steps}, bit-identical states: {identical}");
			builder.AppendLine($"- max position difference: {maxPosition.ToString("R", invariant)} m (tolerance {PositionTolerance:0e0} m)");
			builder.AppendLine($"- max heading difference: {maxHeading.ToString("R", invariant)} rad (tolerance {HeadingTolerance:0e0} rad)");
			builder.AppendLine($"- state digest, runner recording: `{runnerDigest}`; plain loop: `{referenceDigest}`");
			builder.AppendLine($"- end state (Mono): `{StateDigest.Describe(end)}`");
			builder.AppendLine($"- recording: `{Relative(recording)}`");
			builder.AppendLine();
			builder.AppendLine("## Unity recording vs CLI CSV of the same trace (CoreCLR)");
			builder.AppendLine();
			var cliPath = Path.Combine(PlayModeSupport.ArtifactsDir, CliCsvName);
			if (!File.Exists(cliPath))
			{
				builder.AppendLine($"Not compared: `{Relative(cliPath)}` not found. Generate it with:");
				builder.AppendLine();
				builder.AppendLine($"    dotnet run --project core/dotnet/FerrySim.Cli -- run --vessel vessels/t-class.json --inputs unity/Assets/FerrySim/Sim/Tests/Fixtures/ahead-then-hard-over.csv --out artifacts/unity/bridge/{CliCsvName} --sample-hz 100");
				return builder.ToString();
			}

			TelemetryCsv.Split(File.ReadAllLines(recording), recording, out _, out var unityRows);
			TelemetryCsv.Split(File.ReadAllLines(cliPath), cliPath, out _, out var cliRows);
			var count = Math.Min(unityRows.Count, cliRows.Count);
			double maxNorth = 0.0, maxEast = 0.0, maxHeadingDeg = 0.0;
			var same = 0;
			for (var i = 0; i < count; i++)
			{
				same += unityRows[i] == cliRows[i] ? 1 : 0;
				var u = unityRows[i].Split(',');
				var c = cliRows[i].Split(',');
				maxNorth = Math.Max(maxNorth, Math.Abs(Parse(u[1]) - Parse(c[1])));
				maxEast = Math.Max(maxEast, Math.Abs(Parse(u[2]) - Parse(c[2])));
				var dh = Math.Abs(Parse(u[3]) - Parse(c[3]));
				maxHeadingDeg = Math.Max(maxHeadingDeg, Math.Min(dh, 360.0 - dh));
			}

			builder.AppendLine($"- rows: Unity {unityRows.Count}, CLI {cliRows.Count}; identical text: {same} of {count}");
			builder.AppendLine($"- max |north difference|: {maxNorth.ToString("0.###", invariant)} m, max |east difference|: {maxEast.ToString("0.###", invariant)} m (CSV resolution 0.001 m)");
			builder.AppendLine($"- max |heading difference|: {maxHeadingDeg.ToString("0.####", invariant)} deg (CSV resolution 0.0001 deg)");
			return builder.ToString();
		}

		private static double Parse(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

		private static string Relative(string path) =>
			path != null && path.StartsWith(PlayModeSupport.RepoRoot, StringComparison.Ordinal)
				? path.Substring(PlayModeSupport.RepoRoot.Length + 1)
				: path;
	}
}
