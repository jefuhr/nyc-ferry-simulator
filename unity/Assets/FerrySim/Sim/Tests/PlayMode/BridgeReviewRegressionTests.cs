using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// Review regressions: ended runs resuming, optional recording failures consuming physics time,
	/// replay observers surviving reset, digest-only divergence hidden by the UI, Assets output overrides,
	/// and acceptance fixtures leaking their producers when an assertion interrupts the workflow.
	/// </summary>
	public class BridgeReviewRegressionTests
	{
		private readonly List<GameObject> _objects = new List<GameObject>();

		[TearDown]
		public void TearDown()
		{
			foreach (var item in _objects)
			{
				if (item != null) Object.DestroyImmediate(item);
			}
			_objects.Clear();
			foreach (var panel in _panels) Object.DestroyImmediate(panel);
			_panels.Clear();
		}

		private VesselSimulationRunner Runner(string folder = null)
		{
			var runner = PlayModeSupport.NewRunner("ReviewRegression", new ScenarioSettings
			{
				startHeadingDegrees = 35.0,
				startSpeedKnots = 4.0,
			}, new ConstantControls(new ControlInputs(0.4, 0.4, 0.4)), PlayModeSupport.LoadTClass(), folder);
			_objects.Add(runner.gameObject);
			return runner;
		}

		private SimDebugReadout Readout(VesselSimulationRunner runner)
		{
			var go = new GameObject("ReviewReadout");
			_objects.Add(go);
			var panel = ScriptableObject.CreateInstance<PanelSettings>();
			go.AddComponent<UIDocument>().panelSettings = panel;
			var readout = go.AddComponent<SimDebugReadout>();
			readout.Runner = runner;
			// Keep the externally supplied panel's ownership with the test.
			_panels.Add(panel);
			return readout;
		}

		private readonly List<PanelSettings> _panels = new List<PanelSettings>();

		[UnityTest]
		public IEnumerator EndRun_FreezesStateRecordingAndEvents_UntilResetEvenAfterReenable()
		{
			var runner = Runner(PlayModeSupport.FreshArtifactFolder("review-ended-run"));
			runner.AdvanceFrame(0.1);
			var ended = 0;
			runner.RunEnded += () => ended++;
			runner.EndRun();
			var state = runner.State;
			var steps = runner.Driver.StepCount;
			var bytes = File.ReadAllBytes(runner.LastRecordingPath);
			for (var i = 0; i < 3; i++) yield return null;
			runner.Resume();
			runner.TogglePause();
			runner.Resume();
			runner.enabled = false;
			runner.enabled = true;
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(runner.AdvanceFrame(0.1).Steps, Is.Zero);
			Assert.That(runner.State, Is.EqualTo(state));
			Assert.That(runner.Driver.StepCount, Is.EqualTo(steps));
			Assert.That(File.ReadAllBytes(runner.LastRecordingPath), Is.EqualTo(bytes));
			Assert.That(ended, Is.EqualTo(1));
			runner.ResetRun();
			Assert.That(runner.AdvanceFrame(0.1).Steps, Is.EqualTo(10));
			// Disable a live run too: re-enable must retain its debrief state until reset.
			runner.enabled = false;
			state = runner.State;
			steps = runner.Driver.StepCount;
			bytes = File.ReadAllBytes(runner.LastRecordingPath);
			yield return null;
			runner.enabled = true;
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(runner.State, Is.EqualTo(state));
			Assert.That(runner.Driver.StepCount, Is.EqualTo(steps));
			Assert.That(runner.Recorder, Is.Null);
			Assert.That(File.ReadAllBytes(runner.LastRecordingPath), Is.EqualTo(bytes));
			runner.ResetRun();
			Assert.That(runner.AdvanceFrame(0.1).Steps, Is.EqualTo(10));
		}

		[TestCase(false, false)]
		[TestCase(true, false)]
		[TestCase(false, true)]
		public void RecorderFailure_DetachesClosesAndReports_WithoutChangingAnyPhysics(bool flushFailure, bool disposeFailure)
		{
			var runner = Runner(PlayModeSupport.FreshArtifactFolder("review-recorder-failure"));
			var reference = Runner();
			var log = new StepLog();
			runner.Driver.AddObserver(log);
			var recorder = runner.Recorder;
			var writerField = typeof(RunRecorder).GetField("_telemetry", BindingFlags.Instance | BindingFlags.NonPublic);
			var writer = new FailingWriter((TextWriter)writerField.GetValue(recorder), flushFailure, disposeFailure);
			writerField.SetValue(recorder, writer);
			var inputsField = typeof(RunRecorder).GetField("_inputs", BindingFlags.Instance | BindingFlags.NonPublic);
			var inputs = new FailingWriter((TextWriter)inputsField.GetValue(recorder), false, false);
			inputsField.SetValue(recorder, inputs);
			runner.AdvanceFrame(0.1);
			reference.AdvanceFrame(0.1);
			writer.Fail = true;
			LogAssert.Expect(LogType.Warning, new Regex("Recording incomplete.*injected recorder"));
			try
			{
				for (var frame = 0; frame < 30; frame++)
				{
					runner.AdvanceFrame(0.1);
					reference.AdvanceFrame(0.1);
					Assert.That(runner.State, Is.EqualTo(reference.State), "physics at frame " + frame);
					Assert.That(runner.Driver.StepCount, Is.EqualTo(reference.Driver.StepCount));
					Assert.That(runner.Driver.Accumulator, Is.EqualTo(reference.Driver.Accumulator));
				}
				Assert.That(runner.Recorder, Is.Null);
				Assert.That(runner.Driver.RemoveObserver(recorder), Is.False, "failed recorder detached");
				Assert.That(writer.Closed, Is.True);
				Assert.That(inputs.Closed, Is.True, "both writers close even if the first close fails");
				Assert.That(log.States.Count, Is.EqualTo(runner.Driver.StepCount), "unrelated observers still receive every step");
				var readout = Readout(runner);
				readout.Refresh();
				Assert.That(readout.Text, Does.Contain("RECORDING INCOMPLETE").And.Contain("injected recorder"));
				runner.EndRun();
				Assert.That(runner.LastRecordingPath, Is.Null, "a partial recording is never advertised as complete");
			}
			finally
			{
				writer.Fail = false;
				writer.ThrowOnDispose = false;
			}
		}

		[Test]
		public void ReplayReset_DetachesOldVerifiers_AndPreservesExternalObservers()
		{
			var runner = Runner(PlayModeSupport.FreshArtifactFolder("review-replay-reset"));
			runner.AdvanceFrame(0.2);
			runner.EndRun();
			var run = RecordedRun.Load(runner.LastRecordingPath);
			var log = new StepLog();
			runner.Driver.AddObserver(log);
			runner.InitializeReplay(run, runner.Parameters);
			var field = typeof(VesselSimulationRunner).GetField("_replayVerifier", BindingFlags.Instance | BindingFlags.NonPublic);
			for (var reset = 0; reset < 4; reset++)
			{
				runner.AdvanceFrame(0.01);
				var previous = (ReplayVerifier)field.GetValue(runner);
				var rows = previous.Result.RowsCompared;
				runner.ResetRun();
				runner.AdvanceFrame(0.01);
				Assert.That(previous.Result.RowsCompared, Is.EqualTo(rows), "old verifier receives no more rows");
				Assert.That(runner.Driver.RemoveObserver(previous), Is.False, "old verifier no longer retained");
			}
			var last = (ReplayVerifier)field.GetValue(runner);
			runner.RecordRuns = false;
			runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral), runner.Parameters);
			Assert.That(runner.Driver.RemoveObserver(last), Is.False, "live transition detaches the last replay verifier");
			var count = log.States.Count;
			runner.AdvanceFrame(0.1);
			Assert.That(log.States.Count - count, Is.EqualTo(10));
		}

		[Test]
		public void DigestOnlyDivergence_IsExplicitInTheReadout()
		{
			var scenario = new ScenarioSettings
			{
				startHeadingDegrees = 35.0, startSpeedKnots = 4.0,
				startLatitude = 40.7040, startLongitude = -74.0010,
				environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 1.3, currentTowardDegrees = 200.0, windSpeedKnots = 14.0, windFromDegrees = 300.0, depthMetres = 9.5 },
			};
			var smoother = new FerrySim.Controls.ControlInputSmoother(0.05);
			var controls = new ScriptedControls(step =>
			{
				if (step == 0) smoother.Reset();
				var time = step / 100.0;
				return smoother.Step(new ControlInputs(0.2 * Math.Floor(time / 7.0) % 1.2, 0.37 + 0.3 * Math.Sin(time * 0.21), time > 40.0 ? Math.Sin(time * 0.05) : 0.0));
			});
			var runner = PlayModeSupport.NewRunner("DigestUI", scenario, controls, PlayModeSupport.LoadTClass(), PlayModeSupport.FreshArtifactFolder("review-digest-ui"));
			_objects.Add(runner.gameObject);
			runner.Driver.StepLimit = 6000;
			while (!runner.Driver.AtStepLimit) runner.AdvanceFrame(0.1);
			var inputsPath = runner.Recorder.InputsPath;
			runner.EndRun();
			var lines = File.ReadAllLines(inputsPath);
			var victim = Array.FindIndex(lines, line => line.StartsWith("50.00,"));
			var cells = lines[victim].Split(',');
			cells[3] = (double.Parse(cells[3], System.Globalization.CultureInfo.InvariantCulture) + 1e-9).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
			lines[victim] = string.Join(",", cells);
			File.WriteAllLines(inputsPath, lines);
			runner.InitializeReplay(RecordedRun.Load(runner.LastRecordingPath), runner.Parameters);
			var readout = Readout(runner);
			readout.Refresh();
			Assert.That(readout.Text, Does.Contain("so far"));
			LogAssert.Expect(LogType.Error, new Regex("every row matches at CSV precision but the states differ"));
			while (!runner.Driver.AtStepLimit) runner.AdvanceFrame(0.1);
			Assert.That(runner.ReplayResult.RowsMatch, Is.True);
			Assert.That(runner.ReplayResult.DigestMatches, Is.False);
			readout.Refresh();
			Assert.That(readout.Text, Does.Contain("REPLAY DIVERGED").And.Not.Contain("so far"));
		}

		[TestCase("absolute-root")]
		[TestCase("absolute-child")]
		[TestCase("relative-child")]
		[TestCase("relative-dotdot")]
		public void RecordingOverride_RejectsAssetsBeforeCreatingAnyFiles(string form)
		{
			var runner = Runner();
			runner.RecordingDirectory = form == "absolute-root" ? Application.dataPath
				: form == "absolute-child" ? Path.Combine(Application.dataPath, "review-runs")
				: form == "relative-child" ? "Assets/review-runs"
				: "outside/../Assets/review-runs";
			var before = Directory.GetFileSystemEntries(Application.dataPath, "*", SearchOption.AllDirectories);
			Assert.That(() => { var path = runner.RecordingDirectory; }, Throws.InvalidOperationException.With.Message.Contains("Assets"));
			runner.RecordRuns = true;
			LogAssert.Expect(LogType.Warning, new Regex("Recording incomplete.*Assets"));
			runner.ResetRun();
			Assert.That(runner.Recorder, Is.Null);
			Assert.That(runner.AdvanceFrame(0.1).Steps, Is.EqualTo(10), "unsafe output does not stop physics");
			var readout = Readout(runner);
			readout.Refresh();
			Assert.That(readout.Text, Does.Contain("RECORDING INCOMPLETE").And.Contain("Assets"));
			Assert.That(Directory.GetFileSystemEntries(Application.dataPath, "*", SearchOption.AllDirectories), Is.EqualTo(before));
		}

		[Test]
		public void TrajectoryFixture_AssertionFailure_StillClosesRecordingAndDestroysRunner()
		{
			var fixture = new TrajectoryMatchTests();
			fixture.SetUp();
			var operation = fixture.UnityDrivenRun_MatchesAPlainLoop_AheadThenHardOver();
			VesselSimulationRunner runner = null;
			try
			{
				Assert.That(operation.MoveNext(), Is.True);
				runner = Object.FindObjectsByType<VesselSimulationRunner>(FindObjectsSortMode.None).Single(r => r.name == "TrajectoryVessel");
				var recorder = runner.Recorder;
				Assert.Throws<AssertionException>(() => Assert.Fail("controlled interruption before trajectory assertions"));
				(operation as IDisposable)?.Dispose();
				fixture.TearDown();
				Assert.That(recorder.IsFinished, Is.True, "fixture closes its run after an assertion failure");
				Assert.That(runner == null, Is.True, "fixture destroys its owned runner immediately");
			}
			finally
			{
				fixture.TearDown();
				if (runner != null) Object.DestroyImmediate(runner.gameObject);
			}
		}

		[UnityTest]
		public IEnumerator SceneFixture_InterruptedCapture_ReleasesItsResourcesAndScene()
		{
			var fixture = new BridgeDevSceneTests();
			fixture.Setup();
			var operation = fixture.KeyboardAhead_PutsTheVesselUnderway_AndTheSceneIsCaptured();
			GameObject capture = null;
			VesselSimulationRunner runner = null;
			PanelSettings panel = null;
			RenderTexture target = null;
			try
			{
				while (operation.MoveNext())
				{
					capture = GameObject.Find("CaptureReadout");
					if (capture != null) break;
					yield return operation.Current;
				}
				Assert.That(capture, Is.Not.Null);
				runner = Object.FindAnyObjectByType<VesselSimulationRunner>();
				var recorder = runner.Recorder;
				panel = capture.GetComponent<UIDocument>().panelSettings;
				target = panel.targetTexture;
				Assert.Throws<AssertionException>(() => Assert.Fail("controlled interruption during capture"));
				(operation as IDisposable)?.Dispose();
				fixture.TearDown();
				for (var frame = 0; frame < 3; frame++) yield return null;
				Assert.That(capture == null, Is.True, "capture GameObject released on iterator disposal");
				Assert.That(panel == null, Is.True, "capture panel released");
				Assert.That(target == null, Is.True, "capture target destroyed as well as released");
				Assert.That(recorder.IsFinished, Is.True);
				Assert.That(runner == null, Is.True, "loaded scene released");
				Assert.That(UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/FerrySim/Sim/Scenes/BridgeDev.unity").isLoaded, Is.False);
			}
			finally
			{
				(operation as IDisposable)?.Dispose();
				if (capture != null) Object.DestroyImmediate(capture);
				if (panel != null) Object.DestroyImmediate(panel);
				if (target != null) { target.Release(); Object.DestroyImmediate(target); }
				if (runner != null) Object.DestroyImmediate(runner.gameObject);
			}
		}

		[Test]
		public void TelemetryAvailability_RequiresACompletedStep_AndEndsWithTheRun()
		{
			var runner = new GameObject("TelemetryAvailability").AddComponent<VesselSimulationRunner>();
			_objects.Add(runner.gameObject);
			IVesselTelemetrySource source = runner;
			Assert.That(source.HasTelemetry, Is.False, "uninitialized source has no data");
			runner.RecordRuns = false;
			runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral), PlayModeSupport.LoadTClass());
			Assert.That(source.HasTelemetry, Is.False, "initial state is not a completed physics sample");
			runner.AdvanceFrame(0.005);
			Assert.That(source.HasTelemetry, Is.False, "partial fixed step has no data");
			runner.AdvanceFrame(0.005);
			Assert.That(source.HasTelemetry, Is.True);
			runner.Pause();
			Assert.That(source.HasTelemetry, Is.True, "paused current samples remain available");
			runner.EndRun();
			Assert.That(source.HasTelemetry, Is.False);
			runner.ResetRun();
			Assert.That(source.HasTelemetry, Is.False, "reset invalidates the old sample");
			runner.AdvanceFrame(0.01);
			Assert.That(source.HasTelemetry, Is.True);
			runner.enabled = false;
			Assert.That(source.HasTelemetry, Is.False);
			runner.enabled = true;
			Assert.That(source.HasTelemetry, Is.False, "re-enable does not resurrect an ended sample");
		}

		[Test]
		public void RunIdentity_IsUniqueAcrossRunners_ResetAndReplayStarts()
		{
			var runner = Runner(PlayModeSupport.FreshArtifactFolder("review-run-identity"));
			var ids = new System.Collections.Generic.HashSet<long>();
			Assert.That(runner.RunId, Is.GreaterThan(0));
			Assert.That(ids.Add(runner.RunId), Is.True);
			var other = Runner();
			Assert.That(ids.Add(other.RunId), Is.True, "IDs are process-wide, not local reset counters");
			runner.ResetRun();
			Assert.That(ids.Add(runner.RunId), Is.True, "reset starts a distinct run");
			runner.AdvanceFrame(0.1);
			var liveId = runner.RunId;
			runner.EndRun();
			Assert.That(runner.RunId, Is.EqualTo(liveId), "ending retains the debrief run identity");
			var recording = RecordedRun.Load(runner.LastRecordingPath);
			runner.InitializeReplay(recording, runner.Parameters);
			Assert.That(ids.Add(runner.RunId), Is.True, "replay starts a distinct run");
			Assert.That(runner.HasTelemetry, Is.False);
			runner.AdvanceFrame(0.01);
			Assert.That(runner.HasTelemetry, Is.True);
			runner.ResetRun();
			Assert.That(ids.Add(runner.RunId), Is.True, "replay reset also changes run identity");
			while (!runner.Driver.AtStepLimit) runner.AdvanceFrame(0.01);
			Assert.That(runner.HasTelemetry, Is.False, "finished replay has ended");
		}

		[Test]
		public void ContactLatch_CapturesSingleStepBetweenFrames_TracksLastAndPeak_AndResets()
		{
			var runner = new GameObject("ContactLatch").AddComponent<VesselSimulationRunner>();
			_objects.Add(runner.gameObject);
			runner.RecordRuns = false;
			runner.Factory = new ContactFactory();
			runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral), PlayModeSupport.LoadTClass());
			IVesselTelemetrySource source = runner;
			Assert.That(source.Contact, Is.EqualTo(ContactLatch.None));
			// All five physics steps run synchronously; no render/UI update can see the transient force.
			Assert.That(runner.AdvanceFrame(0.05).Steps, Is.EqualTo(5));
			Assert.That(source.Telemetry.ContactForce, Is.Zero);
			Assert.That(source.Contact.StepsWithContact, Is.EqualTo(1));
			Assert.That(source.Contact.LastContactStep, Is.EqualTo(1), "physics step indices are zero-based");
			Assert.That(source.Contact.LastContactForce, Is.EqualTo(125.0));
			Assert.That(source.Contact.PeakContactForce, Is.EqualTo(125.0));
			Assert.That(source.Contact.PeakContactStep, Is.EqualTo(1));
			runner.AdvanceFrame(0.05);
			Assert.That(source.Telemetry.ContactForce, Is.Zero);
			Assert.That(source.Contact.StepsWithContact, Is.EqualTo(4));
			Assert.That(source.Contact.LastContactStep, Is.EqualTo(7));
			Assert.That(source.Contact.LastContactForce, Is.EqualTo(80.0));
			Assert.That(source.Contact.PeakContactForce, Is.EqualTo(250.0));
			Assert.That(source.Contact.PeakContactStep, Is.EqualTo(5), "equal peaks retain their first step");
			runner.EndRun();
			Assert.That(source.Contact.StepsWithContact, Is.EqualTo(4), "debrief retains the ended run's latch");
			runner.ResetRun();
			Assert.That(source.Contact, Is.EqualTo(ContactLatch.None));
			runner.AdvanceFrame(0.05);
			Assert.That(source.Contact.StepsWithContact, Is.EqualTo(1), "reset does not multiply the step observer");
		}

		private sealed class ContactFactory : ISimulationFactory
		{
			private readonly FerrySim.Core.Dynamics.VesselDynamicsFactory _inner = new FerrySim.Core.Dynamics.VesselDynamicsFactory();
			public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState) =>
				Create(parameters, environment, EmptyWorldGeometry.Instance, initialState);
			public ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState) =>
				new ContactSimulation(_inner.Create(parameters, environment, geometry, initialState));
		}

		private sealed class ContactSimulation : ISimulation
		{
			private static readonly double[] Forces = { 0.0, 125.0, 0.0, 0.0, 0.0, 250.0, 250.0, 80.0, 0.0, 0.0 };
			private readonly ISimulation _inner;
			private int _steps;
			private double _force;
			public ContactSimulation(ISimulation inner) => _inner = inner;
			public VesselParameters Parameters => _inner.Parameters;
			public VesselState State => _inner.State;
			public VesselTelemetry Telemetry
			{
				get
				{
					var value = _inner.Telemetry;
					return new VesselTelemetry(value.Port, value.Starboard, value.PortRudderAngle, value.StarboardRudderAngle,
						value.SpeedThroughWater, value.SpeedOverGround, value.CourseOverGround, value.DepthUnderKeel, _force);
				}
			}
			public void Step(in ControlInputs controls)
			{
				_inner.Step(controls);
				_force = _steps < Forces.Length ? Forces[_steps] : 0.0;
				_steps++;
			}
			public void Reset(in VesselState initialState)
			{
				_inner.Reset(initialState);
				_steps = 0;
				_force = 0.0;
			}
		}

		private sealed class ScriptedControls : IControlInputSource
		{
			private readonly Func<long, ControlInputs> _sample;
			public ScriptedControls(Func<long, ControlInputs> sample) => _sample = sample;
			public ControlInputs ControlsForStep(long step) => _sample(step);
		}

		private sealed class FailingWriter : TextWriter
		{
			private readonly TextWriter _inner;
			private readonly bool _flushFailure;
			public bool Fail;
			public bool ThrowOnDispose;
			public bool Closed;
			public FailingWriter(TextWriter inner, bool flushFailure, bool disposeFailure)
			{
				_inner = inner;
				_flushFailure = flushFailure;
				ThrowOnDispose = disposeFailure;
			}
			public override System.Text.Encoding Encoding => _inner.Encoding;
			public override void Write(string value) { if (Fail && !_flushFailure) throw new IOException("injected recorder write failure"); _inner.Write(value); }
			public override void Write(char value) { if (Fail && !_flushFailure) throw new IOException("injected recorder write failure"); _inner.Write(value); }
			public override void Flush() { if (Fail && _flushFailure) throw new IOException("injected recorder flush failure"); _inner.Flush(); }
			protected override void Dispose(bool disposing)
			{
				Closed = true;
				_inner.Dispose();
				if (Fail && ThrowOnDispose) throw new IOException("injected recorder close failure");
			}
		}
	}
}
