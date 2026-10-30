using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.HarborE2E.Tests
{
	public class StartAndSeamTests
	{
		private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static string Output => Path.Combine(Root, "artifacts/harbor-e2e/prepared");

		[Test, Explicit("Real catalog, all 26 berths, both hulls, local tide extrema on four representative days")]
		public void EveryCatalogChoiceAtRepresentativeLocalTideExtrema()
		{
			var packs = new EnvironmentPacks(Path.Combine(Root, "data/processed"));
			var geometry = LandingGeometry.Load(Path.Combine(Root, "data/processed/harbor/unity/contacts.json"), Path.Combine(Application.dataPath, "FerrySim/Harbor/Data/landing-contact-surfaces.json"));
			var vessels = new List<KeyValuePair<string, VesselParameters>>();
			foreach (var hull in new[] { "t-class", "k-class" }) vessels.Add(new KeyValuePair<string,VesselParameters>("vessels/" + hull + ".json", VesselParametersJson.Load(Path.Combine(Root, "vessels/" + hull + ".json"))));
			var catalog = new HarborScenarioCatalog(geometry, vessels, packs, trafficPacks: new TrafficPacks(Path.Combine(Root,"data/processed")), depths: new SharedDepthEnvironments(packs));
			Assert.That(catalog.Landings.Count, Is.EqualTo(24));
			var rows = new JArray();
			var invalid = new List<string>();
			foreach (var date in new[] { "2024-10-07", "2025-01-13", "2025-04-14", "2025-07-16" })
			{
				var pack = packs.LoadHarborDay("harbor/" + date);
				var environment = new DataEnvironment(pack, pack.EpochUtc);
				foreach (var landing in catalog.Landings)
				foreach (var berth in landing.Berths)
				{
					var stop = JObject.Parse(File.ReadAllText(Path.Combine(Root,"data/processed/harbor/runtime/landings.json")))["stops"];
					double n = 0, e = 0;
					foreach (var item in stop) if ((string)item["stop_id"] == landing.StopId) { n = (double)item["north_m"]; e = (double)item["east_m"]; }
					var low = double.PositiveInfinity; var high = double.NegativeInfinity; var lowMinute = 0; var highMinute = 0;
					for (var minute = 0; minute < 1440; minute++)
					{
						var water = environment.SampleWaterLevel(n,e,minute*60);
						if (water < low) { low = water; lowMinute = minute; }
						if (water > high) { high = water; highMinute = minute; }
					}
					foreach (var minute in new[] { lowMinute, highMinute })
					foreach (var vessel in catalog.Vessels)
					{
						var choice = new ScenarioChoice(landing.StopId,date,minute,vessel.Id,berth.Id);
						var accepted = catalog.TryCreate(choice,out var settings,out var problem);
						var hasDepth = catalog.TryGetStartDepth(choice,out var depth);
						var row = new JObject { ["stop"] = landing.StopId, ["landing"] = landing.Name, ["berth"] = berth.Id, ["hull"] = vessel.Id,
							["date"] = date, ["minute"] = minute, ["extremum"] = minute == lowMinute ? "local low" : "local high", ["accepted"] = accepted,
							["depth_known"] = hasDepth, ["under_keel_m"] = hasDepth ? (JToken)depth.UnderKeel : null, ["reason"] = problem };
						rows.Add(row);
						if (accepted && (!hasDepth || depth.UnderKeel < 0)) invalid.Add(row.ToString());
						if (!accepted && catalog.CanStart(landing.StopId,berth.Id) && (!hasDepth || depth.UnderKeel >= 0)) invalid.Add(row.ToString());
						if (!accepted && string.IsNullOrWhiteSpace(problem)) invalid.Add("unnamed refusal " + landing.StopId);
					}
				}
			}
			Directory.CreateDirectory(Output);
			File.WriteAllText(Path.Combine(Output,"start-extrema.json"),rows.ToString()+"\n");
			Assert.That(rows.Count, Is.EqualTo(4*26*2*2));
			Assert.That(invalid, Is.Empty, string.Join("\n",invalid));
		}

		[Test]
		public void SyntheticHeadOnEncounterAtATileSeamNeedsNoRenderer()
		{
			const double seam = 4000;
			var vessel = new TrafficVessel(0,200000001,"SYNTHETIC",31,TrafficClass.TugTow,30,10,3,0,240,5,1,true,false);
			var reports = new TrafficReports(new int[5],new int[5],new double[] {0,60,120,180,240},
				new double[] {seam+500,seam+200,seam-100,seam-400,seam-700},new double[5],new float[] {5,5,5,5,5},
				new float[] {(float)Math.PI,(float)Math.PI,(float)Math.PI,(float)Math.PI,(float)Math.PI},new float[] {(float)Math.PI,(float)Math.PI,(float)Math.PI,(float)Math.PI,(float)Math.PI});
			var pack = new TrafficPack("2025-07-14",new DateTimeOffset(2025,7,14,4,0,0,TimeSpan.Zero),-3600,90000,new[] {vessel},reports,new string('0',64));
			var outline = new HullOutline(new double[] {10,10,-10,-10}, new double[] {-3,3,3,-3});
			var monitor = new TrafficMonitor(new TrafficReplay(pack),0,-1,outline,TrafficAlarmLimits.Default);
			for (long step=1;step<=12000;step++)
			{
				var t=SimulationClock.TimeAtStep(step);
				monitor.Update(step,VesselState.Planar(t,seam-500+5*t,0,0,5),null);
			}
			Assert.That(monitor.AlarmsRaised,Is.EqualTo(1));
			Assert.That(monitor.CollisionsStarted,Is.EqualTo(1));
			Assert.That(monitor.EventCount,Is.EqualTo(4));
		}
	}
}
