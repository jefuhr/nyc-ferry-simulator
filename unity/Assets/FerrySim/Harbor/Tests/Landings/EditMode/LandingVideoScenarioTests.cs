using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.Harbor.Tests.Landings
{
	/// <summary>Failure modes: invented berth aliases, missing captions, unsafe blocked runs and a fabricated passing baseline.</summary>
	public class LandingVideoScenarioTests
	{
		internal static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
		private static string Folder => Path.Combine(Root, "unity/Assets/FerrySim/Harbor/Scenarios/landings");

		[Test]
		public void NineCaptionsHaveExplicitMappingAndDisabledExperiments()
		{
			var scenarios = LandingVideoScenario.LoadDirectory(Folder);
			Assert.That(scenarios.Length, Is.EqualTo(9));
			Assert.That(scenarios.Select(s => s.Id).Distinct().Count(), Is.EqualTo(9));
			foreach (var scenario in scenarios)
			{
				Assert.That((bool)scenario.Data["gusts_enabled"], Is.False);
				Assert.That((bool)scenario.Data["fender_overrides_enabled"], Is.False);
				Assert.That((string)scenario.Data["mapping"]["confidence"], Is.Not.Empty);
				Assert.That((double)scenario.Data["start_bow_clearance_m"], Is.EqualTo(400));
			}
		}

		[Test]
		public void MissingSlipAndMarinaAreBlockedRatherThanNearestMatched()
		{
			var scenarios = LandingVideoScenario.LoadDirectory(Folder);
			foreach (var id in new[] { "wfc-slip2-northbound", "stuy-cove-marina", "pier11-charlie-to-bravo", "stg-east" })
				Assert.That(scenarios.Single(s => s.Id == id).BlockedReason, Is.Not.Empty, id);
		}


		[Test]
		public void IncompleteWindCannotSilentlyBecomeNorthWind()
		{
			var data = LandingVideoScenario.LoadDirectory(Folder)[0].Data.DeepClone();
			data["wind"]["mean_knots"] = 10;
			data["wind"]["from_degrees"] = null;
			Assert.Throws<FormatException>(() => LandingVideoScenario.Parse(data.ToString()));
		}

		[Test]
		public void NonFiniteWindAndUnreportedGustAreRefused()
		{
			foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1 })
			{
				var data = LandingVideoScenario.LoadDirectory(Folder)[0].Data.DeepClone();
				data["wind"]["mean_knots"] = invalid;
				Assert.Throws<FormatException>(() => LandingVideoScenario.Parse(data.ToString()));
			}
			var missing = LandingVideoScenario.LoadDirectory(Folder)[0].Data.DeepClone();
			missing["wind"]["mean_knots"] = null; missing["wind"]["gust_knots"] = 10;
			Assert.Throws<FormatException>(() => LandingVideoScenario.Parse(missing.ToString()));
		}

		[Test]
		public void MissingBerthAndLoosenedThresholdAreRefused()
		{
			var data = LandingVideoScenario.LoadDirectory(Folder)[0].Data.DeepClone();
			data["mapping"]["berth_id"] = null;
			Assert.Throws<FormatException>(() => LandingVideoScenario.Parse(data.ToString()));
			data = LandingVideoScenario.LoadDirectory(Folder)[0].Data.DeepClone();
			data["checks"]["peak_force_n_max"] = 999999;
			Assert.Throws<FormatException>(() => LandingVideoScenario.Parse(data.ToString()));
		}


		[Test]
		public void CaptionPhasesMatchCachedCurrentDirectionsIncludingBlockedDescriptors()
		{
			var pack = FerrySim.Core.Serialization.EnvironmentPackJson.Load(Path.Combine(Root, "data/processed/harbor/env/days/2025-07-14"));
			foreach (var scenario in LandingVideoScenario.LoadDirectory(Folder))
			{
				var tide = scenario.Data["tide"];
				var id = (string)tide["reference_station"];
				if (id == null) continue;
				var current = pack.CurrentStations.Single(station => station.Id == id).Velocity.Sample((int)scenario.Data["minute"] * 60);
				var heading = (double)tide["phase_toward_deg"] * Math.PI / 180;
				Assert.That(current.Magnitude, Is.GreaterThanOrEqualTo(.5), scenario.Id);
				Assert.That((current.North * Math.Cos(heading) + current.East * Math.Sin(heading)) / current.Magnitude,
					Is.GreaterThan(Math.Cos(Math.PI / 4)), scenario.Id);
			}
		}

		[Test]
		public void VideoObservationsAreSeparateFromOperationalPolicyNotes()
		{
			var notes = JObject.Parse(File.ReadAllText(Path.Combine(Root, "data/manifests/crew-landing-notes.json")));
			Assert.That(notes["notes"].Any(n => (string)n["kind"] == "video-observation"), Is.False);
			var observations = (JArray)notes["video_observations"];
			Assert.That(observations.Count, Is.EqualTo(9));
			foreach (var scenario in LandingVideoScenario.LoadDirectory(Folder))
			{
				var observation = observations.Single(n => (string)n["id"] == (string)scenario.Data["observation_id"]);
				Assert.That((string)observation["kind"], Is.EqualTo("video-observation"));
				Assert.That((string)observation["confidence"], Is.EqualTo("low"));
				Assert.That((bool)observation["water_side_crew_reported"], Is.False);
			}
		}

		[Test]
		public void CurrentModelBaselineWritesEveryVerdictWithoutTuning()
		{
			var rows = new JArray();
			foreach (var scenario in LandingVideoScenario.LoadDirectory(Folder))
			{
				var result = scenario.Run(Root, verifyReplay: true);
				rows.Add(result);
				Assert.That((string)result["verdict"], Is.EqualTo("pass").Or.EqualTo("fail").Or.EqualTo("blocked"));
				if ((string)result["verdict"] != "blocked")
				{
					Assert.That((bool)result["replay_matches"], Is.True, scenario.Id + " saved control replay");
					Assert.That((int)result["steps"], Is.GreaterThan(1000));
					if (result["caption_phase_matches_start"] != null) Assert.That((bool)result["caption_phase_matches_start"], Is.True, scenario.Id);
					if (result["first_contact_knots"].Type == JTokenType.Null) Assert.That((double)result["peak_fender_force_n"], Is.Zero);
				}
				TestContext.Out.WriteLine(result.ToString(Newtonsoft.Json.Formatting.None));
			}
			var output = Path.Combine(Root, "artifacts/landing-behaviour"); Directory.CreateDirectory(output);
			File.WriteAllText(Path.Combine(output, "scenario-results.json"), rows.ToString() + "\n");
			Assert.That(rows.Count, Is.EqualTo(9));
		}
	}
}
