using System;
using System.Collections.Generic;

namespace FerrySim.Sim
{
	/// <summary>
	/// What the scenario picker offers, and how a choice becomes a <see cref="ScenarioSettings"/>. The simulation
	/// side implements it from the slice's landings, the environment packs it can find and the vessel files; UI
	/// tests use a fake. Every list is in a fixed, documented order, so pickers and tests are deterministic.
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
		/// can't run: an unknown landing, day or vessel, or a time outside the day's coverage.
		/// </summary>
		bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem);
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
		public ScenarioLanding(string stopId, string name)
		{
			StopId = stopId ?? throw new ArgumentNullException(nameof(stopId));
			Name = name ?? throw new ArgumentNullException(nameof(name));
		}

		/// <summary>The GTFS stop id, for example <c>87</c> for Wall St/Pier 11.</summary>
		public string StopId { get; }

		/// <summary>The GTFS stop name.</summary>
		public string Name { get; }
	}

	/// <summary>A scenario day: one real day of tide, current and wind.</summary>
	public sealed class ScenarioDay
	{
		public ScenarioDay(string date, string packName, TimeSpan localUtcOffset, bool windMissing)
		{
			Date = date ?? throw new ArgumentNullException(nameof(date));
			PackName = packName ?? throw new ArgumentNullException(nameof(packName));
			LocalUtcOffset = localUtcOffset;
			WindMissing = windMissing;
		}

		/// <summary>The local (America/New_York) date, yyyy-MM-dd.</summary>
		public string Date { get; }

		/// <summary>The environment pack as region/date, for example <c>lower-east-river/2025-07-14</c>.</summary>
		public string PackName { get; }

		/// <summary>America/New_York's offset from UTC on that day (-4 h or -5 h).</summary>
		public TimeSpan LocalUtcOffset { get; }

		/// <summary>True when the day has no wind data, so the wind reads as still air.</summary>
		public bool WindMissing { get; }
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

	/// <summary>What the user picked: where, which day, what time and which vessel.</summary>
	public readonly struct ScenarioChoice
	{
		public ScenarioChoice(string stopId, string date, int localMinutes, string vesselId)
		{
			StopId = stopId;
			Date = date;
			LocalMinutes = localMinutes;
			VesselId = vesselId;
		}

		/// <summary>The landing's GTFS stop id.</summary>
		public string StopId { get; }

		/// <summary>The scenario day, yyyy-MM-dd (local date).</summary>
		public string Date { get; }

		/// <summary>Start time as minutes after local midnight in America/New_York, 0 to 1439.</summary>
		public int LocalMinutes { get; }

		/// <summary>The vessel's <see cref="ScenarioVessel.Id"/>.</summary>
		public string VesselId { get; }
	}
}
