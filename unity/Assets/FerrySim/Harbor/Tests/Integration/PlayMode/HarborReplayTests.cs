using System;
using System.Collections;
using System.IO;
using System.Reflection;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.HarborWater;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>Real default composition: stale replay water, replay-first datum-only placement, and duplicated preview depth.</summary>
	public class HarborReplayTests
	{
		[UnityTearDown]
		public IEnumerator Unload() => HarborSceneFixture.Unload();

		[UnityTest, Category("HarborRealData")]
		public IEnumerator DefaultComposition_SharesPreviewAndRunDepth_AcrossTwoDays()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			DepthSet first = null;
			foreach (var day in new[] { "2025-07-16", "2025-07-15" })
			{
				var choice = new ScenarioChoice("87", day, 738, services.HarborCatalog.Vessels[0].Id, traffic: false);
				Assert.That(services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
				var preview = (DataEnvironment)typeof(HarborScenarioCatalog).GetField("_residentEnvironment", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(services.HarborCatalog);
				Assert.That(preview, Is.Not.Null);
				first ??= preview.Pack.Depth;
				Assert.That(preview.Pack.Depth, Is.SameAs(first));
				Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
				Assert.That(runner.RunEnvironment.Pack.Depth, Is.SameAs(preview.Pack.Depth), "default factory preview and active runner must own one depth set");
			}
			var run = RecordA(bootstrap);
			runner.InitializeReplay(run);
			Assert.That(runner.RunEnvironment.Pack.Depth, Is.SameAs(first), "replay must reuse the resident verified depth too");
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator ReplayA_AfterDifferentTideB_RebindsBeforePlacementAndTraffic()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var run = RecordA(bootstrap);
			var a = services.Water.Current;
			var choice = new ScenarioChoice("87", "2025-07-16", 180, services.HarborCatalog.Vessels[0].Id, traffic: false);
			Assert.That(services.HarborCatalog.TryCreate(choice, out var b, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(b, out problem), Is.True, problem);
			Assert.That(Math.Abs(a.EllipsoidalHeight(351, 748, 0) - services.Water.EllipsoidalHeight(351, 748, 0)), Is.GreaterThan(.1), "B has a materially different tide");
			CheckReplay(bootstrap, run);
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator ReplayA_InAFreshScene_BindsWaterAndPatchesBeforeAnyOrdinaryRun()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load();
			var run = RecordA(HarborSceneFixture.Bootstrap());
			yield return HarborSceneFixture.Unload();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			Assert.That(services.RunsBound, Is.Zero);
			CheckReplay(bootstrap, run);
			Assert.That(services.RunsBound, Is.EqualTo(1));
		}

		private static RecordedRun RecordA(HarborBootstrap bootstrap)
		{
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = true;
			runner.RecordingDirectory = SimPaths.Resolve("artifacts/harbor-review-fixes/recordings/" + Guid.NewGuid().ToString("N"));
			var choice = new ScenarioChoice("87", "2025-07-16", 738, services.HarborCatalog.Vessels[0].Id, traffic: true);
			Assert.That(services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			runner.Resume();
			runner.AdvanceFrame(.1);
			runner.EndRun();
			Assert.That(runner.LastRecordingPath, Is.Not.Null, runner.RecordingError);
			runner.RecordRuns = false;
			return RecordedRun.Load(runner.LastRecordingPath);
		}

		private static void CheckReplay(HarborBootstrap bootstrap, RecordedRun run)
		{
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			Action beforePlacement = () =>
			{
				Assert.That(services.Water.Current?.Environment, Is.SameAs(runner.RunEnvironment), "replay water is bound before the own hull's first placement");
				Assert.That(services.Traffic.Traffic, Is.SameAs(runner.Traffic), "the traffic callback also precedes first placement");
			};
			runner.BeforePlacement += beforePlacement;
			try { runner.InitializeReplay(run); }
			finally { runner.BeforePlacement -= beforePlacement; }
			runner.Resume();
			runner.AdvanceFrame(.035);
			runner.ApplyRenderPose();
			Assert.That(services.Water.Current.Environment, Is.SameAs(runner.RunEnvironment));
			Assert.That(runner.RunEnvironment.ScenarioStartUtc, Is.EqualTo(run.ScenarioStartUtc), "the replay binds A's recorded start, not B's tide offset");
			Assert.That(services.Patches.Count, Is.EqualTo(runner.RunEnvironment.Pack.Depth.Grids.Count));
			var expected = new HarborWaterService(new HarborDatum(services.Pack.Geoid), runner.RunEnvironment);
			var pose = runner.RenderPose;
			Assert.That(services.Water.EllipsoidalHeight(pose.North, pose.East, runner.RenderTime), Is.EqualTo(expected.EllipsoidalHeight(pose.North, pose.East, runner.RenderTime)));
			var anchor = runner.GetComponent<CesiumGlobeAnchor>();
			Assert.That(anchor.longitudeLatitudeHeight.z + pose.Down, Is.EqualTo(expected.EllipsoidalHeight(pose.North, pose.East, runner.RenderTime)).Within(.001), "own hull uses A");
			var patch = services.Patches[0];
			patch.UpdateWater(runner.RenderTime);
			var llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(patch.Frame.ToEcef(patch.Mesh.vertices[0]));
			var ne = LocalTangentPlane.World.ToLocal(new GeodeticPosition(llh.y, llh.x));
			// Nodes are on the common 100 m lattice. Recover that exact input instead of sampling a float vertex's
			// micrometre-scale inverse-conversion error just outside a strict coverage boundary.
			var nodeNorth = Math.Round(ne.North / HarborWaterPatch.Spacing) * HarborWaterPatch.Spacing;
			var nodeEast = Math.Round(ne.East / HarborWaterPatch.Spacing) * HarborWaterPatch.Spacing;
			Assert.That(Math.Abs(ne.North - nodeNorth), Is.LessThan(.003));
			Assert.That(Math.Abs(ne.East - nodeEast), Is.LessThan(.003));
			Assert.That(llh.z, Is.EqualTo(expected.EllipsoidalHeight(nodeNorth, nodeEast, runner.RenderTime)).Within(.002), "visual patch node uses A");
			services.Traffic.ViewRange = 100000;
			services.Traffic.Place();
			var checkedTargets = 0;
			foreach (var hull in services.Traffic.Hulls)
			{
				if (!hull.Shown) continue;
				var geo = hull.GetComponentInParent<CesiumGeoreference>();
				var actual = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)geo.transform.InverseTransformPoint(hull.transform.position));
				llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(actual);
				Assert.That(llh.z, Is.EqualTo(expected.EllipsoidalHeight(hull.North, hull.East, services.Traffic.PlacedTime)).Within(.01), "traffic target uses A");
				checkedTargets++;
			}
			Assert.That(checkedTargets, Is.GreaterThan(0));
			TestContext.WriteLine("replay water identity, own hull, patch node and " + checkedTargets + " traffic targets match A");
		}
	}
}
