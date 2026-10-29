using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;

namespace FerrySim.Slice
{
	/// <summary>
	/// What a scenario start at a berth face is, shared by the slice's catalog (<see cref="SliceScenarioCatalog"/>) and the
	/// harbor's: the time on the day's pack, the traffic, the bow-on pose <see cref="SliceScenarioCatalog.StartBowClearance"/> m
	/// off the face (<see cref="LandingApproach.BowOn"/>), the clearance from every kept contact segment
	/// (<see cref="FaceClearance"/>), the scenario's own validation, and the water under the hull at the start. The caller
	/// resolves the landing, the berth face, the day and the vessel from its own lists; this decides whether that start
	/// can run, with the one-sentence reason a picker shows when it cannot. Every rule and message is the slice catalog's.
	/// </summary>
	public static class BerthStart
	{
		/// <summary>
		/// The scenario for <paramref name="choice"/> at <paramref name="face"/>: at rest, bow-on, on <paramref name="day"/>'s
		/// pack at the pack's local midnight plus the choice's minutes. False, with the reason, when the time is outside the day
		/// or its coverage, traffic is asked for on a day without it, the face bends, the hull would start within
		/// <see cref="FaceClearance.MinimumMetres"/> of any kept segment of <paramref name="landings"/>, or the scenario does not
		/// validate. <paramref name="berths"/> are the landing's berths: with two or more, the chosen one's name goes into the
		/// scenario name.
		/// </summary>
		public static bool TryCreate(LandingGeometry landings, LandingFace face, IReadOnlyList<LandingBerth> berths, VesselParameters vessel, string vesselFile,
			EnvironmentPackDay day, ScenarioChoice choice, EnvironmentPacks packs, TrafficPacks trafficPacks, out ScenarioSettings settings, out string problem)
		{
			settings = null;
			if (choice.LocalMinutes < 0 || choice.LocalMinutes > SliceScenarioCatalog.LastMinute)
			{
				problem = $"The start time must be 0 to {SliceScenarioCatalog.LastMinute} minutes after local midnight (00:00 to 23:59), not {choice.LocalMinutes}.";
				return false;
			}

			var start = SliceScenarioCatalog.StartUtc(day, choice.LocalMinutes);
			if (!day.Covers(start))
			{
				problem = $"{EnvironmentPacks.Iso(start)} is outside the {day.Date} pack, which covers {EnvironmentPacks.Iso(day.CoverageStartUtc)} to {EnvironmentPacks.Iso(day.CoverageEndUtc)}.";
				return false;
			}

			if (choice.Traffic && trafficPacks.DayFolder(choice.Date) == null)
			{
				problem = "Traffic isn't available for this day in this build.";
				return false;
			}
			if (choice.Traffic && !Enum.IsDefined(typeof(TrafficLeaveOut), choice.LeaveOut))
			{
				problem = "The traffic leave-out mode is unknown.";
				return false;
			}
			var name = string.Format(CultureInfo.InvariantCulture, "{0} ({1}), {2}, from {3}, {4}", face.StopName, face.StopId, day.Name, EnvironmentPacks.Iso(start), vessel.Id);
			if (berths.Count > 1)
			{
				foreach (var berth in berths)
					if (berth.Id == choice.BerthId || (string.IsNullOrEmpty(choice.BerthId) && berth.Order == 0)) name += ", " + berth.Name;
			}
			ScenarioSettings candidate;
			try
			{
				candidate = LandingApproach.BowOn(face, vessel, SliceScenarioCatalog.StartBowClearance, name, vesselFile, EnvironmentPacks.Iso(start), 0.0,
					new ScenarioEnvironment { pack = day.Name });
			}
			catch (ArgumentException error)
			{
				problem = error.Message;
				return false;
			}

			var position = candidate.StartPosition;
			var clearance = FaceClearance.Check(vessel, position.North, position.East, candidate.StartHeading, landings.Geometry.Segments);
			if (!clearance.IsClear())
			{
				problem = string.Format(CultureInfo.InvariantCulture, "A {0} starting at {1} would be {2:F2} m from landing face {3}; a start needs {4:F1} m.",
					vessel.Id, face.StopName, clearance.Overlaps ? 0.0 : clearance.Distance, clearance.NearestSegmentId, FaceClearance.MinimumMetres);
				return false;
			}

			try
			{
				candidate.visibilityMetres = choice.VisibilityMetres;
				candidate.traffic = choice.Traffic ? new ScenarioTraffic { day = choice.Date,
					leaveOut = ScenarioTraffic.LeaveOutText(choice.LeaveOut), leaveOutMmsi = choice.LeaveOutMmsi } : null;
				candidate.Validate(packs, trafficPacks);
			}
			catch (Exception error) when (error is ArgumentException || error is FormatException)
			{
				problem = error.Message;
				return false;
			}

			settings = candidate;
			problem = null;
			return true;
		}

		/// <summary>
		/// The water under <paramref name="vessel"/>'s contact hull at <paramref name="settings"/>' start pose, sampled from
		/// <paramref name="environment"/> at <paramref name="time"/> seconds after the pack's epoch
		/// (<see cref="SliceScenarioCatalog.MinimumStartDepth"/>), against its maximum draft. False when the bed under the hull is
		/// unknown.
		/// </summary>
		public static bool TryGetStartDepth(DataEnvironment environment, VesselParameters vessel, ScenarioSettings settings, double time, out ScenarioStartDepth depth)
		{
			depth = default;
			var position = settings.StartPosition;
			var outline = FaceClearance.WorldOutline(vessel, position.North, position.East, settings.StartHeading);
			var minimum = SliceScenarioCatalog.MinimumStartDepth(environment, outline, time);
			if (double.IsNaN(minimum)) return false;
			depth = new ScenarioStartDepth(minimum, (vessel.Particulars.MaxDraft ?? vessel.Particulars.Draft).Value);
			return true;
		}
	}
}
