using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// Recording and replaying runs with traffic. Failure modes guarded: a recording that does not name its traffic pack, digest
	/// and the vessel it left out, or loses its events; a traffic run that does not replay bit for bit, events included; a replay
	/// against another day's traffic, or a changed pack, that silently diverges instead of being refused with both digests named
	/// (through <see cref="ReplayPlayer.Verify"/> and the runner, which keeps its run); a run without traffic given a traffic pack;
	/// an automatic leave-out chosen again at replay instead of the recorded one; a run without traffic writing any traffic key;
	/// a start alongside a moored vessel recorded as a collision, or not replayed bit for bit; an alarm still up at the end
	/// recorded without its minimum so far, or that snapshot left out of the replay's check.
	/// </summary>
	public class TrafficRecordingTests
	{
		private TrafficTestPacks _packs;
		private GameObject _host;
		private string _folder;

		[SetUp]
		public void SetUp()
		{
			_packs = new TrafficTestPacks();
			_folder = Path.Combine(_packs.Root, "runs");
		}

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_packs.Dispose();
		}

		private VesselSimulationRunner Runner()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_host = new GameObject("traffic recording runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = true;
			runner.RecordingDirectory = _folder;
			runner.TelemetrySampleHz = 10;
			runner.EnvironmentPacks = _packs.EnvironmentPacks;
			runner.TrafficPacks = _packs.TrafficPacks;
			runner.MaxStepsPerFrame = 100;
			return runner;
		}

		private static VesselParameters Vessel => VesselParametersJson.Load(Path.Combine(Application.dataPath, "..", "..", "vessels", "t-class.json"));

		private TrafficPack HeadOnPacks()
		{
			var pack = _packs.WriteTraffic(TrafficTestPacks.Date, TrafficTestPacks.Track.Straight(366000001, 43100.0, 1300.0, 0.0, 4.0, Math.PI, 12),
				Ferry(367000001, 30.0, 20.0));
			// Another day with the same vessels: another digest.
			_packs.WriteTraffic(TrafficTestPacks.OtherDate, TrafficTestPacks.Track.Straight(366000001, 43100.0, 1300.0, 0.0, 4.0, Math.PI, 12));
			return pack;
		}

		private static TrafficTestPacks.Track Ferry(int mmsi, double north, double east)
		{
			var ferry = TrafficTestPacks.Track.Straight(mmsi, 43140.0, north, east, 0.0, 0.0, 5);
			ferry.Ferry = true;
			ferry.Class = TrafficClass.FerryPassenger;
			return ferry;
		}

		private RecordedRun Record(ScenarioSettings scenario, int frames = 120)
		{
			var runner = Runner();
			runner.Initialize(scenario, new ConstantControls(new ControlInputs(0.6, 0.6, 0.0)), Vessel);
			for (var frame = 0; frame < frames; frame++)
			{
				runner.AdvanceFrame(1.0);
			}

			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			return RecordedRun.Load(runner.LastRecordingPath);
		}

		[Test]
		public void ATrafficRunsRecording_NamesItsPack_AndKeepsItsEvents()
		{
			var pack = HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.Automatic));
			var keys = run.Header.Keys.ToList();
			Assert.That(run.Header["traffic"], Is.EqualTo("pack"));
			Assert.That(run.Header["traffic_pack"], Is.EqualTo(TrafficTestPacks.Date));
			Assert.That(run.Header["traffic_digest"], Is.EqualTo(pack.Digest));
			Assert.That(run.Header["traffic_epoch_utc"], Is.EqualTo("2025-07-14T04:00:00Z"));
			Assert.That(run.Header["traffic_leave_out"], Is.EqualTo("auto"));
			Assert.That(run.Header["traffic_removed_mmsi"], Is.EqualTo("367000001"), "the ferry 36 m from the start");
			Assert.That(run.Header["traffic_events"], Is.EqualTo(Path.GetFileNameWithoutExtension(run.TelemetryPath) + ".traffic.csv"));
			Assert.That(keys.IndexOf("traffic"), Is.GreaterThan(keys.IndexOf("world_geometry")), "after the world geometry");
			Assert.That(keys.IndexOf("traffic_events"), Is.GreaterThan(keys.IndexOf("param.inputs")));
			Assert.That((run.HasTraffic, run.TrafficPack, run.TrafficDigest, run.TrafficRemovedMmsi, run.TrafficLeaveOut), Is.EqualTo((true, TrafficTestPacks.Date, pack.Digest, 367000001, TrafficLeaveOut.Automatic)));
			var log = run.TrafficEvents;
			Assert.That(log.Header["traffic_digest"], Is.EqualTo(pack.Digest));
			Assert.That(log.Header["cpa_alarm_m"], Is.EqualTo("100"));
			Assert.That(log.Header["tcpa_alarm_s"], Is.EqualTo("120"));
			Assert.That(log.Records.Select(r => r.Kind).Take(2), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised, TrafficEventKind.CollisionStarted }));
			Assert.That(log.Records.All(r => r.Mmsi == 366000001), Is.True, "the left-out ferry raised nothing");
			Assert.That(log.Trailer["events"], Is.EqualTo(log.Records.Count.ToString()));
			Assert.That(log.Trailer["steps"], Is.EqualTo("12000"));
			Assert.That(log.Digest, Does.StartWith("fnv1a64:"));
		}

		[Test]
		public void ARunWithoutTraffic_WritesNoTrafficKeyAndNoLog()
		{
			HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario(trafficDay: string.Empty), 10);
			Assert.That(run.Header.Keys.Where(k => k.StartsWith("traffic", StringComparison.Ordinal)), Is.Empty);
			Assert.That(run.HasTraffic, Is.False);
			Assert.That(Directory.GetFiles(_folder, "*.traffic.csv"), Is.Empty);
			Assert.That(Directory.GetFiles(_folder).Length, Is.EqualTo(2), "telemetry and control trace, as before traffic");
		}

		[Test]
		public void ATrafficRun_ReplaysBitForBit_EventsIncluded_ThroughVerifyAndTheRunner()
		{
			HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.Automatic));
			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.Traffic, Is.Not.Null);
			Assert.That(result.Traffic.Compared, Is.EqualTo(run.TrafficEvents.Records.Count));
			Assert.That(result.Traffic.ReplayedDigest, Is.EqualTo(run.TrafficEvents.Digest));
			var runner = Runner();
			runner.RecordRuns = false;
			runner.InitializeReplay(run, Vessel);
			Assert.That(runner.Traffic.Removed.Mmsi, Is.EqualTo(367000001), "the recorded leave-out, not a new choice");
			for (var frame = 0; frame < 130 && !runner.ReplayCompleted; frame++)
			{
				runner.AdvanceFrame(1.0);
			}

			Assert.That(runner.ReplayCompleted, Is.True);
			Assert.That(runner.ReplayResult.Matches, Is.True, runner.ReplayResult.ToString());
			Assert.That(runner.ReplayResult.Traffic.Compared, Is.EqualTo(run.TrafficEvents.Records.Count));
		}

		[Test]
		public void AStartAlongside_IsRecorded_WithNoAlarmOrCollision_AndReplaysBitForBit()
		{
			// A moored barge 40 by 10 m, its side 0.5 m off the T-class's starboard side at the start: within the alongside margin,
			// not overlapping. The run goes ahead at 0.6 and leaves it behind.
			var barge = TrafficTestPacks.Track.Straight(366000009, 43100.0, 0.0, 9.5, 0.0, 0.0, 12);
			barge.Moving = false;
			barge.Length = 40.0;
			barge.Name = "MOORED BARGE";
			_packs.WriteTraffic(TrafficTestPacks.Date, barge);
			var run = Record(TrafficTestPacks.Scenario());
			var log = run.TrafficEvents;
			Assert.That(log.Header["alongside_margin_m"], Is.EqualTo("5"));
			Assert.That(log.Records.Select(r => (r.Kind, r.Mmsi)).ToArray(), Is.EqualTo(new[] { (TrafficEventKind.AlongsideStarted, 366000009), (TrafficEventKind.AlongsideEnded, 366000009) }),
				"alongside at the first step, parted as it went ahead: no alarm, no collision");
			Assert.That(log.Records[0].Step, Is.EqualTo(1));
			Assert.That(log.Records[1].StartStep, Is.EqualTo(1));
			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.Traffic.Compared, Is.EqualTo(2));
			Assert.That(result.Traffic.ReplayedDigest, Is.EqualTo(log.Digest));
		}

		[Test]
		public void ARunEndingWithItsAlarmUp_RecordsTheAlarmsMinimumSoFar_AndTheReplayChecksIt()
		{
			// 30 s of the head-on approach: the tug's alarm is up, the two still hundreds of metres apart, when the run ends.
			HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.Automatic), 30);
			var log = run.TrafficEvents;
			Assert.That(log.Records.Select(r => r.Kind).ToArray(), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised }));
			Assert.That(log.HasOpenSnapshot, Is.True);
			Assert.That(log.Trailer["open_episodes"], Is.EqualTo("1"));
			var open = log.Open.Single();
			Assert.That((open.Kind, open.Mmsi, open.StartStep, open.Step), Is.EqualTo((TrafficEventKind.AlarmOpen, 366000001, log.Records[0].Step, 3000L)), "a snapshot at the last step, not an end");
			Assert.That(open.MinimumCpa, Is.LessThanOrEqualTo(log.Records[0].Cpa));
			var result = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.Traffic.OpenCompared, Is.EqualTo(1), "the snapshot is replayed too");

			// A snapshot that says otherwise does not replay.
			var events = Path.Combine(Path.GetDirectoryName(run.TelemetryPath), run.Header["traffic_events"]);
			var lines = File.ReadAllLines(events).ToList();
			var row = lines.FindIndex(l => l.Contains(",alarm-open,366000001,"));
			lines[row] = lines[row].Replace(",alarm-open,366000001,", ",alarm-open,366000002,");
			File.WriteAllLines(events, lines);
			var edited = ReplayPlayer.Verify(RecordedRun.Load(run.TelemetryPath), Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(edited.RowsMatch && edited.DigestMatches, Is.True, "the vessel's run itself replays");
			Assert.That(edited.TrafficMatches, Is.False);
			Assert.That(edited.ToString(), Does.Contain("open episode").And.Contain("366000002"));
		}

		[Test]
		public void AnotherDaysTraffic_OrAChangedPack_IsRefused_NamingBothDigests()
		{
			var pack = HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario());
			var other = _packs.TrafficPacks.Load(TrafficTestPacks.OtherDate);
			var refused = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, traffic: other);
			Assert.That(refused.Matches, Is.False);
			Assert.That(refused.RowsCompared, Is.Zero, "nothing replayed");
			Assert.That(refused.ToString(), Does.Contain(pack.Digest).And.Contain(other.Digest));
			var runner = Runner();
			runner.Initialize(TrafficTestPacks.Scenario(trafficDay: string.Empty), new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.5);
			var going = runner.RunId;
			var error = Assert.Throws<InvalidOperationException>(() => runner.InitializeReplay(run, Vessel, null, other));
			Assert.That(error.Message, Does.Contain(pack.Digest).And.Contain(other.Digest));
			Assert.That(runner.RunId, Is.EqualTo(going), "the run that was going goes on");
			Assert.That(runner.IsRunActive, Is.True);

			// The same day's pack, changed and rebuilt: another digest, refused the same way through the default lookup.
			var changed = _packs.WriteTraffic(TrafficTestPacks.Date, TrafficTestPacks.Track.Straight(366000001, 43100.0, 1301.0, 0.0, 4.0, Math.PI, 12), Ferry(367000001, 30.0, 20.0));
			var stale = ReplayPlayer.Verify(run, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(stale.Matches, Is.False);
			Assert.That(stale.ToString(), Does.Contain(pack.Digest).And.Contain(changed.Digest));
			// A run without traffic given a traffic pack.
			var plain = Record(TrafficTestPacks.Scenario(trafficDay: string.Empty), 5);
			var plainRefused = ReplayPlayer.Verify(plain, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, traffic: other);
			Assert.That(plainRefused.Matches, Is.False);
			Assert.That(plainRefused.ToString(), Does.Contain("no traffic").And.Contain(other.Digest));
		}

		[Test]
		public void ARecordingWhoseEventsDiffer_DoesNotReplay()
		{
			HeadOnPacks();
			var run = Record(TrafficTestPacks.Scenario());
			var events = Path.Combine(Path.GetDirectoryName(run.TelemetryPath), run.Header["traffic_events"]);
			var lines = File.ReadAllLines(events).ToList();
			var row = lines.FindIndex(l => l.Contains(",alarm,366000001,"));
			Assert.That(row, Is.GreaterThan(0), "the tug's alarm is logged");
			lines[row] = lines[row].Replace(",alarm,366000001,", ",alarm,366000002,");
			File.WriteAllLines(events, lines);
			var edited = RecordedRun.Load(run.TelemetryPath);
			var result = ReplayPlayer.Verify(edited, Vessel, new VesselDynamicsFactory(), packs: _packs.EnvironmentPacks, trafficPacks: _packs.TrafficPacks);
			Assert.That(result.RowsMatch && result.DigestMatches, Is.True, "the vessel's run itself replays");
			Assert.That(result.TrafficMatches, Is.False, "but not its traffic");
			Assert.That(result.ToString(), Does.Contain("traffic event").And.Contain("differs").And.Contain("366000002"));
			File.Delete(events);
			Assert.Throws<FileNotFoundException>(() => RecordedRun.Load(run.TelemetryPath), "a traffic run without its events is no complete recording");
		}
	}
}
