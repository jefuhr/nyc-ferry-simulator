using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The learner's fog signal (Inland Rule 35(a) and (b), Rule 32(c), Annex III). Failure modes guarded: an interval of
	/// more than 2 minutes; a blast outside 4 to 6 s; the stopped signal given while making way or the other way round; a
	/// signal in clear weather or with an unlimited setting; signals timed by frames or wall time instead of physics steps,
	/// so a replay or another frame rate would sound them at other times; and a whistle outside Annex III's band for a
	/// vessel under 75 m.
	/// </summary>
	public class FogSignalScheduleTests
	{
		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		[Test]
		public void TheSchedule_KeepsRule35sIntervalAndRule32sProlongedBlast()
		{
			Assert.That(FogSignalSchedule.IntervalSeconds, Is.LessThanOrEqualTo(120.0), "Rule 35: at intervals of not more than 2 minutes");
			Assert.That(FogSignalSchedule.IntervalSteps, Is.EqualTo((long)(FogSignalSchedule.IntervalSeconds * SimulationClock.StepsPerSecond)));
			Assert.That(FogSignalSchedule.ProlongedBlastSeconds, Is.InRange(4.0, 6.0), "Rule 32(c): a prolonged blast is 4 to 6 seconds");
			Assert.That(FogSignalSchedule.TwoBlastGapSeconds, Is.EqualTo(2.0), "Rule 35(b): about 2 seconds between them");
			Assert.That(FogSignalSchedule.WhistleHertz, Is.InRange(250.0, 700.0), "Annex III, 33 CFR 86.01(b)(iii): 250-700 Hz under 75 m");
		}

		[TestCase(0.0, true, true)]
		[TestCase(4.99, true, true)]
		[TestCase(5.0, false, false)]
		[TestCase(6.99, false, false)]
		[TestCase(7.0, false, true)]
		[TestCase(11.99, false, true)]
		[TestCase(12.0, false, false)]
		[TestCase(119.0, false, false)]
		public void ABlast_SoundsForItsPattern(double secondsSinceSignal, bool makingWay, bool stopped)
		{
			Assert.That(FogSignalSchedule.IsBlasting(FogSignalKind.OneProlonged, secondsSinceSignal), Is.EqualTo(makingWay), "Rule 35(a): one prolonged blast");
			Assert.That(FogSignalSchedule.IsBlasting(FogSignalKind.TwoProlonged, secondsSinceSignal), Is.EqualTo(stopped), "Rule 35(b): two prolonged blasts, 2 s apart");
		}

		[Test]
		public void MakingWay_IsSpeedThroughTheWater()
		{
			Assert.That(FogSignalSchedule.KindFor(0.0), Is.EqualTo(FogSignalKind.TwoProlonged));
			Assert.That(FogSignalSchedule.KindFor(Units.KnotsToMetresPerSecond(0.1)), Is.EqualTo(FogSignalKind.TwoProlonged));
			Assert.That(FogSignalSchedule.KindFor(Units.KnotsToMetresPerSecond(0.2)), Is.EqualTo(FogSignalKind.OneProlonged));
			Assert.That(FogSignalSchedule.KindFor(Units.KnotsToMetresPerSecond(-1.0)), Is.EqualTo(FogSignalKind.OneProlonged), "making sternway is making way");
			Assert.That(FogSignalSchedule.KindFor(Units.KnotsToMetresPerSecond(10.0)), Is.EqualTo(FogSignalKind.OneProlonged));
		}

		[TestCase(0.0, false)]
		[TestCase(5000.0, false)]
		[TestCase(1852.0, false)]
		[TestCase(1000.0, true)]
		[TestCase(200.0, true)]
		public void Signals_SoundOnlyInRestrictedVisibility_OnTheIntervalSteps(double visibility, bool sounds)
		{
			Assert.That(FogSignalSchedule.IsSignalStep(0, visibility), Is.EqualTo(sounds));
			Assert.That(FogSignalSchedule.IsSignalStep(FogSignalSchedule.IntervalSteps, visibility), Is.EqualTo(sounds));
			Assert.That(FogSignalSchedule.IsSignalStep(FogSignalSchedule.IntervalSteps - 1, visibility), Is.False);
			Assert.That(FogSignalSchedule.IsSignalStep(1, visibility), Is.False);
		}

		[Test]
		public void TheSignals_FollowTheRunsSteps_NotItsFrames()
		{
			var coarse = Run(new[] { 0.5 }, 250.0);
			var uneven = Run(new[] { 0.013, 0.31, 0.07, 1.0 / 60.0 }, 250.0);

			Assert.That(coarse.Select(e => e.Step), Is.EqualTo(new[] { 0L, 12000L, 24000L }));
			Assert.That(coarse.Select(e => e.Time), Is.EqualTo(new[] { 0.0, 120.0, 240.0 }));
			Assert.That(uneven, Is.EqualTo(coarse), "the same steps, times and kinds at another frame rate");
			Assert.That(coarse[0].Kind, Is.EqualTo(FogSignalKind.TwoProlonged), "at rest at the start: underway but stopped");
			Assert.That(coarse[1].Kind, Is.EqualTo(FogSignalKind.OneProlonged), "ahead on both engines: making way");
		}

		[Test]
		public void InClearWeather_NoSignalIsRecorded()
		{
			Assert.That(Run(new[] { 0.5 }, MeteorologicalVisibility.Unlimited), Is.Empty);
			Assert.That(Run(new[] { 0.5 }, 3000.0), Is.Empty);
		}

		[Test]
		public void TheRecording_GivesTheSameSignalsAgain_FromItsVisibilityAndSpeedThroughTheWater()
		{
			var folder = SliceTestData.ArtifactFolder("fog-signal-recompute");
			var live = Run(new[] { 0.5 }, 500.0, folder);
			var recording = RecordedRun.Load(_host.GetComponent<VesselSimulationRunner>().LastRecordingPath);

			Assert.That(recording.VisibilityMetres, Is.EqualTo(500.0));
			Assert.That(live, Has.Count.EqualTo(3));
			Assert.That(FogSignalSchedule.Recompute(recording), Is.EqualTo(live), "a debrief recomputes exactly what was sounded");

			var clear = Run(new[] { 0.5 }, MeteorologicalVisibility.Unlimited, SliceTestData.ArtifactFolder("fog-signal-recompute-clear"));
			Assert.That(clear, Is.Empty);
			Assert.That(FogSignalSchedule.Recompute(RecordedRun.Load(_host.GetComponent<VesselSimulationRunner>().LastRecordingPath)), Is.Empty);
		}

		private List<FogSignalEvent> Run(double[] frames, double visibility, string recordingFolder = null)
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_host = new GameObject("fog signal schedule (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = recordingFolder != null;
			if (recordingFolder != null)
			{
				runner.RecordingDirectory = recordingFolder;
			}

			runner.MaxStepsPerFrame = 100;
			var position = LocalTangentPlane.World.ToGeodetic(250.0, 700.0);
			var scenario = new ScenarioSettings
			{
				name = "fog signal schedule",
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = 49.0,
				scenarioStartUtc = "2025-07-20T02:00:00Z",
				visibilityMetres = visibility,
			};
			runner.Initialize(scenario, new ConstantControls(new ControlInputs(0.5, 0.5, 0.0)), SliceTestData.LoadTClass());
			var recorder = new FogSignalRecorder(visibility);
			runner.Driver.AddObserver(recorder);
			var frame = 0;
			while (runner.Driver.StepCount < 25000)
			{
				runner.AdvanceFrame(frames[frame++ % frames.Length]);
			}

			runner.Driver.RemoveObserver(recorder);
			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			return recorder.Events.ToList();
		}
	}
}
