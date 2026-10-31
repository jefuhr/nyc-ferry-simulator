using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace FerrySim.FrameStalls.Tests
{
	/// <summary>Storage waits, bounded failure, exact recording bytes and allocation-free step submission.</summary>
	public class RecorderBufferTests
	{
		private string _folder;
		private static readonly VesselTelemetry Telemetry = new VesselTelemetry(
			new EngineTelemetry(123.456, 61.728, GearState.Ahead, 0.7),
			new EngineTelemetry(97.32, -48.66, GearState.Astern, 0.4),
			-0.000001, 0.12, 4.8, 5.2, 1.7, 7.25, 1234.56);

		[SetUp]
		public void SetUp()
		{
			_folder = Path.Combine(Path.GetTempPath(), "ferrysim-recorder-buffer-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(_folder, true);

		[TestCase(false)]
		[TestCase(true)]
		public void ABlockedSinkDoesNotBlockStepSubmission(bool blockFlush)
		{
			GateWriter gate = null;
			using var recorder = RunRecorder.Create(_folder, "blocked", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("blocked.csv", StringComparison.Ordinal)
					? gate = new GateWriter(new StreamWriter(path, false, new UTF8Encoding(false)), blockFlush)
					: new StreamWriter(path, false, new UTF8Encoding(false)));
			gate.Armed = true;
			using var returned = new ManualResetEventSlim();
			Exception producerFailure = null;
			var producer = new Thread(() =>
			{
				try { Step(recorder, 0); }
				catch (Exception error) { producerFailure = error; }
				finally { returned.Set(); }
			});
			try
			{
				producer.Start();
				Assert.That(gate.Entered.Wait(3000), Is.True, "the storage operation must actually be blocked");
				Assert.That(returned.Wait(1000), Is.True, "an owed step must return while storage remains blocked");
				Assert.That(gate.Release.IsSet, Is.False);
				Assert.That(gate.CallbackThread, Is.Not.EqualTo(producer.ManagedThreadId));
				Assert.That(producerFailure, Is.Null);
				Assert.That(recorder.Failure, Is.Null);
			}
			finally
			{
				gate.Release.Set();
				Assert.That(producer.Join(5000), Is.True, "release the fixture before leaving the test");
				recorder.Finish(1, State(1), Telemetry);
			}
			Assert.That(recorder.Failure, Is.Null);
			Assert.That(gate.Closed, Is.True);
		}

		[Test]
		public void FullBufferStopsRecordingWithoutWaitingAndRenamesFailedFilesAfterClose()
		{
			GateWriter gate = null;
			using var recorder = RunRecorder.Create(_folder, "overflow", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("overflow.csv", StringComparison.Ordinal)
					? gate = new GateWriter(new StreamWriter(path, false, new UTF8Encoding(false)), false)
					: new StreamWriter(path, false, new UTF8Encoding(false)), 512);
			gate.Armed = true;
			Step(recorder, 0);
			try
			{
				Assert.That(gate.Entered.Wait(3000), Is.True);
				using var returned = new ManualResetEventSlim();
				var producer = new Thread(() =>
				{
					for (var step = 1; step < 30; step++) Step(recorder, step);
					returned.Set();
				});
				producer.Start();
				try { Assert.That(returned.Wait(1000), Is.True, "overflow must not wait for the blocked consumer"); }
				finally
				{
					gate.Release.Set();
					Assert.That(producer.Join(5000), Is.True);
				}
				Assert.That(recorder.Failure, Is.InstanceOf<IOException>());
				Assert.That(recorder.Failure.Message, Does.Contain("buffer"));
				Assert.That(recorder.IsFinished, Is.True);
				Assert.That(SpinWait.SpinUntil(() => File.Exists(recorder.TelemetryPath + ".failed"), 5000), Is.True);
				Assert.That(gate.Closed, Is.True);
				Assert.That(File.Exists(recorder.TelemetryPath), Is.False);
				Assert.That(File.Exists(recorder.InputsPath), Is.False);
				Assert.That(File.Exists(recorder.InputsPath + ".failed"), Is.True);
			}
			finally { gate.Release.Set(); }
		}

		[Test]
		public void FinishWaitsForPendingOutputBeforeReturningACompleteReplayPath()
		{
			GateWriter gate = null;
			using var recorder = RunRecorder.Create(_folder, "finish", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("finish.csv", StringComparison.Ordinal)
					? gate = new GateWriter(new StreamWriter(path, false, new UTF8Encoding(false)), false)
					: new StreamWriter(path, false, new UTF8Encoding(false)));
			gate.Armed = true;
			Step(recorder, 0);
			using var started = new ManualResetEventSlim();
			using var returned = new ManualResetEventSlim();
			var finish = new Thread(() => { started.Set(); recorder.Finish(1, State(1), Telemetry); returned.Set(); });
			try
			{
				Assert.That(gate.Entered.Wait(3000), Is.True);
				finish.Start();
				Assert.That(started.Wait(3000), Is.True);
				Assert.That(returned.Wait(100), Is.False, "normal finalization must not race replay against unwritten rows");
			}
			finally
			{
				gate.Release.Set();
				if (finish.ThreadState != ThreadState.Unstarted) Assert.That(finish.Join(5000), Is.True);
			}
			Assert.That(returned.IsSet, Is.True);
			Assert.That(recorder.Failure, Is.Null);
			Assert.That(File.ReadAllText(recorder.InputsPath), Does.Contain("# steps: 1\n").And.Contain("# state_digest: "));
			Assert.That(File.ReadAllText(recorder.TelemetryPath), Does.Contain("0.010,"));
		}

		[Test]
		public void RingWrapPreservesEveryCharacterAndTheFinalTrailer()
		{
			var telemetry = new StringWriter();
			var inputs = new StringWriter();
			using var reference = new RunRecorder(telemetry, inputs, 100,
				new[] { new KeyValuePair<string, string>("param.inputs", "wrap.inputs.csv") },
				new[] { "ferrysim control trace recorded by Unity, for wrap.csv" });
			using var actual = RunRecorder.Create(_folder, "wrap", 100, new List<KeyValuePair<string, string>>(), null, null, bufferCharacters: 512);
			for (var step = 0; step < 600; step++)
			{
				Step(reference, step); Step(actual, step);
				Assert.That(SpinWait.SpinUntil(() => actual.PendingWriteCharacters == 0 || actual.Failure != null, 3000), Is.True);
				Assert.That(actual.Failure, Is.Null);
			}
			reference.Finish(600, State(600), Telemetry); actual.Finish(600, State(600), Telemetry);
			Assert.That(actual.Failure, Is.Null);
			Assert.That(actual.OutputBufferBytes, Is.EqualTo(2048));
			Assert.That(File.ReadAllText(actual.TelemetryPath), Is.EqualTo(telemetry.ToString()));
			Assert.That(File.ReadAllText(actual.InputsPath), Is.EqualTo(inputs.ToString()));
		}

		[TestCase("write")]
		[TestCase("flush")]
		[TestCase("close")]
		public void AWorkerStorageFailureClosesAndQuarantinesEveryFile(string operation)
		{
			FaultWriter faulty = null;
			using var recorder = RunRecorder.Create(_folder, "failure", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("failure.csv", StringComparison.Ordinal)
					? faulty = new FaultWriter(new StreamWriter(path, false, new UTF8Encoding(false)), operation)
					: new StreamWriter(path, false, new UTF8Encoding(false)));
			faulty.Armed = true;
			Step(recorder, 0);
			if (operation == "close") recorder.Finish(1, State(1), Telemetry);
			Assert.That(SpinWait.SpinUntil(() => recorder.IsOutputClosed, 5000), Is.True);
			Assert.That(recorder.Failure, Is.InstanceOf<IOException>());
			Assert.That(recorder.Failure.Message, Is.EqualTo("fixture " + operation + " failure"));
			Assert.That(faulty.Closed, Is.True);
			Assert.That(faulty.CallbackThread, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId));
			foreach (var path in new[] { recorder.TelemetryPath, recorder.InputsPath })
			{
				Assert.That(File.Exists(path), Is.False);
				Assert.That(File.Exists(path + ".failed"), Is.True);
			}
			Assert.Throws<FileNotFoundException>(() => RecordedRun.Load(recorder.TelemetryPath));
		}

		[Test]
		public void AFailedRenameRetainsTheOriginalErrorAndReportsTheCleanupError()
		{
			FaultWriter faulty = null;
			using var recorder = RunRecorder.Create(_folder, "rename", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("rename.csv", StringComparison.Ordinal)
					? faulty = new FaultWriter(new StreamWriter(path, false, new UTF8Encoding(false)), "write")
					: new StreamWriter(path, false, new UTF8Encoding(false)));
			Directory.CreateDirectory(recorder.TelemetryPath + ".failed");
			LogAssert.Expect(LogType.Warning, "[FerrySim] Recording incomplete: Could not mark failed recording file '" + recorder.TelemetryPath + "' as .failed.");
			faulty.Armed = true; Step(recorder, 0);
			Assert.That(SpinWait.SpinUntil(() => recorder.IsOutputClosed, 5000), Is.True);
			Assert.That(recorder.Failure, Is.InstanceOf<AggregateException>());
			Assert.That(recorder.Failure.ToString(), Does.Contain("fixture write failure").And.Contain("Could not mark failed recording"));
			Assert.That(faulty.Closed, Is.True);
			Assert.That(File.Exists(recorder.TelemetryPath), Is.True, "never overwrite the conflicting failed destination");
			Assert.That(File.Exists(recorder.InputsPath), Is.False, "companions are quarantined before telemetry");
			Assert.Throws<FileNotFoundException>(() => RecordedRun.Load(recorder.TelemetryPath));
			LogAssert.NoUnexpectedReceived();
		}

		[Test]
		public void AFlushedPrefixWithoutFinishKeepsNormalNamesAndLoadsAsACrashShortenedRun()
		{
			var scenario = new ScenarioSettings();
			var simulation = new FixtureSimulation();
			var vessel = VesselParametersJson.Load(Path.Combine(Application.dataPath, "..", "..", "vessels", "t-class.json"));
			var header = RunRecorder.BuildHeader(scenario, vessel, simulation, "fixture", State(0), scenario.environment.Create(), 100);
			using var recorder = RunRecorder.Create(_folder, "prefix", 100, header);
			for (var step = 0; step <= 100; step++) Step(recorder, step);
			var buffered = typeof(RunRecorder).GetField("_buffered", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(recorder);
			buffered.GetType().GetMethod("Drain").Invoke(buffered, null);
			var captured = Path.Combine(_folder, "crash-prefix"); Directory.CreateDirectory(captured);
			File.Copy(recorder.TelemetryPath, Path.Combine(captured, "prefix.csv"));
			File.Copy(recorder.InputsPath, Path.Combine(captured, "prefix.inputs.csv"));
			var run = RecordedRun.Load(Path.Combine(captured, "prefix.csv"));
			Assert.That(run.Rows.Count, Is.EqualTo(101));
			Assert.That(run.StepCount, Is.EqualTo(100));
			Assert.That(run.StateDigest, Is.Null, "this captured prefix has no successful-Finish trailer");
			recorder.Dispose();
			Assert.That(recorder.Failure, Is.Null);
			Assert.That(recorder.IsOutputClosed, Is.True);
			Assert.That(Directory.GetFiles(_folder, "*.failed"), Is.Empty);
			Assert.That(RecordedRun.Load(recorder.TelemetryPath).StateDigest, Is.Null);
		}

		[TestCase("EndRun")]
		[TestCase("OnDisable")]
		[TestCase("OnApplicationQuit")]
		public void RunnerEndAndLifecycleCallbacksDrainBeforePublishingReplay(string callback)
		{
			GateWriter gate = null;
			using var recorder = RunRecorder.Create(_folder, "lifecycle", 100, new List<KeyValuePair<string, string>>(), null, null,
				path => path.EndsWith("lifecycle.csv", StringComparison.Ordinal)
					? gate = new GateWriter(new StreamWriter(path, false, new UTF8Encoding(false)), false)
					: new StreamWriter(path, false, new UTF8Encoding(false)));
			var host = new GameObject("buffered recording lifecycle fixture"); host.SetActive(false);
			var runner = host.AddComponent<VesselSimulationRunner>();
			var driver = new FixedStepDriver(new FixtureSimulation(), new ConstantControls(ControlInputs.Neutral));
			driver.AddObserver(recorder);
			SetRunnerField(runner, "_driver", driver); SetRunnerField(runner, "_recorder", recorder); SetRunnerField(runner, "_runActive", true);
			var endedAfterClose = false;
			runner.RunEnded += () => endedAfterClose = gate.Closed && recorder.IsOutputClosed && runner.LastRecordingPath == recorder.TelemetryPath;
			gate.Armed = true; driver.Advance(0.01);
			using var started = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim();
			Exception failure = null;
			var finish = new Thread(() =>
			{
				try
				{
					started.Set();
					typeof(VesselSimulationRunner).GetMethod(callback, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(runner, null);
				}
				catch (Exception error) { failure = error; }
				finally { returned.Set(); }
			});
			try
			{
				Assert.That(gate.Entered.Wait(3000), Is.True); finish.Start();
				Assert.That(started.Wait(3000), Is.True);
				Assert.That(returned.Wait(100), Is.False);
				Assert.That(runner.LastRecordingPath, Is.Null);
				gate.Release.Set(); Assert.That(finish.Join(5000), Is.True);
				Assert.That(failure, Is.Null); Assert.That(recorder.Failure, Is.Null);
				Assert.That(endedAfterClose, Is.True);
				Assert.That(File.ReadAllText(recorder.InputsPath), Does.Contain("# steps: 1\n").And.Contain("# state_digest: "));
			}
			finally
			{
				gate.Release.Set();
				if ((finish.ThreadState & ThreadState.Unstarted) == 0) Assert.That(finish.Join(5000), Is.True);
				UnityEngine.Object.DestroyImmediate(host);
			}
		}

		[Test]
		public void FileFactoryPreservesTheSynchronousRecordingBytesAndDigest()
		{
			var header = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("fixture", "fixed-buffer-run") };
			var referenceHeader = new List<KeyValuePair<string, string>>(header)
				{ new KeyValuePair<string, string>("param.inputs", "fixed.inputs.csv") };
			var telemetry = new StringWriter();
			var inputs = new StringWriter();
			using var reference = new RunRecorder(telemetry, inputs, 100, referenceHeader,
				new[] { "ferrysim control trace recorded by Unity, for fixed.csv" });
			using var actual = RunRecorder.Create(_folder, "fixed", 100, header);
			for (var step = 0; step < 1234; step++)
			{
				Step(reference, step);
				Step(actual, step);
			}
			reference.Finish(1234, State(1234), Telemetry);
			actual.Finish(1234, State(1234), Telemetry);
			Assert.That(actual.Failure, Is.Null);
			var expectedTelemetry = Encoding.UTF8.GetBytes(telemetry.ToString());
			var expectedInputs = Encoding.UTF8.GetBytes(inputs.ToString());
			Assert.That(File.ReadAllBytes(actual.TelemetryPath), Is.EqualTo(expectedTelemetry));
			Assert.That(File.ReadAllBytes(actual.InputsPath), Is.EqualTo(expectedInputs));
			Assert.That(actual.Digest.Text, Is.EqualTo(reference.Digest.Text));
			Assert.That(Hash(expectedTelemetry), Is.EqualTo("ba5dfa96e911cbb9312feb4bd861e76c3082a05710f9773882f4fbd2cd8ab106"));
			Assert.That(Hash(expectedInputs), Is.EqualTo("3d38a5985eca661d6969fa6e8f1fb8e1dc4ffde2edffd8c9d6ed0cfc7619da94"));
			Assert.That(actual.Digest.Text, Is.EqualTo("fnv1a64:602e2f1670cbab40"));
			TestContext.WriteLine("fixed telemetry SHA256=" + Hash(expectedTelemetry));
			TestContext.WriteLine("fixed inputs SHA256=" + Hash(expectedInputs));
			TestContext.WriteLine("fixed state digest=" + actual.Digest.Text);
		}

		[Test]
		public void ReusableSerializersMatchLegacyBytesAtNumericAndCsvBoundaries()
		{
			var buffer = new char[8192];
			var states = new[] { State(0), State(1234), VesselState.Planar(double.MaxValue, double.MaxValue, -double.MaxValue, 0.0) };
			foreach (var state in states)
			{
				var writer = new StringWriter();
				TelemetryCsv.WriteRow(writer, buffer, state, Telemetry, ControlInputs.Neutral);
				Assert.That(writer.ToString(), Is.EqualTo(TelemetryCsv.FormatRow(state, Telemetry, ControlInputs.Neutral) + "\n"));
			}
			foreach (var value in new[] { -0.0, double.Epsilon, -double.Epsilon, 1.0 / 3.0, -1.0, 1.0 })
			{
				var writer = new StringWriter();
				var controls = new ControlInputs(value, -value, 0.12345678901234567);
				TelemetryCsv.WriteTraceRow(writer, buffer, 123456789, controls);
				Assert.That(writer.ToString(), Is.EqualTo(ControlTrace.FormatRow(123456789, controls) + "\n"));
			}
			var vessel = new TrafficVessel(0, 111111111, " fixture,\"岸🚢\" ", 0, TrafficClass.TugTow, 20, 7, 3, 0, 100, 2, 1, true, false);
			var trafficEvent = new TrafficEvent((TrafficEventKind)0, 123, 0, 123, 0, vessel.Mmsi, default, double.NaN, -0.0);
			foreach (var metadata in new[] { vessel, null })
			{
				var writer = new StringWriter();
				TelemetryCsv.WriteTrafficRow(writer, buffer, trafficEvent, metadata);
				Assert.That(writer.ToString(), Is.EqualTo(TrafficEventLog.FormatRow(trafficEvent, metadata) + "\n"));
			}
			var invalid = new StringWriter();
			Assert.Throws<InvalidOperationException>(() => TelemetryCsv.WriteRow(invalid, buffer, VesselState.Planar(0, double.NaN, 0, 0), Telemetry, ControlInputs.Neutral));
			Assert.That(invalid.ToString(), Is.Empty, "an invalid row must not publish a prefix");
		}

		[Test]
		public void FileFactoryStepSubmissionAllocatesNothingAfterSetup()
		{
			using var recorder = RunRecorder.Create(_folder, "allocation", 100, new List<KeyValuePair<string, string>>());
			for (var step = 0; step < 50; step++) Step(recorder, step);
			var chars = new char[16];
			var allocations = Recorder.Get("GC.Alloc"); allocations.enabled = false; allocations.FilterToCurrentThread();
			try
			{
				allocations.enabled = true; var control = new string(chars); allocations.enabled = false;
				var calibration = allocations.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				allocations.enabled = true;
				for (var step = 50; step < 1050; step++) Step(recorder, step);
				allocations.enabled = false;
				var count = allocations.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; 1000 recording steps GC.Alloc=" + count);
				Assert.That(count, Is.Zero);
				Assert.That(recorder.Failure, Is.Null);
			}
			finally
			{
				allocations.enabled = false; allocations.CollectFromAllThreads();
				recorder.Finish(1050, State(1050), Telemetry);
			}
		}

		[Test]
		public void ReusableSerializersAllocateNothingAfterWarmup()
		{
			var buffer = new char[8192];
			var state = State(1234);
			var controls = new ControlInputs(1.0 / 3.0, -0.12345678901234567, -0.0);
			var vessel = new TrafficVessel(0, 111111111, " fixture,\"岸🚢\" ", 0, TrafficClass.TugTow, 20, 7, 3, 0, 100, 2, 1, true, false);
			var trafficEvent = new TrafficEvent((TrafficEventKind)0, 123, 0, 123, 0, vessel.Mmsi, default, double.NaN, -0.0);
			for (var i = 0; i < 20; i++)
			{
				TelemetryCsv.WriteRow(TextWriter.Null, buffer, state, Telemetry, controls);
				TelemetryCsv.WriteTraceRow(TextWriter.Null, buffer, 1234, controls);
				TelemetryCsv.WriteTrafficRow(TextWriter.Null, buffer, trafficEvent, vessel);
			}
			var chars = new char[16];
			var allocations = Recorder.Get("GC.Alloc"); allocations.enabled = false; allocations.FilterToCurrentThread();
			try
			{
				allocations.enabled = true; var control = new string(chars); allocations.enabled = false;
				var calibration = allocations.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				allocations.enabled = true;
				for (var i = 0; i < 1000; i++)
				{
					TelemetryCsv.WriteRow(TextWriter.Null, buffer, state, Telemetry, controls);
					TelemetryCsv.WriteTraceRow(TextWriter.Null, buffer, 1234, controls);
					TelemetryCsv.WriteTrafficRow(TextWriter.Null, buffer, trafficEvent, vessel);
				}
				allocations.enabled = false;
				var count = allocations.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; 3000 reusable rows GC.Alloc=" + count);
				Assert.That(count, Is.Zero);
			}
			finally { allocations.enabled = false; allocations.CollectFromAllThreads(); }
		}

		private static void Step(RunRecorder recorder, int step)
		{
			var controls = new ControlInputs((step % 17 - 8) / 8.0, (step % 23 - 11) / 11.0, step % 2 == 0 ? -0.0 : 0.12345678901234567);
			recorder.BeforeStep(step, State(step), Telemetry, controls);
			recorder.AfterStep(step + 1, State(step + 1), Telemetry);
		}

		private static VesselState State(int step) => VesselState.Planar(step * 0.01, step * 0.031 - 0.0001, -0.0, 1.2, 4.8, -0.000001, 0.003);
		private static void SetRunnerField(VesselSimulationRunner runner, string name, object value) =>
			typeof(VesselSimulationRunner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runner, value);

		private sealed class FixtureSimulation : ISimulation
		{
			private int _step;
			public VesselParameters Parameters => null;
			public VesselState State => RecorderBufferTests.State(_step);
			public VesselTelemetry Telemetry => RecorderBufferTests.Telemetry;
			public void Step(in ControlInputs controls) => _step++;
			public void Reset(in VesselState state) => _step = 0;
		}

		private sealed class FaultWriter : TextWriter
		{
			private readonly TextWriter _inner;
			private readonly string _operation;
			public volatile bool Armed, Closed;
			public int CallbackThread;
			public FaultWriter(TextWriter inner, string operation) { _inner = inner; _operation = operation; }
			public override Encoding Encoding => _inner.Encoding;
			private void Fail(string operation)
			{
				if (Armed && operation == _operation)
				{
					CallbackThread = Thread.CurrentThread.ManagedThreadId;
					throw new IOException("fixture " + operation + " failure");
				}
			}
			public override void Write(string value) { Fail("write"); _inner.Write(value); }
			public override void Write(char[] buffer, int index, int count) { Fail("write"); _inner.Write(buffer, index, count); }
			public override void Flush() { Fail("flush"); _inner.Flush(); }
			protected override void Dispose(bool disposing)
			{
				if (disposing) { _inner.Dispose(); Closed = true; Fail("close"); }
				base.Dispose(disposing);
			}
		}
		private static string Hash(byte[] bytes)
		{
			using var hash = SHA256.Create();
			return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
		}

		private sealed class GateWriter : TextWriter
		{
			private readonly TextWriter _inner;
			private readonly bool _blockFlush;
			public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
			public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
			public volatile bool Armed, Closed;
			public int CallbackThread;
			public GateWriter(TextWriter inner, bool blockFlush) { _inner = inner; _blockFlush = blockFlush; }
			public override Encoding Encoding => _inner.Encoding;
			private void Block(bool flush)
			{
				if (!Armed || flush != _blockFlush) return;
				CallbackThread = Thread.CurrentThread.ManagedThreadId;
				Entered.Set();
				if (!Release.Wait(10000)) throw new TimeoutException("The blocked-sink fixture was not released.");
			}
			public override void Write(string value) { Block(false); _inner.Write(value); }
			public override void Write(char value) { Block(false); _inner.Write(value); }
			public override void Write(char[] buffer, int index, int count) { Block(false); _inner.Write(buffer, index, count); }
			public override void Flush() { Block(true); _inner.Flush(); }
			protected override void Dispose(bool disposing)
			{
				if (disposing) { _inner.Dispose(); Closed = true; }
				base.Dispose(disposing);
			}
		}
	}
}
