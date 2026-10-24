using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	public enum DebriefEventKind
	{
		GearShift = 0,
		Contact = 1,
		DepthAlarm = 2,
	}

	public enum EngineSide
	{
		None = 0,
		Port = 1,
		Starboard = 2,
	}

	/// <summary>Something worth marking on the debrief timeline. Values are SI; text is made at display time.</summary>
	public readonly struct DebriefEvent
	{
		public DebriefEvent(DebriefEventKind kind, double time, double endTime, EngineSide side, GearState from, GearState to, double value)
		{
			Kind = kind;
			Time = time;
			EndTime = endTime;
			Side = side;
			From = from;
			To = to;
			Value = value;
		}

		public DebriefEventKind Kind { get; }

		public double Time { get; }

		/// <summary>When it ended; NaN when it was still going at the end of the recording.</summary>
		public double EndTime { get; }

		public double Duration => EndTime - Time;

		public EngineSide Side { get; }

		/// <summary>Gear shifts: the steady gear before the shift.</summary>
		public GearState From { get; }

		/// <summary>Gear shifts: the steady gear reached, or <see cref="GearState.Shifting"/> if the recording ended first.</summary>
		public GearState To { get; }

		/// <summary>Contact: peak force, newtons. Depth alarm: shallowest depth under keel, metres. Gear shift: NaN.</summary>
		public double Value { get; }

		public string Describe(DistanceUnit unit)
		{
			switch (Kind)
			{
				case DebriefEventKind.GearShift:
					var side = Side == EngineSide.Port ? "PORT" : "STBD";
					return double.IsNaN(EndTime)
						? $"{side} {GearLabel(From)} → still shifting at the end"
						: $"{side} {GearLabel(From)} → {GearLabel(To)} in {Duration.ToString("F1", CultureInfo.InvariantCulture)} s";
				case DebriefEventKind.Contact:
					return $"CONTACT, peak {InstrumentFormat.Force(Value)} kN";
				default:
					return $"DEPTH ALARM, min {InstrumentFormat.Distance(Value, unit)} {DisplayUnits.Symbol(unit)} under keel";
			}
		}

		private static string GearLabel(GearState gear) => gear.ToString().ToUpperInvariant();
	}

	/// <summary>
	/// Finds the events the debrief marks: gear shifts (one event from leaving a steady gear to reaching
	/// the next, so its length is the shift time), contact episodes (touches closer together than the
	/// contact indicator's hold time are one episode, as on the panel) and depth alarms (the panel's own
	/// <see cref="DepthAlarm"/> logic run over the recording). When the recording carries the threshold in
	/// force at each sample, the alarms are the ones the learner had; otherwise they are a retrospective
	/// reconstruction at the threshold given, which the debrief labels as such.
	/// </summary>
	public static class DebriefEvents
	{
		/// <summary>Whether depth alarms come from the recording's own threshold history rather than a threshold given now.</summary>
		public static bool UsesRecordedThresholds(TelemetryRecording recording) => recording?.DepthAlarmThreshold != null;

		/// <param name="recording">May be null (no events).</param>
		/// <param name="depthAlarmThresholdMetres">Used only when the recording carries no threshold history.</param>
		public static List<DebriefEvent> Detect(TelemetryRecording recording, double depthAlarmThresholdMetres)
		{
			var events = new List<DebriefEvent>();
			if (recording == null)
			{
				return events;
			}

			GearShifts(recording, recording.PortGear, EngineSide.Port, events);
			GearShifts(recording, recording.StarboardGear, EngineSide.Starboard, events);
			Contacts(recording, events);
			DepthAlarms(recording, depthAlarmThresholdMetres, events);
			return events.OrderBy(e => e.Time).ThenBy(e => e.Kind).ThenBy(e => e.Side).ToList();
		}

		private static void GearShifts(TelemetryRecording recording, IReadOnlyList<GearState> gears, EngineSide side, List<DebriefEvent> events)
		{
			var time = recording.Time;
			var hasSteady = false;
			var steady = GearState.Neutral;
			var shiftStart = -1;
			var from = GearState.Shifting;
			for (var i = 0; i < recording.Count; i++)
			{
				var gear = gears[i];
				if (gear == GearState.Shifting)
				{
					if (shiftStart < 0)
					{
						shiftStart = i;
						from = hasSteady ? steady : GearState.Shifting;
					}

					continue;
				}

				if (shiftStart >= 0)
				{
					events.Add(new DebriefEvent(DebriefEventKind.GearShift, time[shiftStart], time[i], side, from, gear, double.NaN));
					shiftStart = -1;
				}
				else if (hasSteady && gear != steady)
				{
					events.Add(new DebriefEvent(DebriefEventKind.GearShift, time[i], time[i], side, steady, gear, double.NaN));
				}

				steady = gear;
				hasSteady = true;
			}

			if (shiftStart >= 0)
			{
				events.Add(new DebriefEvent(DebriefEventKind.GearShift, time[shiftStart], double.NaN, side, from, GearState.Shifting, double.NaN));
			}
		}

		private static void Contacts(TelemetryRecording recording, List<DebriefEvent> events)
		{
			var time = recording.Time;
			var force = recording.ContactForce;
			var open = false;
			var start = 0.0;
			var peak = 0.0;
			var lastTouch = 0.0;
			var end = double.NaN;
			for (var i = 0; i < recording.Count; i++)
			{
				var f = force[i];
				var touching = !double.IsNaN(f) && !double.IsInfinity(f) && f > 0.0;
				if (open && time[i] - lastTouch >= ContactMonitor.DefaultHoldSeconds && !double.IsNaN(end))
				{
					events.Add(new DebriefEvent(DebriefEventKind.Contact, start, end, EngineSide.None, default, default, peak));
					open = false;
				}

				if (touching)
				{
					if (!open)
					{
						open = true;
						start = time[i];
						peak = f;
					}
					else
					{
						peak = Math.Max(peak, f);
					}

					lastTouch = time[i];
					end = double.NaN;
				}
				else if (open && double.IsNaN(end))
				{
					end = time[i];
				}
			}

			if (open)
			{
				events.Add(new DebriefEvent(DebriefEventKind.Contact, start, end, EngineSide.None, default, default, peak));
			}
		}

		private static void DepthAlarms(TelemetryRecording recording, double thresholdMetres, List<DebriefEvent> events)
		{
			var time = recording.Time;
			var depth = recording.DepthUnderKeel;
			var recorded = recording.DepthAlarmThreshold;
			var alarm = new DepthAlarm(recorded != null ? recorded[0] : thresholdMetres);
			var raised = false;
			var start = 0.0;
			var shallowest = double.PositiveInfinity;
			for (var i = 0; i < recording.Count; i++)
			{
				if (recorded != null)
				{
					alarm.ThresholdMetres = recorded[i];
				}

				alarm.Update(depth[i], time[i]);
				if (alarm.IsRaised)
				{
					if (!raised)
					{
						raised = true;
						start = time[i];
						shallowest = double.PositiveInfinity;
					}

					if (!double.IsNaN(depth[i]))
					{
						shallowest = Math.Min(shallowest, depth[i]);
					}
				}
				else if (raised)
				{
					events.Add(new DebriefEvent(DebriefEventKind.DepthAlarm, start, time[i], EngineSide.None, default, default, shallowest));
					raised = false;
				}
			}

			if (raised)
			{
				events.Add(new DebriefEvent(DebriefEventKind.DepthAlarm, start, double.NaN, EngineSide.None, default, default, shallowest));
			}
		}
	}
}
