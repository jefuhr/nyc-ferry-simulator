using System;
using System.Diagnostics;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	public class InstrumentModelAllocationTests
	{
		[Test]
		public void Step_DoesNotAllocate_IncludingAlarmTransitionsAndOverflow()
		{
			var m = new InstrumentModel(InstrumentFixtures.Load());
			var taps = ManeuverRunner.Create(ManeuverRunner.Load("t-class.json"));
			// Keep the real read-only dynamics taps in the measured path, not a cheap test double.
			ManeuverRunner.Run(taps, new ControlInputs(.7, .7, 0), 10);
			var calibrationBefore = GC.GetAllocatedBytesForCurrentThread();
			GC.KeepAlive(new byte[64]);
			Assert.That(GC.GetAllocatedBytesForCurrentThread() - calibrationBefore, Is.GreaterThan(0), "allocation counter positive control");
			for (var i = 0; i < 10000; i++) m.Step(InstrumentFixtures.State(i * .01), InstrumentFixtures.Telemetry(100), ControlInputs.Neutral, taps);
			var timer = Stopwatch.StartNew();
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 10000; i < 110000; i++) m.Step(InstrumentFixtures.State(i * .01), InstrumentFixtures.Telemetry(100, i % 2 == 0 ? 10 : -1, i % 2), ControlInputs.Neutral, taps);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before; timer.Stop();
			TestContext.Out.WriteLine($"100000 steps: {allocated} B; {timer.Elapsed.TotalMilliseconds * 10:F3} ns/step");
			Assert.That(allocated, Is.Zero);
		}
	}
}
