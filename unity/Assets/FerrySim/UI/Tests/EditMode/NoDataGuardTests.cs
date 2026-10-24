using System;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The instruments obey <c>IVesselTelemetrySource.HasTelemetry</c> (review finding 1). Guards against: a
	/// bridge that has not stepped, failed to start or has ended showing its default zeros (0.0 kn, 000°T,
	/// 0 rpm, NEUTRAL, 0.0 ft) as if they were measurements; the last values staying on screen, unmarked,
	/// after the run ends; a depth alarm raised or lit by those zeros; the HUD showing values the panel
	/// would not; and the clock throwing on, or inventing a time from, a scenario start that was never set.
	/// </summary>
	public class NoDataGuardTests
	{
		private const double Threshold = DepthAlarm.DefaultThresholdMetres;

		[Test]
		public void Panel_ShowsNoData_ForASourceThatHasNotStepped()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.NeverStarted();

			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);

			AssertNoData(panel, "a source that has not stepped");
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Normal), "a zero depth that was never measured raises no alarm");
		}

		[Test]
		public void Panel_ShowsNoData_InsteadOfTheLastValues_WhenTheRunEnds()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(depthUnderKeel: 1.0);
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0);
			Assert.That(panel.ShowingNoData, Is.False);
			Assert.That(panel.Heading.text, Is.EqualTo("045"));
			Assert.That(panel.Depth.AlarmLit, Is.True, "the fixture is shallow enough to raise the alarm while data flows");

			// The run ends: the source still holds its last sample, but it is no longer current.
			source.HasTelemetry = false;
			panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.1);

			AssertNoData(panel, "after the run ended");
		}

		[Test]
		public void Hud_ShowsNoData_WhenTheSourceHasNoTelemetry()
		{
			var hud = new HudOverlay();
			var source = StubTelemetrySource.Underway();
			hud.Refresh(source);
			Assert.That(hud.Speed.text, Is.EqualTo("8.0"), "the fixture is valid while it has telemetry");

			source.HasTelemetry = false;
			hud.Refresh(source);

			foreach (var label in new[] { hud.Speed, hud.Heading, hud.PortGear, hud.StarboardGear, hud.RudderText })
			{
				Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), label.name);
			}

			Assert.That(double.IsNaN(hud.PortLever.Lever) && double.IsNaN(hud.StarboardLever.Lever), Is.True, "no lever markers");
			Assert.That(double.IsNaN(hud.Rudder.PortDegrees) && double.IsNaN(hud.Rudder.StarboardDegrees), Is.True, "no rudder pointers");
		}

		[Test]
		public void Clock_ShowsNoValue_WhenTheScenarioStartWasNeverSet()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway();
			source.ScenarioStartUtc = default;

			Assert.DoesNotThrow(() => panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0));

			Assert.That(panel.Clock.text, Is.EqualTo(InstrumentFormat.NoValue), "0001-01-01 is not a scenario date");
			Assert.That(panel.Heading.text, Is.EqualTo("045"), "the rest of the sample is still valid and shown");
			Assert.That(panel.Elapsed.text, Is.EqualTo("T+0:10"));
		}

		[Test]
		public void Clock_ShowsNoValue_ForAnInstantOutsideTheCalendar()
		{
			var panel = new WheelhousePanel();
			var source = StubTelemetrySource.Underway(time: 1e15);

			Assert.DoesNotThrow(() => panel.Refresh(source, DistanceUnit.Feet, Threshold, 0.0));

			Assert.That(panel.Clock.text, Is.EqualTo(InstrumentFormat.NoValue));
		}

		private static void AssertNoData(WheelhousePanel panel, string context)
		{
			Assert.That(panel.ShowingNoData, Is.True, context + ": the NO DATA badge");
			foreach (var label in new[]
			{
				panel.Heading, panel.SpeedOverGround, panel.CourseOverGround, panel.SpeedThroughWater, panel.Depth.Value,
				panel.Port.ShaftRpm, panel.Starboard.ShaftRpm, panel.Port.LeverPercent, panel.Starboard.LeverDirection,
				panel.Clock, panel.Elapsed, panel.Contact.Value,
			})
			{
				Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{context}: {label.name}");
			}

			Assert.That(panel.Port.Tachometer.ReadoutText, Is.EqualTo(InstrumentFormat.NoValue), context + ": tachometer");
			Assert.That(panel.Starboard.Gear.Text, Is.EqualTo(InstrumentFormat.NoValue), context + ": gear badge");
			Assert.That(double.IsNaN(panel.Compass.HeadingDegrees), Is.True, context + ": compass card hidden");
			Assert.That(double.IsNaN(panel.Rudder.PortDegrees), Is.True, context + ": rudder pointer hidden");
			Assert.That(panel.Depth.AlarmLit, Is.False, context + ": no alarm indication without data");
		}
	}
}
