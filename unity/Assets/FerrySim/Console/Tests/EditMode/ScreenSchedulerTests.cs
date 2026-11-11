using NUnit.Framework;

namespace FerrySim.Console.Tests
{
	// Failure modes: two refreshes in one frame, catch-up bursts, cap violations, focused starvation and hidden work.
	public class ScreenSchedulerTests
	{
		[Test] public void NoViewerDoesNoWork_AndFocusWinsTheFirstSlot()
		{
			var scheduler = new ScreenScheduler();
			Assert.That(scheduler.Next(0, 0), Is.EqualTo(-1));
			scheduler.SetWheelhouseVisible(true);
			scheduler.SetFocus("plotter");
			Assert.That(scheduler.Next(.1, 1), Is.EqualTo(1));
			Assert.That(scheduler.Next(.1, 1), Is.EqualTo(-1));
		}
		[TestCase(60), TestCase(120), TestCase(10)]
		public void OneMinuteHonoursCapsAndStarvesNothing(int fps)
		{
			var scheduler = new ScreenScheduler();
			scheduler.SetWheelhouseVisible(true);
			scheduler.SetPopOutShown("alarms", true);
			scheduler.SetFocus("radar");
			var counts = new int[5];
			var last = new double[5];
			for (var i = 0; i < 5; i++) last[i] = -100;
			for (var frame = 0; frame < fps * 60; frame++)
			{
				var time = frame / (double)fps;
				var slot = scheduler.Next(time, frame);
				if (slot < 0) continue;
				Assert.That(time - last[slot], Is.GreaterThanOrEqualTo((slot == 0 ? .05 : .1) - 1e-9));
				last[slot] = time; counts[slot]++;
				Assert.That(scheduler.Next(time, frame), Is.EqualTo(-1));
			}
			for (var i = 0; i < 5; i++)
			{
				Assert.That(counts[i], Is.InRange(50, i == 0 ? 1200 : 600), "slot " + i);
				Assert.That(60 - last[i], Is.LessThan(.8), "no starvation even below aggregate cadence");
			}
		}
		[Test] public void PopOutDemandIsPerScreen_AlarmHasNoMount()
		{
			var scheduler = new ScreenScheduler();
			scheduler.SetPopOutShown("engine-port", true);
			Assert.That(scheduler.Next(0, 0), Is.EqualTo(2));
			scheduler.SetPopOutShown("engine-port", false);
			Assert.That(scheduler.Next(1, 1), Is.EqualTo(-1));
			scheduler.SetWheelhouseVisible(true);
			Assert.That(scheduler.IsDemanded(4), Is.False);
		}
	}
}
