using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Scenarios on an environment pack's day. Failure modes guarded: a pack name that could reach outside the pack folders
	/// or is not region/date; an unknown pack or a start outside the day's coverage accepted, or refused without saying
	/// which pack, where it was looked for, or the window; calm and uniform fields that still apply when a pack is set; the
	/// pack's day read at the wrong time (the epoch offset lost or doubled); a player's StreamingAssets copy not taking
	/// precedence; a day listing out of date order, with the wrong local offset, or broken by one bad folder; a pack whose
	/// bytes changed loading anyway; and calm and uniform scenarios changing at all.
	/// </summary>
	public class PackScenarioTests
	{
		private SyntheticPacks _packs;

		[SetUp]
		public void SetUp() => _packs = new SyntheticPacks();

		[TearDown]
		public void TearDown() => _packs.Dispose();

		private static ScenarioSettings OnPack(string pack, string startUtc) => new ScenarioSettings
		{
			name = "pack scenario",
			startLatitude = 40.7003,
			startLongitude = -74.0144,
			startHeadingDegrees = 45.0,
			scenarioStartUtc = startUtc,
			environment = new ScenarioEnvironment { pack = pack },
		};

		[TestCase("lower-east-river")]
		[TestCase("lower-east-river/")]
		[TestCase("/2025-07-14")]
		[TestCase("lower-east-river/2025-7-14")]
		[TestCase("lower-east-river/2025-07-14/extra")]
		[TestCase("../outside/2025-07-14")]
		[TestCase("Lower East River/2025-07-14")]
		[TestCase("lower-east-river/2025-02-30")]
		public void APackNameThatIsNotRegionSlashDate_IsRefused(string name)
		{
			var error = Assert.Throws<ArgumentException>(() => OnPack(name, "2025-07-14T16:00:00Z").Validate(_packs.Packs));

			Assert.That(error.Message, Does.Contain("is not region/date"));
			Assert.That(error.Message, Does.Contain(name));
			Assert.That(EnvironmentPacks.TryParseName(name, out _, out _, out _), Is.False);
		}

		[Test]
		public void AnUnknownPack_IsRefused_NamingItAndWhereItWasLookedFor()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var name = SyntheticPacks.Name("2025-07-15");

			var error = Assert.Throws<ArgumentException>(() => OnPack(name, "2025-07-15T16:00:00Z").Validate(_packs.Packs));

			Assert.That(error.Message, Does.Contain("Scenario 'pack scenario'"));
			Assert.That(error.Message, Does.Contain($"environment pack '{name}' was not found"));
			Assert.That(error.Message, Does.Contain(Path.Combine(_packs.Root, SyntheticPacks.Region, "env", "days", "2025-07-15", "manifest.json")));
		}

		[TestCase("2025-07-14T02:59:59Z", false)]
		[TestCase("2025-07-14T03:00:00Z", true)]
		[TestCase("2025-07-14T16:00:00Z", true)]
		[TestCase("2025-07-15T05:00:00Z", true)]
		[TestCase("2025-07-15T05:00:01Z", false)]
		public void AStartOutsideTheDaysCoverage_IsRefused_NamingTheWindow(string start, bool inside)
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var settings = OnPack(SyntheticPacks.Name(SyntheticPacks.SummerDate), start);

			if (inside)
			{
				Assert.DoesNotThrow(() => settings.Validate(_packs.Packs));
				return;
			}

			var error = Assert.Throws<ArgumentException>(() => settings.Validate(_packs.Packs));
			Assert.That(error.Message, Does.Contain("outside environment pack 'test-harbor/2025-07-14'"));
			Assert.That(error.Message, Does.Contain("which covers 2025-07-14T03:00:00Z to 2025-07-15T05:00:00Z"));
		}

		[Test]
		public void APack_IgnoresCalmAndTheUniformFields()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var settings = OnPack(SyntheticPacks.Name(SyntheticPacks.SummerDate), "2025-07-14T16:00:00Z");
			settings.environment.calm = false;
			settings.environment.currentSpeedKnots = -3.0;
			settings.environment.windFromDegrees = 999.0;
			settings.environment.depthMetres = -1.0;

			Assert.DoesNotThrow(() => settings.Validate(_packs.Packs), "invalid uniform fields are not looked at");
			Assert.That(settings.environment.Kind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(settings.environment.SpecText(), Is.EqualTo("pack"));
			Assert.Throws<InvalidOperationException>(() => settings.environment.Create(), "a pack needs the scenario start");

			var environment = settings.CreateEnvironment(_packs.Packs);
			Assert.That(environment.Kind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(environment.StartRun(), Is.InstanceOf<DataEnvironment>());
			Assert.That(environment.Observer.SampleDepth(0.0, 0.0, 0.0), Is.EqualTo(SyntheticPacks.Level(12.0 * 3600.0) - SyntheticPacks.Bed).Within(1e-9),
				"the pack's depth, not the uniform field's");
		}

		[Test]
		public void APackRun_ReadsTheDayAtSimulationTimePlusTheStartsOffsetFromTheEpoch()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			// 16:00Z is 12:00 EDT: 43 200 s after local midnight.
			var environment = OnPack(SyntheticPacks.Name(SyntheticPacks.SummerDate), "2025-07-14T16:00:00Z").CreateEnvironment(_packs.Packs);
			var run = (DataEnvironment)environment.StartRun();

			Assert.That(run.EpochOffset, Is.EqualTo(43200.0));
			Assert.That(environment.WaterLevel(0.0, 0.0, 600.0), Is.EqualTo(SyntheticPacks.Level(43800.0)).Within(1e-12));
			Assert.That(environment.LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4.0)));
			Assert.That(environment.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 16, 0, 0, TimeSpan.Zero)));
			Assert.That(environment.PackFolder, Is.EqualTo(Path.Combine(_packs.Root, SyntheticPacks.Region, "env", "days", SyntheticPacks.SummerDate)));
		}

		[Test]
		public void TheFirstRootWithThePack_IsTheOneRead_AsStreamingAssetsBeforeTheRepository()
		{
			var streaming = Path.Combine(_packs.Root, "streaming");
			var repository = Path.Combine(_packs.Root, "repository");
			_packs.Write(SyntheticPacks.SummerDate, levelOffset: 1.0, root: streaming);
			_packs.Write(SyntheticPacks.SummerDate, root: repository);
			_packs.Write(SyntheticPacks.WinterDate, root: repository);
			var packs = new EnvironmentPacks(streaming, repository);

			Assert.That(packs.DayFolder(SyntheticPacks.Name(SyntheticPacks.SummerDate)), Does.StartWith(streaming));
			Assert.That(packs.DayFolder(SyntheticPacks.Name(SyntheticPacks.WinterDate)), Does.StartWith(repository), "a day only the repository has");
			var summer = packs.Load(SyntheticPacks.Name(SyntheticPacks.SummerDate));
			Assert.That(summer.WaterLevelStations[0].Level.Value(0), Is.EqualTo(SyntheticPacks.Level(SyntheticPacks.CoverageStart) + 1.0).Within(1e-12));
		}

		[Test]
		public void TheDays_AreListedOldestFirst_WithTheirLocalOffsetAndWind_AndABadFolderIsLeftOutWithItsReason()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			_packs.Write(SyntheticPacks.WinterDate, withWind: false);
			var days = Path.Combine(_packs.Root, SyntheticPacks.Region, "env", "days");
			Directory.CreateDirectory(Path.Combine(days, "not-a-date"));
			Directory.CreateDirectory(Path.Combine(days, "2025-03-01"));
			File.WriteAllText(Path.Combine(days, "2025-03-01", "manifest.json"), "{\"format\": \"ferrysim-env/1\", \"region\": \"elsewhere\"}");
			var problems = new List<string>();

			var listed = _packs.Packs.Days(SyntheticPacks.Region, problems);

			Assert.That(listed.Select(d => d.Date), Is.EqualTo(new[] { SyntheticPacks.WinterDate, SyntheticPacks.SummerDate }));
			Assert.That(listed[0].LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-5.0)), "January is standard time");
			Assert.That(listed[1].LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4.0)), "July is daylight time");
			Assert.That(listed[0].WindMissing, Is.True);
			Assert.That(listed[1].WindMissing, Is.False);
			Assert.That(listed[1].Name, Is.EqualTo("test-harbor/2025-07-14"));
			Assert.That(problems, Has.Count.EqualTo(2), string.Join("\n", problems));
			Assert.That(problems.Any(p => p.Contains("not-a-date") && p.Contains("is not region/date")), Is.True, string.Join("\n", problems));
			Assert.That(problems.Any(p => p.Contains("2025-03-01") && p.Contains("region is 'elsewhere'")), Is.True, string.Join("\n", problems));
		}

		[Test]
		public void APackWhoseBytesChanged_IsRefused_NamingTheFile()
		{
			var folder = _packs.Write(SyntheticPacks.SummerDate);
			var currents = Path.Combine(folder, "currents.csv");
			File.WriteAllText(currents, File.ReadAllText(currents).Replace("0.3,", "0.31,"));
			var settings = OnPack(SyntheticPacks.Name(SyntheticPacks.SummerDate), "2025-07-14T16:00:00Z");

			Assert.DoesNotThrow(() => settings.Validate(_packs.Packs), "validation reads only the manifest");
			var error = Assert.Throws<EnvironmentPackException>(() => settings.CreateEnvironment(_packs.Packs));
			Assert.That(error.File, Is.EqualTo(currents));
		}

		[Test]
		public void APackScenario_RoundTripsThroughJson()
		{
			var settings = ScenarioAsset.FromJson("{\"name\": \"json pack\", \"scenarioStartUtc\": \"2025-07-14T16:00:00Z\", \"environment\": {\"pack\": \"lower-east-river/2025-07-14\"}}");

			Assert.That(settings.environment.pack, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(ScenarioAsset.FromJson(ScenarioAsset.ToJson(settings)).environment.pack, Is.EqualTo("lower-east-river/2025-07-14"));
		}

		[Test]
		public void CalmAndUniformScenarios_AreUnchanged()
		{
			var calm = new ScenarioSettings();
			var uniform = new ScenarioSettings
			{
				environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 2.0, currentTowardDegrees = 200.0, windSpeedKnots = 15.0, windFromDegrees = 300.0, depthMetres = 12.0 },
			};

			Assert.That(calm.environment.Kind, Is.EqualTo(EnvironmentKind.Calm));
			Assert.That(uniform.environment.Kind, Is.EqualTo(EnvironmentKind.Uniform));
			Assert.That(calm.environment.SpecText(), Is.EqualTo("calm"));
			Assert.That(uniform.environment.SpecText(), Is.EqualTo("current=2kn@200,wind=15kn@from300,depth=12m"));
			var environment = uniform.CreateEnvironment(_packs.Packs);
			Assert.That(environment.Kind, Is.EqualTo(EnvironmentKind.Uniform));
			Assert.That(environment.StartRun(), Is.SameAs(environment.UniformEnvironment));
			Assert.That(environment.UniformEnvironment.Current, Is.EqualTo(uniform.environment.Current));
			Assert.That(environment.HasTide, Is.False);
			Assert.That(double.IsNaN(environment.WaterLevel(0.0, 0.0, 0.0)), Is.True);
			Assert.That(calm.CreateEnvironment(_packs.Packs).Kind, Is.EqualTo(EnvironmentKind.Calm));
		}
	}
}
