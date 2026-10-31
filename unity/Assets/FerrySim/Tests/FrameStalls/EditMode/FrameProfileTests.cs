using System;
using FerrySim.Harbor.Player.Journeys;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace FerrySim.FrameStalls.Tests
{
	public class FrameProfileTests
	{
		[Test]
		public void SlowFrameWindowsAreBoundedAndDoNotRepeatOverlappingFrames()
		{
			using var profile = new HarborFrameProfile(5);
			for (var frame = 0; frame < 7; frame++) profile.Sample(frame, 2, frame == 3 || frame == 4 ? 21 : 5);
			Assert.That(profile.SampledFrames, Is.EqualTo(7));
			Assert.That(profile.RetainedFrames, Is.EqualTo(5));
			Assert.That(profile.Overflow, Is.True, "Two overlapping windows need six unique rows; capacity five must flag loss.");
		}

		[Test]
		public void FrameProfilingHasNoManagedAllocationAfterWarmup()
		{
			using var profile = new HarborFrameProfile(1200);
			for (var i = 0; i < 10; i++) profile.Sample(i, 2, 21);
			var chars = new char[16];
			var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true; var control = new string(chars); recorder.enabled = false;
				var calibration = recorder.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				recorder.enabled = true;
				for (var i = 10; i < 1010; i++) profile.Sample(i, 2, 21);
				recorder.enabled = false;
				var measured = recorder.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; frame profiling GC.Alloc=" + measured);
				Assert.That(measured, Is.Zero);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

	}
}
