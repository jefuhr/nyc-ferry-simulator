using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Traffic
{
	/// <summary>Which vessel a run leaves out of its traffic.</summary>
	public enum TrafficLeaveOut
	{
		/// <summary>Every vessel of the day is there.</summary>
		Off = 0,

		/// <summary>The vessel with a given MMSI is left out.</summary>
		Mmsi = 1,

		/// <summary>The ferry that ran the learner's trip is left out: the nearest ferry candidate to the start (<see cref="OwnFerryFinder"/>).</summary>
		Automatic = 2,
	}

	/// <summary>The ferry candidate the automatic leave-out chose, or none.</summary>
	public readonly struct OwnFerryMatch
	{
		public OwnFerryMatch(TrafficVessel vessel, double distance, double timeOffset)
		{
			Vessel = vessel;
			Distance = distance;
			TimeOffset = timeOffset;
		}

		/// <summary>The vessel, or null when no ferry candidate was near enough.</summary>
		public TrafficVessel Vessel { get; }

		public bool Found => Vessel != null;

		/// <summary>From the learner's start to the vessel, m.</summary>
		public double Distance { get; }

		/// <summary>From the start time to the position used, s: 0 when the vessel was present then, else its nearest report's.</summary>
		public double TimeOffset { get; }
	}

	/// <summary>
	/// Finds the ferry that ran the learner's trip: the ferry candidate (the pack's <c>ferry_candidate</c>, the fleet's T and K
	/// boats) nearest the learner's start at the start time. A candidate present then is taken where the replay puts it; one that
	/// is not, at its report nearest in time within <see cref="Window"/>. The nearest within <see cref="Distance"/> wins, ties
	/// going to the smaller time offset and then the smaller MMSI; with none that near, nothing is left out. The distance covers
	/// the berths of one landing; the window, two of the pack's one-minute reports either side, so a ferry whose AIS dropped a
	/// report at the start is still found.
	/// </summary>
	public static class OwnFerryFinder
	{
		/// <summary>Farthest a candidate may be from the learner's start, m.</summary>
		public const double Distance = 150.0;

		/// <summary>Farthest a candidate's report may be from the start time, s, when it is not present then.</summary>
		public const double Window = 120.0;

		public static OwnFerryMatch Find(TrafficReplay replay, double packTime, double north, double east)
		{
			if (replay == null)
			{
				throw new ArgumentNullException(nameof(replay));
			}

			var pack = replay.Pack;
			var reports = pack.Reports;
			TrafficVessel best = null;
			var bestDistance = double.PositiveInfinity;
			var bestOffset = double.PositiveInfinity;
			foreach (var vessel in pack.Vessels)
			{
				if (!vessel.FerryCandidate)
				{
					continue;
				}

				double n, e, offset;
				if (replay.TrySample(vessel.Index, packTime, out var sample))
				{
					n = sample.North;
					e = sample.East;
					offset = 0.0;
				}
				else
				{
					var report = NearestReport(reports, vessel, packTime);
					offset = reports.Time(report) - packTime;
					if (!(Math.Abs(offset) <= Window))
					{
						continue;
					}

					n = vessel.Moving ? reports.North(report) : vessel.StaticNorth;
					e = vessel.Moving ? reports.East(report) : vessel.StaticEast;
				}

				var distance = Math.Sqrt((n - north) * (n - north) + (e - east) * (e - east));
				if (!(distance <= Distance))
				{
					continue;
				}

				// Vessels come in MMSI order, so a later one wins only when strictly better.
				if (distance < bestDistance || (distance == bestDistance && Math.Abs(offset) < Math.Abs(bestOffset)))
				{
					best = vessel;
					bestDistance = distance;
					bestOffset = offset;
				}
			}

			return best == null ? new OwnFerryMatch(null, double.NaN, double.NaN) : new OwnFerryMatch(best, bestDistance, bestOffset);
		}

		private static int NearestReport(TrafficReports reports, TrafficVessel vessel, double packTime)
		{
			var low = vessel.FirstReport;
			var high = vessel.FirstReport + vessel.Reports - 1;
			while (low < high)
			{
				var middle = (low + high) >> 1;
				if (reports.Time(middle) < packTime)
				{
					low = middle + 1;
				}
				else
				{
					high = middle;
				}
			}

			// low is the first report at or after the time (or the last); the one before may be nearer.
			if (low > vessel.FirstReport && packTime - reports.Time(low - 1) <= reports.Time(low) - packTime)
			{
				return low - 1;
			}

			return low;
		}
	}

	/// <summary>
	/// One scenario's traffic: the day's pack and its replay, the time base and the vessel left out. Simulation time t reads the
	/// pack at t + (scenario start − the pack's epoch), as the environment does. Each run gets a fresh <see cref="TrafficMonitor"/>
	/// from <see cref="StartRun"/>.
	/// </summary>
	public sealed class TrafficRun
	{
		/// <summary>The recording header's <c>traffic</c> value for a run with traffic.</summary>
		public const string PackText = "pack";

		/// <summary>The recording header's <c>traffic_removed_mmsi</c> value when no vessel is left out.</summary>
		public const string NoneRemoved = "none";

		public TrafficRun(TrafficPack pack, DateTimeOffset scenarioStartUtc, TrafficLeaveOut leaveOut, int removedMmsi, string packFolder = null, OwnFerryMatch ownFerry = default)
		{
			Pack = pack ?? throw new ArgumentNullException(nameof(pack));
			Replay = new TrafficReplay(pack);
			ScenarioStartUtc = scenarioStartUtc.ToUniversalTime();
			EpochOffset = (ScenarioStartUtc.UtcTicks - pack.EpochUtc.UtcTicks) / (double)TimeSpan.TicksPerSecond;
			LeaveOut = leaveOut;
			PackFolder = packFolder;
			OwnFerry = ownFerry;
			if (removedMmsi != 0)
			{
				Removed = pack.Vessel(removedMmsi) ?? throw new ArgumentException($"traffic pack {pack.Date} has no vessel with MMSI {removedMmsi} to leave out.");
			}
		}

		/// <summary>
		/// The scenario's traffic, with <paramref name="leaveOut"/> resolved: off; the vessel with <paramref name="mmsi"/>; or the
		/// ferry candidate nearest (<paramref name="startNorth"/>, <paramref name="startEast"/>) at the start (<see cref="OwnFerryFinder"/>),
		/// none when no candidate is near enough.
		/// </summary>
		public static TrafficRun ForScenario(TrafficPack pack, DateTimeOffset scenarioStartUtc, TrafficLeaveOut leaveOut, int mmsi, double startNorth, double startEast, string packFolder = null)
		{
			if (pack == null)
			{
				throw new ArgumentNullException(nameof(pack));
			}

			switch (leaveOut)
			{
				case TrafficLeaveOut.Off:
					return new TrafficRun(pack, scenarioStartUtc, leaveOut, 0, packFolder);
				case TrafficLeaveOut.Mmsi:
					return new TrafficRun(pack, scenarioStartUtc, leaveOut, mmsi, packFolder);
				default:
					var probe = new TrafficRun(pack, scenarioStartUtc, TrafficLeaveOut.Off, 0, packFolder);
					var match = OwnFerryFinder.Find(probe.Replay, probe.PackTime(0.0), startNorth, startEast);
					return new TrafficRun(pack, scenarioStartUtc, leaveOut, match.Found ? match.Vessel.Mmsi : 0, packFolder, match);
			}
		}

		public TrafficPack Pack { get; }

		public TrafficReplay Replay { get; }

		/// <summary>Simulation time zero, UTC.</summary>
		public DateTimeOffset ScenarioStartUtc { get; }

		/// <summary>Seconds from the pack's epoch to the scenario start: pack time = simulation time + this.</summary>
		public double EpochOffset { get; }

		public TrafficLeaveOut LeaveOut { get; }

		/// <summary>The vessel left out, or null.</summary>
		public TrafficVessel Removed { get; }

		/// <summary>The MMSI left out, or 0.</summary>
		public int RemovedMmsi => Removed?.Mmsi ?? 0;

		/// <summary>The removed vessel's index in the pack, or −1.</summary>
		public int RemovedVessel => Removed?.Index ?? -1;

		/// <summary>The automatic leave-out's choice (empty for the other modes).</summary>
		public OwnFerryMatch OwnFerry { get; }

		/// <summary>The day folder the pack was read from, when known.</summary>
		public string PackFolder { get; }

		/// <summary>The pack time at simulation time <paramref name="time"/>, s since the pack's epoch.</summary>
		public double PackTime(double time) => time + EpochOffset;

		/// <summary>The recording's <c>traffic_removed_mmsi</c>: the MMSI left out, or <see cref="NoneRemoved"/>.</summary>
		public string RemovedText => Removed != null ? Removed.Mmsi.ToString(System.Globalization.CultureInfo.InvariantCulture) : NoneRemoved;

		/// <summary>A fresh monitor for a new run, with no alarms and no steps seen.</summary>
		public TrafficMonitor StartRun(HullOutline outline, TrafficAlarmLimits limits) => new TrafficMonitor(Replay, EpochOffset, RemovedVessel, outline, limits);

		/// <summary>True when the vessel is in the run's traffic at simulation time <paramref name="time"/>: present then and not left out.</summary>
		public bool IsShown(int vessel, double time) => vessel != RemovedVessel && Replay.IsPresent(vessel, PackTime(time));

		public override string ToString() => $"traffic pack {Pack.Date} (digest {Pack.Digest}), {(Removed != null ? "leaving out " + Removed : "nothing left out")}";
	}
}
