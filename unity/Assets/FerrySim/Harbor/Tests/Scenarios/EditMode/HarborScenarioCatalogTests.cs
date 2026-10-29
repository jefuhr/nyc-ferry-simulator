using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.Slice;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests
{
	/// <summary>
	/// The harbor catalog on small fixtures (<see cref="HarborTestData"/>). Failure modes guarded: a landing that cannot
	/// start dropped from the list, or offered without a note; a refusal that does not name the landing or berth; a berth
	/// silently replaced by another face, or a day by another day; a start placed at the stop's position instead of bow-on
	/// off the face; berths re-sorted; a day without "station blend" or with traffic the build does not have; an aground
	/// start accepted, or a shallow one refused instead of warned; a harbor-landings mark ignored; the launcher started
	/// with settings the catalog did not make, or a start claimed with no launcher connected.
	/// </summary>
	public class HarborScenarioCatalogTests
	{
		private string _root;

		[SetUp]
		public void MakePacks() => _root = HarborTestData.PacksRoot();

		[TearDown]
		public void DeletePacks()
		{
			if (_root != null && Directory.Exists(_root))
			{
				Directory.Delete(_root, true);
			}
		}

		private HarborScenarioCatalog Catalog(IStartDepthEnvironments depths = null, IReadOnlyList<HarborBerthMark> marks = null, IScenarioLauncher launcher = null,
			bool withTraffic = true) =>
			new HarborScenarioCatalog(HarborTestData.Geometry(), HarborTestData.Vessels(), new EnvironmentPacks(_root), trafficPacks: new TrafficPacks(withTraffic ? _root : Path.Combine(_root, "none")),
				marks: marks, launcher: launcher, depths: depths ?? new HarborTestData.FlatDepths(-10f));

		[Test]
		public void EveryStop_IsListed_InStopIdOrder_EvenWhenItCannotStart()
		{
			var catalog = Catalog();
			Assert.That(catalog.Landings.Select(l => l.StopId), Is.EqualTo(new[] { "4", "19", "20", "87", "113", "118", "137" }), "numeric stop id order, including the crew faces and a stop whose every segment is left out");
			Assert.That(catalog.Landings.Select(l => l.Name), Does.Contain("Sunset Park/BAT").And.Contain("St. George"));
			Assert.That(catalog.Excluded.Where(e => e.StartsWith("landing", StringComparison.Ordinal)), Is.Empty, "nothing is dropped: " + string.Join("\n", catalog.Excluded));
		}

		[Test]
		public void ACrewFace_CarriesTheNote_AndIsRefusedByName_WithoutAnotherFaceSubstituted()
		{
			var catalog = Catalog();
			foreach (var (stop, name) in new[] { ("113", "East 90th St"), ("118", "Sunset Park/BAT"), ("137", "St. George") })
			{
				var landing = catalog.Landings.Single(l => l.StopId == stop);
				Assert.That(landing.Note, Is.EqualTo(HarborScenarioCatalog.CrewAnswerNote), stop);
				Assert.That(landing.Berths.Select(b => b.Note), Is.All.EqualTo(HarborScenarioCatalog.CrewAnswerNote), stop);
				Assert.That(catalog.CanStart(stop, null), Is.False);
				Assert.That(catalog.TryCreate(new ScenarioChoice(stop, "2025-07-14", 540, "t-class"), out var settings, out var problem), Is.False);
				Assert.That(settings, Is.Null, "no other face is used instead");
				Assert.That(problem, Is.EqualTo(name + ": waiting for a crew answer on the landing side."));
				Assert.That(catalog.TryGetStartDepth(new ScenarioChoice(stop, "2025-07-14", 540, "t-class"), out _), Is.False);
			}
		}

		[Test]
		public void AStartableLanding_HasItsEvidenceAsItsNote()
		{
			var catalog = Catalog();
			Assert.That(catalog.Landings.Single(l => l.StopId == "4").Note, Is.EqualTo("Charted face, landing side chart-checked"));
			Assert.That(catalog.Landings.Single(l => l.StopId == "19").Note, Is.EqualTo("Charted face, landing side crew-reported"));
			Assert.That(catalog.Landings.Single(l => l.StopId == "20").Note, Is.EqualTo("Barge derived from the stop position (low confidence), landing side chart-checked"));
			Assert.That(catalog.Landings.Single(l => l.StopId == "4").Berths.Single().Note, Is.EqualTo(catalog.Landings.Single(l => l.StopId == "4").Note), "one berth: the landing's note is the berth's");
		}

		[Test]
		public void Berths_StayInSourceOrder_AndOneWithoutAStartBasis_IsListedWithANote_AndRefusedByName()
		{
			var catalog = Catalog();
			var pier11 = catalog.Landings.Single(l => l.StopId == "87");
			Assert.That(pier11.Berths.Select(b => b.Id), Is.EqualTo(new[] { "enc:EAST", "enc:NORTH", "enc:SOUTH" }), "source order, the south pontoon still listed");
			Assert.That(pier11.Berths.Select(b => b.Name), Is.EqualTo(new[] { "Outer pontoon (east)", "North pontoon", "South pontoon" }));
			Assert.That(pier11.Berths[0].Note, Is.EqualTo("Charted face, landing side chart-checked"));
			Assert.That(pier11.Berths[2].Note, Is.EqualTo(HarborScenarioCatalog.CrewAnswerNote));
			Assert.That(pier11.Note, Is.EqualTo(pier11.Berths[0].Note), "the landing's note is its default berth's");
			Assert.That(catalog.CanStart("87", "enc:SOUTH"), Is.False);
			Assert.That(catalog.CanStart("87", "enc:NORTH"), Is.True);
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", "enc:SOUTH"), out var settings, out var problem), Is.False);
			Assert.That(settings, Is.Null);
			Assert.That(problem, Is.EqualTo("Wall St/Pier 11, South pontoon: waiting for a crew answer on the landing side."));
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", "enc:WEST"), out _, out problem), Is.False);
			Assert.That(problem, Is.EqualTo("There is no berth 'enc:WEST' at Wall St/Pier 11."));
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", "enc:NORTH"), out settings, out problem), Is.True, problem);
			Assert.That(settings.name, Does.Contain("North pontoon"));
		}

		[Test]
		public void AMarkFromTheLandingAudit_ShowsAsTheNote_AndRefusesWhenItSaysSo()
		{
			var marks = new[]
			{
				new HarborBerthMark("87", "enc:NORTH", "Ramp side unconfirmed by crew", "the ramp side is unconfirmed by crew"),
				new HarborBerthMark("4", null, "Shallow at low water: check the depth", null),
			};
			var catalog = Catalog(marks: marks);
			var pier11 = catalog.Landings.Single(l => l.StopId == "87");
			Assert.That(pier11.Berths[1].Note, Is.EqualTo("Ramp side unconfirmed by crew"));
			Assert.That(catalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 540, "t-class", "enc:NORTH"), out var settings, out var problem), Is.False);
			Assert.That(settings, Is.Null);
			Assert.That(problem, Is.EqualTo("Wall St/Pier 11, North pontoon: the ramp side is unconfirmed by crew."));
			Assert.That(pier11.Berths[0].Note, Is.EqualTo("Charted face, landing side chart-checked"), "other berths keep their evidence");
			var hunters = catalog.Landings.Single(l => l.StopId == "4");
			Assert.That(hunters.Note, Is.EqualTo("Shallow at low water: check the depth"));
			Assert.That(hunters.Berths.Single().Note, Is.EqualTo("Shallow at low water: check the depth"));
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 540, "t-class"), out _, out problem), Is.True, problem);
		}

		[Test]
		public void AStart_IsBowOnFourMetresOffTheFace_NeverTheStopPosition()
		{
			var catalog = Catalog();
			var geometry = HarborTestData.Geometry();
			foreach (var vesselId in new[] { "t-class", "k-class" })
			{
				Assert.That(catalog.TryCreate(new ScenarioChoice("20", "2025-07-14", 600, vesselId), out var settings, out var problem), Is.True, problem);
				var vessel = catalog.Vessel(vesselId);
				var expected = LandingApproach.BowOn(geometry.BerthFace("20"), vessel, SliceScenarioCatalog.StartBowClearance, "x", settings.vesselFile, settings.scenarioStartUtc);
				Assert.That((settings.StartPosition - expected.StartPosition).Magnitude, Is.LessThan(1e-6), vesselId);
				Assert.That(settings.StartHeading, Is.EqualTo(expected.StartHeading), vesselId);
				var toFace = FaceClearance.Check(vessel, settings.StartPosition.North, settings.StartPosition.East, settings.StartHeading, geometry.BerthFace("20").Segments.Select(s => s.Segment).ToArray());
				Assert.That(toFace.Distance, Is.EqualTo(4.0).Within(1e-6), "the bow 4 m off the face");
				Assert.That(settings.StartPosition.North, Is.GreaterThan(610.0), "on the water side, not at the barge");
				Assert.That(settings.startSpeedKnots, Is.Zero);
				Assert.That(settings.environment.pack, Is.EqualTo("harbor/2025-07-14"));
				Assert.That(settings.StartUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 14, 0, 0, TimeSpan.Zero)), "10:00 EDT");
				Assert.That(settings.name, Does.Contain("Dumbo/Fulton Ferry").And.Contain("(20)").And.Contain(vesselId));
			}
		}

		[Test]
		public void Days_AreTheHarborPacks_OldestFirst_WithStationBlend_AndTrafficOnlyWhenTheBuildHasIt()
		{
			var catalog = Catalog();
			Assert.That(catalog.Days.Select(d => d.Date), Is.EqualTo(new[] { "2025-01-13", "2025-07-14" }));
			Assert.That(catalog.Days.Select(d => d.CurrentMethod), Is.All.EqualTo("station blend"));
			Assert.That(catalog.Days.Select(d => d.PackName), Is.EqualTo(new[] { "harbor/2025-01-13", "harbor/2025-07-14" }));
			Assert.That(catalog.Days[0].LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-5)));
			Assert.That(catalog.Days[1].LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4)));
			Assert.That(catalog.Days[0].WindMissing, Is.True);
			Assert.That(catalog.Days[1].WindMissing, Is.False);
			Assert.That(catalog.Days.Select(d => d.HasTraffic), Is.EqualTo(new[] { false, true }));
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-01-13", 540, "t-class", traffic: true), out _, out var problem), Is.False);
			Assert.That(problem, Is.EqualTo("Traffic isn't available for this day in this build."));
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 540, "t-class", traffic: true), out var settings, out problem), Is.True, problem);
			Assert.That(settings.traffic.day, Is.EqualTo("2025-07-14"));
			Assert.That(settings.traffic.leaveOut, Is.EqualTo(ScenarioTraffic.Automatic));
			Assert.That(Catalog(withTraffic: false).Days.Select(d => d.HasTraffic), Is.All.False);
		}

		[TestCase("999", "2025-07-14", 600, "t-class", "There is no landing '999' in the harbor to start from.")]
		[TestCase(null, "2025-07-14", 600, "t-class", "There is no landing '' in the harbor to start from.")]
		[TestCase("4", "2025-07-21", 600, "t-class", "There is no environment pack for 2025-07-21 in harbor.")]
		[TestCase("4", "2025-07-14", 600, "q-class", "There is no vessel 'q-class' to start with.")]
		[TestCase("4", "2025-07-14", -1, "t-class", "The start time must be 0 to 1439 minutes after local midnight (00:00 to 23:59), not -1.")]
		[TestCase("4", "2025-07-14", 1440, "t-class", "The start time must be 0 to 1439 minutes after local midnight (00:00 to 23:59), not 1440.")]
		public void AChoiceThatCannotRun_IsRefused_WithTheSliceCatalogsWording(string stop, string date, int minutes, string vessel, string reason)
		{
			var catalog = Catalog();
			Assert.That(catalog.TryCreate(new ScenarioChoice(stop, date, minutes, vessel), out var settings, out var problem), Is.False);
			Assert.That(settings, Is.Null);
			Assert.That(problem, Is.EqualTo(reason));
		}

		[Test]
		public void Visibility_MustBeClearOrAtLeastTheMinimum()
		{
			var catalog = Catalog();
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 600, "t-class", visibilityMetres: 5), out _, out var problem), Is.False);
			Assert.That(problem, Does.StartWith("Visibility must be"));
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 600, "t-class", visibilityMetres: 463), out var settings, out problem), Is.True, problem);
			Assert.That(settings.visibilityMetres, Is.EqualTo(463));
		}

		// Depth through the day's pack, under the whole hull: aground refused, shallow warned, afloat silent, unknown bed no number.
		[TestCase(-1.5f, false, "This start would be aground: minimum depth is 1.50 m and vessel draft is 1.90 m.")]
		[TestCase(-2.0f, true, null)]
		[TestCase(-3.5f, true, null)]
		public void Depth_RefusesAground_WarnsShallow_AndStartsAfloat(float bed, bool starts, string refusal)
		{
			var depths = new HarborTestData.FlatDepths(bed);
			var catalog = Catalog(depths);
			var choice = new ScenarioChoice("4", "2025-07-14", 540, "t-class");
			Assert.That(catalog.TryGetStartDepth(choice, out var depth), Is.True);
			Assert.That(depth.MinimumDepth, Is.EqualTo(-bed).Within(1e-9));
			Assert.That(depth.MaximumDraft, Is.EqualTo(1.9).Within(1e-9), "the T-class's maxDraft");
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.EqualTo(starts), problem);
			if (refusal != null)
			{
				Assert.That(settings, Is.Null);
				Assert.That(problem, Is.EqualTo(refusal));
			}
			else
			{
				Assert.That(settings, Is.Not.Null);
			}
		}

		[Test]
		public void Depth_FollowsTheChosenDaysWater_AndKeepsOnlyOneDayResident()
		{
			var depths = new HarborTestData.FlatDepths(-2.0f, level: 0.5);
			var catalog = Catalog(depths);
			Assert.That(catalog.TryGetStartDepth(new ScenarioChoice("4", "2025-07-14", 540, "t-class"), out var depth), Is.True);
			Assert.That(depth.MinimumDepth, Is.EqualTo(2.5).Within(1e-9), "water level minus bed");
			Assert.That(depth.UnderKeel, Is.EqualTo(0.6).Within(1e-9));
			Assert.That(depths.Requests, Is.EqualTo(1));
			catalog.TryGetStartDepth(new ScenarioChoice("4", "2025-07-14", 600, "k-class"), out _);
			Assert.That(depths.Requests, Is.EqualTo(1), "the same day stays resident");
			catalog.TryGetStartDepth(new ScenarioChoice("4", "2025-01-13", 600, "k-class"), out _);
			Assert.That(depths.Requests, Is.EqualTo(2), "another day replaces it");
			catalog.TryGetStartDepth(new ScenarioChoice("4", "2025-07-14", 600, "k-class"), out _);
			Assert.That(depths.Requests, Is.EqualTo(3), "and is not kept beside it");
			Assert.That(catalog.ResidentDepthDay, Is.EqualTo("2025-07-14"));
		}

		[Test]
		public void AnUnknownBed_GivesNoDepth_AndDoesNotRefuse()
		{
			var catalog = Catalog(new HarborTestData.FlatDepths(float.NaN));
			var choice = new ScenarioChoice("4", "2025-07-14", 540, "t-class");
			Assert.That(catalog.TryGetStartDepth(choice, out _), Is.False);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(settings, Is.Not.Null);
		}

		[Test]
		public void TheLauncher_IsTheHarborRunnersAtIntegration_AndRefusesUntilItIsConnected()
		{
			var catalog = Catalog();
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 540, "t-class"), out var settings, out var problem), Is.True, problem);
			Assert.That(catalog.TryStart(settings, out problem), Is.False);
			Assert.That(problem, Is.EqualTo(HarborScenarioCatalog.NoLauncher));
			var launcher = new HarborTestData.RecordingLauncher();
			catalog.Launcher = launcher;
			Assert.That(catalog.TryStart(settings, out problem), Is.True, problem);
			Assert.That(launcher.Started.Single(), Is.SameAs(settings));
			launcher.Problem = "The harbor runner is busy.";
			Assert.That(catalog.TryStart(settings, out problem), Is.False);
			Assert.That(problem, Is.EqualTo("The harbor runner is busy."));
			Assert.That(catalog.TryStart(null, out problem), Is.False);
			Assert.That(problem, Is.EqualTo("No scenario was given to start."));
		}

		[Test]
		public void Vessels_AreTheTAndKClasses_InThatOrder()
		{
			var catalog = Catalog();
			Assert.That(catalog.Vessels.Select(v => v.Id), Is.EqualTo(new[] { "t-class", "k-class" }));
			Assert.That(catalog.Vessels.Select(v => v.VesselFile), Is.EqualTo(new[] { "vessels/t-class.json", "vessels/k-class.json" }));
			Assert.That(catalog.VesselFile("k-class"), Is.EqualTo("vessels/k-class.json"));
		}

		[Test]
		public void WithoutLandings_EveryChoiceIsRefused_SayingWhy()
		{
			var catalog = new HarborScenarioCatalog(null, HarborTestData.Vessels(), new EnvironmentPacks(_root), landingError: "contacts.json: missing");
			Assert.That(catalog.Landings, Is.Empty);
			Assert.That(catalog.TryCreate(new ScenarioChoice("4", "2025-07-14", 600, "t-class"), out _, out var problem), Is.False);
			Assert.That(problem, Is.EqualTo("No landing can be used, because contacts.json: missing."));
		}
	}
}
