using System;
using System.Collections;
using System.IO;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.PlayTests
{
	public sealed class HarborTrafficPlayTests
	{
		// Real player-loop callbacks: a disabled renderer must not leave its long-range signal children drawing.
		[UnityTest]
		public IEnumerator SignalLifecycleAcrossRangeDisableReconnectAndDestruction()
		{
			var folder = Path.Combine(Path.GetTempPath(), "harbor-traffic-play-" + Guid.NewGuid().ToString("N"));
			WriteFixture(folder);
			var world = new GameObject("harbor traffic lifecycle fixture");
			try
			{
				var geo = world.AddComponent<CesiumGeoreference>();
				geo.SetOriginLongitudeLatitudeHeight(WorldOrigin.LongitudeDegrees, WorldOrigin.LatitudeDegrees, 0);
				var host = new GameObject("runner"); host.transform.SetParent(world.transform, false);
				var runner = host.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				runner.EnvironmentPacks = new EnvironmentPacks(folder);
				runner.TrafficPacks = new TrafficPacks(folder);
				runner.Initialize(new ScenarioSettings {
					name = "harbor signal fixture", startLatitude = WorldOrigin.LatitudeDegrees, startLongitude = WorldOrigin.LongitudeDegrees,
					scenarioStartUtc = "2025-07-14T16:00:00Z", environment = new ScenarioEnvironment { pack = "fixture/2025-07-14" },
					traffic = new ScenarioTraffic { day = "2025-07-14", leaveOut = ScenarioTraffic.Off }
				}, new ConstantControls(ControlInputs.Neutral));
				var eye = new GameObject("camera").AddComponent<Camera>(); eye.transform.SetParent(world.transform, false);
				eye.transform.localPosition = new Vector3(0, 10, 4500);
				var sun = new GameObject("test sky").AddComponent<SunController>(); sun.transform.SetParent(world.transform, false);
				sun.SetUtc(new DateTime(2025, 7, 14, 6, 0, 0, DateTimeKind.Utc)); sun.Apply();
				sun.enabled = false; // Explicit fixed night fixture; the runner keeps its independent replay epoch.
				var view = world.AddComponent<TrafficView>();
				var area = new TrafficTileArea(new[] { new TrafficBounds(-1000, 8000, -1000, 1000) });
				view.ConnectHarbor(runner, geo, new ConstantWaterSurface(-32, 0), area, eye, sun);
				yield return null;
				view.Place();
				var hull = view.Hulls[0];
				Assert.That(hull.DetailedHullShown, Is.True);
				eye.transform.localPosition = new Vector3(0, 10, 6000);
				yield return null;
				view.Place();
				Assert.That(hull.DetailedHullShown, Is.False);
				Assert.That(view.LightsDrawn, Is.GreaterThan(0));
				view.enabled = false;
				yield return null;
				Assert.That(hull.gameObject.activeInHierarchy, Is.False);
				Assert.That(view.LightsDrawn, Is.Zero);
				view.enabled = true;
				yield return null;
				Assert.That(hull.gameObject.activeInHierarchy, Is.True);
				view.ConnectHarbor(runner, geo, new ConstantWaterSurface(-32, 0), area, eye, sun);
				Assert.That(hull.gameObject.activeInHierarchy, Is.False, "deferred destroy hides the old hierarchy immediately");
				yield return null;
				Assert.That(hull == null, Is.True);
				Assert.That(world.GetComponentsInChildren<NavigationLights>(true).Length, Is.EqualTo(1));
				// Scene teardown may destroy the separately parented hull before disabling this component.
				Object.DestroyImmediate(view.Hulls[0].gameObject);
				view.enabled = false;
				yield return null;
				Assert.That(view.LightsDrawn, Is.Zero);
				view.enabled = true;
				view.ConnectHarbor(runner, geo, new ConstantWaterSurface(-32, 0), area, eye, sun);
				yield return null;
				Object.Destroy(view);
				yield return null;
				yield return null;
				Assert.That(world.GetComponentsInChildren<NavigationLights>(true).Length, Is.Zero);
			}
			finally { Object.DestroyImmediate(world); Directory.Delete(folder, true); }
		}

		private static void WriteFixture(string folder)
		{
			const string date = "2025-07-14";
			var epoch = EnvironmentPackJson.NewYorkMidnight(new DateTime(2025, 7, 14));
			var times = new[] { -3600.0, 90000.0 }; var zero = new[] { 0.0, 0.0 };
			var depth = new DepthSet("fixture", "NAVD88", 20, new[] { new DepthGrid("fixture", -1000, -1000, 1000, 3, 3,
				new float[] { -12, -12, -12, -12, -12, -12, -12, -12, -12 }) },
				new[] { new DepthSource("synthetic.tif", new string('a', 64), "flat fixture") }, "");
			var current = new CurrentStation("C1", "fixture", "harmonic-prediction", 40.7, -74.015, 0, 0, 1.5, "C1.json", new string('b', 64), new VectorSeries(times, zero, zero));
			var water = new WaterLevelStation("G1", "fixture", 40.7, -74.015, 0, 0, "G1.json", new string('c', 64), new ScalarSeries(times, zero), new[] { false, false });
			EnvironmentPackWriter.Write(new EnvironmentPack("fixture", date, epoch, -3600, 90000, depth, new[] { current }, new[] { water }, new WindStation[0], new[] { "synthetic" }, ""), Path.Combine(folder, "fixture", "env"));
			var vessel = new TrafficVessel(0, 366000001, "TEST", 70, TrafficClass.Cargo, 90, 20, 3, 43100, 43300, 2, 1, true, false);
			var reports = new TrafficReports(new[] { 0, 0 }, new[] { 0, 0 }, new[] { 43100.0, 43300.0 }, new[] { 1000.0, 1000.0 }, new[] { 0.0, 0.0 }, new[] { 0f, 0f }, new[] { 0f, 0f }, new[] { 0f, 0f });
			TrafficPackWriter.Write(new TrafficPack(date, epoch, -3600, 90000, new[] { vessel }, reports, ""), Path.Combine(folder, "harbor", "traffic", date));
		}
	}
}
