using System;
using FerrySim.Core.Contracts;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Harbor.Player.Journeys.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.HarborE2E.Tests
{
	public class BenchmarkTests
	{
		[Test]
		public void TimingBuildRestoresTheSettingOnSuccessAndFailure()
		{
			var original=PlayerSettings.enableFrameTimingStats;
			HarborBenchmarkBuild.WithFrameTiming(() => Assert.That(PlayerSettings.enableFrameTimingStats,Is.True));
			Assert.That(PlayerSettings.enableFrameTimingStats,Is.EqualTo(original));
			Assert.Throws<InvalidOperationException>(() => HarborBenchmarkBuild.WithFrameTiming(() => {throw new InvalidOperationException("deliberate build failure");}));
			Assert.That(PlayerSettings.enableFrameTimingStats,Is.EqualTo(original));
		}

		[Test]
		public void AcceleratedClockKeepsTheRunnerActiveAndRetainsEvery100HzStep()
		{
			var original=Time.timeScale;
			var host=new GameObject("accelerated clock regression");
			var runner=host.AddComponent<FerrySim.Sim.VesselSimulationRunner>();
			try
			{
				runner.RecordRuns=false;
				runner.Initialize(new FerrySim.Sim.ScenarioSettings(),new FerrySim.Sim.ConstantControls(ControlInputs.Neutral));
				HarborBenchmark.ConfigureRate(runner,5);
				Assert.That(runner.enabled,Is.True,"disabling the component ends the run");
				Assert.That(runner.IsRunActive,Is.True);
				runner.Resume();
				runner.AdvanceFrame(.01*Time.timeScale);
				Assert.That(runner.Driver.StepCount,Is.EqualTo(5));
				Assert.That(runner.State.Time,Is.EqualTo(.05).Within(1e-12));
				Assert.That(runner.Driver.TotalDroppedTime,Is.Zero);
			}
			finally { runner.EndRun();Time.timeScale=original;UnityEngine.Object.DestroyImmediate(host); }
		}

		[Test]
		public void SwitchMemorySnapshotIncludesHeapAndCounterAvailability()
		{
			var host=new GameObject("switch memory diagnostics");
			var sampler=host.AddComponent<HarborBenchmark>();
			try
			{
				sampler.InitializeSamplerForTests();
				var snapshot=sampler.MemorySnapshot();
				Assert.That((long)snapshot["mono_heap_bytes"],Is.GreaterThan(0));
				Assert.That((long)snapshot["mono_used_bytes"],Is.GreaterThan(0));
				Assert.That((long)snapshot["mono_heap_bytes"],Is.GreaterThanOrEqualTo((long)snapshot["mono_used_bytes"]));
				Assert.That((long)snapshot["total_reserved_bytes"],Is.GreaterThan(0));
				Assert.That((bool)snapshot["system_counter_valid"],Is.True);
				Assert.That(snapshot["gfx_counter_valid"],Is.Not.Null);
				Assert.That(snapshot["texture_counter_valid"],Is.Not.Null);
			}
			finally {UnityEngine.Object.DestroyImmediate(host);}
		}

		[Test]
		public void ScriptedInputsAndFrameSamplingAllocateNothingAfterWarmup()
		{
			var host=new GameObject("benchmark allocation test");
			var sampler=host.AddComponent<HarborBenchmark>();
			sampler.InitializeSamplerForTests();
			Assert.That(sampler.SystemCounterValid,Is.True,"measure the active sampler, including profiler reads");
			var state=VesselState.Planar(0,0,0,0);
			var route=new HarborJourney {Points=new[] {new HorizontalVector(100,100)},SpanIds=Array.Empty<string>(),SpanRings=Array.Empty<HorizontalVector[]>(),FaceIds=Array.Empty<int>()};
			var controls=new HarborJourneyControls(route,() => state,() => false,26);
			for (var i=0;i<10;i++) {controls.ControlsForStep(i);sampler.SampleFrameForTests();}
			var chars=new char[16]; var recorder=Recorder.Get("GC.Alloc");recorder.enabled=false;recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled=true;var control=new string(chars);recorder.enabled=false;
				var calibration=recorder.sampleBlockCount;GC.KeepAlive(control);
				Assert.That(calibration,Is.EqualTo(1),"one deliberate allocation");
				recorder.enabled=true;
				for (var i=10;i<1010;i++) {controls.ControlsForStep(i);sampler.SampleFrameForTests();}
				recorder.enabled=false;var measured=recorder.sampleBlockCount;
				TestContext.WriteLine("positive control="+calibration+"; inputs and frame sampler GC.Alloc samples="+measured);
				Assert.That(measured,Is.Zero);
			}
			finally {recorder.enabled=false;recorder.CollectFromAllThreads();UnityEngine.Object.DestroyImmediate(host);}
		}
	}
}
