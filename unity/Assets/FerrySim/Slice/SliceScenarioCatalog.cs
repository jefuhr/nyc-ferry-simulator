using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;

namespace FerrySim.Slice
{
	/// <summary>
	/// What the slice's scenario picker offers (<see cref="IScenarioCatalog"/>): the landings whose primary face a bow-on start
	/// can use, the days with an environment pack, and the vessels. A choice becomes a scenario that starts at rest, bow-on,
	/// <see cref="StartBowClearance"/> m off the middle of the landing's primary face and heading straight in (the slice trips'
	/// start, <see cref="LandingApproach.BowOn"/>), at the pack's local-midnight epoch plus the chosen minutes, on that day's
	/// pack. Sim/README.md (The scenario catalog) has the rules. Landings are read through <see cref="LandingGeometry"/>, never
	/// changed. Every list is in a fixed order: landings by GTFS stop id (numeric ids by value, then any others by ordinal
	/// text), days oldest first, vessels as configured.
	/// </summary>
	public sealed partial class SliceScenarioCatalog : IScenarioCatalog, IScenarioStartDepthSource
	{
		/// <summary>Bow to the primary face at the start, m: the slice E2E trips' start (Tests/E2E, TripData.Start).</summary>
		public const double StartBowClearance = 4.0;

		/// <summary>Latest start, minutes after local midnight.</summary>
		public const int LastMinute = 24 * 60 - 1;

		private readonly LandingGeometry _landings;
		private readonly EnvironmentPacks _packs;
		private readonly TrafficPacks _trafficPacks;
		private readonly List<ScenarioLanding> _landingList = new List<ScenarioLanding>();
		private readonly Dictionary<string, LandingFace> _faces = new Dictionary<string, LandingFace>(StringComparer.Ordinal);
		private readonly List<ScenarioDay> _dayList = new List<ScenarioDay>();
		private readonly Dictionary<string, EnvironmentPackDay> _days = new Dictionary<string, EnvironmentPackDay>(StringComparer.Ordinal);
		private readonly List<ScenarioVessel> _vesselList = new List<ScenarioVessel>();
		private readonly Dictionary<string, VesselParameters> _vessels = new Dictionary<string, VesselParameters>(StringComparer.Ordinal);
		private readonly List<string> _excluded = new List<string>();

		/// <param name="landings">The slice's landing faces, or null when they did not load (<paramref name="landingError"/> says why).</param>
		/// <param name="vessels">The vessels a run can use, in the order a picker shows them: each file and its loaded parameters.</param>
		/// <param name="packs">Where the days' packs are found.</param>
		/// <param name="region">The region whose packs are the days, for example <c>lower-east-river</c>.</param>
		/// <param name="landingError">Why the landings did not load, when <paramref name="landings"/> is null.</param>
		public SliceScenarioCatalog(LandingGeometry landings, IReadOnlyList<KeyValuePair<string, VesselParameters>> vessels, EnvironmentPacks packs, string region,
			string landingError = null, TrafficPacks trafficPacks = null)
		{
			_landings = landings;
			_packs = packs ?? throw new ArgumentNullException(nameof(packs));
			_trafficPacks = trafficPacks ?? ScenarioTraffic.DefaultPacks;
			Region = region ?? throw new ArgumentNullException(nameof(region));
			LandingError = landings == null ? landingError ?? "the slice's landing faces did not load" : null;
			AddLandings();
			AddDays();
			AddVessels(vessels ?? throw new ArgumentNullException(nameof(vessels)));
		}

		public IReadOnlyList<ScenarioLanding> Landings => _landingList;

		public IReadOnlyList<ScenarioDay> Days => _dayList;

		public IReadOnlyList<ScenarioVessel> Vessels => _vesselList;

		public string Region { get; }

		/// <summary>Why the landings did not load, or null.</summary>
		public string LandingError { get; }

		/// <summary>Landings, days and vessels left out, each with its reason.</summary>
		public IReadOnlyList<string> Excluded => _excluded;

		/// <summary>The parameters of a listed vessel, or null.</summary>
		public VesselParameters Vessel(string id) => id != null && _vessels.TryGetValue(id, out var vessel) ? vessel : null;

		/// <summary>A listed day's manifest summary, or null.</summary>
		public EnvironmentPackDay Day(string date) => date != null && _days.TryGetValue(date, out var day) ? day : null;

		/// <summary>The UTC start of <paramref name="localMinutes"/> after local midnight on a listed day: the pack's epoch plus the minutes.</summary>
		public static DateTimeOffset StartUtc(EnvironmentPackDay day, int localMinutes) => day.EpochUtc.AddMinutes(localMinutes);

		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
		{
			settings = null;
			if (LandingError != null)
			{
				problem = $"No landing can be used, because {LandingError}.";
				return false;
			}

			if (choice.StopId == null || !_faces.TryGetValue(choice.StopId, out var face))
			{
				problem = $"There is no landing '{choice.StopId}' in the slice to start from.";
				return false;
			}

			var berths = _landings.Berths(choice.StopId);
			var knownBerth = string.IsNullOrEmpty(choice.BerthId);
			foreach (var berth in berths) if (berth.Id == choice.BerthId) knownBerth = true;
			if (!knownBerth)
			{
				problem = $"There is no berth '{choice.BerthId}' at {face.StopName}.";
				return false;
			}
			if (!(choice.VisibilityMetres == 0 || (choice.VisibilityMetres >= ScenarioSettings.MinimumVisibilityMetres && !double.IsInfinity(choice.VisibilityMetres))))
			{
				problem = "Visibility must be clear (0) or at least " + ScenarioSettings.MinimumVisibilityMetres.ToString(CultureInfo.InvariantCulture) + " m.";
				return false;
			}

			try
			{
				face = _landings.BerthFace(choice.StopId, choice.BerthId);
			}
			catch (LandingDataException error)
			{
				problem = error.Message;
				return false;
			}

			if (choice.Date == null || !_days.TryGetValue(choice.Date, out var day))
			{
				problem = $"There is no environment pack for {choice.Date} in {Region}.";
				return false;
			}

			var vessel = Vessel(choice.VesselId);
			if (vessel == null)
			{
				problem = $"There is no vessel '{choice.VesselId}' to start with.";
				return false;
			}

			if (choice.LocalMinutes < 0 || choice.LocalMinutes > LastMinute)
			{
				problem = $"The start time must be 0 to {LastMinute} minutes after local midnight (00:00 to 23:59), not {choice.LocalMinutes}.";
				return false;
			}

			var start = StartUtc(day, choice.LocalMinutes);
			if (!day.Covers(start))
			{
				problem = $"{EnvironmentPacks.Iso(start)} is outside the {day.Date} pack, which covers {EnvironmentPacks.Iso(day.CoverageStartUtc)} to {EnvironmentPacks.Iso(day.CoverageEndUtc)}.";
				return false;
			}

			if (choice.Traffic && _trafficPacks.DayFolder(choice.Date) == null)
			{
				problem = "Traffic isn't available for this day in this build.";
				return false;
			}
			if (choice.Traffic && !Enum.IsDefined(typeof(TrafficLeaveOut), choice.LeaveOut))
			{
				problem = "The traffic leave-out mode is unknown.";
				return false;
			}
			var file = VesselFile(choice.VesselId);
			var name = string.Format(CultureInfo.InvariantCulture, "{0} ({1}), {2}, from {3}, {4}", face.StopName, face.StopId, day.Name, EnvironmentPacks.Iso(start), vessel.Id);
			if (berths.Count > 1)
			{
				foreach (var berth in berths)
					if (berth.Id == choice.BerthId || (string.IsNullOrEmpty(choice.BerthId) && berth.Order == 0)) name += ", " + berth.Name;
			}
			ScenarioSettings candidate;
			try
			{
				candidate = LandingApproach.BowOn(face, vessel, StartBowClearance, name, file, EnvironmentPacks.Iso(start), 0.0,
					new ScenarioEnvironment { pack = day.Name });
			}
			catch (ArgumentException error)
			{
				problem = error.Message;
				return false;
			}

			var position = candidate.StartPosition;
			var clearance = FaceClearance.Check(vessel, position.North, position.East, candidate.StartHeading, _landings.Geometry.Segments);
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
				candidate.Validate(_packs, _trafficPacks);
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

		/// <summary>The listed vessel's file, or null.</summary>
		public string VesselFile(string id)
		{
			foreach (var vessel in _vesselList)
			{
				if (vessel.Id == id)
				{
					return vessel.VesselFile;
				}
			}

			return null;
		}

		/// <summary>Numeric stop ids by value, before any other id; others by ordinal text.</summary>
		public static int CompareStopIds(string a, string b)
		{
			var aNumber = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var aValue);
			var bNumber = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bValue);
			if (aNumber && bNumber)
			{
				return aValue != bValue ? aValue.CompareTo(bValue) : string.CompareOrdinal(a, b);
			}

			if (aNumber != bNumber)
			{
				return aNumber ? -1 : 1;
			}

			return string.CompareOrdinal(a, b);
		}

		private void AddLandings()
		{
			if (_landings == null)
			{
				return;
			}

			var stops = new List<string>();
			var names = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var segment in _landings.Segments)
			{
				if (!names.ContainsKey(segment.StopId))
				{
					stops.Add(segment.StopId);
					names[segment.StopId] = segment.StopName;
				}
			}

			stops.Sort(CompareStopIds);
			foreach (var stop in stops)
			{
				LandingFace face;
				try
				{
					_landings.PrimaryFace(stop);
					face = _landings.BerthFace(stop);
				}
				catch (LandingDataException error)
				{
					_excluded.Add($"landing {stop} {names[stop]}: {error.Message}");
					continue;
				}

				_faces[stop] = face;
				var berths = new List<ScenarioBerth>();
				foreach (var berth in _landings.Berths(stop))
				{
					try
					{
						_landings.BerthFace(stop, berth.Id);
						berths.Add(new ScenarioBerth(berth.Id, berth.Name));
					}
					catch (LandingDataException error)
					{
						_excluded.Add(error.Message);
					}
				}
				_landingList.Add(new ScenarioLanding(stop, face.StopName, berths));
			}
		}

		private void AddDays()
		{
			var problems = new List<string>();
			foreach (var day in _packs.Days(Region, problems))
			{
				_days[day.Date] = day;
				_dayList.Add(new ScenarioDay(day.Date, day.Name, day.LocalUtcOffset, day.WindMissing, _trafficPacks.DayFolder(day.Date) != null));
			}

			foreach (var problem in problems)
			{
				_excluded.Add("day: " + problem);
			}
		}

		private void AddVessels(IReadOnlyList<KeyValuePair<string, VesselParameters>> vessels)
		{
			foreach (var pair in vessels)
			{
				var parameters = pair.Value;
				if (parameters == null || string.IsNullOrEmpty(parameters.Id))
				{
					_excluded.Add($"vessel {pair.Key}: it has no parameters or no id");
					continue;
				}

				if (_vessels.ContainsKey(parameters.Id))
				{
					_excluded.Add($"vessel {pair.Key}: its id {parameters.Id} is listed already");
					continue;
				}

				if (parameters.Contact == null)
				{
					_excluded.Add($"vessel {pair.Key}: it has no contact outline, so it cannot start next to a landing");
					continue;
				}

				_vessels[parameters.Id] = parameters;
				_vesselList.Add(new ScenarioVessel(parameters.Id, string.IsNullOrEmpty(parameters.DisplayName) ? parameters.Id : parameters.DisplayName, pair.Key));
			}
		}
	}
}
