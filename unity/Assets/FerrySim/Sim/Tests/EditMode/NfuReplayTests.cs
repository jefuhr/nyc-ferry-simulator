using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Sim.Tests
{
	/// <summary>Guards format selection at open, silent v1 data loss and replay after removing jog.</summary>
	public class NfuReplayTests
	{
		internal sealed class ZeroJogControls : INfuControlSource
		{
			public ControlInputs ControlsForStep(long step) => ControlInputs.Neutral;
		}

		internal sealed class JogControls : INfuControlSource
		{
			public ControlInputs ControlsForStep(long step) => new ControlInputs(0.4, 0.3, -0.5, step < 240 ? 0.63 : step < 360 ? -0.2 : 0);
		}

		[Test]
		public void JogRecordsReplaysAndVerifies_AndStrippedTraceFails()
		{
			var host = new GameObject("jog recording test");
			try
			{
				var runner = host.AddComponent<VesselSimulationRunner>();
				runner.RecordingDirectory = TestSupport.FreshArtifactFolder("nfu-replay");
				runner.Initialize(new ScenarioSettings(), new JogControls());
				for (var i = 0; i < 420; i++) runner.Driver.StepOnce();
				runner.EndRun();
				Assert.That(runner.RecordingError, Is.Null);
				var run = RecordedRun.Load(runner.LastRecordingPath);
				Assert.That(run.Inputs.At(0).Jog, Is.EqualTo(0.63));
				Assert.That(File.ReadAllText(runner.LastRecordingPath), Does.Contain("helm,nfu_jog\n"));
				var result = ReplayPlayer.Verify(run, TestSupport.LoadTClass(), new VesselDynamicsFactory());
				Assert.That(result.Matches, Is.True, result.ToString());
				runner.InitializeReplay(run);
				Assert.That(runner.ControlSource, Is.InstanceOf<INfuControlSource>());
				runner.EndRun();

				var tracePath = Path.ChangeExtension(runner.LastRecordingPath, null) + RunRecorder.InputsSuffix;
				var lines = File.ReadAllLines(tracePath).Select(line => line.StartsWith("#", StringComparison.Ordinal) ? line : string.Join(",", line.Split(',').Take(4)));
				File.WriteAllLines(tracePath, lines.ToArray());
				var stripped = ReplayPlayer.Verify(RecordedRun.Load(runner.LastRecordingPath), TestSupport.LoadTClass(), new VesselDynamicsFactory());
				Assert.That(stripped.Matches, Is.False, "even a zero jog recording must retain its format evidence");
				Assert.That(stripped.ToString(), Does.Contain("nfu_jog"));
			}
			finally { Object.DestroyImmediate(host); }
		}

		[Test]
		public void RecorderChoosesOnceAtOpen_EvenWhenJogStaysZero()
		{
			var telemetry = new StringWriter();
			var inputs = new StringWriter();
			using var recorder = new RunRecorder(telemetry, inputs, 100, null, controlSource: new ZeroJogControls());
			recorder.BeforeStep(0, default, default, ControlInputs.Neutral);
			recorder.AfterStep(1, default, default);
			recorder.Finish(1, default, default);
			Assert.That(recorder.Failure, Is.Null);
			Assert.That(inputs.ToString(), Does.StartWith(ControlTraceV2Tests.V2Header + "\n0.00,0,0,0,0\n"));
			Assert.That(telemetry.ToString(), Does.Contain("helm,nfu_jog\n"));
			Assert.That(ReplayPlayer.ControlsForTrace(ControlTrace.Parse(inputs.ToString().Split('\n'))), Is.InstanceOf<INfuControlSource>());
			Assert.That(ReplayPlayer.ControlsForTrace(ControlTrace.Parse(new[] { ControlTrace.HeaderRow, "0,0,0,0" })), Is.Not.InstanceOf<INfuControlSource>());
		}

		[Test]
		public void FourColumnRecorderFailsBeforeDroppingJog()
		{
			var telemetry = new StringWriter();
			var inputs = new StringWriter();
			using var recorder = new RunRecorder(telemetry, inputs, 10, null);
			recorder.BeforeStep(0, default, default, new ControlInputs(0, 0, 0, 1));
			Assert.That(recorder.Failure, Is.Not.Null);
			Assert.That(recorder.Failure.Message, Does.Contain("jog").IgnoreCase);
			Assert.That(recorder.RowCount, Is.Zero, "the invalid input must not leave a success-looking row");
		}
	}
}
