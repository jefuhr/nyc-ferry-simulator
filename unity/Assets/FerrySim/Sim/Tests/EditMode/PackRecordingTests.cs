using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Recording and replaying pack runs. Failure modes guarded: a recording that does not name its pack, digest and epoch
	/// (format ferrysim-env/1's keys), or still carries uniform env.* values that a replay would use instead; a calm or
	/// uniform header changing by a byte; a pack run that does not replay bit for bit; a replay against another day's pack,
	/// a changed pack or a missing one that silently diverges instead of being refused with both digests named (through
	/// <see cref="ReplayPlayer.Verify"/> and the runner's in-scene replay, which must leave the current run going); a
	/// calm recording replayed against a pack; and a header digest that is not a digest.
	/// </summary>
	public class PackRecordingTests
	{
		private static readonly VesselParameters Vessel = TestSupport.LoadTClass();

		private SyntheticPacks _packs;
		private GameObject _host;

		[SetUp]
		public void SetUp() => _packs = new SyntheticPacks();

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_packs.Dispose();
		}

		private VesselSimulationRunner Runner(string folder)
		{
			_host = new GameObject("pack recording runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordingDirectory = folder;
			runner.EnvironmentPacks = _packs.Packs;
			return runner;
		}

		private static ScenarioSettings Underway(string date = SyntheticPacks.SummerDate)
		{
			var position = LocalTangentPlane.World.ToGeodetic(-200.0, 100.0);
			return new ScenarioSettings
			{
				name = "pack recording",
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = 35.0,
				startSpeedKnots = 3.0,
				scenarioStartUtc = date == SyntheticPacks.SummerDate ? "2025-07-14T16:00:00.25Z" : "2025-01-14T17:00:00Z",
				environment = new ScenarioEnvironment { pack = SyntheticPacks.Name(date) },
			};
		}

		private static IControlInputSource Inputs() => new ScriptedControls(step => new ControlInputs(0.3 + 0.2 * Math.Sin(step * 0.003), 0.35, step > 600 ? 0.4 : 0.0));

		private string Record(ScenarioSettings scenario, string folder)
		{
			var runner = Runner(folder);
			runner.Initialize(scenario, Inputs(), Vessel);
			for (var frame = 0; frame < 120; frame++)
			{
				runner.AdvanceFrame(0.0123);
			}

			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			return runner.LastRecordingPath;
		}

		private string Digest(string date) => File.ReadAllText(Path.Combine(_packs.Root, SyntheticPacks.Region, "env", "days", date, "DIGEST")).Trim();

		[Test]
		public void APackRecording_NamesThePackItsDigestAndEpoch_AndNoUniformValues()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var path = Record(Underway(), TestSupport.FreshArtifactFolder("pack-recording-header"));

			var run = RecordedRun.Load(path);
			var keys = run.Header.Keys.ToList();
			Assert.That(run.Header["environment"], Is.EqualTo("pack"));
			Assert.That(run.Header["environment_pack"], Is.EqualTo("test-harbor/2025-07-14"));
			Assert.That(run.Header["environment_digest"], Is.EqualTo(Digest(SyntheticPacks.SummerDate)), "the day DIGEST");
			Assert.That(run.Header["environment_epoch_utc"], Is.EqualTo("2025-07-14T04:00:00Z"));
			Assert.That(run.Header["environment_wind_station"], Is.EqualTo("W1"));
			Assert.That(run.Header["scenario_start_utc"], Is.EqualTo("2025-07-14T16:00:00.25Z"), "to the tick");
			Assert.That(keys.IndexOf("environment_pack"), Is.EqualTo(keys.IndexOf("environment") + 1), "the pack lines follow environment, as the CLI writes them");
			Assert.That(keys.Any(key => key.StartsWith("env.", StringComparison.Ordinal)), Is.False);
			Assert.That(run.EnvironmentKind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(run.Environment, Is.Null);
			Assert.That(run.EnvironmentEpochUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero)));
		}

		[Test]
		public void TheCalmAndUniformHeaders_AreExactlyTheUniformBuildersHeader()
		{
			var factory = new VesselDynamicsFactory();
			foreach (var scenario in new[]
			{
				new ScenarioSettings(),
				new ScenarioSettings { environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 1.3, currentTowardDegrees = 200.0, windSpeedKnots = 14.0, windFromDegrees = 300.0, depthMetres = 9.5 } },
			})
			{
				var environment = scenario.CreateEnvironment(_packs.Packs);
				var initial = scenario.InitialState(environment.Observer);
				var simulation = factory.Create(Vessel, environment.StartRun(), initial);
				var uniform = RunRecorder.BuildHeader(scenario, Vessel, simulation, factory.GetType().FullName, initial, scenario.environment.Create(), 10);
				var built = RunRecorder.BuildHeader(scenario, Vessel, simulation, factory.GetType().FullName, initial, environment, 10);

				Assert.That(TelemetryCsv.FormatHeader(built), Is.EqualTo(TelemetryCsv.FormatHeader(uniform)), scenario.environment.SpecText());
			}
		}

		[Test]
		public void APackRun_ReplaysBitForBit()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var path = Record(Underway(), TestSupport.FreshArtifactFolder("pack-recording-replay"));

			var run = RecordedRun.Load(path);
			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.Packs);

			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.RecordedDigest, Is.Not.Null);
			Assert.That(result.RowsCompared, Is.EqualTo(run.Rows.Count));
		}

		[Test]
		public void AReplayAgainstAnotherDaysPack_IsRefused_NamingBothDigests()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			_packs.Write(SyntheticPacks.WinterDate);
			var run = RecordedRun.Load(Record(Underway(), TestSupport.FreshArtifactFolder("pack-recording-other-day")));
			var other = _packs.Packs.Load(SyntheticPacks.Name(SyntheticPacks.WinterDate));

			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), pack: other);

			Assert.That(result.Matches, Is.False);
			Assert.That(result.RowsCompared, Is.Zero, "nothing is replayed");
			Assert.That(result.ToString(), Does.Contain(Digest(SyntheticPacks.SummerDate)));
			Assert.That(result.ToString(), Does.Contain(Digest(SyntheticPacks.WinterDate)));
		}

		[Test]
		public void AReplayAgainstAChangedOrMissingPack_IsRefused()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var run = RecordedRun.Load(Record(Underway(), TestSupport.FreshArtifactFolder("pack-recording-changed")));
			var recorded = Digest(SyntheticPacks.SummerDate);

			// The same day rebuilt with other water levels: a new digest under the same name.
			_packs.Write(SyntheticPacks.SummerDate, levelOffset: 0.1);
			var changed = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.Packs);
			Assert.That(changed.Matches, Is.False);
			Assert.That(changed.ToString(), Does.Contain(recorded));
			Assert.That(changed.ToString(), Does.Contain(Digest(SyntheticPacks.SummerDate)));

			Directory.Delete(Path.Combine(_packs.Root, SyntheticPacks.Region, "env", "days", SyntheticPacks.SummerDate), true);
			var missing = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.Packs);
			Assert.That(missing.Matches, Is.False);
			Assert.That(missing.ToString(), Does.Contain(recorded));
			Assert.That(missing.ToString(), Does.Contain("was not found"));
		}

		[Test]
		public void TheRunnersReplay_RefusesAnotherDaysPack_AndKeepsTheCurrentRun_ThenReplaysTheRightOne()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			_packs.Write(SyntheticPacks.WinterDate);
			var folder = TestSupport.FreshArtifactFolder("pack-recording-runner");
			var run = RecordedRun.Load(Record(Underway(), folder));
			var runner = _host.GetComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			runner.Initialize(Underway(SyntheticPacks.WinterDate), Inputs(), Vessel);
			var current = runner.RunId;

			var error = Assert.Throws<InvalidOperationException>(() => runner.InitializeReplay(run, Vessel, _packs.Packs.Load(SyntheticPacks.Name(SyntheticPacks.WinterDate))));

			Assert.That(error.Message, Does.Contain(Digest(SyntheticPacks.SummerDate)));
			Assert.That(error.Message, Does.Contain(Digest(SyntheticPacks.WinterDate)));
			Assert.That(runner.RunId, Is.EqualTo(current), "the refused replay leaves the run going");
			Assert.That(runner.IsRunActive, Is.True);

			runner.InitializeReplay(run, Vessel);
			while (!runner.ReplayCompleted)
			{
				runner.AdvanceFrame(0.25);
			}

			Assert.That(runner.ReplayResult.Matches, Is.True, runner.ReplayResult.ToString());
			Assert.That(runner.RunEnvironment.Pack.Digest, Is.EqualTo(Digest(SyntheticPacks.SummerDate)));
		}

		[Test]
		public void ACalmRecordingReplayedAgainstAPack_IsRefused()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var path = Record(new ScenarioSettings { name = "calm recording" }, TestSupport.FreshArtifactFolder("pack-recording-calm"));
			var run = RecordedRun.Load(path);

			Assert.That(run.EnvironmentKind, Is.EqualTo(EnvironmentKind.Calm));
			Assert.That(ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory()).Matches, Is.True, "a calm recording replays as before");
			var refused = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), pack: _packs.Packs.Load(SyntheticPacks.Name(SyntheticPacks.SummerDate)));
			Assert.That(refused.Matches, Is.False);
			Assert.That(refused.ToString(), Does.Contain("environment calm"));
		}

		[Test]
		public void AHeaderDigestThatIsNotADigest_FailsToLoad()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var path = Record(Underway(), TestSupport.FreshArtifactFolder("pack-recording-bad-digest"));
			var lines = File.ReadAllLines(path);
			for (var i = 0; i < lines.Length; i++)
			{
				if (lines[i].StartsWith("# environment_digest:", StringComparison.Ordinal))
				{
					lines[i] = "# environment_digest: not-a-digest";
				}
			}

			File.WriteAllLines(path, lines);

			var error = Assert.Throws<FormatException>(() => RecordedRun.Load(path));
			Assert.That(error.Message, Does.Contain("environment_digest"));
		}
	}
}
