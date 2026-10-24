using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Event marks on the debrief timeline. Guards against: a shift reported as two unrelated events
	/// (ahead→shifting, shifting→astern) instead of one AHEAD → ASTERN with its duration, fender bounces
	/// splitting one landing into many contacts, depth alarms that disagree with what the panel's alarm
	/// would have done (hysteresis), and events still open at the end of the recording getting an
	/// invented end time.
	/// </summary>
	public class DebriefEventsTests
	{
		private static TelemetryRecording Fixture => TelemetryCsv.Load(UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv));

		[Test]
		public void CrashStop_HasOneAheadToAsternShiftPerEngine()
		{
			var events = DebriefEvents.Detect(Fixture, DepthAlarm.DefaultThresholdMetres);

			Assert.That(events.Count, Is.EqualTo(2), string.Join("; ", events.Select(e => e.Describe(DistanceUnit.Metres))));
			foreach (var side in new[] { EngineSide.Port, EngineSide.Starboard })
			{
				var shift = events.Single(e => e.Side == side);
				Assert.That(shift.Kind, Is.EqualTo(DebriefEventKind.GearShift));
				Assert.That(shift.From, Is.EqualTo(GearState.Ahead));
				Assert.That(shift.To, Is.EqualTo(GearState.Astern));
				Assert.That(shift.Time, Is.EqualTo(45.1).Within(1e-9), "first row showing SHIFTING");
				Assert.That(shift.EndTime, Is.EqualTo(48.3).Within(1e-9), "first row showing ASTERN");
				Assert.That(shift.Duration, Is.EqualTo(3.2).Within(1e-9));
			}

			Assert.That(events[0].Describe(DistanceUnit.Feet), Is.EqualTo("PORT AHEAD → ASTERN in 3.2 s"));
		}

		[Test]
		public void FenderBounces_AreOneContactAndALaterTouchIsAnother()
		{
			var recording = Modify(Fixture, (time, depth, force) =>
			{
				if (time >= 50.0 - 1e-9 && time <= 50.4 + 1e-9)
				{
					return (depth, 40000.0 - Math.Abs(time - 50.2) * 100000.0);
				}

				if (Math.Abs(time - 51.5) < 1e-9)
				{
					return (depth, 12000.0);
				}

				return Math.Abs(time - 60.0) < 1e-9 ? (depth, 800.0) : (depth, 0.0);
			});

			var contacts = DebriefEvents.Detect(recording, DepthAlarm.DefaultThresholdMetres)
				.Where(e => e.Kind == DebriefEventKind.Contact).ToList();

			Assert.That(contacts.Count, Is.EqualTo(2));
			Assert.That(contacts[0].Time, Is.EqualTo(50.0).Within(1e-9));
			Assert.That(contacts[0].Value, Is.EqualTo(40000.0).Within(1e-6), "peak force of the episode");
			Assert.That(contacts[0].EndTime, Is.EqualTo(51.6).Within(1e-9), "first zero row after the last touch");
			Assert.That(contacts[1].Time, Is.EqualTo(60.0).Within(1e-9));
			Assert.That(contacts[1].Value, Is.EqualTo(800.0));
			Assert.That(contacts[0].Describe(DistanceUnit.Metres), Is.EqualTo("CONTACT, peak 40 kN"));
		}

		[Test]
		public void DepthAlarms_MatchThePanelAlarmIncludingHysteresis()
		{
			// Depth dips below 2 m at 40 s, hovers inside the hysteresis band, and recovers past 2.2 m at 55 s.
			var recording = Modify(Fixture, (time, depth, force) =>
			{
				if (time < 40.0)
				{
					return (5.0, force);
				}

				if (time < 50.0)
				{
					return (1.6, force);
				}

				return time < 55.0 ? (2.1, force) : (3.0, force);
			});

			var alarms = DebriefEvents.Detect(recording, 2.0).Where(e => e.Kind == DebriefEventKind.DepthAlarm).ToList();

			Assert.That(alarms.Count, Is.EqualTo(1), "hovering at 2.1 m must not re-raise");
			Assert.That(alarms[0].Time, Is.EqualTo(40.0).Within(1e-9));
			Assert.That(alarms[0].EndTime, Is.EqualTo(55.0).Within(1e-9));
			Assert.That(alarms[0].Value, Is.EqualTo(1.6).Within(1e-9), "shallowest depth during the alarm");
			Assert.That(alarms[0].Describe(DistanceUnit.Metres), Is.EqualTo("DEPTH ALARM, min 1.6 m under keel"));
			Assert.That(alarms[0].Describe(DistanceUnit.Feet), Is.EqualTo("DEPTH ALARM, min 5.2 ft under keel"));
		}

		[Test]
		public void EventsStillOpenAtTheEnd_HaveNoEndTime()
		{
			var recording = Modify(Fixture, (time, depth, force) => (time > 60.0 ? 0.5 : depth, time > 65.0 ? 100.0 : 0.0));

			var events = DebriefEvents.Detect(recording, 2.0);

			Assert.That(double.IsNaN(events.Single(e => e.Kind == DebriefEventKind.DepthAlarm).EndTime), Is.True);
			Assert.That(double.IsNaN(events.Single(e => e.Kind == DebriefEventKind.Contact).EndTime), Is.True);
		}

		[Test]
		public void RecordingThatEndsMidShift_LeavesTheShiftOpen()
		{
			var recording = Truncate(Fixture, 47.0);

			var shift = DebriefEvents.Detect(recording, 2.0).First(e => e.Kind == DebriefEventKind.GearShift);

			Assert.That(shift.From, Is.EqualTo(GearState.Ahead));
			Assert.That(shift.To, Is.EqualTo(GearState.Shifting));
			Assert.That(double.IsNaN(shift.EndTime), Is.True);
			Assert.That(shift.Describe(DistanceUnit.Feet), Is.EqualTo("PORT AHEAD → still shifting at the end"));
		}

		[Test]
		public void EventsAreInTimeOrder()
		{
			var recording = Modify(Fixture, (time, depth, force) => (time > 40.0 ? 0.5 : depth, Math.Abs(time - 36.0) < 1e-9 ? 500.0 : 0.0));

			var events = DebriefEvents.Detect(recording, 2.0);

			for (var i = 1; i < events.Count; i++)
			{
				Assert.That(events[i].Time, Is.GreaterThanOrEqualTo(events[i - 1].Time));
			}

			Assert.That(events.First().Kind, Is.EqualTo(DebriefEventKind.Contact));
		}

		/// <summary>Rebuilds a recording with depth and contact force replaced by <paramref name="change"/>(time, depth, force).</summary>
		private static TelemetryRecording Modify(TelemetryRecording source, Func<double, double, double, (double depth, double force)> change)
		{
			var builder = new TelemetryRecordingBuilder(source.Label + " (modified)", source.Header);
			for (var i = 0; i < source.Count; i++)
			{
				var sample = source.Sample(i);
				var t = sample.Telemetry;
				var (depth, force) = change(sample.State.Time, t.DepthUnderKeel, t.ContactForce);
				builder.Add(sample.State,
					new VesselTelemetry(t.Port, t.Starboard, t.PortRudderAngle, t.StarboardRudderAngle,
						t.SpeedThroughWater, t.SpeedOverGround, t.CourseOverGround, depth, force),
					sample.Controls);
			}

			return builder.Build();
		}

		private static TelemetryRecording Truncate(TelemetryRecording source, double endTime)
		{
			var builder = new TelemetryRecordingBuilder(source.Label, source.Header);
			for (var i = 0; i < source.Count && source.Time[i] <= endTime; i++)
			{
				builder.Add(source.Sample(i));
			}

			return builder.Build();
		}
	}
}
