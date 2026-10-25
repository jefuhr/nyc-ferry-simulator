using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The coverage warnings (outside the day's data, outside the depth grid) follow the source's per-run counts.
	/// Guards against: a warning missing while the count is above zero or shown at zero; a warning that flickers
	/// when misses come every other step; a paused run (no new steps) dropping a live warning; a stopped count
	/// staying "live" for ever; a restarted counter or a seek back read as more misses; and a warning carried into
	/// a new run once reset.
	/// </summary>
	public class CoverageMonitorTests
	{
		[Test]
		public void NoCount_NoWarning()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(0, 0.0);
			monitor.Update(0, 5.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.None));
		}

		[Test]
		public void ARisingCount_IsLive_AndStaysLiveWhilePaused()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(0, 1.0);
			monitor.Update(3, 1.1);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live));

			// Paused: the same sample frame after frame, however much real time passes.
			for (var frame = 0; frame < 500; frame++)
			{
				monitor.Update(3, 1.1);
			}

			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live));
		}

		[Test]
		public void ACountThatStops_IsLiveForTheHold_ThenEarlier()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(4, 10.0);
			monitor.Update(4, 10.0 + CoverageMonitor.DefaultHoldSeconds * 0.9);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live), "inside the hold");
			monitor.Update(4, 10.0 + CoverageMonitor.DefaultHoldSeconds);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Earlier), "the hold has passed without a new miss");
			Assert.That(monitor.Count, Is.EqualTo(4));

			monitor.Update(5, 30.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live), "a new miss is live again");
		}

		[Test]
		public void MissesOnEveryOtherStep_DoNotFlicker()
		{
			var monitor = new CoverageMonitor();
			long count = 0;
			for (var frame = 0; frame < 200; frame++)
			{
				if (frame % 2 == 0)
				{
					count++;
				}

				monitor.Update(count, frame * 0.0167);
				Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live), $"frame {frame}");
			}
		}

		[Test]
		public void ACounterThatStartsOver_IsANewEpisode_NotMoreMisses()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(50, 1.0);
			monitor.Update(50, 9.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Earlier));

			monitor.Update(0, 9.1);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.None), "a counter back at zero has nothing to warn about");
			monitor.Update(2, 9.2);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live));
		}

		[Test]
		public void ASeekBack_StartsOver()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(7, 20.0);
			monitor.Update(7, 30.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Earlier));

			monitor.Update(7, 2.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live), "time went back: the count is all the monitor knows");
		}

		[Test]
		public void WithoutTime_ARisingCountIsLive_UntilTimeIsKnownAndTheHoldPasses()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(1, double.NaN);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live));
			monitor.Update(1, double.NaN);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live));
			monitor.Update(1, 4.0);
			monitor.Update(1, 4.0 + CoverageMonitor.DefaultHoldSeconds);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Earlier));
		}

		[Test]
		public void Reset_ForgetsTheRun()
		{
			var monitor = new CoverageMonitor();
			monitor.Update(9, 1.0);
			monitor.Reset();
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.None));
			Assert.That(monitor.Count, Is.EqualTo(0));
			monitor.Update(9, 1.0);
			Assert.That(monitor.Indication, Is.EqualTo(CoverageIndication.Live), "after a reset the same count is news again");
		}
	}
}
