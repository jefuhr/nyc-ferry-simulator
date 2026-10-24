using System;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Recording and replay. Failure modes guarded: inputs written with too few digits (the replay then drifts),
	/// a row or trace change one step out, the final instant missing, a recording that cannot be rebuilt
	/// (initial state, environment or sample rate lost), and a replay check that passes because the CSV rounding
	/// hides a divergence in the last bits.
	/// </summary>
	public class RecorderReplayTests
	{
		private static readonly VesselParameters Vessel = TestSupport.LoadTClass();

		/// <summary>Underway in a current and a crosswind, with smoothed, never-round inputs that change every step.</summary>
		private static ScenarioSettings DemandingScenario() => new ScenarioSettings
		{
			name = "replay-roundtrip",
			startLatitude = 40.7040,
			startLongitude = -74.0010,
			startHeadingDegrees = 35.0,
			startSpeedKnots = 4.0,
			environment = new ScenarioEnvironment
			{
				calm = false,
				currentSpeedKnots = 1.3,
				currentTowardDegrees = 200.0,
				windSpeedKnots = 14.0,
				windFromDegrees = 300.0,
				depthMetres = 9.5,
			},
		};

		private static IControlInputSource WobblyInputs()
		{
			var smoother = new ControlInputSmoother(0.05);
			return new ScriptedControls(step =>
			{
				var t = step / 100.0;
				var raw = new ControlInputs(0.2 * Math.Floor(t / 7.0) % 1.2, 0.37 + 0.3 * Math.Sin(t * 0.21), t > 40.0 ? Math.Sin(t * 0.05) : 0.0);
				if (step == 0)
				{
					smoother.Reset();
				}

				return smoother.Step(raw);
			});
		}

		private static RunRecorder Record(string folder, string name, ScenarioSettings scenario, IControlInputSource inputs, long steps, int sampleHz, StepLog log)
		{
			var factory = new VesselDynamicsFactory();
			var environment = scenario.environment.Create();
			var initial = scenario.InitialState(environment);
			var simulation = factory.Create(Vessel, environment, initial);
			var header = RunRecorder.BuildHeader(scenario, Vessel, simulation, factory.GetType().FullName, initial, environment, sampleHz);
			var recorder = RunRecorder.Create(folder, name, sampleHz, header);
			var driver = new FixedStepDriver(simulation, inputs);
			driver.AddObserver(recorder);
			if (log != null)
			{
				driver.AddObserver(log);
			}

			// Irregular frame times, as a live run would see.
			var random = new Random(17);
			driver.StepLimit = steps;
			while (!driver.AtStepLimit)
			{
				driver.Advance(0.005 + random.NextDouble() * 0.03);
			}

			recorder.Finish(driver.StepCount, simulation.State, simulation.Telemetry);
			return recorder;
		}

		[Test]
		public void Replay_FromTheRecordedFiles_ReproducesEveryStateExactly()
		{
			var folder = TestSupport.FreshArtifactFolder("replay-roundtrip");
			var live = new StepLog();
			var recorder = Record(folder, "replay-roundtrip", DemandingScenario(), WobblyInputs(), 9037, 10, live);

			var run = RecordedRun.Load(recorder.TelemetryPath);
			var replayed = new StepLog();
			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), replayed);

			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.RowsCompared, Is.EqualTo(905), "rows every 0.1 s plus the final row");
			Assert.That(result.RecordedDigest, Is.Not.Null);
			Assert.That(result.ReplayedDigest, Is.EqualTo(result.RecordedDigest));
			Assert.That(replayed.States.Count, Is.EqualTo(live.States.Count));
			for (var i = 0; i < live.States.Count; i++)
			{
				Assert.That(replayed.States[i], Is.EqualTo(live.States[i]), $"state after step {i + 1}");
				Assert.That(replayed.Controls[i], Is.EqualTo(live.Controls[i]), $"controls of step {i}");
			}
		}

		[Test]
		public void Replay_CatchesADivergenceTheCsvRoundingWouldHide()
		{
			var folder = TestSupport.FreshArtifactFolder("replay-tamper");
			var recorder = Record(folder, "replay-tamper", DemandingScenario(), WobblyInputs(), 6000, 10, null);
			var lines = File.ReadAllLines(recorder.InputsPath).ToList();
			var victim = lines.FindIndex(l => l.StartsWith("50.00,", StringComparison.Ordinal));
			Assume.That(victim, Is.GreaterThan(0));
			var cells = lines[victim].Split(',');
			// The helm is tracking (the rudder follows the order), so a nano-unit nudge changes the physics.
			cells[3] = (double.Parse(cells[3], CultureInfo.InvariantCulture) + 1e-9).ToString("R", CultureInfo.InvariantCulture);
			lines[victim] = string.Join(",", cells);
			File.WriteAllLines(recorder.InputsPath, lines);

			var result = ReplayPlayer.Verify(RecordedRun.Load(recorder.TelemetryPath), Vessel, new VesselDynamicsFactory());

			Assert.That(result.RowsMatch, Is.True, "the nudge is below CSV precision");
			Assert.That(result.DigestMatches, Is.False, "the state digest must see it");
			Assert.That(result.Matches, Is.False);
		}

		[Test]
		public void Recorder_WritesRowsAtTheSampleRateAndAFinalRow()
		{
			var folder = TestSupport.FreshArtifactFolder("recorder-rows");
			var trace = new ControlTrace();
			trace.Append(0, new ControlInputs(0.4, 0.4, 0.0));
			trace.Append(500, new ControlInputs(0.4, 0.4, 0.5));
			var recorder = Record(folder, "rows", new ScenarioSettings(), trace, 1234, 10, null);

			TelemetryCsv.Split(File.ReadAllLines(recorder.TelemetryPath), "rows", out var header, out var rows);
			Assert.That(rows.Count, Is.EqualTo(125), "0.0 to 12.3 s every 0.1 s, then the final row at 12.34 s");
			Assert.That(rows[0], Does.StartWith("0.000,"));
			Assert.That(rows[1], Does.StartWith("0.100,"));
			Assert.That(rows[124], Does.StartWith("12.340,"));
			Assert.That(rows[50], Does.StartWith("5.000,").And.EndWith(",0.4000,0.4000,0.5000"), "the row at a change carries the new controls");
			Assert.That(rows[49], Does.EndWith(",0.4000,0.4000,0.0000"));

			var inputs = File.ReadAllLines(recorder.InputsPath);
			var data = inputs.Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToArray();
			Assert.That(data, Is.EqualTo(new[] { ControlTrace.HeaderRow, "0.00,0.4,0.4,0", "5.00,0.4,0.4,0.5", "12.34,0.4,0.4,0.5" }), "changes only, plus the closing row");
			Assert.That(inputs.Any(l => l.StartsWith("# state_digest: fnv1a64:", StringComparison.Ordinal)), Is.True);
			Assert.That(inputs.Any(l => l == "# steps: 1234"), Is.True);

			foreach (var key in new[] { "vessel", "maneuver", "maneuver_start_s", "environment", "model", "factory", "physics_hz", "sample_hz", "initial_heading_deg", "param.inputs" })
			{
				Assert.That(header.ContainsKey(key), Is.True, $"CLI header key '{key}' missing");
			}

			Assert.That(header["sample_hz"], Is.EqualTo("10"));
			Assert.That(header["param.inputs"], Is.EqualTo("rows.inputs.csv"));
		}

		[Test]
		public void Recorder_HeaderRebuildsTheExactRun()
		{
			var folder = TestSupport.FreshArtifactFolder("recorder-header");
			var scenario = DemandingScenario();
			var environment = scenario.environment.Create();
			var initial = scenario.InitialState(environment);
			var recorder = Record(folder, "header", scenario, new ConstantControls(new ControlInputs(0.3, 0.3, 0.0)), 20, 100, null);

			var run = RecordedRun.Load(recorder.TelemetryPath);

			Assert.That(run.InitialState, Is.EqualTo(initial));
			Assert.That(run.Environment.Current, Is.EqualTo(environment.Current));
			Assert.That(run.Environment.Wind, Is.EqualTo(environment.Wind));
			Assert.That(run.Environment.Depth, Is.EqualTo(environment.Depth));
			Assert.That(run.SampleHz, Is.EqualTo(100));
			Assert.That(run.VesselFile, Is.EqualTo("vessels/t-class.json"));
			Assert.That(run.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.Zero)));
			Assert.That(run.Header["environment"], Is.EqualTo("current=1.3kn@200,wind=14kn@from300,depth=9.5m"));
		}

		[Test]
		public void Recorder_RejectsSampleRatesThatDoNotDivideThePhysicsRate()
		{
			Assert.That(() => new RunRecorder(new StringWriter(), new StringWriter(), 3, null), Throws.InstanceOf<ArgumentOutOfRangeException>());
			Assert.That(() => new RunRecorder(new StringWriter(), new StringWriter(), 0, null), Throws.InstanceOf<ArgumentOutOfRangeException>());
		}
	}
}
