using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace FerrySim.FrameStalls.Tests
{
	/// <summary>All three recording files, open episodes, Unicode names and the traffic submission allocation contract.</summary>
	public class RecorderTrafficBufferTests
	{
		private string _folder;
		[SetUp]
		public void SetUp()
		{
			_folder = Path.Combine(Path.GetTempPath(), "ferrysim-traffic-buffer-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
		}
		[TearDown]
		public void TearDown() => Directory.Delete(_folder, true);

		[Test]
		public void AllThreeFilesIncludingOpenEpisodesMatchTheSynchronousBytes()
		{
			var traffic = Traffic(); var referenceTraffic = Traffic();
			var monitor = Monitor(traffic); var referenceMonitor = Monitor(referenceTraffic);
			var telemetry = new StringWriter(); var inputs = new StringWriter(); var events = new StringWriter();
			var header = RunRecorder.TrafficHeader(traffic);
			var referenceHeader = new List<KeyValuePair<string, string>>(header)
			{
				new KeyValuePair<string, string>("param.inputs", "traffic.inputs.csv"),
				new KeyValuePair<string, string>(RunRecorder.TrafficEventsKey, "traffic.traffic.csv"),
			};
			using var reference = new RunRecorder(telemetry, inputs, 100, referenceHeader,
				new[] { "ferrysim control trace recorded by Unity, for traffic.csv" });
			typeof(RunRecorder).GetMethod("AttachTraffic", BindingFlags.Instance | BindingFlags.NonPublic)
				.Invoke(reference, new object[] { events, referenceTraffic, referenceMonitor, "traffic.csv" });
			using var actual = RunRecorder.Create(_folder, "traffic", 100, header, traffic, monitor);
			for (var step = 0; step < 3000; step++)
			{
				var controls = new ControlInputs((step % 11) / 11.0, 1.0 / 3.0, -0.0);
				var before = State(step); var after = State(step + 1);
				reference.BeforeStep(step, before, default, controls); actual.BeforeStep(step, before, default, controls);
				reference.AfterStep(step + 1, after, default); actual.AfterStep(step + 1, after, default);
				referenceMonitor.Update(step + 1, after, reference); monitor.Update(step + 1, after, actual);
			}
			reference.Finish(3000, State(3000), default); actual.Finish(3000, State(3000), default);
			Assert.That(actual.Failure, Is.Null); Assert.That(reference.Failure, Is.Null);
			Assert.That(actual.IsOutputClosed, Is.True);
			Assert.That(actual.OutputBufferBytes, Is.EqualTo(6815744));
			Assert.That(File.ReadAllBytes(actual.TelemetryPath), Is.EqualTo(Encoding.UTF8.GetBytes(telemetry.ToString())));
			Assert.That(File.ReadAllBytes(actual.InputsPath), Is.EqualTo(Encoding.UTF8.GetBytes(inputs.ToString())));
			Assert.That(File.ReadAllBytes(actual.TrafficPath), Is.EqualTo(Encoding.UTF8.GetBytes(events.ToString())));
			Assert.That(events.ToString(), Does.Contain(",alarm,").And.Contain(",alarm-open,").And.Contain("岸🚢"));
			Assert.That(actual.Digest.Text, Is.EqualTo(reference.Digest.Text));
			Assert.That(monitor.Digest, Is.EqualTo(referenceMonitor.Digest));
		}

		[Test]
		public void APendingFlushPreservesUtf8WhenALaterTrafficNameWrapsBetweenSurrogates()
		{
			const int capacity = 512;
			var traffic = Traffic(); var referenceTraffic = Traffic();
			var telemetry = new StringWriter(); var inputs = new StringWriter(); var events = new StringWriter();
			var header = RunRecorder.TrafficHeader(traffic);
			var referenceHeader = new List<KeyValuePair<string, string>>(header)
			{
				new KeyValuePair<string, string>("param.inputs", "wrap.inputs.csv"),
				new KeyValuePair<string, string>(RunRecorder.TrafficEventsKey, "wrap.traffic.csv"),
			};
			using var reference = new RunRecorder(telemetry, inputs, 100, referenceHeader,
				new[] { "ferrysim control trace recorded by Unity, for wrap.csv" });
			typeof(RunRecorder).GetMethod("AttachTraffic", BindingFlags.Instance | BindingFlags.NonPublic)
				.Invoke(reference, new object[] { events, referenceTraffic, Monitor(referenceTraffic), "wrap.csv" });
			using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
			ConsumerGate gate = null;
			using var actual = RunRecorder.Create(_folder, "wrap", 100, header, traffic, Monitor(traffic),
				path => path.EndsWith("wrap.csv", StringComparison.Ordinal)
					? gate = new ConsumerGate(new StreamWriter(path, false, new UTF8Encoding(false)), entered, release)
					: new StreamWriter(path, false, new UTF8Encoding(false)), capacity);
			var trafficWriter = (TextWriter)typeof(RunRecorder).GetField("_traffic", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actual);
			var earlier = new TrafficEvent(TrafficEventKind.AlarmRaised, 1, 0, 1, 0, 111111111, default, double.NaN, -0.0);
			var later = new TrafficEvent(TrafficEventKind.AlarmRaised, 2, 0, 2, 0, 111111111, default, double.NaN, -0.0);
			var earlierRow = TrafficEventLog.FormatRow(earlier, traffic.Pack.Vessels[0]) + "\n";
			var laterRow = TrafficEventLog.FormatRow(later, traffic.Pack.Vessels[0]) + "\n";
			var highSurrogate = laterRow.IndexOf('\ud83d');
			Assert.That(highSurrogate, Is.GreaterThan(0));
			Assert.That(char.IsLowSurrogate(laterRow[highSurrogate + 1]), Is.True);
			var paddingLength = capacity - 1 - earlierRow.Length - highSurrogate;
			Assert.That(paddingLength, Is.GreaterThan(2));
			var padding = "#" + new string('x', paddingLength - 2) + "\n";
			trafficWriter.Write(padding); events.Write(padding);
			Assert.That(SpinWait.SpinUntil(() => actual.PendingWriteCharacters == 0 || actual.Failure != null, 3000), Is.True);
			Assert.That(actual.Failure, Is.Null);
			try
			{
				gate.Armed = true;
				actual.BeforeStep(0, State(0), default, ControlInputs.Neutral);
				reference.BeforeStep(0, State(0), default, ControlInputs.Neutral);
				Assert.That(entered.Wait(3000), Is.True, "hold the consumer before it reaches the traffic ring");
				actual.OnTrafficEvent(earlier); reference.OnTrafficEvent(earlier);
				trafficWriter.Flush(); events.Flush();
				actual.OnTrafficEvent(later); reference.OnTrafficEvent(later);
				Assert.That(release.IsSet, Is.False, "both rows and the intervening flush must publish while consumption is gated");
				Assert.That((padding.Length + earlierRow.Length + highSurrogate) % capacity, Is.EqualTo(capacity - 1));
				Assert.That(actual.Failure, Is.Null);
				release.Set();
				Assert.That(SpinWait.SpinUntil(() => actual.PendingWriteCharacters == 0 || actual.Failure != null, 3000), Is.True,
					"consume the interleaving before Finish can replace its flush request");
				actual.AfterStep(1, State(1), default); reference.AfterStep(1, State(1), default);
				actual.Finish(1, State(1), default); reference.Finish(1, State(1), default);
				Assert.That(actual.Failure, Is.Null); Assert.That(actual.IsOutputClosed, Is.True);
				Assert.That(Directory.GetFiles(_folder, "*.failed"), Is.Empty);
				Assert.That(actual.Digest.Text, Is.EqualTo(reference.Digest.Text));
				Assert.That(File.ReadAllBytes(actual.TelemetryPath), Is.EqualTo(Encoding.UTF8.GetBytes(telemetry.ToString())));
				Assert.That(File.ReadAllBytes(actual.InputsPath), Is.EqualTo(Encoding.UTF8.GetBytes(inputs.ToString())));
				Assert.That(File.ReadAllBytes(actual.TrafficPath), Is.EqualTo(Encoding.UTF8.GetBytes(events.ToString())),
					"the UTF-8 encoder must not flush between the wrapped surrogate pair");
			}
			finally { release.Set(); }
		}

		[Test]
		public void ProductionStepAndTrafficEventSubmissionAllocateNothingAfterSetup()
		{
			var traffic = Traffic(); var monitor = Monitor(traffic);
			using var recorder = RunRecorder.Create(_folder, "allocation", 100, RunRecorder.TrafficHeader(traffic), traffic, monitor);
			for (var i = 0; i < 50; i++) Submit(recorder, i);
			var chars = new char[16];
			var allocations = Recorder.Get("GC.Alloc"); allocations.enabled = false; allocations.FilterToCurrentThread();
			try
			{
				allocations.enabled = true; var control = new string(chars); allocations.enabled = false;
				var calibration = allocations.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				allocations.enabled = true;
				for (var i = 50; i < 1050; i++) Submit(recorder, i);
				allocations.enabled = false;
				var count = allocations.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; 1000 recording steps with traffic events GC.Alloc=" + count);
				Assert.That(count, Is.Zero); Assert.That(recorder.Failure, Is.Null);
			}
			finally
			{
				allocations.enabled = false; allocations.CollectFromAllThreads(); recorder.Finish(1050, State(1050), default);
			}
		}

		private sealed class ConsumerGate : TextWriter
		{
			private readonly TextWriter _inner;
			private readonly ManualResetEventSlim _entered, _release;
			public volatile bool Armed;
			public ConsumerGate(TextWriter inner, ManualResetEventSlim entered, ManualResetEventSlim release)
			{
				_inner = inner; _entered = entered; _release = release;
			}
			public override Encoding Encoding => _inner.Encoding;
			public override void Write(string value) => _inner.Write(value);
			public override void Write(char[] buffer, int index, int count)
			{
				if (Armed)
				{
					_entered.Set();
					if (!_release.Wait(10000)) throw new TimeoutException("The gated consumer was not released.");
				}
				_inner.Write(buffer, index, count);
			}
			public override void Flush() => _inner.Flush();
			protected override void Dispose(bool disposing)
			{
				if (disposing) _inner.Dispose();
				base.Dispose(disposing);
			}
		}

		private static void Submit(RunRecorder recorder, int step)
		{
			var controls = new ControlInputs(step % 13 / 13.0, 1.0 / 3.0, -0.0);
			recorder.BeforeStep(step, State(step), default, controls);
			recorder.AfterStep(step + 1, State(step + 1), default);
			var entry = new TrafficEvent(TrafficEventKind.AlarmRaised, step + 1, 0, step + 1, 0, 111111111, default, double.NaN, -0.0);
			recorder.OnTrafficEvent(entry);
		}
		private static VesselState State(int step) => VesselState.Planar(step * 0.01, step * 0.04, 0, 0, 4, 0, 0);
		private static TrafficMonitor Monitor(TrafficRun traffic) => traffic.StartRun(
			new HullOutline(new[] { -10.0, 10.0, 10.0, -10.0 }, new[] { -3.0, -3.0, 3.0, 3.0 }), TrafficAlarmLimits.Default);
		private static TrafficRun Traffic()
		{
			var epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);
			var vessel = new TrafficVessel(0, 111111111, " fixture,\"岸🚢\" ", 0, TrafficClass.TugTow, 20, 7, 3, 0, 100, 2, 1, true, false);
			var reports = new TrafficReports(new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0.0, 100.0 }, new[] { 350.0, -50.0 }, new[] { 20.0, 20.0 },
				new[] { 4.0f, 4.0f }, new[] { (float)Math.PI, (float)Math.PI }, new[] { (float)Math.PI, (float)Math.PI });
			var pack = new TrafficPack("2025-07-14", epoch, 0, 100, new[] { vessel }, reports, new string('a', 64));
			return new TrafficRun(pack, epoch, TrafficLeaveOut.Off, 0);
		}
	}
}
