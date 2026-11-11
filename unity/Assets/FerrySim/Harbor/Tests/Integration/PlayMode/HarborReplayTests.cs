using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.HarborWater;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>Real default composition: stale replay water, replay-first datum-only placement, and duplicated preview depth.</summary>
	public class HarborReplayTests
	{
		private ScenarioChoice? _pickerChoice;

		[SetUp]
		public void RememberPickerChoice() => _pickerChoice = ScenarioPicker.SessionChoice;

		[UnityTearDown]
		public IEnumerator Unload()
		{
			yield return HarborSceneFixture.Unload();
			ScenarioPicker.SessionChoice = _pickerChoice;
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator DefaultComposition_SharesPreviewAndRunDepth_AcrossTwoDays()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load(consoleScreens: false);
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
				Assert.That(runner.HarborRuntime, Is.SameAs(services.Pack), "starts share the scene's verified runtime pack");
				Assert.That(runner.HarborRuntime.Digest, Is.EqualTo(services.Pack.Digest));
				Assert.That(runner.WorldGeometry, Is.SameAs(services.Landings.Geometry), "contacts are parsed once at composition");
				Assert.That(runner.RunEnvironment.Pack, Is.SameAs(preview.Pack), "the previewed day is the atomic start's verified snapshot");
				Assert.That(runner.HarborLoadTimings.RuntimeMilliseconds, Is.Zero);
				Assert.That(runner.HarborLoadTimings.ContactsMilliseconds, Is.Zero);
				Assert.That(runner.HarborLoadTimings.Day.TotalMilliseconds, Is.Zero);
				Assert.That(runner.RunEnvironment.Pack.Depth, Is.SameAs(preview.Pack.Depth), "default factory preview and active runner must own one depth set");
			}
			var run = RecordA(bootstrap);
			runner.InitializeReplay(run);
			Assert.That(runner.RunEnvironment.Pack.Depth, Is.SameAs(first), "replay must reuse the resident verified depth too");
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator SwitchingDays_ReleasesThePreviousSceneRunAndTrafficPack()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load(consoleScreens: false);
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			bootstrap.Runner.RecordRuns = false;
			var retired = StartCapturedBundle(bootstrap);
			var waited = Time.realtimeSinceStartupAsDouble;
			while (!services.Displays.Connected)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180), "the real displays did not connect");
				yield return null;
			}
			services.Displays.Radar.Refresh();
			Assert.That(services.Displays.Radar.Traffic, Is.SameAs(bootstrap.Runner.Traffic));
			// The chart too: shown for a frame so its target adapter reads this run's traffic, then hidden before the switch.
			var chart = services.Displays.Chartplotter;
			chart.Visible = true;
			chart.Refresh();
			Assert.That(chart.Targets.Count, Is.EqualTo(bootstrap.Runner.Traffic.Pack.Vessels.Count), "the chart's adapter read the run's traffic");
			chart.Visible = false;
			services.Displays.RadarShown = false;
			services.Displays.ChartplotterShown = false;
			services.Displays.Tick();
			Assert.That(chart.Visible, Is.False);
			var choice = new ScenarioChoice("87", "2025-07-15", 738, "t-class", traffic: false);
			Assert.That(services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			Assert.That(services.Displays.Radar.Traffic, Is.Null, "a hidden radar releases the previous run's traffic too");
			// Destroyed water patches and traffic hulls finish releasing their old services at the frame boundary.
			yield return null;
			yield return null;
			for (var attempt = 0; attempt < 5; attempt++)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
				yield return null;
			}
			Assert.That(services.Displays.Chartplotter.Visible, Is.False, "the chart stayed hidden: nothing queried its adapter after the switch");
			foreach (var reference in retired)
			{
				Assert.That(reference.IsAlive, Is.False, "scene bindings, preview and displays (the hidden chart's target adapter included) must release the retired bundle");
			}
			Assert.That(bootstrap.Runner.IsRunActive, Is.True);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static WeakReference[] StartCapturedBundle(HarborBootstrap bootstrap)
		{
			var services = (HarborIntegrationServices)bootstrap.Services;
			var choice = new ScenarioChoice("87", "2025-07-16", 738, "t-class", traffic: true);
			Assert.That(services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			var runner = bootstrap.Runner;
			return new[] { new WeakReference(runner.RunEnvironment), new WeakReference(runner.RunEnvironment.Pack),
				new WeakReference(runner.Traffic), new WeakReference(runner.Traffic.Pack), new WeakReference(runner.TrafficMonitor) };
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator PickerSelection_RestoresTheActiveStop_AndRebindsAReplacedView()
		{
			HarborSceneFixture.RequireData();
			ScenarioPicker.SessionChoice = new ScenarioChoice("87", "2025-07-16", 738, "t-class", traffic: false);
			yield return HarborSceneFixture.Load(consoleScreens: false);
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var ui = bootstrap.WheelhouseUI;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			var view = ui.View;
			services.FollowPicker(ui);
			view.PickerVisible = true;
			view.Picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(services.Catalog, "137");
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("137"), "visible highlight labels even a refused face");
			Assert.That(view.Picker.TryStartRun(), Is.False);
			view.PickerVisible = false;
			Assert.That(services.Navigation.SelectedStopId, Is.Empty, "closing a refusal before any run clears its highlight");

			view.PickerVisible = true;
			view.Picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(services.Catalog, "87");
			Assert.That(view.Picker.TryStartRun(), Is.True, view.Picker.Problem);
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("87"), "Started uses its accepted choice");
			view.PickerVisible = true;
			view.Picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(services.Catalog, "137");
			Assert.That(view.Picker.TryStartRun(), Is.False);
			view.PickerVisible = false;
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("87"), "a refusal preserves the active run's stop");

			ui.enabled = false;
			services.FollowPicker(ui);
			ui.enabled = true;
			var replacement = ui.View;
			Assert.That(replacement, Is.Not.SameAs(view));
			services.FollowPicker(ui);
			services.FollowPicker(ui);
			AssertPickerPollingAllocatesNothing(services, ui);
			replacement.PickerVisible = true;
			replacement.Picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(services.Catalog, "112");
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("112"), "the new view is connected immediately");
			view.PickerVisible = true;
			view.Picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(services.Catalog, "89");
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("112"), "the old view has no selection subscription");
			replacement.PickerVisible = false;
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("87"), "dismissal restores the active run's identity");
			view.PickerVisible = false;
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo("87"), "the old visibility subscription is detached too");
			runner.EndRun();
			Assert.That(services.Navigation.SelectedStopId, Is.Empty, "there is no active run after EndRun");
		}

		private static void AssertPickerPollingAllocatesNothing(HarborIntegrationServices services, WheelhouseUI ui)
		{
			var chars = new char[8];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true;
				var control = new string(chars);
				recorder.enabled = false;
				var calibration = recorder.sampleBlockCount;
				GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1), "one deliberate allocation calibrates this Mono recorder");
				recorder.enabled = true;
				for (var i = 0; i < 1000; i++) services.FollowPicker(ui);
				recorder.enabled = false;
				var samples = recorder.sampleBlockCount;
				Assert.That(samples, Is.Zero, "the factory's per-frame picker polling allocates nothing");
				TestContext.WriteLine("picker polling: positive control " + calibration + ", GC.Alloc " + samples);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator ReplayA_AfterDifferentTideB_RebindsBeforePlacementAndTraffic()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load(consoleScreens: false);
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var run = RecordA(bootstrap);
			var a = services.Water.Current;
			var patch = services.Patches[0];
			var mesh = patch.Mesh;
			var choice = new ScenarioChoice("87", "2025-07-16", 180, services.HarborCatalog.Vessels[0].Id, traffic: false);
			Assert.That(services.HarborCatalog.TryCreate(choice, out var b, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(b, out problem), Is.True, problem);
			Assert.That(services.Patches[0], Is.SameAs(patch), "the shared depth layout retains the patch component");
			Assert.That(services.Patches[0].Mesh, Is.SameAs(mesh), "the matching datum and gauges retain the baked mesh");
			Assert.That(Math.Abs(a.EllipsoidalHeight(351, 748, 0) - services.Water.EllipsoidalHeight(351, 748, 0)), Is.GreaterThan(.1), "B has a materially different tide");
			CheckReplay(bootstrap, run);
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator ReplayA_InAFreshScene_BindsWaterAndPatchesBeforeAnyOrdinaryRun()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load(consoleScreens: false);
			var run = RecordA(HarborSceneFixture.Bootstrap());
			yield return HarborSceneFixture.Unload();
			yield return HarborSceneFixture.Load(consoleScreens: false);
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
