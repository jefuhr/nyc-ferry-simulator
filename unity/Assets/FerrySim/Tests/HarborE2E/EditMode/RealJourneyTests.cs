using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Harbor;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Tests.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.HarborE2E.Tests
{
	/// <summary>Explicit real-data acceptance: wrong spans, shallow/uncovered corridors, unoffered starts, unintended contact,
	/// grounding and a state/event replay mismatch must never be accepted as a completed journey.</summary>
	public class RealJourneyTests
	{
		private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static string Output => Path.Combine(Root, "artifacts/harbor-e2e/prepared");
		private static HarborScenarioCatalog Catalog(out LandingGeometry landings, out EnvironmentPacks packs)
		{
			landings = LandingGeometry.Load(Path.Combine(Root, "data/processed/harbor/unity/contacts.json"), Path.Combine(Application.dataPath, "FerrySim/Harbor/Data/landing-contact-surfaces.json"));
			packs = new EnvironmentPacks(Path.Combine(Root, "data/processed"));
			return new HarborScenarioCatalog(landings, new[] { new KeyValuePair<string, VesselParameters>("vessels/t-class.json", VesselParametersJson.Parse(File.ReadAllText(Path.Combine(Root, "vessels/t-class.json")))), new KeyValuePair<string, VesselParameters>("vessels/k-class.json", VesselParametersJson.Parse(File.ReadAllText(Path.Combine(Root, "vessels/k-class.json")))) }, packs,
				trafficPacks: new TrafficPacks(Path.Combine(Root, "data/processed")), depths: new SharedDepthEnvironments(packs));
		}

		[Test, Explicit("Real-data current authoring diagnostic")]
		public void ArrivalCurrentCandidates()
		{
			var catalog = Catalog(out var landings, out var packs);
			var pack = packs.LoadHarborDay("harbor/2025-07-16");
			var env = new FerrySim.Core.Env.DataEnvironment(pack, pack.EpochUtc);
			var data = new JArray();
			foreach (var stop in new[] { "89", "112", "87" })
			{
				var face = landings.PrimaryFace(stop); var p = (face.Start + face.End) * .5;
				var best = double.MaxValue; var minute = -1;
				for (var m = 360; m < 1200; m++)
				{
					var current = env.SampleCurrent(p.North, p.East, m * 60);
					if (current.Magnitude < best) { best = current.Magnitude; minute = m; }
				}
				data.Add(new JObject { ["stop"] = stop, ["arrival_minute"] = minute, ["minimum_current_mps"] = best });
			}
			File.WriteAllText(Path.Combine(Output, "current-candidates.json"), data.ToString() + "\n");
		}

		[Test, Explicit("Requires copied floor A and tools/harbor-benchmark/prepare.py outputs; writes deterministic test traces")]
		public void AuthorAndDriveTheThreeJourneys()
		{
			var catalog = Catalog(out var landings, out var packs);
			var routes = JObject.Parse(File.ReadAllText(Path.Combine(Output, "routes.json")));
			var summaries = new JArray();
			var errors = new List<string>();
			foreach (var token in routes["routes"])
			{
				var reference = ContactScenarios.BowOnLanding(catalog.Vessel("t-class"), ContactScenarios.EstimatedBargeFender, Units.KnotsToMetresPerSecond(2));
				token["estimated_force_limit_n"] = reference.PeakForce;
				var route = HarborJourney.Parse(token);
				Assert.That(catalog.TryCreate(new ScenarioChoice(route.Departure, route.Date, route.Minute, "t-class"), out var settings, out var problem), Is.True, problem);
				var environment = RunEnvironment.ForPack(packs.LoadHarborDay("harbor/" + route.Date), settings.StartUtc);
				var actualEnvironment = environment.StartRun();
				var vessel = catalog.Vessel("t-class");
				var initial = settings.InitialState(environment.Observer);
				var simulation = (VesselDynamicsSimulation)new VesselDynamicsFactory().Create(vessel, actualEnvironment, landings.Geometry, initial);
				var controls = new HarborJourneyControls(route, () => simulation.State, () => simulation.ContactCount > 0, vessel.Particulars.LengthOverall.Value);
				var driver = new FixedStepDriver(simulation, controls);
				var observer = new HarborJourneyObserver(route, simulation);
				driver.AddObserver(observer);
				var trace = new ControlTrace();
				var prior = ControlInputs.Neutral;
				using (var log = new StreamWriter(Path.Combine(Output, route.Name + ".authoring.csv")))
				{
					log.WriteLine("step,time_s,phase,north_m,east_m,heading_rad,surge_mps,sway_mps,under_keel_m,contact_n");
					for (long step = 0; step < 600000 && !controls.Complete && observer.Failures.Count == 0; step++)
					{
						var input = controls.ControlsForStep(step);
						if (step == 0 || !input.Equals(prior)) trace.Append(step, input);
						prior = input;
						// The authoring loop applies the sampled input once per actual core step.
						observer.BeforeStep(step, simulation.State, simulation.Telemetry, input);
						simulation.Step(input);
						observer.AfterStep(step + 1, simulation.State, simulation.Telemetry);
						if (step % 100 == 0)
						{
							var s = simulation.State;
							log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R}",step,s.Time,controls.Phase,s.North,s.East,s.Heading,s.Surge,s.Sway,simulation.Telemetry.DepthUnderKeel,simulation.Telemetry.ContactForce));
						}
						if (!simulation.State.IsFinite() || simulation.Grounding.Grounded) break;
					}
				}
				var state = simulation.State;
				trace.Append((long)Math.Round(state.Time * 100), prior);
				using (var writer = new StreamWriter(Path.Combine(Output, route.Name + ".inputs.csv"))) trace.Write(writer, new[] { "Simulation test inputs only, not navigation advice.", "100 Hz floor A authoring; current method station blend; actual runtime recording must be replayed separately." });
				var summary = new JObject { ["route"] = route.Name, ["completed"] = controls.Complete, ["phase"] = controls.Phase, ["duration_s"] = state.Time,
					["north_m"] = state.North, ["east_m"] = state.East, ["depth_misses"] = environment.PackRun.Coverage.DepthMisses, ["time_holds"] = environment.PackRun.Coverage.TimeHolds,
					["grounded_steps"] = observer.GroundedSteps, ["minimum_under_keel_m"] = observer.MinimumUnderKeel,
					["query_saturation"] = simulation.SaturatedSegmentQueries, ["first_contact_speed_mps"] = observer.FirstContactSpeed,
					["first_contact_id"] = observer.FirstContactId, ["crossings"] = new JArray(observer.Crossings), ["failures"] = new JArray(observer.Failures),
					["peak_contact_force_n"] = observer.PeakForce, ["force_limit_n"] = route.ForceLimit, ["bottomed_out"] = observer.BottomedOut, ["final_hold_okay"] = observer.FinalHoldOkay, ["arrival_verdict"] = observer.LandingVerdict(state) };
				summaries.Add(summary);
				TestContext.WriteLine(summary.ToString());
				if (!controls.Complete || observer.Failures.Count > 0 || observer.GroundedSteps > 0 || environment.PackRun.Coverage.DepthMisses != 0 || environment.PackRun.Coverage.TimeHolds != 0 || simulation.SaturatedSegmentQueries != 0 || observer.Crossings.Count != route.SpanIds.Length || (!route.HoldOnly && observer.LandingVerdict(state) != "pass")) errors.Add(route.Name + ": " + summary);
			}
			File.WriteAllText(Path.Combine(Output, "routes.accepted.json"), routes.ToString() + "\n");
			File.WriteAllText(Path.Combine(Output, "authoring-verdicts.json"), summaries.ToString() + "\n");
			Assert.That(errors, Is.Empty, string.Join("\n", errors));
		}
	}
}
