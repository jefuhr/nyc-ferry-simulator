using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The panel and HUD refresh on every rendered frame, so their steady states must not make garbage (GC
	/// spikes stutter the 3D view). Guards against per-frame allocations while there is no telemetry, while
	/// the sample has not changed (paused, or several frames inside one physics step) with an alarm
	/// flashing, in the contact and alarm logic, and in the readouts of the water and the air, shown or moving.
	/// Moving telemetry has separate PlayMode coverage.
	/// </summary>
	public class RefreshAllocationTests
	{
		[Test]
		public void Refresh_WithoutTelemetry_AllocatesNothing()
		{
			var view = new WheelhouseView { HudVisible = true };
			var source = StubTelemetrySource.NeverStarted();
			for (var frame = 0; frame < 3; frame++)
			{
				view.Refresh(source, frame * 0.1);
			}

			Assert.That(() => view.Refresh(source, 0.5), Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void Refresh_OfAnUnchangedSample_AllocatesNothing_WhileTheAlarmFlashes()
		{
			var view = new WheelhouseView { HudVisible = true };
			var source = StubTelemetrySource.Underway(depthUnderKeel: 1.0, contactForce: 25000.0);
			source.Contact = new ContactLatch(1, 999, 25000.0, 25000.0, 999);
			for (var frame = 0; frame < 12; frame++)
			{
				view.Refresh(source, frame * 0.1);
			}

			// 1.25 s and 1.85 s fall in opposite phases of the 1 Hz flash, so both paths are measured.
			Assert.That(() => view.Refresh(source, 1.25), Is.Not.AllocatingGCMemory());
			Assert.That(() => view.Refresh(source, 1.85), Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void Refresh_OfAnUnchangedSample_WithTheEnvironmentShown_AllocatesNothing()
		{
			var view = new WheelhouseView { HudVisible = true };
			view.EnvironmentSource = StubEnvironmentSource.With(Readouts.Pack(timeHolds: 3, depthMisses: 2));
			var source = StubTelemetrySource.Underway(depthUnderKeel: 1.0);
			for (var frame = 0; frame < 12; frame++)
			{
				view.Refresh(source, frame * 0.1);
			}

			Assert.That(() => view.Refresh(source, 1.25), Is.Not.AllocatingGCMemory());
			Assert.That(() => view.Refresh(source, 1.85), Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void Refresh_WithAnEnvironmentButNoTelemetry_AllocatesNothing()
		{
			var view = new WheelhouseView { HudVisible = true };
			view.EnvironmentSource = StubEnvironmentSource.With(Readouts.Pack(windMissing: true));
			var source = StubTelemetrySource.NeverStarted();
			for (var frame = 0; frame < 3; frame++)
			{
				view.Refresh(source, frame * 0.1);
			}

			Assert.That(() => view.Refresh(source, 0.5), Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void EnvironmentReadoutsThatChangeEveryFrame_AllocateNothing_WithinOneClockSecond()
		{
			// Every readout moves each frame (tide, trend, set, drift, wind, the wind going missing and back, both
			// coverage counts) while the clock stays inside one second, so only the documented clock string could
			// allocate, and it does not change here. Warm-up shows every state once.
			var view = new WheelhouseView { HudVisible = true };
			var environment = StubEnvironmentSource.With(Readouts.Pack());
			view.EnvironmentSource = environment;
			var source = StubTelemetrySource.Underway(time: 10.0);
			for (var frame = 0; frame < 400; frame++)
			{
				environment.Environment = Moving(frame);
				view.Refresh(source, frame * 0.01);
			}

			Assert.That(() =>
			{
				for (var frame = 400; frame < 800; frame++)
				{
					environment.Environment = Moving(frame);
					view.Refresh(source, frame * 0.01);
				}
			}, Is.Not.AllocatingGCMemory());
		}

		private static EnvironmentReadout Moving(int frame)
		{
			var phase = frame % 200;
			return Readouts.Pack(
				levelMetres: -1.2 + phase * 0.012,
				rateMetresPerSecond: (phase - 100) * 1e-6,
				current: Readouts.CurrentToward(phase * 0.02, frame * 3.7),
				wind: Readouts.WindFrom(phase * 0.15, frame * 2.3),
				windMissing: phase >= 180,
				timeHolds: phase >= 150 ? phase : 0,
				depthMisses: phase % 50 < 10 ? phase : 0);
		}

		[Test]
		public void ContactAndAlarmLogic_AllocateNothing()
		{
			var monitor = new ContactMonitor();
			var alarm = new DepthAlarm();
			monitor.Update(ContactLatch.None, 0.0, true);
			alarm.Update(5.0, 0.0);

			Assert.That(() =>
			{
				var latch = ContactLatch.None;
				for (var frame = 1; frame <= 200; frame++)
				{
					var latestStep = frame * 3 - 1;
					if (frame % 7 == 0)
					{
						latch = new ContactLatch(latch.StepsWithContact + 1, latestStep - 1, 100.0 * frame, 100.0 * frame, latestStep - 1);
					}

					monitor.Update(latch, (latestStep + 1) / 100.0, frame % 11 != 0);
					alarm.Update(frame % 20 < 10 ? 1.0 : 5.0, frame * 0.03);
				}
			}, Is.Not.AllocatingGCMemory());
		}
	}
}
