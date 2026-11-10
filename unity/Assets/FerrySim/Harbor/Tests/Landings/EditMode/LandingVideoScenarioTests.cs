using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Slice;
using FerrySim.Sim;
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
		public void ApproachOnlyScenarioUsesSuppliedPoseAndReportsFaceRelativeHeading()
		{
			// A synthetic opt-in copy verifies the new driver; none of the nine caption definitions changes.
			var data = (JObject)LandingVideoScenario.LoadDirectory(Folder).Single(s => s.Id == "br-west").Data.DeepClone();
			data["id"] = "approach-only-proxy-test"; data["source"] = "Synthetic driver integration test on a cached main-face proxy.";
			var landings = LandingGeometry.Load(Path.Combine(Root, "data/processed/harbor/unity/contacts.json"), Path.Combine(Root, "unity/Assets/FerrySim/Harbor/Data/landing-contact-surfaces.json"));
			var face = landings.BerthFace((string)data["mapping"]["stop_id"], (string)data["mapping"]["berth_id"]);
			var middle = (face.Start + face.End) * .5; var direction = face.End - face.Start;
			var normal = new HorizontalVector(direction.East, -direction.North) * (1 / direction.Magnitude);
			var tangent = new HorizontalVector(-normal.East, normal.North);
			var vessel = VesselParametersJson.Load(Path.Combine(Root, "vessels/t-class.json"));
			var bow = FaceClearance.BodyOutline(vessel).Max(p => p.North);
			var start = middle + normal * (bow + 100); var end = middle + normal * (bow + 60);
			JArray Point(HorizontalVector p) => new JArray(p.North, p.East);
			var a = start + normal * 20; var b = end - normal * 20;
			data["start"] = new JObject { ["mode"] = "approach-only", ["position_ne_m"] = Point(start),
				["heading_rad"] = Math.Atan2(-normal.East, -normal.North) + .01,
				["corridor_ne_m"] = new JArray(Point(a + tangent * 30), Point(b + tangent * 30), Point(b - tangent * 30), Point(a - tangent * 30), Point(a + tangent * 30)) };
			var result = LandingVideoScenario.Parse(data.ToString()).Run(Root, verifyReplay: true);
			Assert.That((string)result["verdict"], Is.Not.EqualTo("blocked"), (string)result["reason"]);
			Assert.That((string)result["start_mode"], Is.EqualTo("approach-only"));
			Assert.That((bool)result["approach_corridor_verified"], Is.True);
			Assert.That((int)result["driver_phase"], Is.GreaterThanOrEqualTo(0));
			Assert.That((bool)result["replay_matches"], Is.True);
			var output = Path.GetDirectoryName((string)result["hold_csv"]);
			var first = ControlTrace.Load(Path.Combine(output, "controls.csv")).At(0);
			Assert.That(first.PortLever, Is.GreaterThan(0)); Assert.That(first.StarboardLever, Is.GreaterThan(0));
			var lastHold = File.ReadAllLines((string)result["hold_csv"]).Last().Split(',');
			var error = double.Parse(lastHold[15], System.Globalization.CultureInfo.InvariantCulture);
			Assert.That((double)result["heading_error_deg"], Is.EqualTo(Math.Abs(Angles.ToDegrees(error))).Within(1e-9));
		}

		[Test]
		public void CurrentModelBaselineWritesEveryVerdictWithoutTuning()
		{
			var rows = new JArray();
			foreach (var scenario in LandingVideoScenario.LoadDirectory(Folder))
			{
				var result = scenario.Run(Root, verifyReplay: true);
				rows.Add(result);
				var holdCsv = File.ReadAllLines((string)result["hold_csv"]);
				Assert.That(holdCsv.Length, Is.EqualTo(1 + (int)result["hold_window_steps"]), scenario.Id + " one row per observed hold step");
				if ((string)result["verdict"] == "blocked") Assert.That((int)result["hold_window_steps"], Is.Zero);
				else
				{
					Assert.That((int)result["hold_window_steps"], Is.EqualTo(1000));
					Assert.That((int)result["hold_window_intended_face_steps"], Is.EqualTo(holdCsv.Skip(1).Count(line => line.Split(',')[13] == "1")));
					Assert.That((int)result["hold_window_grounded_steps"], Is.EqualTo(holdCsv.Skip(1).Count(line => line.Split(',')[14] == "1")));
				}
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
			var output = Path.Combine(Root, "artifacts/landing-hold-fixes/scenarios"); Directory.CreateDirectory(output);
			File.WriteAllText(Path.Combine(output, "scenario-results.json"), rows.ToString() + "\n");
			Assert.That(rows.Count, Is.EqualTo(9));
		}
	}
}
