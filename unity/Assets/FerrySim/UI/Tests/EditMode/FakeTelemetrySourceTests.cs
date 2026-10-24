using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The CSV playback source used for UI development. Guards against: interpolated values that the
	/// real fixed-step source would never produce (it must hold the most recent row), playback running
	/// off either end of the recording, the interface returning values that differ from the CSV, a vessel
	/// id from a CSV header escaping the vessels folder, an empty source pretending to have data, telemetry
	/// claimed before the first row or after the end, and a run id that does not change for a new
	/// playback (or repeats one).
	/// </summary>
	public class FakeTelemetrySourceTests
	{
		private GameObject _host;
		private FakeTelemetrySource _source;

		private static string SamplePath => UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv);

		[SetUp]
		public void SetUp()
		{
			_host = new GameObject("FakeTelemetrySourceTests");
			_source = _host.AddComponent<FakeTelemetrySource>();
		}

		[TearDown]
		public void TearDown() => Object.DestroyImmediate(_host);

		[Test]
		public void BeforeLoading_ItSaysItHasNoTelemetry()
		{
			Assert.That(_source.HasTelemetry, Is.False);
			Assert.That(_source.Recording, Is.Null);
		}

		[Test]
		public void LoadCsv_ReadsTheRecordingAndTheVesselNamedInItsHeader()
		{
			Assert.That(_source.TryLoadCsv(SamplePath), Is.True, _source.LoadError);

			Assert.That(_source.HasTelemetry, Is.True);
			Assert.That(_source.Recording.Count, Is.EqualTo(310));
			Assert.That(_source.Parameters, Is.Not.Null);
			Assert.That(_source.Parameters.Id, Is.EqualTo("t-class"));
			Assert.That(_source.State.Time, Is.EqualTo(35.0), "starts at the first row");
		}

		[Test]
		public void LoadCsv_AcceptsRepoRelativePaths()
		{
			Assert.That(_source.TryLoadCsv(UIRepoPaths.SampleCrashStopCsv), Is.True, _source.LoadError);
		}

		[Test]
		public void Seek_HoldsTheMostRecentRowLikeAFixedStepSource()
		{
			_source.TryLoadCsv(SamplePath);

			_source.Seek(45.05);

			Assert.That(_source.PlaybackTime, Is.EqualTo(45.05));
			Assert.That(_source.State.Time, Is.EqualTo(45.0), "no interpolation between rows");
			Assert.That(_source.Controls.PortLever, Is.EqualTo(-1.0), "controls that start applying at 45.0 s");
		}

		[Test]
		public void Seek_ClampsToTheRecording()
		{
			_source.TryLoadCsv(SamplePath);

			_source.Seek(0.0);
			Assert.That(_source.State.Time, Is.EqualTo(35.0));
			_source.Seek(1000.0);
			Assert.That(_source.State.Time, Is.EqualTo(65.89).Within(1e-9));
		}

		[Test]
		public void Advance_LoopsBackToTheStart()
		{
			_source.TryLoadCsv(SamplePath);
			_source.Loop = true;
			_source.Seek(65.8);

			_source.Advance(1.0);

			// 66.8 s is 31.8 s into a 30.89 s recording, so 0.91 s past the start.
			Assert.That(_source.PlaybackTime, Is.EqualTo(35.91).Within(1e-9));
			Assert.That(_source.State.Time, Is.EqualTo(35.9).Within(1e-9));
		}

		[Test]
		public void Advance_StopsAtTheEndWithoutLooping()
		{
			_source.TryLoadCsv(SamplePath);
			_source.Loop = false;
			_source.IsPlaying = true;
			_source.Seek(65.8);

			_source.Advance(1.0);

			Assert.That(_source.PlaybackTime, Is.EqualTo(65.89).Within(1e-9));
			Assert.That(_source.IsPlaying, Is.False);
		}

		[Test]
		public void InterfaceValues_MatchTheRecordedSample()
		{
			_source.TryLoadCsv(SamplePath);
			var recording = _source.Recording;
			var index = recording.IndexAtOrBefore(50.0);
			var expected = recording.Sample(index);

			_source.Seek(50.0);
			IVesselTelemetrySource source = _source;

			Assert.That(source.State, Is.EqualTo(expected.State));
			Assert.That(source.Controls, Is.EqualTo(expected.Controls));
			Assert.That(source.Telemetry.Port.EngineSpeed, Is.EqualTo(expected.Telemetry.Port.EngineSpeed));
			Assert.That(source.Telemetry.Starboard.ShaftSpeed, Is.EqualTo(expected.Telemetry.Starboard.ShaftSpeed));
			Assert.That(source.Telemetry.Port.Gear, Is.EqualTo(GearState.Astern));
			Assert.That(source.Telemetry.SpeedOverGround, Is.EqualTo(expected.Telemetry.SpeedOverGround));
			Assert.That(source.Telemetry.DepthUnderKeel, Is.EqualTo(expected.Telemetry.DepthUnderKeel));
			Assert.That(source.Parameters, Is.SameAs(_source.Parameters));
		}

		[Test]
		public void UnknownVessel_LeavesParametersEmptyWithoutFailing()
		{
			var text = System.IO.File.ReadAllText(SamplePath).Replace("# vessel: t-class", "# vessel: no-such-boat");

			_source.Load(TelemetryCsv.Parse(text, "renamed.csv"), VesselFiles.TryLoad("no-such-boat", out var problem));

			Assert.That(_source.HasTelemetry, Is.True);
			Assert.That(_source.Parameters, Is.Null);
			StringAssert.Contains("no-such-boat", problem);
		}

		[TestCase("../vessels/t-class")]
		[TestCase("t-class/../../secrets")]
		[TestCase("T CLASS")]
		[TestCase("")]
		[TestCase(null)]
		public void UnsafeVesselIds_AreNeverResolvedToFiles(string id)
		{
			Assert.That(VesselFiles.TryLoad(id, out var problem), Is.Null);
			Assert.That(problem, Is.Not.Empty);
		}

		[Test]
		public void MissingCsv_ReportsTheProblemAndHasNoTelemetry()
		{
			Assert.That(_source.TryLoadCsv("no/such/file.csv"), Is.False);

			Assert.That(_source.HasTelemetry, Is.False);
			StringAssert.Contains("not found", _source.LoadError);
		}

		[Test]
		public void HasTelemetry_IsFalseBeforeTheFirstRowAndAfterTheEnd()
		{
			_source.TryLoadCsv(SamplePath);
			Assert.That(_source.HasTelemetry, Is.True, "loading shows the first row");

			_source.Seek(34.9);
			Assert.That(_source.HasTelemetry, Is.False, "before the first row");
			_source.Seek(35.0);
			Assert.That(_source.HasTelemetry, Is.True, "the first row");
			_source.Seek(65.89);
			Assert.That(_source.HasTelemetry, Is.True, "the last row");
			_source.Seek(66.0);
			Assert.That(_source.HasTelemetry, Is.False, "after the end");

			_source.Loop = false;
			_source.IsPlaying = true;
			_source.Seek(65.8);
			_source.Advance(1.0);
			Assert.That(_source.HasTelemetry, Is.False, "playing off the end without looping ends the run");
			Assert.That(_source.IsPlaying, Is.False);
		}

		[Test]
		public void RunId_ChangesForEveryLoadAndReplayStart_AndIsNeverReused()
		{
			var seen = new System.Collections.Generic.HashSet<long>();
			_source.TryLoadCsv(SamplePath);
			var run = _source.RunId;
			Assert.That(seen.Add(run), Is.True);

			_source.Seek(50.0);
			_source.Advance(1.0);
			_source.IsPlaying = false;
			_source.Advance(0.5);
			Assert.That(_source.RunId, Is.EqualTo(run), "playing forward, pausing and resuming stay in the run");

			_source.Seek(40.0);
			Assert.That(seen.Add(_source.RunId), Is.True, "seeking back replays from there: a new run");

			_source.Loop = true;
			_source.Seek(65.8);
			run = _source.RunId;
			_source.Advance(1.0);
			Assert.That(_source.RunId, Is.Not.EqualTo(run), "looping back to the start is a replay");
			Assert.That(seen.Add(_source.RunId), Is.True);

			_source.TryLoadCsv(SamplePath);
			Assert.That(seen.Add(_source.RunId), Is.True, "loading the same file again is a new run");

			var other = new GameObject("FakeTelemetrySourceTests other");
			try
			{
				var second = other.AddComponent<FakeTelemetrySource>();
				second.TryLoadCsv(SamplePath);
				Assert.That(seen.Add(second.RunId), Is.True, "run ids are unique across sources in a process");
			}
			finally
			{
				Object.DestroyImmediate(other);
			}
		}

		[Test]
		public void ScenarioStart_ParsesIsoUtcAndIgnoresGarbage()
		{
			Assert.That(_source.SetScenarioStart("2026-06-15T13:00:00Z"), Is.True);
			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero)));

			Assert.That(_source.SetScenarioStart("next tuesday"), Is.False);
			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero)));
		}
	}
}
