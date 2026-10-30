using System;
using FerrySim.Harbor.Player;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace FerrySim.HarborE2E.Tests
{
	public class MeasurementTests
	{
		[Test]
		public void PartialSweepCannotPassEvenWithManyEchoes()
		{
			var sweep = new RadarCaptureReadiness(512);
			for (var i = 0; i < 43; i++) sweep.Observe(i, i, 1, false);
			Assert.That(sweep.Complete, Is.False, "the old 30 degree capture must fail");
			Assert.That(RadarCaptureReadiness.ContentReady(sweep.Complete, 50000, 100, 1), Is.False);
		}

		[Test]
		public void OnlyEverySpokeSinceOpeningCountsAndDuplicatePollsDoNotAdvance()
		{
			var sweep = new RadarCaptureReadiness(512);
			for (var i = 0; i < 512; i++)
			{
				sweep.Observe(i, (i + 110) % 512, 1, false);
				sweep.Observe(i, (i + 110) % 512, 1, false);
			}
			Assert.That(sweep.ObservedSpokes, Is.EqualTo(512));
			Assert.That(sweep.Complete, Is.True);
			Assert.That(RadarCaptureReadiness.ContentReady(true, 999, 100, 1), Is.False);
			Assert.That(RadarCaptureReadiness.ContentReady(true, 1000, 100, 1), Is.True);
			sweep.Observe(513, 0, 1, true);
			Assert.That(sweep.Complete, Is.False, "a rewind invalidates the picture");
		}

		[Test]
		public void CapturePollHasCalibratedZeroAllocation()
		{
			var sweep = new RadarCaptureReadiness(512);
			sweep.Observe(0, 0, 4, false);
			var chars = new char[16];
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
				recorder.enabled = true;
				for (var i = 1; i < 1000; i++) sweep.Observe(i, i % 512, 4, false);
				recorder.enabled = false;
				var samples = recorder.sampleBlockCount;
				Assert.That(samples, Is.Zero);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

		[Test]
		public void WindowsNativeCounterLayoutMatchesX64AndRejectsZeros()
		{
			Assert.That(HarborProcessMemory.WindowsCounterSize, Is.EqualTo(IntPtr.Size == 8 ? 80 : 44));
			Assert.That(HarborProcessMemory.Plausible(0, 0), Is.False);
			Assert.That(HarborProcessMemory.Plausible(300000000, 200000000), Is.False);
			Assert.That(HarborProcessMemory.Plausible(300000000, 400000000), Is.True);
		}
	}
}
