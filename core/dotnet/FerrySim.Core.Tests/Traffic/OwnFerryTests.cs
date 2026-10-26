using System;
using System.IO;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// Leaving out the learner's own ferry. Failure modes guarded: the automatic choice taking a vessel that is not a ferry
	/// candidate, the farther candidate, one outside the distance or the time window, or the wrong one of a tie; a given MMSI
	/// not in the pack accepted; the left-out vessel still shown.
	/// </summary>
	[TestFixture]
	public class OwnFerryTests
	{
		private string _root;
		private TrafficPack _pack;

		// 08:30 local on the fixture's day: 30600 s after the epoch.
		private static readonly DateTimeOffset Start = new DateTimeOffset(2025, 7, 14, 12, 30, 0, TimeSpan.Zero);

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "ferrysim-core-tests", Guid.NewGuid().ToString("N"));
			_pack = TrafficPackJson.Load(PythonTrafficFixture.Write(_root));
		}

		[TearDown]
		public void TearDown()
		{
			try
			{
				Directory.Delete(_root, true);
			}
			catch (IOException)
			{
			}
		}

		[Test]
		public void Automatic_TakesTheNearestFerryCandidate_PresentAtTheStart()
		{
			// FERRY NEAR is at (1000, 980) at 08:30; FERRY LATE's first report, 90 s later, is at (1120, 1000).
			var run = TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Automatic, 0, 1000.0, 1000.0);
			Assert.That(run.Removed.Mmsi, Is.EqualTo(100000010));
			Assert.That(run.OwnFerry.Distance, Is.EqualTo(20.0).Within(1e-9));
			Assert.That(run.OwnFerry.TimeOffset, Is.EqualTo(0.0));
			Assert.That(run.RemovedText, Is.EqualTo("100000010"));
			Assert.That(run.IsShown(run.RemovedVessel, 0.0), Is.False, "left out");
			Assert.That(run.IsShown(_pack.Vessel(100000011).Index, 120.0), Is.True, "every other vessel stays");
		}

		[Test]
		public void Automatic_FindsACandidateByItsNearestReport_WithinTheWindow()
		{
			var run = TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Automatic, 0, 1120.0, 1010.0);
			Assert.That(run.Removed.Mmsi, Is.EqualTo(100000011), "10 m from its report 90 s after the start, nearer than FERRY NEAR's 124 m");
			Assert.That(run.OwnFerry.TimeOffset, Is.EqualTo(90.0));
			// Three minutes earlier neither is present. FERRY LATE's first report is 270 s off, outside the 120 s window; FERRY NEAR's
			// first, 60 s off at (1000, 1100), is 72 m from this start.
			var early = TrafficRun.ForScenario(_pack, Start.AddMinutes(-3), TrafficLeaveOut.Automatic, 0, 1060.0, 1060.0);
			Assert.That(early.Removed.Mmsi, Is.EqualTo(100000010));
			Assert.That(early.OwnFerry.TimeOffset, Is.EqualTo(60.0));
			Assert.That(early.OwnFerry.Distance, Is.EqualTo(Math.Sqrt(60.0 * 60.0 + 40.0 * 40.0)).Within(1e-9));
		}

		[Test]
		public void Automatic_LeavesNothingOut_WhenNoCandidateIsNearEnough()
		{
			var run = TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Automatic, 0, -4000.0, 4000.0);
			Assert.That(run.Removed, Is.Null);
			Assert.That(run.OwnFerry.Found, Is.False);
			Assert.That(run.RemovedText, Is.EqualTo("none"));
			Assert.That(run.RemovedVessel, Is.EqualTo(-1));
			// STEADY ONE (a cargo ship, not a candidate) passes 100 m from here at 3600 s: never chosen.
			var notFerry = TrafficRun.ForScenario(_pack, _pack.EpochUtc.AddSeconds(3600), TrafficLeaveOut.Automatic, 0, 100.0, 200.0);
			Assert.That(notFerry.Removed, Is.Null);
		}

		[Test]
		public void AGivenMmsi_IsLeftOut_AndAnUnknownOneRefused()
		{
			var run = TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Mmsi, 100000002, 0.0, 0.0);
			Assert.That(run.Removed.Name, Is.EqualTo("TURNER"));
			Assert.That(run.LeaveOut, Is.EqualTo(TrafficLeaveOut.Mmsi));
			var error = Assert.Throws<ArgumentException>(() => TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Mmsi, 123456789, 0.0, 0.0));
			Assert.That(error.Message, Does.Contain("123456789").And.Contain("2025-07-14"));
			var off = TrafficRun.ForScenario(_pack, Start, TrafficLeaveOut.Off, 100000002, 0.0, 0.0);
			Assert.That(off.Removed, Is.Null, "off ignores the MMSI");
		}

		[Test]
		public void Ties_GoToTheSmallerTimeOffset_ThenTheSmallerMmsi()
		{
			var a = TrafficFixtures.Straight(300000001, 0.0, 0.0, 50.0, 0.0, 0.0, 10);
			var b = TrafficFixtures.Straight(300000002, 0.0, 0.0, -50.0, 0.0, 0.0, 10);
			a.Ferry = b.Ferry = true;
			var pack = TrafficFixtures.Pack(b, a);
			var match = OwnFerryFinder.Find(new TrafficReplay(pack), 120.0, 0.0, 0.0);
			Assert.That(match.Vessel.Mmsi, Is.EqualTo(300000001), "equal distance and time: the smaller MMSI");
		}
	}
}
