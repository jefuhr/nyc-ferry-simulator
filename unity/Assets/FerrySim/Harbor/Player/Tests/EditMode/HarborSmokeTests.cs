using System;
using NUnit.Framework;
using FerrySim.Sim;
using UnityEngine.Profiling;
namespace FerrySim.Harbor.Player.Tests
{
	/// <summary>Never run the harbor harness in a slice-first player, or accept a path outside installed data.</summary>
	public class HarborSmokeTests
	{
		[TestCase("Assets/FerrySim/Harbor/Scenes/Harbor.unity", true)]
		[TestCase("Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity", false)]
		[TestCase("Assets/Other.unity", false)]
		public void OnlyHarborFirstPlayersStartTheHarness(string scene, bool expected) => Assert.That(HarborPlayerSmokeRun.IsHarborPlayer(scene), Is.EqualTo(expected));
		[TestCase("/installed/data/file", true), TestCase("/installed/database/file", false), TestCase("/repo/file", false)]
		public void PayloadContainmentUsesADirectoryBoundary(string path, bool expected) => Assert.That(HarborPlayerSmokeRun.IsWithin("/installed/data", path), Is.EqualTo(expected));
		[Test] public void PerFrameDrivePollAllocatesNothingWithCalibratedRecorder()
		{
			Func<bool> poll = () => HarborPlayerSmokeRun.DriveComplete(3, RunHold.None, true, 10);
			poll();
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
				Assert.That(calibration, Is.EqualTo(1), "deliberate one-string positive control");
				recorder.enabled = true;
				for (var i = 0; i < 1000; i++) poll();
				recorder.enabled = false;
				var samples = recorder.sampleBlockCount;
				TestContext.WriteLine("GC.Alloc positive control=" + calibration + "; measured samples=" + samples);
				Assert.That(samples, Is.Zero);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

	}
}
