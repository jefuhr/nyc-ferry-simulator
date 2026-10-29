using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;

namespace FerrySim.Harbor
{
	/// <summary>
	/// What the harbor's scenario picker offers (<see cref="IScenarioCatalog"/>) and the water under a start
	/// (<see cref="IScenarioStartDepthSource"/>), built over the harbor's contacts (version 2, with their berth fields), the
	/// harbor's day packs and the vessel files; and the launcher (<see cref="IScenarioLauncher"/>), which hands a scenario to
	/// the harbor runtime's atomic start once <see cref="Launcher"/> is connected. Decision 0005 fixes the rules:
	/// <list type="bullet">
	/// <item>Every landing in the contacts is listed, in stable stop id order (numeric ids by value, then others by text), and
	/// every berth in the exporter's order, the default first. A landing or berth that cannot start stays listed: its
	/// <see cref="ScenarioLanding.Note"/> or <see cref="ScenarioBerth.Note"/> says why, and <see cref="TryCreate"/> refuses it
	/// naming the landing (and the berth, when the landing has several). A startable berth's note is its evidence: what the
	/// face is and who vouches for its landing side.</item>
	/// <item>The three faces whose landing side is a crew question (East 90th St, Sunset Park/BAT, St. George) are refused with
	/// <see cref="CrewAnswerReason"/>. Marks from the harbor landing audit (<see cref="HarborBerthMark"/>) add or replace a
	/// berth's note and can refuse it too.</item>
	/// <item>A start is the slice's start, through the shared <see cref="BerthStart"/>: at rest, bow-on
	/// <see cref="SliceScenarioCatalog.StartBowClearance"/> m off the middle of the chosen face, heading straight in, at the
	/// pack's local midnight plus the chosen minutes, clear of every kept contact segment. Nothing ever substitutes another
	/// face or another day, and no GTFS stop position is used as a pose.</item>
	/// <item>Depth comes from the harbor's own pack under the whole hull: a start aground (negative under-keel clearance) is
	/// refused, and the picker warns below 0.3 m, exactly as the slice does. One day's environment stays resident at a time:
	/// the harbor's depth set is 74 MB, so the 28 days are never kept together.</item>
	/// <item>Every harbor day's <see cref="ScenarioDay.CurrentMethod"/> is <see cref="CurrentMethod"/>; traffic is available when
	/// the build has the day's traffic pack.</item>
	/// </list>
	/// </summary>
	public sealed class HarborScenarioCatalog : IScenarioCatalog, IScenarioStartDepthSource, IScenarioLauncher
	{
		/// <summary>The harbor's pack region: its days are <c>data/processed/harbor/env/days/&lt;date&gt;</c>.</summary>
		public const string Region = "harbor";

		/// <summary>How the harbor packs make their currents (<see cref="ScenarioDay.CurrentMethod"/>).</summary>
		public const string CurrentMethod = "station blend";

		/// <summary>The note on a landing or berth whose landing side is still a crew question.</summary>
		public const string CrewAnswerNote = "Waiting for a crew answer on the landing side";

		/// <summary>The rest of the refusal sentence for such a landing: "East 90th St: waiting for a crew answer on the landing side."</summary>
		public const string CrewAnswerReason = "waiting for a crew answer on the landing side";

		/// <summary>The note on a landing whose data has no selectable face at all.</summary>
		public const string NoFaceNote = "No landing face in the data";

		public const string NoLauncher = "No harbor run can start yet: the harbor runner is not connected.";

		private readonly LandingGeometry _landings;
		private readonly EnvironmentPacks _packs;
		private readonly TrafficPacks _trafficPacks;
		private readonly IStartDepthEnvironments _depths;
		private readonly List<ScenarioLanding> _landingList = new List<ScenarioLanding>();
		private readonly Dictionary<string, LandingEntry> _byStop = new Dictionary<string, LandingEntry>(StringComparer.Ordinal);
		private readonly List<ScenarioDay> _dayList = new List<ScenarioDay>();
		private readonly Dictionary<string, EnvironmentPackDay> _days = new Dictionary<string, EnvironmentPackDay>(StringComparer.Ordinal);
		private readonly List<ScenarioVessel> _vesselList = new List<ScenarioVessel>();
		private readonly Dictionary<string, VesselParameters> _vessels = new Dictionary<string, VesselParameters>(StringComparer.Ordinal);
		private readonly List<string> _excluded = new List<string>();
		private string _residentDay;
		private DataEnvironment _residentEnvironment;

		/// <param name="landings">The harbor's contacts, or null when they did not load (<paramref name="landingError"/> says why).</param>
		/// <param name="vessels">The vessels a run can use, in the order a picker shows them: each file and its loaded parameters.</param>
		/// <param name="packs">Where the harbor's day packs are found.</param>
		/// <param name="landingError">Why the contacts did not load, when <paramref name="landings"/> is null.</param>
		/// <param name="trafficPacks">Where the traffic packs are found; null for the default roots.</param>
		/// <param name="marks">The harbor landing audit's notes and refusals; null or empty for none.</param>
		/// <param name="launcher">What starts a run (the harbor runtime's atomic start); null until integration, when every start is refused with <see cref="NoLauncher"/>.</param>
		/// <param name="depths">Where the depth preview gets a day's environment; null loads the pack through <paramref name="packs"/>.</param>
		public HarborScenarioCatalog(LandingGeometry landings, IReadOnlyList<KeyValuePair<string, VesselParameters>> vessels, EnvironmentPacks packs,
			string landingError = null, TrafficPacks trafficPacks = null, IReadOnlyList<HarborBerthMark> marks = null, IScenarioLauncher launcher = null,
			IStartDepthEnvironments depths = null)
		{
			_landings = landings;
			_packs = packs ?? throw new ArgumentNullException(nameof(packs));
			_trafficPacks = trafficPacks ?? ScenarioTraffic.DefaultPacks;
			_depths = depths ?? new PackDepthEnvironments(_packs);
			Launcher = launcher;
			LandingError = landings == null ? landingError ?? "the harbor's contacts did not load" : null;
			AddLandings(marks ?? Array.Empty<HarborBerthMark>());
			AddDays();
			AddVessels(vessels ?? throw new ArgumentNullException(nameof(vessels)));
		}

		public IReadOnlyList<ScenarioLanding> Landings => _landingList;

		public IReadOnlyList<ScenarioDay> Days => _dayList;

		public IReadOnlyList<ScenarioVessel> Vessels => _vesselList;

		/// <summary>What starts a run; null until the harbor runtime is connected.</summary>
		public IScenarioLauncher Launcher { get; set; }

		/// <summary>Why the contacts did not load, or null.</summary>
		public string LandingError { get; }

		/// <summary>Days and vessels left out, each with its reason. Landings are never left out: they are listed with a note.</summary>
		public IReadOnlyList<string> Excluded => _excluded;

		/// <summary>The date whose environment the depth preview keeps resident, or null.</summary>
		public string ResidentDepthDay => _residentDay;

		/// <summary>Why the last depth preview had no environment to sample, or null.</summary>
		public string DepthProblem { get; private set; }

		/// <summary>True when the berth (null or empty for the landing's default) is listed and can start.</summary>
		public bool CanStart(string stopId, string berthId) =>
			stopId != null && _byStop.TryGetValue(stopId, out var landing) && landing.Find(berthId) is BerthEntry berth && berth.Refusal == null;

		/// <summary>The parameters of a listed vessel, or null.</summary>
		public VesselParameters Vessel(string id) => id != null && _vessels.TryGetValue(id, out var vessel) ? vessel : null;

		/// <summary>A listed day's manifest summary, or null.</summary>
		public EnvironmentPackDay Day(string date) => date != null && _days.TryGetValue(date, out var day) ? day : null;

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

		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
		{
			if (!TryCreateCandidate(choice, out settings, out problem)) return false;
			if (TryGetStartDepth(choice, settings, out var depth) && depth.UnderKeel < 0)
			{
				settings = null;
				problem = string.Format(CultureInfo.InvariantCulture,
					"This start would be aground: minimum depth is {0:F2} m and vessel draft is {1:F2} m.", depth.MinimumDepth, depth.MaximumDraft);
				return false;
			}
			return true;
		}

		/// <summary>The water under the hull at a choice's start, sampled from the harbor pack (<see cref="BerthStart.TryGetStartDepth"/>).</summary>
		public bool TryGetStartDepth(ScenarioChoice choice, out ScenarioStartDepth depth)
		{
			depth = default;
			if (!TryCreateCandidate(choice, out var settings, out _)) return false;
			return TryGetStartDepth(choice, settings, out depth);
		}

		/// <summary>Hands the scenario to <see cref="Launcher"/>; refuses with <see cref="NoLauncher"/> until one is connected.</summary>
		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			if (settings == null)
			{
				problem = "No scenario was given to start.";
				return false;
			}

			var launcher = Launcher;
			if (launcher == null)
			{
				problem = NoLauncher;
				return false;
			}

			return launcher.TryStart(settings, out problem);
		}

		private bool TryCreateCandidate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
		{
			settings = null;
			if (LandingError != null)
			{
				problem = $"No landing can be used, because {LandingError}.";
				return false;
			}

			if (choice.StopId == null || !_byStop.TryGetValue(choice.StopId, out var landing))
			{
				problem = $"There is no landing '{choice.StopId}' in the harbor to start from.";
				return false;
			}

			var berth = landing.Find(choice.BerthId);
			if (berth == null)
			{
				problem = $"There is no berth '{choice.BerthId}' at {landing.Name}.";
				return false;
			}

			if (berth.Refusal != null)
			{
				problem = landing.Berths.Count > 1 ? $"{landing.Name}, {berth.Name}: {berth.Refusal}." : $"{landing.Name}: {berth.Refusal}.";
				return false;
			}

			if (!(choice.VisibilityMetres == 0 || (choice.VisibilityMetres >= ScenarioSettings.MinimumVisibilityMetres && !double.IsInfinity(choice.VisibilityMetres))))
			{
				problem = "Visibility must be clear (0) or at least " + ScenarioSettings.MinimumVisibilityMetres.ToString(CultureInfo.InvariantCulture) + " m.";
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

			return BerthStart.TryCreate(_landings, berth.Face, landing.LandingBerths, vessel, VesselFile(choice.VesselId), day, choice, _packs, _trafficPacks, out settings, out problem);
		}

		private bool TryGetStartDepth(ScenarioChoice choice, ScenarioSettings settings, out ScenarioStartDepth depth)
		{
			depth = default;
			var environment = ResidentEnvironment(choice.Date);
			if (environment == null) return false;
			return BerthStart.TryGetStartDepth(environment, Vessel(choice.VesselId), settings, choice.LocalMinutes * 60.0, out depth);
		}

		// One day at a time: another day's environment replaces it, so the harbor's depth set is never resident twice.
		private DataEnvironment ResidentEnvironment(string date)
		{
			if (_residentDay == date && _residentEnvironment != null) return _residentEnvironment;
			_residentDay = null;
			_residentEnvironment = null;
			var environment = _depths.Get(_days[date], out var problem);
			DepthProblem = problem;
			if (environment == null) return null;
			_residentDay = date;
			_residentEnvironment = environment;
			return environment;
		}

		private void AddLandings(IReadOnlyList<HarborBerthMark> marks)
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

			// A stop whose every segment was left out of contact is still a landing: it is listed, with its note.
			foreach (var excluded in _landings.Excluded)
			{
				if (!names.ContainsKey(excluded.StopId))
				{
					stops.Add(excluded.StopId);
					names[excluded.StopId] = excluded.StopName;
				}
			}

			stops.Sort(SliceScenarioCatalog.CompareStopIds);
			foreach (var stop in stops)
			{
				var entry = new LandingEntry(stop, names[stop], _landings.Berths(stop));
				foreach (var berth in entry.LandingBerths)
				{
					entry.Berths.Add(Describe(stop, berth, marks));
				}

				if (entry.Berths.Count == 0)
				{
					entry.Berths.Add(new BerthEntry("stop:" + stop, "Main berth", null, NoFaceNote, "the data has no landing face for it"));
				}

				_byStop[stop] = entry;
				var berths = new List<ScenarioBerth>();
				foreach (var berth in entry.Berths)
				{
					berths.Add(new ScenarioBerth(berth.Id, berth.Name, berth.Note));
				}

				_landingList.Add(new ScenarioLanding(stop, entry.Name, berths, entry.Berths[0].Note));
			}
		}

		/// <summary>A berth's face, its note and its refusal: the face's evidence when it can start, else why it cannot, then the audit's marks.</summary>
		private BerthEntry Describe(string stop, LandingBerth berth, IReadOnlyList<HarborBerthMark> marks)
		{
			LandingFace face = null;
			string note;
			string refusal = null;
			try
			{
				face = _landings.BerthFace(stop, berth.Id);
				note = Evidence(face, berth);
			}
			catch (LandingDataException error)
			{
				// A face whose segments lack a start basis (unverified landing side, no crew report) is a crew question;
				// anything else about the face is a data problem, named.
				var crewQuestion = error.Message.Contains("start-face basis");
				note = crewQuestion ? CrewAnswerNote : "Landing face not usable";
				refusal = crewQuestion ? CrewAnswerReason : "the landing face is not usable (" + error.Message.TrimEnd('.') + ")";
			}

			foreach (var mark in marks)
			{
				if (!mark.Applies(stop, berth.Id)) continue;
				if (mark.Note.Length > 0) note = mark.Note;
				if (mark.Refusal != null) refusal = mark.Refusal;
			}

			return new BerthEntry(berth.Id, berth.Name, refusal == null ? face : null, note, refusal);
		}

		/// <summary>What a startable face is and who vouches for its landing side, for the picker: short, beside the name.</summary>
		private static string Evidence(LandingFace face, LandingBerth berth)
		{
			var first = face.Segments[0];
			var structure = first.Source == "derived-from-gtfs" ? "Barge derived from the stop position" : "Charted face";
			if (first.Confidence == "low")
			{
				structure += " (low confidence)";
			}

			string side;
			switch (first.WaterSideBasis)
			{
				case "pipeline":
					side = "landing side chart-checked";
					break;
				case "crew":
					side = "landing side crew-reported";
					break;
				case "pipeline+crew":
					side = "landing side chart-checked and crew-reported";
					break;
				default:
					side = "landing side unsurveyed, crew berth use";
					break;
			}

			var note = structure + ", " + side;
			if (!string.IsNullOrEmpty(berth.CrewNote))
			{
				note += ", crew note " + berth.CrewNote;
			}

			return note;
		}

		private void AddDays()
		{
			var problems = new List<string>();
			foreach (var day in _packs.Days(Region, problems))
			{
				_days[day.Date] = day;
				_dayList.Add(new ScenarioDay(day.Date, day.Name, day.LocalUtcOffset, day.WindMissing, _trafficPacks.DayFolder(day.Date) != null, CurrentMethod));
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

		private sealed class LandingEntry
		{
			public LandingEntry(string stopId, string name, IReadOnlyList<LandingBerth> landingBerths)
			{
				StopId = stopId;
				Name = name;
				LandingBerths = landingBerths;
			}

			public string StopId { get; }

			public string Name { get; }

			/// <summary>The contacts' berths, in their order (empty for a stop without one).</summary>
			public IReadOnlyList<LandingBerth> LandingBerths { get; }

			public List<BerthEntry> Berths { get; } = new List<BerthEntry>();

			/// <summary>The berth with this id, the default for null or empty, or null for an unknown id.</summary>
			public BerthEntry Find(string berthId)
			{
				if (string.IsNullOrEmpty(berthId)) return Berths[0];
				foreach (var berth in Berths)
				{
					if (berth.Id == berthId) return berth;
				}

				return null;
			}
		}

		private sealed class BerthEntry
		{
			public BerthEntry(string id, string name, LandingFace face, string note, string refusal)
			{
				Id = id;
				Name = name;
				Face = face;
				Note = note;
				Refusal = refusal;
			}

			public string Id { get; }

			public string Name { get; }

			/// <summary>The face a start approaches; null when the berth cannot start.</summary>
			public LandingFace Face { get; }

			public string Note { get; }

			/// <summary>The rest of the refusal sentence, or null when the berth can start.</summary>
			public string Refusal { get; }
		}
	}
}
