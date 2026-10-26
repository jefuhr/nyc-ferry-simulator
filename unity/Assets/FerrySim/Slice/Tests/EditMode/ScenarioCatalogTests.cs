using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The slice's scenario catalog on the real landings, the real environment packs and both vessel files. Failure modes
	/// guarded: landings, days or vessels in an order a picker cannot rely on (stop ids sorted as text put 114 before 4);
	/// a landing without a usable primary face offered anyway; a start that is not the trips' (bow-on, 4 m off the middle of
	/// the primary face, at rest), not the pack's local midnight plus the minutes, or not on the pack; a start for either
	/// vessel within a metre of any kept contact segment (a face, barge, pier, bulkhead or shoreline), which the bootstrap
	/// would refuse; choices that cannot run accepted, or refused without a reason a picker can show; and days whose local
	/// offset or wind does not come from their own pack.
	/// </summary>
	public class ScenarioCatalogTests
	{
		private const string Region = "lower-east-river";

		private static EnvironmentPacks RepositoryPacks => new EnvironmentPacks(Path.Combine(SliceTestData.RepoRoot, EnvironmentPacks.ProcessedFolder));

		private static string DaysFolder => Path.Combine(SliceTestData.RepoRoot, EnvironmentPacks.RegionFolder(Region), "days");

		private static SliceScenarioCatalog Catalog(EnvironmentPacks packs = null)
		{
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var vessels = new List<KeyValuePair<string, VesselParameters>>();
			foreach (var file in new[] { "vessels/t-class.json", "vessels/k-class.json" })
			{
				vessels.Add(new KeyValuePair<string, VesselParameters>(file, VesselParametersJson.Load(Path.Combine(SliceTestData.RepoRoot, file))));
			}

			return new SliceScenarioCatalog(landings, vessels, packs ?? RepositoryPacks, Region);
		}

		private static void RequirePacks()
		{
			if (!Directory.Exists(DaysFolder))
			{
				Assert.Ignore($"The environment packs are not built ({DaysFolder}); run PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all.");
			}
		}

		// Failure modes: the fourth pontoon offered, an unknown berth silently substituted,
		// chosen face ignored, traffic unavailable but accepted, or visibility lost in settings.
		// A retained collision face is not necessarily an eligible start; check every subdivision.
		[TestCase("no-outward")]
		[TestCase("unlisted")]
		[TestCase("subdivision")]
		public void NondefaultBerthWithoutStartBasis_IsRetainedForContactButExcludedFromStarts(string change)
		{
			const string northId = "enc:0226111D16AAB198";
			var records = JArray.Parse(File.ReadAllText(SliceTestData.ContactsPath));
			var north = records.OfType<JObject>().Where(r => (bool?)r["berth_face"] == true && (string)r["berth_id"] == northId).ToArray();
			Assert.That(north.Length, Is.GreaterThan(1));
			foreach (var record in north)
			{
				if (change == "no-outward")
				{
					record["berth_side_evidence"]["verified"] = false;
					record["berth_side_evidence"]["outward_reports"] = 0;
				}
				if (change == "unlisted")
				{
					var note = record["berth_use_evidence"]["note"];
					note["berths"] = new JArray(note["berths"].Where(b => (string)b["id"] != northId).ToArray());
				}
			}
			if (change == "subdivision") north[1]["solid_side_evidence"]["probe_in_other_structure"] = true;
			var geometry = LandingGeometry.Parse(records.ToString(), "contacts.json", SliceTestData.ShippedSurfaces());
			Assert.That(north.All(r => geometry.Find((int)r["id"]) != null), Is.True, "solid-side collision fallback remains");
			var catalog = new SliceScenarioCatalog(geometry, Array.Empty<KeyValuePair<string, VesselParameters>>(), RepositoryPacks, Region);
			var offered = catalog.Landings.Single(l => l.StopId == "87").Berths.Select(b => b.Id).ToArray();
			Assert.That(offered, Is.EqualTo(new[] { "enc:02261FA117EFB198", "enc:0226015B0512B198" }));
			Assert.That(catalog.Excluded.Any(reason => reason.Contains("stop 87 berth " + northId) && reason.Contains("start-face basis")), Is.True);
			Assert.That(() => geometry.BerthFace("87", northId), Throws.TypeOf<LandingDataException>().With.Message.Contains("stop 87 berth " + northId).And.Message.Contains("start-face basis"));
		}

		[Test]
		public void IneligibleDefault_ExcludesTheLandingEvenWhenOtherBerthsAreEligible()
		{
			var records = JArray.Parse(File.ReadAllText(SliceTestData.ContactsPath));
			foreach (var record in records.Where(r => (bool?)r["primary_face"] == true && (string)r["stop_id"] == "87"))
				record["berth_side_evidence"]["outward_reports"] = 0;
			var geometry = LandingGeometry.Parse(records.ToString(), "contacts.json", SliceTestData.ShippedSurfaces());
			var catalog = new SliceScenarioCatalog(geometry, Array.Empty<KeyValuePair<string, VesselParameters>>(), RepositoryPacks, Region);
			Assert.That(catalog.Landings.Any(l => l.StopId == "87"), Is.False);
			Assert.That(catalog.Excluded.Any(reason => reason.Contains("landing 87")), Is.True);
		}

		[TestCase("enc:0226111D16AAB198")]
		[TestCase("enc:0226015B0512B198")]
		public void ShippedNondefaultBerth_HasAStartBasisOnEverySubdivision_AndIsOffered(string berthId)
		{
			var geometry = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			Assert.That(geometry.BerthFace("87", berthId).Segments.All(s => s.WaterSideVerifiedByPipeline || s.WaterSideCrewReported || s.CrewBerthSolidSide), Is.True);
			Assert.That(Catalog().Landings.Single(l => l.StopId == "87").Berths.Any(b => b.Id == berthId), Is.True);
		}

		[Test]
		public void Pier11_StartDepthMatrix_CoversEveryBerthDayHourAndVessel()
		{
			RequirePacks();
			var catalog = Catalog();
			var landing = catalog.Landings.Single(l => l.StopId == "87");
			var folder = Path.Combine(SliceTestData.RepoRoot, "artifacts/pier11-and-picker");
			Directory.CreateDirectory(folder);
			var times = new List<double>();
			var aground = 0;
			var rows = 0;
			using (var writer = new StreamWriter(Path.Combine(folder, "start-depth-matrix.csv")))
			{
				writer.WriteLine("berth_id,berth_name,day,local_hour,vessel,minimum_depth_m,maximum_draft_m,under_keel_m,aground");
				foreach (var day in catalog.Days)
				foreach (var berth in landing.Berths)
				for (var hour = 0; hour < 24; hour++)
				foreach (var vessel in catalog.Vessels)
				{
					var watch = System.Diagnostics.Stopwatch.StartNew();
					Assert.That(catalog.TryGetStartDepth(new ScenarioChoice("87", day.Date, hour * 60, vessel.Id, berth.Id), out var depth), Is.True);
					watch.Stop();
					if (hour > 0) times.Add(watch.Elapsed.TotalMilliseconds);
					Assert.That(double.IsNaN(depth.MinimumDepth) || double.IsInfinity(depth.MinimumDepth), Is.False);
					Assert.That(depth.MaximumDraft, Is.EqualTo(catalog.Vessel(vessel.Id).Particulars.MaxDraft.Value));
					writer.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3:00},{4},{5:F6},{6:F6},{7:F6},{8}",
						berth.Id, berth.Name, day.Date, hour, vessel.Id, depth.MinimumDepth, depth.MaximumDraft, depth.UnderKeel, depth.UnderKeel < 0));
					if (depth.UnderKeel < 0) aground++;
					rows++;
				}
			}
			times.Sort();
			File.WriteAllText(Path.Combine(folder, "start-depth-summary.txt"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"{0} starts, {1} aground. Warm depth query milliseconds: p50 {2:F3}, p95 {3:F3}, max {4:F3}.\n", rows, aground, times[times.Count / 2], times[(int)(times.Count * .95)], times.Last()));
			Assert.That(rows, Is.EqualTo(3 * 28 * 24 * 2));
		}

		[Test]
		public void Pier11_OffersThreeBerths_AndEveryChoiceStartsClearAtItsOwnFace()
		{
			RequirePacks();
			var catalog = Catalog();
			var landing = catalog.Landings.Single(l => l.StopId == "87");
			Assert.That(landing.Berths.Select(b => b.Id), Is.EqualTo(new[] { "enc:02261FA117EFB198", "enc:0226111D16AAB198", "enc:0226015B0512B198" }));
			var geometry = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			foreach (var berth in landing.Berths)
			foreach (var vessel in catalog.Vessels)
			{
				var choice = new ScenarioChoice("87", catalog.Days[0].Date, 540, vessel.Id, berth.Id, visibilityMetres: 463);
				Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
				var expected = LandingApproach.BowOn(geometry.BerthFace("87", berth.Id), catalog.Vessel(vessel.Id), SliceScenarioCatalog.StartBowClearance, "test", vessel.VesselFile, settings.scenarioStartUtc);
				Assert.That((settings.StartPosition - expected.StartPosition).Magnitude, Is.LessThan(1e-6));
				Assert.That(settings.StartHeading, Is.EqualTo(expected.StartHeading));
				Assert.That(settings.name, Does.Contain(berth.Name));
				Assert.That(settings.visibilityMetres, Is.EqualTo(463));
			}
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", catalog.Days[0].Date, 540, "t-class", "unknown"), out _, out var refusal), Is.False);
			Assert.That(refusal, Is.EqualTo("There is no berth 'unknown' at Wall St/Pier 11."));
		}

		[TestCase(-1.0)]
		[TestCase(9.9)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		public void InvalidVisibility_IsRefusedWithAReadableReason(double visibility)
		{
			RequirePacks();
			Assert.That(Catalog().TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", visibilityMetres: visibility), out _, out var reason), Is.False);
			Assert.That(reason, Does.StartWith("Visibility must be"));
		}

		[Test]
		public void AnUnpackagedTrafficDay_IsNotOfferedOrAccepted()
		{
			RequirePacks();
			var normal = Catalog();
			var geometry = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var catalog = new SliceScenarioCatalog(geometry, normal.Vessels.Select(v => new KeyValuePair<string, VesselParameters>(v.VesselFile, normal.Vessel(v.Id))).ToArray(),
				RepositoryPacks, Region, trafficPacks: new TrafficPacks(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
			Assert.That(catalog.Days.All(d => !d.HasTraffic), Is.True);
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", traffic: true), out _, out var reason), Is.False);
			Assert.That(reason, Is.EqualTo("Traffic isn't available for this day in this build."));
		}

		[Test]
		public void TheLandings_AreEveryStopWithAPrimaryFace_InGtfsStopIdOrder()
		{
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var catalog = Catalog();
			var expected = landings.Segments.Select(s => s.StopId).Distinct().Where(stop => landings.Segments.Any(s => s.StopId == stop && s.IsPrimary))
				.OrderBy(stop => long.Parse(stop)).ToList();

			Assert.That(catalog.Landings.Select(l => l.StopId), Is.EqualTo(expected));
			Assert.That(catalog.Landings.Select(l => l.StopId).First(), Is.EqualTo("4"), "numeric order, not text order");
			Assert.That(catalog.Landings.Single(l => l.StopId == "87").Name, Is.EqualTo("Wall St/Pier 11"));
			Assert.That(catalog.Excluded.Where(e => e.StartsWith("landing")), Is.Empty, string.Join("\n", catalog.Excluded));
		}

		[Test]
		public void StopIds_SortNumericallyFirst_ThenAsText()
		{
			var ids = new List<string> { "B2", "114", "8", "A", "20", "087", "4" };

			ids.Sort(SliceScenarioCatalog.CompareStopIds);

			Assert.That(ids, Is.EqualTo(new[] { "4", "8", "20", "087", "114", "A", "B2" }));
		}

		[Test]
		public void TheDays_AreTheBuiltPacks_OldestFirst_EachWithItsOwnOffsetAndWind()
		{
			RequirePacks();
			var catalog = Catalog();
			var folders = Directory.GetDirectories(DaysFolder).Select(Path.GetFileName).OrderBy(d => d, StringComparer.Ordinal).ToList();

			Assert.That(catalog.Days.Select(d => d.Date), Is.EqualTo(folders));
			Assert.That(catalog.Days, Has.Count.EqualTo(28), "the four scenario weeks");
			foreach (var day in catalog.Days)
			{
				var pack = RepositoryPacks.Load(day.PackName);
				Assert.That(day.PackName, Is.EqualTo(Region + "/" + day.Date));
				Assert.That(day.LocalUtcOffset, Is.EqualTo(new DateTimeOffset(DateTime.ParseExact(day.Date, "yyyy-MM-dd", null).Ticks, TimeSpan.Zero) - pack.EpochUtc), day.Date);
				Assert.That(day.WindMissing, Is.EqualTo(pack.WindStations.Count == 0), day.Date);
			}

			Assert.That(catalog.Days.First(d => d.Date == "2025-01-13").LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-5.0)));
			Assert.That(catalog.Days.First(d => d.Date == "2025-07-14").LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4.0)));
		}

		[Test]
		public void TheVessels_AreTheTAndKClasses_InThatOrder()
		{
			var catalog = Catalog();

			Assert.That(catalog.Vessels.Select(v => v.Id), Is.EqualTo(new[] { "t-class", "k-class" }));
			Assert.That(catalog.Vessels.Select(v => v.VesselFile), Is.EqualTo(new[] { "vessels/t-class.json", "vessels/k-class.json" }));
			Assert.That(catalog.Vessels[1].DisplayName, Does.StartWith("K-class"));
		}

		[Test]
		public void AChoice_StartsBowOnFourMetresOffThePrimaryFace_AtThePacksMidnightPlusTheMinutes()
		{
			RequirePacks();
			var catalog = Catalog();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);

			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 600, "t-class"), out var settings, out var problem), Is.True, problem);

			var vessel = catalog.Vessel("t-class");
			var expected = LandingApproach.BowOn(landings.PrimaryFace("87"), vessel, 4.0, settings.name, "vessels/t-class.json", "2025-07-14T14:00:00Z");
			Assert.That(settings.startLatitude, Is.EqualTo(expected.startLatitude));
			Assert.That(settings.startLongitude, Is.EqualTo(expected.startLongitude));
			Assert.That(settings.startHeadingDegrees, Is.EqualTo(expected.startHeadingDegrees));
			Assert.That(settings.startSpeedKnots, Is.Zero, "at rest");
			Assert.That(settings.vesselFile, Is.EqualTo("vessels/t-class.json"));
			Assert.That(settings.StartUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 14, 0, 0, TimeSpan.Zero)), "10:00 EDT is 14:00 UTC");
			Assert.That(settings.environment.pack, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(settings.name, Does.Contain("Wall St/Pier 11"));
			var start = settings.StartPosition;
			var toFace = FaceClearance.Check(vessel, start.North, start.East, settings.StartHeading, landings.PrimaryFace("87").Segments.Select(s => s.Segment).ToArray());
			Assert.That(toFace.Distance, Is.EqualTo(4.0).Within(1e-6), "the bow 4 m off the face");

			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-01-13", 600, "k-class"), out var winter, out problem), Is.True, problem);
			Assert.That(winter.StartUtc, Is.EqualTo(new DateTimeOffset(2025, 1, 13, 15, 0, 0, TimeSpan.Zero)), "10:00 EST is 15:00 UTC");
			Assert.That(winter.vesselFile, Is.EqualTo("vessels/k-class.json"));
		}

		/// <summary>
		/// Every landing with a primary face, for both vessel files: the catalog's start against every kept contact segment
		/// (contacts.json v2's berth faces, barge sides and ends, piers, bulkheads and shorelines), one by one. A start that
		/// overlaps any of them, or lies within <see cref="FaceClearance.MinimumMetres"/> of one, is listed and fails; the
		/// report gives the nearest segment of each kind for every start.
		/// </summary>
		[Test]
		public void EveryLandingAndVessel_StartsClearOfEveryKeptContactSegment()
		{
			RequirePacks();
			var catalog = Catalog();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var stops = landings.Segments.Where(s => s.IsPrimary).Select(s => s.StopId).Distinct().OrderBy(stop => long.Parse(stop)).ToList();
			Assert.That(catalog.Landings.Select(l => l.StopId), Is.EqualTo(stops), "every landing with a primary face is offered");
			Assert.That(catalog.Vessels.Select(v => v.Id), Is.EqualTo(new[] { "t-class", "k-class" }));
			Assert.That(landings.Segments.Select(s => s.Id), Is.EqualTo(landings.Geometry.Segments.Select(s => s.Id)), "every kept segment is checked: the runtime's geometry");
			var report = new List<string>
			{
				$"Catalog starts (bow {SliceScenarioCatalog.StartBowClearance:F1} m off the middle of the primary face, heading in) against each of the {landings.Segments.Count} kept contact segments; nearest of each kind, m (segment id).",
			};
			var refused = new List<string>();
			foreach (var stop in stops)
			{
				foreach (var vesselId in new[] { "t-class", "k-class" })
				{
					Assert.That(catalog.TryCreate(new ScenarioChoice(stop, "2025-07-14", 720, vesselId), out var settings, out var problem), Is.True, $"{stop} {vesselId}: {problem}");
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
							refused.Add($"{stop} {segment.StopName} {vesselId}: {(clearance.Overlaps ? "overlaps" : $"{clearance.Distance:F2} m from")} {segment.Kind} {segment.Id} (stop {segment.StopId})");
						}
					}

					var all = FaceClearance.Check(vessel, start.North, start.East, settings.StartHeading, landings.Geometry.Segments);
					Assert.That(all.IsClear(), Is.EqualTo(nearest.Values.All(v => v.Key >= FaceClearance.MinimumMetres)), $"{stop} {vesselId}: the start check over the whole geometry agrees");
					var name = catalog.Landings.Single(l => l.StopId == stop).Name;
					report.Add($"{stop} {name} {vesselId}: " + string.Join(", ", nearest.Select(n => $"{n.Key} {n.Value.Key:F2} ({n.Value.Value})")));
				}
			}

			report.Add(refused.Count == 0
				? $"No start overlaps a barge side or end, a pier, a bulkhead or a shoreline, or lies within {FaceClearance.MinimumMetres:F1} m of any kept segment."
				: "Starts inside the clearance:\n" + string.Join("\n", refused));
			File.WriteAllText(Path.Combine(SliceTestData.ArtifactFolder("scenario-catalog"), "start-clearances.txt"), string.Join("\n", report) + "\n");
			Assert.That(refused, Is.Empty, string.Join("\n", refused));
		}

		[TestCase("999", "2025-07-14", 600, "t-class", "no landing '999'")]
		[TestCase(null, "2025-07-14", 600, "t-class", "no landing")]
		[TestCase("87", "2025-07-21", 600, "t-class", "no environment pack for 2025-07-21")]
		[TestCase("87", null, 600, "t-class", "no environment pack")]
		[TestCase("87", "2025-07-14", 600, "q-class", "no vessel 'q-class'")]
		[TestCase("87", "2025-07-14", -1, "t-class", "0 to 1439 minutes")]
		[TestCase("87", "2025-07-14", 1440, "t-class", "0 to 1439 minutes")]
		public void AChoiceThatCannotRun_IsRefused_WithAReason(string stop, string date, int minutes, string vessel, string reason)
		{
			RequirePacks();
			var catalog = Catalog();

			Assert.That(catalog.TryCreate(new ScenarioChoice(stop, date, minutes, vessel), out var settings, out var problem), Is.False);
			Assert.That(settings, Is.Null);
			Assert.That(problem, Does.Contain(reason));
		}

		[Test]
		public void TheFirstAndLastMinutes_AreInsideEveryDay()
		{
			RequirePacks();
			var catalog = Catalog();

			foreach (var day in catalog.Days)
			{
				Assert.That(catalog.TryCreate(new ScenarioChoice("20", day.Date, 0, "t-class"), out _, out var problem), Is.True, problem);
				Assert.That(catalog.TryCreate(new ScenarioChoice("20", day.Date, SliceScenarioCatalog.LastMinute, "t-class"), out _, out problem), Is.True, problem);
			}
		}

		[Test]
		public void WithoutLandings_EveryChoiceIsRefused_SayingWhy()
		{
			var catalog = new SliceScenarioCatalog(null, new List<KeyValuePair<string, VesselParameters>>(), RepositoryPacks, Region, "contacts.json: missing");

			Assert.That(catalog.Landings, Is.Empty);
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 600, "t-class"), out _, out var problem), Is.False);
			Assert.That(problem, Does.Contain("contacts.json: missing"));
		}

		[Test]
		public void TheEnvironmentFolder_NamesTheRegion()
		{
			Assert.That(SliceScenarios.RegionOf("data/processed/lower-east-river/env", out var problem), Is.EqualTo("lower-east-river"), problem);
			Assert.That(SliceScenarios.RegionOf("data/processed/lower-east-river", out problem), Is.Null);
			Assert.That(problem, Does.Contain("data/processed/<region>/env"));
			Assert.That(SliceScenarios.RegionOf("data/processed/../env", out _), Is.Null);
			Assert.That(SliceScenarios.RegionOf(null, out _), Is.Null);
		}
	}
}
