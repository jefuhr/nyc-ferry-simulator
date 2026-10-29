using System.Collections;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>
	/// End to end on the checked-in harbor scene with the real services and data. Failure modes: a start that replaces the
	/// run without water, navigation or traffic bound; a refused face started anyway or refused without a shown reason; a run
	/// stepping without water under the hull; barges, aids or the day's traffic absent; a vessel leaving covered water
	/// that keeps stepping, or stops without the HUD saying why.
	/// </summary>
	public class HarborIntegrationPlayTests
	{
		[UnityTearDown]
		public IEnumerator UnloadHarborScene() => HarborSceneFixture.Unload();

		[UnityTest, Category("HarborRealData")]
		public IEnumerator HarborScene_Pier11AndStGeorge_WaterNavigationTraffic_AndACoverageHold()
		{
			HarborSceneFixture.RequireData();
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			runner.RecordRuns = false;
			var catalog = services.HarborCatalog;
			Assert.That(catalog.Landings.Count, Is.EqualTo(24));
			Assert.That(catalog.Days.Count, Is.EqualTo(28));
			Assert.That(services.Navigation, Is.Not.Null);
			Assert.That(services.Navigation.Barges.Count, Is.EqualTo(28), "one rigid barge per structure, resident before any run");
			Assert.That(services.Navigation.Aids.Count, Is.EqualTo(285), "the drawn aids; the two boundary aids stay undrawn");
			Assert.That(services.Traffic, Is.Not.Null);
			Assert.That(services.Water.Current, Is.Null, "before the first run the water is the datum alone");
			Assert.That(services.Patches.Count, Is.Zero);
			Assert.That(runner.IsRunActive, Is.False);

			// St. George: listed, refused through the picker, with the reason shown.
			var ui = bootstrap.WheelhouseUI;
			yield return null;
			var picker = ui.View.Picker;
			ui.View.PickerVisible = true;
			picker.Landings.SelectedIndex = HarborSceneFixture.LandingIndex(catalog, HarborSceneFixture.StGeorgeStopId);
			Assert.That(picker.Choice.StopId, Is.EqualTo(HarborSceneFixture.StGeorgeStopId));
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Does.Contain("St. George").And.Contain("waiting for a crew answer on the landing side"));
			Assert.That(picker.ProblemLabel.text, Is.EqualTo(picker.Problem), "the refusal is shown inline");
			Assert.That(runner.IsRunActive, Is.False, "a refused face never starts");
			Assert.That(services.Navigation.SelectedStopId, Is.EqualTo(HarborSceneFixture.StGeorgeStopId), "the picker's selection labels the stop");

			// Pier 11 on a traffic day at its busiest minute, through the catalog and the atomic launcher.
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay, HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[0].Id, traffic: true);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			ui.View.PickerVisible = false;
			Assert.That(runner.IsRunActive, Is.True);
			Assert.That(runner.HarborRuntime, Is.Not.Null);
			Assert.That(runner.HarborRuntime.Digest, Is.EqualTo(services.Pack.Digest), "the runner's own copy of the runtime pack is the scene's");
			Assert.That(runner.HarborRuntime.EnvironmentDepthDigest, Is.EqualTo(runner.RunEnvironment.Pack.Depth.Digest));
			Assert.That(services.RunsBound, Is.EqualTo(1));
			Assert.That(services.Water.Current, Is.Not.Null, "the run's water was bound on RunStarted");
			Assert.That(services.Water.Current.Environment, Is.SameAs(runner.RunEnvironment));
			Assert.That(services.Patches.Count, Is.EqualTo(runner.RunEnvironment.Pack.Depth.Grids.Count), "one visual patch per depth grid");
			Assert.That(runner.TrafficEnabled, Is.True);
			Assert.That(services.Traffic.RosterCount, Is.GreaterThan(0), "the day's roster");
			yield return HarborSceneFixture.WaitReady(bootstrap, "pier-11");

			// The run steps on the run's water with water under the hull.
			var started = Time.realtimeSinceStartupAsDouble;
			while (runner.Driver.StepCount < 50)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(30), "the run did not step after the initial view");
				yield return null;
			}

			Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
			Assert.That(runner.Environment.DepthMisses, Is.Zero, "water under the hull at every step");
			Assert.That(runner.Environment.TimeHolds, Is.Zero);
			var state = runner.State;
			var water = services.WaterSurface.EllipsoidalHeight(state.North, state.East, runner.RenderTime);
			Assert.That(double.IsFinite(water), Is.True);
			Assert.That(water, Is.EqualTo(services.Water.Current.EllipsoidalHeight(state.North, state.East, runner.RenderTime)), "the forwarder is the run's water");
			Assert.That(water - services.Water.Datum.Height(state.North, state.East), Is.EqualTo(runner.Environment.WaterLevelNavd88).Within(1e-3), "datum plus the run's local tide");
			var under = 0;
			foreach (var patch in services.Patches)
			{
				if (patch != null && patch.NodeCount > 0 && patch.Frame.OriginEcef.x != 0)
				{
					under++;
				}
			}

			Assert.That(under, Is.EqualTo(services.Patches.Count), "every patch baked its nodes");
			Assert.That(services.Traffic.ActiveCount, Is.GreaterThan(0), "traffic present on a traffic day");
			Assert.That(services.Navigation.Barges[0].Anchor, Is.Not.Null);

			// Leaving covered water from a fixture start at the southern coverage edge: the run holds and the HUD says why.
			var edge = HarborSceneFixture.FixtureStart("Coverage edge fixture", HarborSceneFixture.CoverageEdgeNorth, HarborSceneFixture.CoverageEdgeEast, 180, "2025-07-16T16:00:00Z", false, 15);
			Assert.That(bootstrap.TryStart(edge, out problem), Is.True, problem);
			Assert.That(services.RunsBound, Is.EqualTo(2), "a new run binds new water before placement");
			Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
			yield return HarborSceneFixture.WaitReady(bootstrap, "coverage-edge");
			started = Time.realtimeSinceStartupAsDouble;
			while (runner.Hold == RunHold.None)
			{
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(60), "the vessel never left covered water");
				yield return null;
			}

			Assert.That(runner.Hold, Is.EqualTo(RunHold.OutsideCoveredWater));
			Assert.That(runner.State.North, Is.LessThan(-7999.5), "held at the edge of the depth grids");
			var held = runner.Driver.StepCount;
			yield return new WaitForSecondsRealtime(0.25f);
			Assert.That(runner.Driver.StepCount, Is.EqualTo(held), "nothing continues on fallback data");
			Assert.That(bootstrap.ShownHold, Is.EqualTo(RunHold.OutsideCoveredWater));
			Assert.That(bootstrap.Status, Is.EqualTo(HarborBootstrap.HoldStatus(RunHold.OutsideCoveredWater)));
			var label = ui.Document.rootVisualElement.Q<Label>("harbor-status");
			Assert.That(label.text, Does.Contain("Run held").And.Contain("left the water"));
			Assert.That(label.resolvedStyle.display, Is.Not.EqualTo(DisplayStyle.None));

			// A new start resumes.
			Assert.That(catalog.TryCreate(choice, out settings, out problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
			yield return null;
			Assert.That(bootstrap.ShownHold, Is.EqualTo(RunHold.None));
			Assert.That(bootstrap.Status, Is.Not.EqualTo(HarborBootstrap.HoldStatus(RunHold.OutsideCoveredWater)));
			Assert.That(services.RunsBound, Is.EqualTo(3));
		}
	}
}
