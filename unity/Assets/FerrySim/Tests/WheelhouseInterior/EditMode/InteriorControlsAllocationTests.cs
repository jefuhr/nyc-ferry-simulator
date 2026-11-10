using System;
using System.Diagnostics;
using System.IO;
using FerrySim.Core.Instruments;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class InteriorControlsAllocationTests
	{
		[Test]
		public void RefreshWithMovingValuesAndFlashingAlarmAllocatesZero()
		{
			using var f = new InstrumentFixture();
			f.Alarms.Step(0, true, 0, 0);
			for (var i = 0; i < 1000; i++) Tick(f, i);
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false; recorder.FilterToCurrentThread();
			var chars = new char[16];
			recorder.enabled = true; var positive = new string(chars); recorder.enabled = false;
			GC.KeepAlive(positive);
			Assert.That(recorder.sampleBlockCount, Is.EqualTo(1), "GC.Alloc positive control");
			const int count = 20000;
			var start = Stopwatch.GetTimestamp();
			recorder.enabled = true;
			for (var i = 0; i < count; i++) Tick(f, i);
			recorder.enabled = false;
			var elapsed = Stopwatch.GetTimestamp() - start;
			var samples = recorder.sampleBlockCount;
			recorder.CollectFromAllThreads();

			var report = "refreshes=" + count + " GC.Alloc samples=" + samples + " microseconds/refresh=" + elapsed * 1e6 / Stopwatch.Frequency / count;
			TestContext.WriteLine(report);
			var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/wheelhouse-controls")); Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "refresh-cost.txt"), report);
			Assert.That(samples, Is.Zero, "zero allocation samples means zero managed bytes");
			Assert.That(InteriorLampTests.Levels(f)[0], Is.EqualTo(f.Alarms.LampOn(0, f.Time) ? 1 : 0), "cost must include working lamps");
		}
		private static void Tick(InstrumentFixture f, int i)
		{
			f.Time = i * .01;
			for (var c = 0; c < f.Values.Length; c++) f.Values[c] = Math.Sin(i * .03 + c);
			f.View.Refresh();
		}
	}
}
