using System;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace FerrySim.Sim.Tests
{
	/// <summary>Failure modes: stale samples in the p95 window, wrong percentile rank and per-step timing allocations.</summary>
	public class TrafficStepTimingTests
	{
		[Test]
		public void TimingWindow_UsesLatest256Samples_AndAllocatesNothing()
		{
			var window = new TrafficStepTimingWindow();
			for (var i = 1; i <= 300; i++)
			{
				window.Record(i);
			}

			Assert.That(window.LastMilliseconds, Is.EqualTo(300));
			Assert.That(window.P95Milliseconds, Is.EqualTo(288));
			var chars = new char[8];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			recorder.enabled = true;
			var control = new string (chars);
			recorder.enabled = false;
			var calibration = recorder.sampleBlockCount;
			GC.KeepAlive(control);
			Assert.That(calibration, Is.EqualTo(1), "positive control: one deliberate string allocation");
			recorder.enabled = true;
			for (var i = 0; i < 10000; i++)
			{
				window.Record(i % 17);
				_ = window.P95Milliseconds;
			}

			recorder.enabled = false;
			var allocations = recorder.sampleBlockCount;
			recorder.CollectFromAllThreads();
			Assert.That(allocations, Is.Zero);
		}
	}
}
