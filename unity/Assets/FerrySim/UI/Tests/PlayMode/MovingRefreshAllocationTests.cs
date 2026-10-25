using System;
using System.Collections;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Profiling;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Guards changing readouts, rather than only paused samples: engine/shaft rpm, levers, unequal
	/// rudders, heading/course/speed, depth, clock/elapsed, flashing/acknowledged alarms and live/recent contact.
	/// Warm-up covers two complete alarm/contact cycles. Only the synchronous UI refresh is measured; the test runner, telemetry producer and rendering are outside it.
	/// </summary>
	public sealed class MovingRefreshAllocationTests
	{
		[UnityTest]
		public IEnumerator MovingPanelAndHud_AllocateOnlyForClockSeconds() => Measure(false);

		[UnityTest]
		public IEnumerator MovingPanelAndHud_WithStoppedClock_AllocateZeroBytes() => Measure(true);

		private static IEnumerator Measure(bool stoppedClock)
		{
			using (var rig = new UITestRig(1280, 720, withSource: false))
			{
				var host = new GameObject("Moving telemetry");
				try
				{
					var source = host.AddComponent<StubRunner>();
					source.HasTelemetry = true;
					source.RunId = 1;
					source.ScenarioStartUtc = UITestRig.ScenarioStart;
					rig.UI.Source = source;
					rig.View.HudVisible = true;
					for (var frame = 0; frame < 240; frame++)
					{
						Move(source, frame, stoppedClock);
						if (frame % 120 == 90) rig.View.AcknowledgeAlarms();
						rig.View.Refresh(source, frame * 0.1);
						yield return null;
					}

					var recorder = Recorder.Get("GC.Alloc");
					recorder.enabled = false;
					recorder.FilterToCurrentThread();
					var probe = new char[8];
					recorder.enabled = true;
					var probeText = new string(probe);
					recorder.enabled = false;
					GC.KeepAlive(probeText);
					Assert.That(recorder.sampleBlockCount, Is.EqualTo(1), "allocation counter must detect a deliberate string allocation");
					var shownClock = rig.View.Panel.Clock.text;
					var clockChanges = 0;
					var firstOverBudgetFrame = -1;
					var overBudgetFrames = 0;
					long total = 0;
					long maximum = 0;
					var nonzero = 0;
					var states = 0;
					var indications = 0;
					for (var frame = 240; frame < 840; frame++)
					{
						Move(source, frame, stoppedClock);
						if (frame % 120 == 90) rig.View.AcknowledgeAlarms();
						recorder.enabled = true;
						rig.View.Refresh(source, frame * 0.1);
						recorder.enabled = false;
						var allocations = recorder.sampleBlockCount;
						var allowed = shownClock != rig.View.Panel.Clock.text ? 1 : 0;
						if (allocations > allowed)
						{
							overBudgetFrames++;
							if (firstOverBudgetFrame < 0) firstOverBudgetFrame = frame;
						}
						if (shownClock != rig.View.Panel.Clock.text) clockChanges++;
						shownClock = rig.View.Panel.Clock.text;
						total += allocations;
						maximum = Math.Max(maximum, allocations);
						if (allocations != 0) nonzero++;
						states |= 1 << (int)rig.View.Panel.Alarm.State;
						indications |= 1 << (int)rig.View.Panel.Contact.Indication;
						yield return null;
					}

					var report = $"frames=600 total_allocations={total} mean_allocations={total / 600.0:F3} max_allocations={maximum} over_budget_frames={overBudgetFrames} first_over_budget_frame={firstOverBudgetFrame} nonzero_frames={nonzero} clock_changes={clockChanges} stopped_clock={stoppedClock} alarm_mask={states} contact_mask={indications}\n";
					var path = Path.Combine(UITestRig.RepoRoot, stoppedClock ? "artifacts/health/moving-refresh-stopped.txt" : "artifacts/health/moving-refresh.txt");
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					File.WriteAllText(path, report);
					TestContext.WriteLine(report);
					recorder.CollectFromAllThreads();
					Assert.That(states & 7, Is.EqualTo(7), "normal, active and acknowledged alarms exercised");
					Assert.That(indications & 7, Is.EqualTo(7), "none, live and recent contacts exercised");
					Assert.That(total, Is.LessThanOrEqualTo(clockChanges), report);
					Assert.That(overBudgetFrames, Is.Zero, report);
				}
				finally
				{
					UnityEngine.Object.Destroy(host);
				}
			}
		}

		private static void Move(StubRunner source, int frame, bool stoppedClock)
		{
			var time = frame * 0.1;
			if (stoppedClock) source.ScenarioStartUtc = UITestRig.ScenarioStart.AddSeconds(-time);
			var phase = frame % 120;
			var heading = Angles.ToRadians(frame * 0.83);
			var engine = new EngineTelemetry(Units.RpmToRadiansPerSecond(900 + frame * 2.1), Units.RpmToRadiansPerSecond(200 + frame * 0.7), GearState.Ahead, 0.5);
			var force = phase >= 65 && phase < 75 ? 1000 + frame * 113.0 : 0.0;
			source.State = VesselState.Planar(time, frame, frame * 2, heading, 4);
			source.Controls = new ControlInputs(Math.Sin(frame * 0.013), Math.Cos(frame * 0.017), 0.5);
			source.Telemetry = new VesselTelemetry(engine, engine, Math.Sin(frame * 0.031) * 0.5, Math.Cos(frame * 0.027) * 0.5,
				3 + frame * 0.002, 4 + frame * 0.003, heading + 0.1, phase >= 60 ? 0.5 + phase * 0.001 : 12 + phase * 0.01, force);
			if (force > 0)
			{
				var step = frame * 10 - 1;
				source.Contact = new ContactLatch(source.Contact.StepsWithContact + 1, step, force, force, step);
			}
		}
	}
}
