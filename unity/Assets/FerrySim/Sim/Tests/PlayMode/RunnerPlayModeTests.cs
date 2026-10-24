using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// The runner MonoBehaviour in the player loop. Failure modes guarded: physics that changes with the frame
	/// rate (acceptance 2), the render pose not between the last two physics states, pause that keeps stepping,
	/// reset that keeps the old clock, recordings left open or missing, a slow frame that steps without bound or
	/// drops time silently, and the telemetry interface reporting a different state from the physics.
	/// </summary>
	public class RunnerPlayModeTests
	{
		private float _savedCaptureDeltaTime;

		[SetUp]
		public void SetUp() => _savedCaptureDeltaTime = Time.captureDeltaTime;

		[TearDown]
		public void TearDown()
		{
			Time.captureDeltaTime = _savedCaptureDeltaTime;
			foreach (var runner in Object.FindObjectsByType<VesselSimulationRunner>(FindObjectsSortMode.None))
			{
				Object.Destroy(runner.gameObject);
			}
		}

		private static ControlTrace ManoeuvringTrace()
		{
			var trace = new ControlTrace();
			trace.Append(0, new ControlInputs(1.0, 1.0, 0.0));
			trace.Append(300, new ControlInputs(0.7, -0.5, 0.4));
			trace.Append(700, new ControlInputs(-0.3, 0.9, -1.0));
			return trace;
		}

		private static ScenarioSettings Scenario() => new ScenarioSettings
		{
			name = "runner-tests",
			startLatitude = 40.7040,
			startLongitude = -74.0010,
			startHeadingDegrees = 35.0,
			startSpeedKnots = 6.0,
			environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 1.2, currentTowardDegrees = 210.0, windSpeedKnots = 12.0, windFromDegrees = 290.0, depthMetres = 12.0 },
		};

		private static IEnumerator RunAt(float framesPerSecond, long steps, StepLog log)
		{
			var runner = PlayModeSupport.NewRunner($"Runner{framesPerSecond}", Scenario(), ManoeuvringTrace(), PlayModeSupport.LoadTClass(), null);
			runner.Driver.StepLimit = steps;
			runner.Driver.AddObserver(log);
			Time.captureDeltaTime = 1f / framesPerSecond;
			yield return PlayModeSupport.Frames(() => runner.Driver.AtStepLimit, 100000);
			Assert.That(runner.Driver.StepCount, Is.EqualTo(steps), $"{framesPerSecond} fps run reached the step limit");
			Object.Destroy(runner.gameObject);
		}

		[UnityTest]
		public IEnumerator FrameRates30And144_GiveIdenticalPhysicsStatesAtTheSameSimulatedTime()
		{
			const long Steps = 1000;
			var at30 = new StepLog();
			var at144 = new StepLog();

			yield return RunAt(30f, Steps, at30);
			yield return RunAt(144f, Steps, at144);

			Assert.That(at30.States.Count, Is.EqualTo(Steps));
			Assert.That(at144.States.Count, Is.EqualTo(Steps));
			for (var i = 0; i < Steps; i++)
			{
				Assert.That(at144.States[i], Is.EqualTo(at30.States[i]), $"state at t = {SimulationClock.TimeAtStep(i + 1)} s");
			}
		}

		[UnityTest]
		public IEnumerator RenderPose_IsInterpolatedBetweenTheLastTwoPhysicsStates()
		{
			var runner = PlayModeSupport.NewRunner("Interpolation", Scenario(), new ConstantControls(new ControlInputs(1.0, 1.0, 0.3)), PlayModeSupport.LoadTClass(), null);
			Time.captureDeltaTime = 1f / 144f;
			for (var frame = 0; frame < 300; frame++)
			{
				yield return null;
				var driver = runner.Driver;
				var pose = runner.RenderPose;
				var expected = VesselPose.Interpolate(driver.PreviousState, driver.CurrentState, driver.InterpolationFactor);
				Assert.That(pose.North, Is.EqualTo(expected.North).Within(1e-9));
				Assert.That(pose.East, Is.EqualTo(expected.East).Within(1e-9));
				Assert.That(pose.Heading, Is.EqualTo(expected.Heading).Within(1e-12));
				var placement = runner.FlatPlacement;
				var position = runner.transform.localPosition;
				Assert.That(position.z, Is.EqualTo((float)(pose.North - placement.RenderOriginNorth)).Within(1e-4f));
				Assert.That(position.x, Is.EqualTo((float)(pose.East - placement.RenderOriginEast)).Within(1e-4f));
			}

			Assert.That(runner.State, Is.EqualTo(runner.Driver.CurrentState), "telemetry source reports the physics state");
			Assert.That(runner.Controls, Is.EqualTo(new ControlInputs(1.0, 1.0, 0.3)));
		}

		[UnityTest]
		public IEnumerator Pause_StopsTheClock_AndReset_StartsANewRecordedRun()
		{
			var folder = PlayModeSupport.FreshArtifactFolder("runner-lifecycle");
			var runner = PlayModeSupport.NewRunner("Lifecycle", Scenario(), new ConstantControls(new ControlInputs(0.5, 0.5, 0.0)), PlayModeSupport.LoadTClass(), folder);
			var started = 0;
			var ended = 0;
			runner.RunStarted += () => started++;
			runner.RunEnded += () => ended++;
			Time.captureDeltaTime = 1f / 50f;
			yield return PlayModeSupport.Frames(() => false, 60);
			Assert.That(runner.Driver.StepCount, Is.InRange(118L, 122L), "60 frames at 50 fps are 1.2 s");

			runner.Pause();
			var pausedAt = runner.Driver.StepCount;
			yield return PlayModeSupport.Frames(() => false, 30);
			Assert.That(runner.Driver.StepCount, Is.EqualTo(pausedAt), "paused: no steps");
			runner.Resume();
			yield return PlayModeSupport.Frames(() => false, 10);
			Assert.That(runner.Driver.StepCount, Is.GreaterThan(pausedAt));

			var firstRecording = runner.Recorder.TelemetryPath;
			runner.ResetRun();
			Assert.That(runner.Driver.StepCount, Is.EqualTo(0));
			Assert.That(runner.State.Time, Is.EqualTo(0.0));
			Assert.That(runner.LastRecordingPath, Is.EqualTo(firstRecording), "the first run's recording is closed");
			Assert.That(runner.Recorder.TelemetryPath, Is.Not.EqualTo(firstRecording), "the new run records to a new file");
			Assert.That(started, Is.EqualTo(1));
			Assert.That(ended, Is.EqualTo(1));
			yield return PlayModeSupport.Frames(() => false, 10);
			runner.EndRun();

			var recordings = Directory.GetFiles(folder, "*.csv").Where(p => !p.EndsWith(RunRecorder.InputsSuffix)).ToArray();
			Assert.That(recordings.Length, Is.EqualTo(2));
			foreach (var recording in recordings)
			{
				var result = ReplayPlayer.Verify(RecordedRun.Load(recording), runner.Parameters, new VesselDynamicsFactory());
				Assert.That(result.Matches, Is.True, Path.GetFileName(recording) + ": " + result);
			}
		}

		[UnityTest]
		public IEnumerator InSceneReplay_PlaysARecordingBackAndChecksEveryState()
		{
			var folder = PlayModeSupport.FreshArtifactFolder("runner-replay");
			var vessel = PlayModeSupport.LoadTClass();
			var recorder = PlayModeSupport.NewRunner("Recorder", Scenario(), ManoeuvringTrace(), vessel, folder);
			recorder.Driver.StepLimit = 900;
			Time.captureDeltaTime = 1f / 45f;
			yield return PlayModeSupport.Frames(() => recorder.Driver.AtStepLimit, 2000);
			recorder.EndRun();
			var recording = recorder.LastRecordingPath;
			Object.Destroy(recorder.gameObject);

			var player = new GameObject("Replayer").AddComponent<VesselSimulationRunner>();
			player.InitializeReplay(RecordedRun.Load(recording), vessel);
			Assert.That(player.IsReplaying, Is.True);
			LogAssert.Expect(LogType.Log, new Regex("Replay of .*: replay matches all .* and every state bit for bit"));
			yield return PlayModeSupport.Frames(() => player.IsPaused, 2000);

			Assert.That(player.IsPaused, Is.True, "the replay stops at the end of the recording");
			Assert.That(player.Driver.StepCount, Is.EqualTo(900L));
			Assert.That(player.ReplayResult.Matches, Is.True, player.ReplayResult.ToString());
			Assert.That(Directory.GetFiles(folder, "*.csv").Length, Is.EqualTo(2), "one recording (telemetry and inputs); a replay is not recorded again");
		}

		[Test]
		public void WorldGeometry_ReachesTheFactory_AndTheRecordingNamesIt()
		{
			var folder = PlayModeSupport.FreshArtifactFolder("runner-geometry");
			var factory = new GeometryCapturingFactory();
			var geometry = new SegmentListGeometry(new[]
			{
				new ContactSegment(new HorizontalVector(0.0, 0.0), new HorizontalVector(10.0, 0.0), new ContactSurface(1e5, 1e4, 0.3, 0.3), 7),
			});
			var runner = new GameObject("Geometry").AddComponent<VesselSimulationRunner>();
			runner.Factory = factory;
			runner.WorldGeometry = geometry;
			runner.RecordRuns = true;
			runner.RecordingDirectory = folder;

			runner.Initialize(Scenario(), new ConstantControls(ControlInputs.Neutral), PlayModeSupport.LoadTClass());
			runner.EndRun();

			Assert.That(factory.LastGeometry, Is.SameAs(geometry), "the four-argument Create receives the world's geometry");
			Assert.That(RecordedRun.Load(runner.LastRecordingPath).Header["world_geometry"], Is.EqualTo(typeof(SegmentListGeometry).FullName));
			Object.Destroy(runner.gameObject);
		}

		private sealed class GeometryCapturingFactory : ISimulationFactory
		{
			private readonly VesselDynamicsFactory _inner = new VesselDynamicsFactory();

			public IWorldGeometry LastGeometry { get; private set; }

			public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState) =>
				Create(parameters, environment, EmptyWorldGeometry.Instance, initialState);

			public ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState)
			{
				LastGeometry = geometry;
				return _inner.Create(parameters, environment, geometry, initialState);
			}
		}

		[UnityTest]
		public IEnumerator SlowFrame_IsCappedLoggedAndCounted()
		{
			var runner = PlayModeSupport.NewRunner("SlowFrame", Scenario(), new ConstantControls(ControlInputs.Neutral), PlayModeSupport.LoadTClass(), null);
			runner.MaxStepsPerFrame = 10;
			LogAssert.Expect(LogType.Warning, new Regex(@"Frame fell behind: dropped 0\.20 s"));

			var advance = runner.AdvanceFrame(0.3);

			Assert.That(advance.Steps, Is.EqualTo(10));
			Assert.That(advance.DroppedTime, Is.EqualTo(0.2).Within(1e-9));
			Assert.That(runner.Driver.DroppedFrameCount, Is.EqualTo(1));
			yield return null;
		}
	}
}
