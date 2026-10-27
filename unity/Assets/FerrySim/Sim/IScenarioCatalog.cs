using System;
using System.Collections.Generic;
using FerrySim.Core.Traffic;

namespace FerrySim.Sim
{
	/// <summary>
	/// What the scenario picker offers, and how a choice becomes a <see cref="ScenarioSettings"/>. The simulation
	/// side implements it from the slice's landings, the environment and traffic packs it can find and the vessel
	/// files; UI tests use a fake. Every list is in a fixed, documented order, so pickers and tests are deterministic.
	/// </summary>
	public interface IScenarioCatalog
	{
		/// <summary>Landings a run can start from, in GTFS stop id order.</summary>
		IReadOnlyList<ScenarioLanding> Landings { get; }

		/// <summary>Scenario days with an environment pack, oldest first.</summary>
		IReadOnlyList<ScenarioDay> Days { get; }

		/// <summary>Vessels a run can use, in the order a picker shows them.</summary>
		IReadOnlyList<ScenarioVessel> Vessels { get; }

		/// <summary>
		/// Builds the scenario for a choice. False, with a one-sentence reason a picker can show, when the choice
		/// can't run: an unknown landing, berth, day or vessel, a time outside the day's coverage, traffic on a day
		/// without it (<see cref="ScenarioDay.HasTraffic"/>), or a visibility that is neither 0 nor at least
		/// <see cref="ScenarioSettings.MinimumVisibilityMetres"/>.
		/// </summary>
		bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem);
	}

	/// <summary>
	/// Optional, beside <see cref="IScenarioCatalog"/>: the water under the keel at a choice's start, so a picker can warn
	/// before a start that would be aground or nearly so. It never refuses a choice; <see cref="IScenarioCatalog.TryCreate"/>
	/// decides what can run.
	/// </summary>
	public interface IScenarioStartDepthSource
	{
		/// <summary>
		/// The water at the start of a choice that <see cref="IScenarioCatalog.TryCreate"/> would accept. False when it would
		/// refuse the choice, or when the scenario has no real depth (calm or uniform water).
		/// </summary>
		bool TryGetStartDepth(ScenarioChoice choice, out ScenarioStartDepth depth);
	}

	/// <summary>The water at a run's start, metres.</summary>
	public readonly struct ScenarioStartDepth
	{
		public ScenarioStartDepth(double minimumDepth, double maximumDraft)
		{
			MinimumDepth = minimumDepth;
			MaximumDraft = maximumDraft;
		}

		/// <summary>The shallowest depth under the hull at the start pose and time: the water level minus the bed, m.</summary>
		public double MinimumDepth { get; }

		/// <summary>The vessel's maximum draft, m.</summary>
		public double MaximumDraft { get; }

		/// <summary>Water under the keel, m: negative when the start is aground.</summary>
		public double UnderKeel => MinimumDepth - MaximumDraft;
	}

	/// <summary>Starts runs. The simulation side implements it; UI tests use a fake.</summary>
	public interface IScenarioLauncher
	{
		/// <summary>
		/// Ends the current run, if any, and starts a new one from <paramref name="settings"/> with a new
		/// <see cref="IVesselTelemetrySource.RunId"/>. False, with a one-sentence reason, when the run can't start.
		/// </summary>
		bool TryStart(ScenarioSettings settings, out string problem);
	}

	/// <summary>A landing a run can start from.</summary>
	public sealed class ScenarioLanding
	{
		private static readonly ScenarioBerth[] NoBerths = new ScenarioBerth[0];

		public ScenarioLanding(string stopId, string name)
			: this(stopId, name, null)
		{
		}

		/// <summary>A landing and its berths, the default first; null or empty berths for a landing with one unnamed berth.</summary>
		public ScenarioLanding(string stopId, string name, IReadOnlyList<ScenarioBerth> berths)
			: this(stopId, name, berths, null)
		{
		}

		/// <summary>A landing, its berths and a note for the picker; null for no note.</summary>
		public ScenarioLanding(string stopId, string name, IReadOnlyList<ScenarioBerth> berths, string note)
		{
			StopId = stopId ?? throw new ArgumentNullException(nameof(stopId));
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Berths = berths ?? NoBerths;
			Note = note ?? string.Empty;
		}

		/// <summary>The GTFS stop id, for example <c>87</c> for Wall St/Pier 11.</summary>
		public string StopId { get; }

		/// <summary>The GTFS stop name.</summary>
		public string Name { get; }

		/// <summary>
		/// The berths a run can start at, the default first and the rest in an order the catalog documents. Never
		/// null. A picker offers a berth choice only when there are two or more.
		/// </summary>
		public IReadOnlyList<ScenarioBerth> Berths { get; }

		/// <summary>
		/// What a picker shows beside the landing: its evidence or a crew restriction, for example that its landing side
		/// is waiting for a crew answer. Empty when there is nothing to say; never null. A landing with a note may still
		/// be refused by <see cref="IScenarioCatalog.TryCreate"/>, which gives the reason.
		/// </summary>
		public string Note { get; }
	}

	/// <summary>A berth at a landing: one landing face a run can start bow-on to.</summary>
	public sealed class ScenarioBerth
	{
		public ScenarioBerth(string id, string name)
			: this(id, name, null)
		{
		}

		/// <summary>A berth and a note for the picker; null for no note.</summary>
		public ScenarioBerth(string id, string name, string note)
		{
			Id = id ?? throw new ArgumentNullException(nameof(id));
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Note = note ?? string.Empty;
		}

		/// <summary>A stable id, unique within its landing, for example <c>enc:02261FA117EFB198</c> for a barge's berth.</summary>
		public string Id { get; }

		/// <summary>The name a picker shows, for example <c>Outer pontoon (east)</c>.</summary>
		public string Name { get; }

		/// <summary>
		/// What a picker shows beside the berth: its evidence or a crew restriction. Empty when there is nothing to say;
		/// never null.
		/// </summary>
		public string Note { get; }
	}

	/// <summary>A scenario day: one real day of tide, current and wind, and the harbor's traffic when it has it.</summary>
	public sealed class ScenarioDay
	{
		public ScenarioDay(string date, string packName, TimeSpan localUtcOffset, bool windMissing, bool hasTraffic = false,
			string currentMethod = null)
		{
			Date = date ?? throw new ArgumentNullException(nameof(date));
			PackName = packName ?? throw new ArgumentNullException(nameof(packName));
			LocalUtcOffset = localUtcOffset;
			WindMissing = windMissing;
			HasTraffic = hasTraffic;
			CurrentMethod = currentMethod ?? string.Empty;
		}

		/// <summary>The local (America/New_York) date, yyyy-MM-dd.</summary>
		public string Date { get; }

		/// <summary>The environment pack as region/date, for example <c>lower-east-river/2025-07-14</c>.</summary>
		public string PackName { get; }

		/// <summary>America/New_York's offset from UTC on that day (-4 h or -5 h).</summary>
		public TimeSpan LocalUtcOffset { get; }

		/// <summary>True when the day has no wind data, so the wind reads as still air.</summary>
		public bool WindMissing { get; }

		/// <summary>True when a run on this day can have the harbor's real traffic: the day's traffic pack is available to this build.</summary>
		public bool HasTraffic { get; }

		/// <summary>
		/// How the day's currents are made, for the scenario information: <c>station blend</c> for the harbor's packs,
		/// <c>channel field</c> for the slice's. Empty when the catalog does not say; never null.
		/// </summary>
		public string CurrentMethod { get; }
	}

	/// <summary>A vessel a run can use.</summary>
	public sealed class ScenarioVessel
	{
		public ScenarioVessel(string id, string displayName, string vesselFile)
		{
			Id = id ?? throw new ArgumentNullException(nameof(id));
			DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
			VesselFile = vesselFile ?? throw new ArgumentNullException(nameof(vesselFile));
		}

		/// <summary>A stable id, for example <c>t-class</c>.</summary>
		public string Id { get; }

		/// <summary>The name a picker shows, for example <c>T-class</c>.</summary>
		public string DisplayName { get; }

		/// <summary>Vessel parameter file, relative to the repository root (or StreamingAssets in a player).</summary>
		public string VesselFile { get; }
	}

	/// <summary>What the user picked: where and at which berth, which day, what time, which vessel, the traffic and the visibility.</summary>
	public readonly struct ScenarioChoice
	{
		public ScenarioChoice(string stopId, string date, int localMinutes, string vesselId, string berthId = null, bool traffic = false,
			TrafficLeaveOut leaveOut = TrafficLeaveOut.Automatic, int leaveOutMmsi = 0, double visibilityMetres = 0.0)
		{
			StopId = stopId;
			Date = date;
			LocalMinutes = localMinutes;
			VesselId = vesselId;
			BerthId = berthId;
			Traffic = traffic;
			LeaveOut = leaveOut;
			LeaveOutMmsi = leaveOutMmsi;
			VisibilityMetres = visibilityMetres;
		}

		/// <summary>The landing's GTFS stop id.</summary>
		public string StopId { get; }

		/// <summary>The scenario day, yyyy-MM-dd (local date).</summary>
		public string Date { get; }

		/// <summary>Start time as minutes after local midnight in America/New_York, 0 to 1439.</summary>
		public int LocalMinutes { get; }

		/// <summary>The vessel's <see cref="ScenarioVessel.Id"/>.</summary>
		public string VesselId { get; }

		/// <summary>The berth's <see cref="ScenarioBerth.Id"/>, or null or empty for the landing's default berth.</summary>
		public string BerthId { get; }

		/// <summary>True to run with the day's real traffic (<see cref="ScenarioDay.HasTraffic"/>).</summary>
		public bool Traffic { get; }

		/// <summary>
		/// Which vessel the traffic leaves out, read only with <see cref="Traffic"/>. The constructor's default leaves out
		/// the ferry that ran the trip (<see cref="TrafficLeaveOut.Automatic"/>).
		/// </summary>
		public TrafficLeaveOut LeaveOut { get; }

		/// <summary>The MMSI to leave out when <see cref="LeaveOut"/> is <see cref="TrafficLeaveOut.Mmsi"/>.</summary>
		public int LeaveOutMmsi { get; }

		/// <summary>Visibility, metres, as <see cref="ScenarioSettings.visibilityMetres"/>: 0 is unlimited.</summary>
		public double VisibilityMetres { get; }
	}
}
