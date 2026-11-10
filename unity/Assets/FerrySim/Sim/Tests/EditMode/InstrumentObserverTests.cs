using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace FerrySim.Sim.Tests
{
	/// <summary>Guards step timing, unavailable data, viewer reset and per-observation allocations.</summary>
	public class InstrumentObserverTests
	{
		internal static InstrumentModel Model() => new InstrumentModel(InstrumentParametersJson.Load(SimPaths.Resolve("vessels/instruments/t-class.json")));
		internal static IStepObserver Observer(InstrumentModel model, ISimulation simulation)
		{
			var type = typeof(FixedStepDriver).Assembly.GetType("FerrySim.Sim.InstrumentStepObserver");
			Assert.That(type, Is.Not.Null, "the instrument observer must exist");
			return (IStepObserver)Activator.CreateInstance(type, model, (IInstrumentTaps)simulation);
		}

		[Test]
		public void ReadoutFollowsEachCompletedStep()
		{
			var simulation = new VesselDynamicsFactory().Create(TestSupport.LoadTClass(), UniformEnvironment.Calm, VesselState.Planar(0, 0, 0, 0));
			var model = Model();
			var expected = Model();
			var driver = new FixedStepDriver(simulation, new NfuReplayTests.JogControls());
			driver.AddObserver(Observer(model, simulation));
			for (var i = 0; i < 500; i++)
			{
				driver.StepOnce();
				expected.Step(simulation.State, simulation.Telemetry, driver.LastControls, (IInstrumentTaps)simulation);
				for (var channel = 0; channel < ChannelNames.Count; channel++)
					Assert.That(model.Value((Channel)channel), Is.EqualTo(expected.Value((Channel)channel)), "channel " + channel + " step " + i);
			}
		}

		[Test]
		public void SourceTimingAndViewerActionsResetWithRunId()
		{
			var host = new GameObject("instrument source test");
			try
			{
				var runner = host.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				var source = (object)runner as IInstrumentSource;
				Assert.That(source, Is.Not.Null);
				Assert.That(source.Instruments, Is.Not.Null);
				Assert.That(source.HasInstruments, Is.False);
				runner.Initialize(new ScenarioSettings { environment = new ScenarioEnvironment { calm = false, depthMetres = 2.5 } }, new NfuReplayTests.JogControls());
				Assert.That(source.HasInstruments, Is.False);
				runner.Driver.StepOnce();
				Assert.That(source.HasInstruments, Is.EqualTo(runner.HasTelemetry).And.True);
				Assert.That(source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
				source.SilenceAlarms();
				Assert.That(source.Instruments.SounderOn(source.Instruments.Group(0)), Is.False);
				Assert.That(source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
				source.AcknowledgeAlarms();
				Assert.That(source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
				source.SetLampTest(true);
				Assert.That(source.Instruments.LampTest, Is.True);
				var previous = runner.RunId;
				runner.ResetRun();
				Assert.That(runner.RunId, Is.Not.EqualTo(previous));
				Assert.That(source.HasInstruments, Is.False);
				Assert.That(source.Instruments.LampTest, Is.False);
				runner.Driver.StepOnce();
				Assert.That(source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
				runner.EndRun();
				Assert.That(source.HasInstruments, Is.False);
			}
			finally { Object.DestroyImmediate(host); }
		}

		[Test]
		public void MissingInstrumentFileKeepsPhysicsRunningAndReadoutUnavailable()
		{
			var host = new GameObject("missing instruments test");
			try
			{
				var runner = host.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				runner.InstrumentParametersFile = "vessels/instruments/missing-test-file.json";
				runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral));
				runner.Driver.StepOnce();
				Assert.That(runner.HasTelemetry, Is.True);
				Assert.That(runner.HasInstruments, Is.False);
				Assert.That(runner.Instruments, Is.Not.Null);
				Assert.That(runner.Instruments.Value(Channel.Heading), Is.NaN);
				runner.AcknowledgeAlarms(); runner.SilenceAlarms(); runner.SetLampTest(true);
				Assert.That(runner.Instruments.LampTest, Is.False);
				runner.InstrumentParametersFile = "vessels/instruments/t-class.json";
				runner.ResetRun();
				runner.Driver.StepOnce();
				Assert.That(runner.HasInstruments, Is.True, "the explicit custom-vessel override is read on a new run");
			}
			finally { Object.DestroyImmediate(host); }
		}

		[Test]
		public void ObserverAfterStepAllocatesNothing_WithPositiveControl()
		{
			var simulation = new VesselDynamicsFactory().Create(TestSupport.LoadTClass(), UniformEnvironment.Calm, default);
			var model = Model();
			var observer = Observer(model, simulation);
			for (var i = 0; i < 100; i++) observer.AfterStep(0, simulation.State, simulation.Telemetry);
			var chars = new char[8];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true;
				var control = new string(chars);
				recorder.enabled = false;
				var calibration = recorder.sampleBlockCount;
				GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				var state = simulation.State;
				var telemetry = simulation.Telemetry;
				recorder.enabled = true;
				for (var i = 0; i < 10000; i++) observer.AfterStep(0, state, telemetry);
				recorder.enabled = false;
				var count = recorder.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; 10000 AfterStep calls GC.Alloc=" + count);
				Assert.That(count, Is.Zero);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}
	}
}
