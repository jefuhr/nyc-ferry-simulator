using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests
{
	/// <summary>
	/// The harbor catalog on the real harbor contacts, the 28 harbor environment packs and both vessel files (the gitignored
	/// pipeline outputs under data/processed/harbor; the tests are ignored, naming the folder, when they are not built).
	/// Failure modes guarded: not all 24 landings listed, or not in stop id order; the three crew faces dropped, offered as
	/// startable or refused without naming them; a berth count or order other than the exporter's; a start for either hull
	/// within a metre of any kept contact segment; a day without "station blend"; the depth preview refusing a start that
	/// is afloat or accepting one aground on a real day; and a preview too slow for the picker to call as a choice changes.
	/// </summary>
	public class HarborCatalogRealDataTests
	{
		private static readonly string[] CrewFaces = { "113", "118", "137" };

		private static EnvironmentPacks RepositoryPacks => new EnvironmentPacks(Path.Combine(HarborTestData.RepoRoot, EnvironmentPacks.ProcessedFolder));

		private static TrafficPacks RepositoryTraffic => new TrafficPacks(Path.Combine(HarborTestData.RepoRoot, EnvironmentPacks.ProcessedFolder));

		private static void RequireData()
		{
			if (!File.Exists(HarborTestData.RealContactsPath))
			{
				Assert.Ignore($"The harbor contacts are not built ({HarborTestData.RealContactsPath}); copy data/processed/harbor/unity from the harbor build.");
			}

			if (!Directory.Exists(HarborTestData.HarborDaysFolder))
			{
				Assert.Ignore($"The harbor environment packs are not built ({HarborTestData.HarborDaysFolder}); run PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region harbor --all.");
			}
		}

		private static HarborScenarioCatalog Catalog() =>
			new HarborScenarioCatalog(LandingGeometry.Load(HarborTestData.RealContactsPath, HarborTestData.SurfacesPath), HarborTestData.Vessels(), RepositoryPacks, trafficPacks: RepositoryTraffic);

		[Test]
		public void All24Landings_InStopIdOrder_WithTheThreeCrewFacesListedButRefused()
		{
			RequireData();
			var catalog = Catalog();
			Assert.That(catalog.Landings, Has.Count.EqualTo(24), string.Join("\n", catalog.Excluded));
			var ids = catalog.Landings.Select(l => long.Parse(l.StopId, CultureInfo.InvariantCulture)).ToList();
			Assert.That(ids, Is.Ordered, "numeric stop id order");
			Assert.That(catalog.Landings[0].Name, Is.EqualTo("Hunters Point South"));
			Assert.That(catalog.Landings.Single(l => l.StopId == "87").Name, Is.EqualTo("Wall St/Pier 11"));
			foreach (var stop in CrewFaces)
			{
				var landing = catalog.Landings.Single(l => l.StopId == stop);
				Assert.That(landing.Note, Is.EqualTo(HarborScenarioCatalog.CrewAnswerNote), landing.Name);
				Assert.That(catalog.TryCreate(new ScenarioChoice(stop, "2025-07-14", 720, "t-class"), out var settings, out var problem), Is.False, landing.Name);
				Assert.That(settings, Is.Null);
				Assert.That(problem, Is.EqualTo(landing.Name + ": waiting for a crew answer on the landing side."));
			}

			foreach (var landing in catalog.Landings.Where(l => !CrewFaces.Contains(l.StopId)))
			{
				Assert.That(catalog.CanStart(landing.StopId, null), Is.True, landing.Name + ": " + landing.Note);
				Assert.That(landing.Note, Is.Not.Empty.And.Not.EqualTo(HarborScenarioCatalog.CrewAnswerNote), landing.Name);
			}
		}

		[Test]
		public void TheBerths_AreTheExportersTwentySix_InSourceOrder()
		{
			RequireData();
			var catalog = Catalog();
			Assert.That(catalog.Landings.Sum(l => l.Berths.Count), Is.EqualTo(26));
			var pier11 = catalog.Landings.Single(l => l.StopId == "87");
			Assert.That(pier11.Berths.Select(b => b.Id), Is.EqualTo(new[] { "enc:02261FA117EFB198", "enc:0226111D16AAB198", "enc:0226015B0512B198" }));
			Assert.That(pier11.Berths.Select(b => b.Name), Is.EqualTo(new[] { "Outer pontoon (east)", "North pontoon", "South pontoon" }));
			Assert.That(pier11.Berths.Select(b => b.Note), Is.All.Contains("crew note pier11-ramped-berths-2026-10-25"));
			foreach (var landing in catalog.Landings.Where(l => l.StopId != "87"))
			{
				Assert.That(landing.Berths.Select(b => b.Name), Is.EqualTo(new[] { "Main berth" }), landing.Name);
			}
		}

		[Test]
		public void TheDays_AreTheTwentyEightHarborPacks_OldestFirst_StationBlend_WithTheTrafficTheBuildHas()
		{
			RequireData();
			var catalog = Catalog();
			var folders = Directory.GetDirectories(HarborTestData.HarborDaysFolder).Select(Path.GetFileName).OrderBy(d => d, StringComparer.Ordinal).ToList();
			Assert.That(catalog.Days.Select(d => d.Date), Is.EqualTo(folders));
			Assert.That(catalog.Days, Has.Count.EqualTo(28));
			Assert.That(catalog.Days.Select(d => d.CurrentMethod), Is.All.EqualTo("station blend"));
			Assert.That(catalog.Days.Select(d => d.PackName), Is.All.StartsWith("harbor/"));
			foreach (var day in catalog.Days)
			{
				Assert.That(day.HasTraffic, Is.EqualTo(RepositoryTraffic.DayFolder(day.Date) != null), day.Date);
			}
		}

		/// <summary>
		/// Every startable berth, for both hulls: the catalog's start against every kept contact segment of the harbor, one by
		/// one, as the slice's catalog test does. The report lists the nearest segment of each kind for every start.
		/// </summary>
		[Test]
		public void EveryStartableBerthAndVessel_StartsClearOfEveryKeptContactSegment()
		{
			RequireData();
			var catalog = Catalog();
			var landings = LandingGeometry.Load(HarborTestData.RealContactsPath, HarborTestData.SurfacesPath);
			var report = new List<string>
			{
				$"Harbor catalog starts (bow {SliceScenarioCatalog.StartBowClearance:F1} m off the middle of the berth face, heading in) against each of the {landings.Segments.Count} kept contact segments; nearest of each kind, m (segment id).",
			};
			var refused = new List<string>();
			var starts = 0;
			foreach (var landing in catalog.Landings)
			foreach (var berth in landing.Berths)
			{
				if (!catalog.CanStart(landing.StopId, berth.Id))
				{
					report.Add($"{landing.StopId} {landing.Name} {berth.Name}: not startable ({berth.Note})");
					continue;
				}

				foreach (var vesselId in new[] { "t-class", "k-class" })
				{
					// Noon on a summer day: the depth check is separate; the clearance is about geometry.
					Assert.That(catalog.TryCreate(new ScenarioChoice(landing.StopId, "2025-07-14", 720, vesselId, berth.Id), out var settings, out var problem)
						|| (problem != null && problem.StartsWith("This start would be aground", StringComparison.Ordinal)), Is.True, $"{landing.Name} {berth.Name} {vesselId}: {problem}");
					if (settings == null)
					{
						settings = LandingApproach.BowOn(landings.BerthFace(landing.StopId, berth.Id), catalog.Vessel(vesselId), SliceScenarioCatalog.StartBowClearance, "refused pose",
							catalog.VesselFile(vesselId), SliceScenarioCatalog.StartUtc(catalog.Day("2025-07-14"), 720).ToString("O"));
					}

					starts++;
					var vessel = catalog.Vessel(vesselId);
					var start = settings.StartPosition;
					var nearest = new SortedDictionary<string, KeyValuePair<double, int>>(StringComparer.Ordinal);
					foreach (var segment in landings.Segments)
					{
						var clearance = FaceClearance.Check(vessel, start.North, start.East, settings.StartHeading, new[] { segment.Segment });
						var distance = clearance.Overlaps ? 0.0 : clearance.Distance;
						if (!nearest.TryGetValue(segment.Kind, out var best) || distance < best.Key)
						{
							nearest[segment.Kind] = new KeyValuePair<double, int>(distance, segment.Id);
						}

						if (!clearance.IsClear())
						{
							refused.Add($"{landing.StopId} {landing.Name} {berth.Name} {vesselId}: {(clearance.Overlaps ? "overlaps" : $"{clearance.Distance:F2} m from")} {segment.Kind} {segment.Id} (stop {segment.StopId})");
						}
					}

					report.Add($"{landing.StopId} {landing.Name} {berth.Name} {vesselId}: " + string.Join(", ", nearest.Select(n => $"{n.Key} {n.Value.Key:F2} ({n.Value.Value})")));
				}
			}

			report.Add(refused.Count == 0
				? $"No start overlaps a barge side or end, a pier, a bulkhead or a shoreline, or lies within {FaceClearance.MinimumMetres:F1} m of any kept segment."
				: "Starts inside the clearance:\n" + string.Join("\n", refused));
			File.WriteAllText(Path.Combine(HarborTestData.ArtifactFolder("scenario-catalog"), "start-clearances.txt"), string.Join("\n", report) + "\n");
			Assert.That(starts, Is.EqualTo(2 * (26 - CrewFaces.Length)), "both hulls at each of the 23 startable berths");
			Assert.That(refused, Is.Empty, string.Join("\n", refused));
		}

		// Pier 11's south pontoon on a summer day: afloat at some hours and aground at others on the harbor's own depth.
		[Test]
		public void Pier11SouthPontoon_RefusesAground_AndKeepsTheWarningBand_OnTheHarborDepth()
		{
			RequireData();
			var catalog = Catalog();
			var aground = false;
			var warning = false;
			var afloat = false;
			for (var minute = 0; minute < 1440; minute += 30)
			{
				var choice = new ScenarioChoice("87", "2025-07-14", minute, "t-class", "enc:0226015B0512B198");
				Assert.That(catalog.TryGetStartDepth(choice, out var depth), Is.True, minute.ToString(CultureInfo.InvariantCulture));
				var accepted = catalog.TryCreate(choice, out var settings, out var problem);
				if (depth.UnderKeel < 0)
				{
					aground = true;
					Assert.That(accepted, Is.False);
					Assert.That(settings, Is.Null);
					Assert.That(problem, Does.Contain("aground").And.Contain("draft"));
				}
				else
				{
					Assert.That(accepted, Is.True, problem);
					if (depth.UnderKeel < 0.3) warning = true; else afloat = true;
				}
			}

			Assert.That(aground, Is.True, "the readiness matrix saw this pontoon aground at low water");
			Assert.That(warning || afloat, Is.True, "and afloat at other hours");
		}

		/// <summary>
		/// A sample of the readiness spike's S1 matrix through the shipped catalog: every berth, both hulls, one day a week at
		/// four-hourly times, written for comparison with results/s1/s1-choices.csv; with the preview's cold and warm times.
		/// </summary>
		[Test]
		public void StartMatrixSample_AndPreviewTiming_AreWrittenForTheReport()
		{
			RequireData();
			var catalog = Catalog();
			var folder = HarborTestData.ArtifactFolder("scenario-catalog-matrix");
			var days = new[] { "2024-10-07", "2025-01-13", "2025-04-14", "2025-07-14" };
			var cold = new List<double>();
			var warm = new List<double>();
			var rows = 0;
			using (var writer = new StreamWriter(Path.Combine(folder, "start-matrix-sample.csv")))
			{
				writer.WriteLine("stop_id,stop_name,berth_id,hull,date,time_s,north_m,east_m,heading_deg,min_depth_m,draft_m,under_keel_m,status,reason");
				foreach (var date in days)
				{
					var first = true;
					foreach (var landing in catalog.Landings)
					foreach (var berth in landing.Berths)
					foreach (var vesselId in new[] { "t-class", "k-class" })
					for (var hour = 0; hour < 24; hour += 4)
					{
						var choice = new ScenarioChoice(landing.StopId, date, hour * 60, vesselId, berth.Id);
						var watch = Stopwatch.StartNew();
						var known = catalog.TryGetStartDepth(choice, out var depth);
						watch.Stop();
						if (catalog.CanStart(landing.StopId, berth.Id))
						{
							(first ? cold : warm).Add(watch.Elapsed.TotalMilliseconds);
							first = false;
						}

						var accepted = catalog.TryCreate(choice, out var settings, out var problem);
						var status = accepted ? (known && depth.UnderKeel < 0.3 ? "warning" : "startable") : known && depth.UnderKeel < 0 ? "aground_refusal" : "excluded_face";
						var pose = settings;
						if (pose == null && catalog.CanStart(landing.StopId, berth.Id))
						{
							var geometry = catalog.Landings;
							pose = LandingApproach.BowOn(LandingGeometry.Load(HarborTestData.RealContactsPath, HarborTestData.SurfacesPath).BerthFace(landing.StopId, berth.Id),
								catalog.Vessel(vesselId), SliceScenarioCatalog.StartBowClearance, "refused pose", catalog.VesselFile(vesselId), SliceScenarioCatalog.StartUtc(catalog.Day(date), hour * 60).ToString("O"));
						}

						writer.WriteLine(string.Join(",", landing.StopId, Quote(landing.Name), berth.Id, vesselId, date, (hour * 3600).ToString(CultureInfo.InvariantCulture),
							pose == null ? "" : pose.StartPosition.North.ToString("R", CultureInfo.InvariantCulture), pose == null ? "" : pose.StartPosition.East.ToString("R", CultureInfo.InvariantCulture),
							pose == null ? "" : pose.startHeadingDegrees.ToString("R", CultureInfo.InvariantCulture),
							known ? depth.MinimumDepth.ToString("R", CultureInfo.InvariantCulture) : "", known ? depth.MaximumDraft.ToString("R", CultureInfo.InvariantCulture) : "",
							known ? depth.UnderKeel.ToString("R", CultureInfo.InvariantCulture) : "", status, Quote(problem ?? "")));
						rows++;
					}
				}
			}

			warm.Sort();
			File.WriteAllText(Path.Combine(folder, "preview-timing.txt"), string.Format(CultureInfo.InvariantCulture,
				"{0} rows. First preview of a day (loads and verifies the day's pack and the harbor depth set), ms: {1}. Warm preview ms: p50 {2:F3}, p95 {3:F3}, max {4:F3}.\n",
				rows, string.Join(", ", cold.Select(t => t.ToString("F0", CultureInfo.InvariantCulture))), warm[warm.Count / 2], warm[(int)(warm.Count * 0.95)], warm.Last()));
			Assert.That(rows, Is.EqualTo(26 * 2 * 4 * 6));
			Assert.That(warm[(int)(warm.Count * 0.95)], Is.LessThan(250.0), "a warm preview must not stall the picker");
		}

		private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
	}
}
