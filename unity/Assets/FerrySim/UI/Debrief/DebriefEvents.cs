using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;

namespace FerrySim.UI
{
	public enum DebriefEventKind
	{
		GearShift = 0,
		Contact = 1,
		DepthAlarm = 2,

		/// <summary>A traffic vessel under the CPA alarm, from raised to cleared.</summary>
		TrafficAlarm = 3,

		/// <summary>The hull overlapping a traffic vessel's hull, from the first overlap to the last.</summary>
		TrafficCollision = 4,

		/// <summary>A traffic vessel the run started beside (no alarm or collision with it), from the start until they parted.</summary>
		TrafficAlongside = 5,
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
			ValueAtStart = false;
			Mmsi = 0;
			VesselName = null;
			VesselClass = null;
		}

		/// <summary>
		/// A traffic event: the vessel it was with, by MMSI, AIS name and the pack's class name; <paramref name="valueAtStart"/> when
		/// the value is the episode's starting one because the log has no minimum for it (<see cref="ValueAtStart"/>).
		/// </summary>
		public DebriefEvent(DebriefEventKind kind, double time, double endTime, double value, int mmsi, string vesselName, string vesselClass, bool valueAtStart = false)
			: this(kind, time, endTime, EngineSide.None, default, default, value)
		{
			Mmsi = mmsi;
			VesselName = vesselName ?? string.Empty;
			VesselClass = vesselClass ?? string.Empty;
			ValueAtStart = valueAtStart;
		}

		/// <summary>Traffic events: the vessel's MMSI; 0 otherwise.</summary>
		public int Mmsi { get; }

		/// <summary>Traffic events: the vessel's AIS name (may be empty); null otherwise.</summary>
		public string VesselName { get; }

		/// <summary>Traffic events: the vessel's class, for example <c>tug-tow</c>; null otherwise.</summary>
		public string VesselClass { get; }

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

		/// <summary>
		/// Contact: peak force, newtons. Depth alarm: shallowest depth under keel, metres. Traffic alarm: the least CPA while it was
		/// up, metres. Traffic collision: the speed the two closed at when they met, m/s. Alongside: the least range while it lasted,
		/// metres. Gear shift: NaN. For an alarm or alongside still open at the end of an older log without the open-episode
		/// snapshot, the starting CPA or range instead (<see cref="ValueAtStart"/>).
		/// </summary>
		public double Value { get; }

		/// <summary>
		/// True when <see cref="Value"/> is the episode's starting CPA or range, not its least: an alarm or alongside still open when
		/// the recording ended, in a log without the open-episode snapshot.
		/// </summary>
		public bool ValueAtStart { get; }

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
				case DebriefEventKind.TrafficAlarm:
					return $"CPA ALARM, {Vessel()}, {(ValueAtStart ? "starting" : "min")} CPA {InstrumentFormat.Distance(Value, unit)} {DisplayUnits.Symbol(unit)}";
				case DebriefEventKind.TrafficCollision:
					return $"COLLISION with {Vessel()} at {InstrumentFormat.Speed(Value)} kn";
				case DebriefEventKind.TrafficAlongside:
					return $"ALONGSIDE AT START, {Vessel()}, " + (double.IsNaN(EndTime) ? "never parted" : $"parted after {Duration.ToString("F1", CultureInfo.InvariantCulture)} s");
				default:
					return $"DEPTH ALARM, min {InstrumentFormat.Distance(Value, unit)} {DisplayUnits.Symbol(unit)} under keel";
			}
		}

		private static string GearLabel(GearState gear) => gear.ToString().ToUpperInvariant();

		private string Vessel() =>
			(string.IsNullOrEmpty(VesselName) ? "unnamed" : VesselName) + " (" + (string.IsNullOrEmpty(VesselClass) ? string.Empty : VesselClass + ", ") +
			"MMSI " + Mmsi.ToString(CultureInfo.InvariantCulture) + ")";
	}

	/// <summary>
	/// Finds the events the debrief marks: gear shifts (one event from leaving a steady gear to reaching
	/// the next, so its length is the shift time), contact episodes (touches closer together than the
	/// contact indicator's hold time are one episode, as on the panel) and depth alarms (the panel's own
	/// <see cref="DepthAlarm"/> logic run over the recording). When the recording carries the threshold in
	/// force at each sample, the alarms are the ones the learner had; otherwise they are a retrospective
	/// reconstruction at the threshold given, which the debrief labels as such. A run with traffic adds its CPA
	/// alarms, collisions and the vessels it started alongside from its traffic event log, as the run raised them at its physics
	/// steps.
	/// </summary>
	public static class DebriefEvents
	{
		/// <summary>Whether depth alarms come from the recording's own threshold history rather than a threshold given now.</summary>
		public static bool UsesRecordedThresholds(TelemetryRecording recording) => recording?.DepthAlarmThreshold != null;

		/// <param name="recording">May be null (no events).</param>
		/// <param name="depthAlarmThresholdMetres">Used only when the recording carries no threshold history.</param>
		public static List<DebriefEvent> Detect(TelemetryRecording recording, double depthAlarmThresholdMetres) => Detect(recording, depthAlarmThresholdMetres, null);

		/// <param name="recording">May be null (no events).</param>
		/// <param name="depthAlarmThresholdMetres">Used only when the recording carries no threshold history.</param>
		/// <param name="traffic">The run's traffic event log, or null for a run without traffic.</param>
		public static List<DebriefEvent> Detect(TelemetryRecording recording, double depthAlarmThresholdMetres, TrafficEventLogFile traffic)
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
			Traffic(traffic, events);
			return events.OrderBy(e => e.Time).ThenBy(e => e.Kind).ThenBy(e => e.Side).ThenBy(e => e.Mmsi).ToList();
		}

		/// <summary>
		/// The log's alarms, from raised to cleared, with the least CPA while up; its collisions, from the first overlapping step to
		/// the last, with the speed they closed at; and the vessels it started alongside, until they parted, with the least range. An
		/// episode the run ended inside has no end (NaN); its least CPA or range comes from the log's open-episode snapshot, or, in
		/// an older log without one, is its starting value, marked as such.
		/// </summary>
		private static void Traffic(TrafficEventLogFile traffic, List<DebriefEvent> events)
		{
			if (traffic == null)
			{
				return;
			}

			var records = traffic.Records;
			for (var i = 0; i < records.Count; i++)
			{
				var start = records[i];
				if (start.Kind != TrafficEventKind.AlarmRaised && start.Kind != TrafficEventKind.CollisionStarted && start.Kind != TrafficEventKind.AlongsideStarted)
				{
					continue;
				}

				var endKind = start.Kind == TrafficEventKind.AlarmRaised ? TrafficEventKind.AlarmCleared :
					start.Kind == TrafficEventKind.CollisionStarted ? TrafficEventKind.CollisionEnded : TrafficEventKind.AlongsideEnded;
				TrafficEventRecord end = null;
				for (var j = i + 1; j < records.Count && end == null; j++)
				{
					if (records[j].Kind == endKind && records[j].Mmsi == start.Mmsi && records[j].StartStep == start.Step)
					{
						end = records[j];
					}
				}

				// An episode still open at the end: its least so far from the snapshot, else (an older log) its starting value.
				var open = end == null ? Open(traffic, start) : null;
				if (start.Kind == TrafficEventKind.AlarmRaised)
				{
					events.Add(new DebriefEvent(DebriefEventKind.TrafficAlarm, start.Time, end != null ? end.Time : double.NaN,
						end != null ? end.MinimumCpa : open != null ? open.MinimumCpa : start.Cpa, start.Mmsi, start.Name, start.ClassName, end == null && open == null));
				}
				else if (start.Kind == TrafficEventKind.AlongsideStarted)
				{
					events.Add(new DebriefEvent(DebriefEventKind.TrafficAlongside, start.Time, end != null ? end.Time : double.NaN,
						end != null ? end.MinimumRange : open != null ? open.MinimumRange : start.Range, start.Mmsi, start.Name, start.ClassName, end == null && open == null));
				}
				else
				{
					events.Add(new DebriefEvent(DebriefEventKind.TrafficCollision, start.Time, end != null ? SimulationClock.TimeAtStep(end.LastStep + 1) : double.NaN,
						start.RelativeSpeed, start.Mmsi, start.Name, start.ClassName));
				}
			}
		}

		/// <summary>The open-episode snapshot row for an episode the recording ended inside, or null.</summary>
		private static TrafficEventRecord Open(TrafficEventLogFile traffic, TrafficEventRecord start)
		{
			var kind = start.Kind == TrafficEventKind.AlarmRaised ? TrafficEventKind.AlarmOpen :
				start.Kind == TrafficEventKind.CollisionStarted ? TrafficEventKind.CollisionOpen : TrafficEventKind.AlongsideOpen;
			foreach (var row in traffic.Open)
			{
				if (row.Kind == kind && row.Mmsi == start.Mmsi && row.StartStep == start.Step)
				{
					return row;
				}
			}

			return null;
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
