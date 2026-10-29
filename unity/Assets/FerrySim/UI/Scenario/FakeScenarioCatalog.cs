using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;

namespace FerrySim.UI
{
	/// <summary>
	/// A stand-in <see cref="IScenarioCatalog"/> and <see cref="IScenarioLauncher"/> for UI development scenes and
	/// tests; the simulation side implements the real ones. It checks a choice the way the contract describes (a
	/// known landing, day and vessel, a time within the day), builds settings with the chosen vessel and start time,
	/// records every call, and can be told to refuse. It does not place the vessel at the landing: the start position
	/// is the world origin, which is the real catalog's job.
	/// </summary>
	public sealed class FakeScenarioCatalog : IScenarioCatalog, IScenarioLauncher
	{
		/// <summary>The 28 AIS scenario days: four Monday-to-Sunday weeks (data-pipeline/ferrydata/environment/README.md).</summary>
		public static readonly string[] ScenarioWeeks = { "2024-10-07", "2025-01-13", "2025-04-14", "2025-07-14" };

		private readonly List<(ScenarioSettings settings, ScenarioChoice choice)> _created = new List<(ScenarioSettings, ScenarioChoice)>();

		public FakeScenarioCatalog(IReadOnlyList<ScenarioLanding> landings, IReadOnlyList<ScenarioDay> days, IReadOnlyList<ScenarioVessel> vessels)
		{
			Landings = landings ?? throw new ArgumentNullException(nameof(landings));
			Days = days ?? throw new ArgumentNullException(nameof(days));
			Vessels = vessels ?? throw new ArgumentNullException(nameof(vessels));
		}

		public IReadOnlyList<ScenarioLanding> Landings { get; }

		public IReadOnlyList<ScenarioDay> Days { get; }

		public IReadOnlyList<ScenarioVessel> Vessels { get; }

		/// <summary>When not null, every <see cref="TryCreate"/> refuses with this (even an empty one).</summary>
		public string CreateProblem { get; set; }

		/// <summary>When not null, every <see cref="TryStart"/> refuses with this (even an empty one).</summary>
		public string StartProblem { get; set; }

		/// <summary>
		/// Landings <see cref="TryCreate"/> refuses, by stop id, each with the rest of its sentence ("waiting for a crew answer on
		/// the landing side"): the refusal is "&lt;name&gt;: &lt;reason&gt;.", as the harbor catalog words it.
		/// </summary>
		public Dictionary<string, string> RefusedLandings { get; } = new Dictionary<string, string>();

		/// <summary>Berths <see cref="TryCreate"/> refuses, by berth id, worded "&lt;landing&gt;, &lt;berth&gt;: &lt;reason&gt;.".</summary>
		public Dictionary<string, string> RefusedBerths { get; } = new Dictionary<string, string>();

		/// <summary>Every call in order: "create &lt;stop&gt; &lt;date&gt; &lt;minutes&gt; &lt;vessel&gt;" and "start &lt;settings name&gt;".</summary>
		public List<string> Calls { get; } = new List<string>();

		public ScenarioSettings LastCreatedSettings { get; private set; }

		public ScenarioSettings LastStartedSettings { get; private set; }

		/// <summary>The choice the last started run was made from.</summary>
		public ScenarioChoice? LastStartedChoice { get; private set; }

		/// <summary>Raised after <see cref="TryStart"/> accepts, with the choice and the day the settings were made from.</summary>
		public event Action<ScenarioChoice, ScenarioDay, ScenarioSettings> RunStarted;

		/// <summary>
		/// The slice's landings (GTFS stop id order), the 28 scenario days with their New York offsets, and the T- and
		/// K-class. <paramref name="daysWithoutWind"/> marks days as having no wind data; the packs built so far have wind
		/// on every day, so a development scene marks one only to show how the picker and the panel say so.
		/// </summary>
		public static FakeScenarioCatalog Slice(params string[] daysWithoutWind)
		{
			var landings = new[]
			{
				new ScenarioLanding("8", "South Williamsburg"),
				new ScenarioLanding("20", "Dumbo/Fulton Ferry"),
				new ScenarioLanding("87", "Wall St/Pier 11"),
				new ScenarioLanding("115", "Corlears Hook"),
				new ScenarioLanding("120", "Brooklyn Navy Yard"),
			};
			var days = new List<ScenarioDay>();
			foreach (var week in ScenarioWeeks)
			{
				var monday = DateTime.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture);
				for (var i = 0; i < 7; i++)
				{
					var date = monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
					// New York's offset at local midnight, as the packs record it.
					NewYorkTime.ToLocal(new DateTimeOffset(monday.AddDays(i).AddHours(5), TimeSpan.Zero), out var daylight);
					days.Add(new ScenarioDay(date, "lower-east-river/" + date, TimeSpan.FromHours(daylight ? -4 : -5),
						Array.IndexOf(daysWithoutWind, date) >= 0));
				}
			}

			var vessels = new[]
			{
				new ScenarioVessel("t-class", "T-class", "vessels/t-class.json"),
				new ScenarioVessel("k-class", "K-class", "vessels/k-class.json"),
			};
			return new FakeScenarioCatalog(landings, days, vessels);
		}

		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
		{
			Calls.Add(string.Format(CultureInfo.InvariantCulture, "create {0} {1} {2} {3}", choice.StopId, choice.Date, choice.LocalMinutes, choice.VesselId));
			settings = null;
			var checkProblem = Check(choice, out var day, out var vessel);
			if (checkProblem == null)
			{
				var landing = Find(Landings, item => item.StopId == choice.StopId);
				if (RefusedLandings.TryGetValue(choice.StopId, out var reason))
				{
					checkProblem = landing.Name + ": " + reason + ".";
				}
				else if (!string.IsNullOrEmpty(choice.BerthId) && RefusedBerths.TryGetValue(choice.BerthId, out reason))
				{
					var berth = Find(landing.Berths, item => item.Id == choice.BerthId);
					checkProblem = landing.Name + ", " + (berth != null ? berth.Name : choice.BerthId) + ": " + reason + ".";
				}
			}

			problem = CreateProblem ?? checkProblem;
			if (problem != null)
			{
				return false;
			}

			var localMidnight = DateTime.ParseExact(choice.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			var startUtc = new DateTimeOffset(localMidnight.AddMinutes(choice.LocalMinutes), day.LocalUtcOffset).ToUniversalTime();
			settings = new ScenarioSettings
			{
				name = string.Format(CultureInfo.InvariantCulture, "fake-{0}-{1}-{2:0000}-{3}", choice.StopId, choice.Date,
					choice.LocalMinutes / 60 * 100 + choice.LocalMinutes % 60, choice.VesselId),
				vesselFile = vessel.VesselFile,
				startLatitude = WorldOrigin.LatitudeDegrees,
				startLongitude = WorldOrigin.LongitudeDegrees,
				scenarioStartUtc = startUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
			};
			_created.Add((settings, choice));
			LastCreatedSettings = settings;
			return true;
		}

		/// <summary>
		/// The harbor's catalog as the picker sees it: the 24 landings in stop id order with their notes (the three whose
		/// landing side is a crew question are listed, noted and refused), Pier 11's three berths, the 28 days with traffic
		/// and station-blend currents, and the T- and K-class. The notes are the shapes the real catalog produces, not its
		/// data; Harbor/Scenarios/README.md has the real rules.
		/// </summary>
		public static FakeScenarioCatalog Harbor(params string[] daysWithoutWind)
		{
			const string crew = "Waiting for a crew answer on the landing side";
			const string charted = "Charted face, landing side chart-checked";
			const string derived = "Barge derived from the stop position (low confidence), landing side chart-checked";
			var landings = new List<ScenarioLanding>
			{
				new ScenarioLanding("4", "Hunters Point South", null, derived),
				new ScenarioLanding("8", "South Williamsburg", null, "Charted face, landing side crew-reported"),
				new ScenarioLanding("11", "Atlantic Ave/BBP Pier 6", null, charted),
				new ScenarioLanding("17", "East 34th Street", null, charted),
				new ScenarioLanding("18", "Greenpoint", null, derived),
				new ScenarioLanding("19", "North Williamsburg", null, "Charted face, landing side chart-checked and crew-reported"),
				new ScenarioLanding("20", "Dumbo/Fulton Ferry", null, derived),
				new ScenarioLanding("23", "Bay Ridge", null, charted),
				new ScenarioLanding("24", "Red Hook/Atlantic Basin", null, charted),
				new ScenarioLanding("25", "Roosevelt Island", null, derived),
				new ScenarioLanding("87", "Wall St/Pier 11", new[]
				{
					new ScenarioBerth("enc:02261FA117EFB198", "Outer pontoon (east)", charted + ", crew note pier11-ramped-berths-2026-10-25"),
					new ScenarioBerth("enc:0226111D16AAB198", "North pontoon", "Charted face, landing side unsurveyed, crew berth use, crew note pier11-ramped-berths-2026-10-25"),
					new ScenarioBerth("enc:0226015B0512B198", "South pontoon", "Charted face, landing side unsurveyed, crew berth use, crew note pier11-ramped-berths-2026-10-25"),
				}, charted + ", crew note pier11-ramped-berths-2026-10-25"),
				new ScenarioLanding("89", "Astoria", null, derived),
				new ScenarioLanding("90", "Long Island City", null, derived),
				new ScenarioLanding("111", "Gov. Island/Yankee Pier", null, charted),
				new ScenarioLanding("112", "Soundview", null, derived),
				new ScenarioLanding("113", "East 90th St", null, crew),
				new ScenarioLanding("114", "Stuyvesant Cove", null, charted),
				new ScenarioLanding("115", "Corlears Hook", null, derived),
				new ScenarioLanding("118", "Sunset Park/BAT", null, crew),
				new ScenarioLanding("120", "Brooklyn Navy Yard", null, derived),
				new ScenarioLanding("136", "Battery Park City/Vesey St.", null, derived),
				new ScenarioLanding("137", "St. George", null, crew),
				new ScenarioLanding("138", "Midtown West 39th St-Pier 79", null, derived),
				new ScenarioLanding("141", "Ferry Point Park", null, derived),
			};
			var days = new List<ScenarioDay>();
			foreach (var week in ScenarioWeeks)
			{
				var monday = DateTime.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture);
				for (var i = 0; i < 7; i++)
				{
					var date = monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
					NewYorkTime.ToLocal(new DateTimeOffset(monday.AddDays(i).AddHours(5), TimeSpan.Zero), out var daylight);
					days.Add(new ScenarioDay(date, "harbor/" + date, TimeSpan.FromHours(daylight ? -4 : -5), Array.IndexOf(daysWithoutWind, date) >= 0, true, "station blend"));
				}
			}

			var catalog = new FakeScenarioCatalog(landings, days, Slice().Vessels);
			foreach (var stop in new[] { "113", "118", "137" })
			{
				catalog.RefusedLandings[stop] = "waiting for a crew answer on the landing side";
			}

			return catalog;
		}

		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			Calls.Add("start " + (settings != null ? settings.name : "(null)"));
			if (StartProblem != null)
			{
				problem = StartProblem;
				return false;
			}

			if (!TryFindChoice(settings, out var choice))
			{
				problem = "These settings did not come from this catalog.";
				return false;
			}

			problem = null;
			LastStartedSettings = settings;
			LastStartedChoice = choice;
			Check(choice, out var day, out _);
			RunStarted?.Invoke(choice, day, settings);
			return true;
		}

		/// <summary>The choice <see cref="TryCreate"/> made <paramref name="settings"/> from (by reference).</summary>
		public bool TryFindChoice(ScenarioSettings settings, out ScenarioChoice choice)
		{
			foreach (var (made, from) in _created)
			{
				if (ReferenceEquals(made, settings))
				{
					choice = from;
					return true;
				}
			}

			choice = default;
			return false;
		}

		private string Check(ScenarioChoice choice, out ScenarioDay day, out ScenarioVessel vessel)
		{
			day = null;
			vessel = null;
			var landing = Find(Landings, item => item.StopId == choice.StopId);
			if (landing == null)
			{
				return $"There is no landing with stop id '{choice.StopId}'.";
			}

			day = Find(Days, item => item.Date == choice.Date);
			if (day == null)
			{
				return $"There is no scenario day {choice.Date}.";
			}

			vessel = Find(Vessels, item => item.Id == choice.VesselId);
			if (vessel == null)
			{
				return $"There is no vessel '{choice.VesselId}'.";
			}

			return choice.LocalMinutes >= 0 && choice.LocalMinutes < 24 * 60 ? null : "The start time must be between 00:00 and 23:59.";
		}

		private static T Find<T>(IReadOnlyList<T> items, Func<T, bool> match) where T : class
		{
			foreach (var item in items)
			{
				if (match(item))
				{
					return item;
				}
			}

			return null;
		}
	}
}
