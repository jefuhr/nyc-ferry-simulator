using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The slice's scenario catalog on the real landings, the real environment packs and both vessel files. Failure modes
	/// guarded: landings, days or vessels in an order a picker cannot rely on (stop ids sorted as text put 114 before 4);
	/// a landing without a usable primary face offered anyway; a start that is not the trips' (bow-on, 4 m off the middle of
	/// the primary face, at rest), not the pack's local midnight plus the minutes, or not on the pack; a start for either
	/// vessel within a metre of any face, which the bootstrap would refuse; choices that cannot run accepted, or refused
	/// without a reason a picker can show; and days whose local offset or wind does not come from their own pack.
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

		[Test]
		public void EveryLandingAndVessel_StartsClearOfEveryFace()
		{
			RequirePacks();
			var catalog = Catalog();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var report = new List<string>();
			foreach (var landing in catalog.Landings)
			{
				foreach (var vessel in catalog.Vessels)
				{
					Assert.That(catalog.TryCreate(new ScenarioChoice(landing.StopId, "2025-07-14", 720, vessel.Id), out var settings, out var problem), Is.True, $"{landing.StopId} {vessel.Id}: {problem}");
					var start = settings.StartPosition;
					var clearance = FaceClearance.Check(catalog.Vessel(vessel.Id), start.North, start.East, settings.StartHeading, landings.Geometry.Segments);
					Assert.That(clearance.IsClear(), Is.True, $"{landing.StopId} {vessel.Id}");
					report.Add($"{landing.StopId} {landing.Name} {vessel.Id}: {clearance.Distance:F2} m from face {clearance.NearestSegmentId}");
				}
			}

			File.WriteAllText(Path.Combine(SliceTestData.ArtifactFolder("scenario-catalog"), "start-clearances.txt"), string.Join("\n", report) + "\n");
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
