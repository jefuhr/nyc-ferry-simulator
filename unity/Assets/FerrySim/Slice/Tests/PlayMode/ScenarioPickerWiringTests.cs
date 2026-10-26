using System.Collections;
using System.Linq;
using FerrySim.Sim;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The slice scene's wheelhouse UI on the real simulation, as wired at M3 wave 2a's integration. Failure modes guarded:
	/// the panel and the HUD reading a fake or no environment instead of the runner's; the picker bound to no catalog or
	/// launcher, or to a scene search's guess; the new-run key not opening the picker; the boat moving or the helm acting
	/// while the picker is open, including through a helm rebuilt behind it; closing the picker resuming a run the captain
	/// had paused, or leaving the helm held; and a run started from the picker not being the landing, day, time and vessel
	/// chosen.
	/// </summary>
	public class ScenarioPickerWiringTests
	{
		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			ScenarioPicker.SessionChoice = null;
			_scene = new SliceScene();
			yield return _scene.Load();
			yield return _scene.FramesUntil(() => _scene.Runner.HasEnvironment, 300, "the scene's run to step");
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			ScenarioPicker.SessionChoice = null;
			yield return SliceScene.Unload();
		}

		[Test]
		public void TheWheelhouseUI_ReadsTheRunsWaterAndAir_AndPicksFromTheBootstrap()
		{
			Assert.That(_scene.UI.EnvironmentSource, Is.SameAs(_scene.Runner), "the panel and the HUD read the run's water and air");
			Assert.That(_scene.UI.ScenarioCatalog, Is.SameAs(_scene.Bootstrap));
			Assert.That(_scene.UI.ScenarioLauncher, Is.SameAs(_scene.Bootstrap));
			Assert.That(_scene.View.EnvironmentSource, Is.SameAs(_scene.Runner));
			Assert.That(_scene.View.Picker.Catalog, Is.SameAs(_scene.Bootstrap));
			Assert.That(_scene.View.Picker.Launcher, Is.SameAs(_scene.Bootstrap));
			Assert.That(_scene.View.PickerVisible, Is.False, "the scene opens on its own run, not the picker");
		}

		[UnityTest]
		public IEnumerator TheNewRunKey_OpensThePicker_WhichHoldsTheRunAndTheHelm_UntilItCloses()
		{
			var runner = _scene.Runner;
			var mapper = _scene.Helm.Mapper;
			Assert.That(runner.IsPaused, Is.False);
			Assert.That(mapper.Enabled, Is.True);

			_scene.Bootstrap.ToggleScenarioPicker();

			Assert.That(_scene.View.PickerVisible, Is.True);
			Assert.That(runner.IsPaused, Is.True, "the run stands still while the picker is open");
			Assert.That(mapper.Enabled, Is.False, "the helm is held while the picker is open");
			var held = runner.State.Time;
			yield return SliceScene.Frames(10);
			Assert.That(runner.State.Time, Is.EqualTo(held), "no step is taken while the picker is open");

			_scene.View.PickerVisible = false;

			Assert.That(runner.IsPaused, Is.False);
			Assert.That(mapper.Enabled, Is.True, "the helm reads its devices again");
			yield return _scene.FramesUntil(() => runner.State.Time > held, 60, "the run to go on");
		}

		[UnityTest]
		public IEnumerator AHelmRebuiltWhileThePickerIsOpen_IsHeldToo_UntilThePickerCloses()
		{
			var runner = _scene.Runner;
			_scene.Bootstrap.ToggleScenarioPicker();
			Assert.That(_scene.View.PickerVisible, Is.True);
			var first = _scene.Helm.Mapper;

			_scene.Helm.enabled = false;
			_scene.Helm.enabled = true;
			var rebuilt = _scene.Helm.Mapper;
			Assert.That(rebuilt, Is.Not.SameAs(first), "enabling the helm again builds a new mapper");
			yield return null;

			Assert.That(rebuilt.Enabled, Is.False, "the new mapper is held while the picker is open, so its pause key and levers do nothing");
			Assert.That(runner.IsPaused, Is.True, "the run still stands still");

			_scene.View.PickerVisible = false;

			Assert.That(_scene.Helm.Mapper, Is.SameAs(rebuilt));
			Assert.That(rebuilt.Enabled, Is.True, "closing the picker gives the new mapper back");
			Assert.That(runner.IsPaused, Is.False);
		}

		[UnityTest]
		public IEnumerator ClosingThePicker_LeavesARunTheCaptainPausedPaused()
		{
			var runner = _scene.Runner;
			runner.Pause();

			_scene.Bootstrap.ToggleScenarioPicker();
			Assert.That(_scene.View.PickerVisible, Is.True);
			_scene.View.PickerVisible = false;

			Assert.That(runner.IsPaused, Is.True, "the captain's pause outlives the picker");
			Assert.That(_scene.Helm.Mapper.Enabled, Is.True, "but the helm is given back");
			runner.Resume();
			yield return null;
		}

		[UnityTest]
		public IEnumerator StartingFromThePicker_RunsTheChosenLandingDayTimeAndVessel()
		{
			var runner = _scene.Runner;
			var first = runner.RunId;
			_scene.Bootstrap.ToggleScenarioPicker();
			var picker = _scene.View.Picker;
			var catalog = _scene.Bootstrap.ScenarioCatalog;
			picker.Landings.SelectedIndex = catalog.Landings.ToList().FindIndex(landing => landing.StopId == "87");
			picker.Days.SelectedIndex = catalog.Days.ToList().FindIndex(day => day.Date == "2025-07-14");
			picker.Berths.SelectedIndex = 2; // Preserve the original southern-berth launcher evidence.
			picker.Time.Minutes = 10 * 60;
			picker.Vessels.SelectedIndex = catalog.Vessels.ToList().FindIndex(vessel => vessel.Id == "k-class");
			Assert.That(picker.Choice, Is.EqualTo(new ScenarioChoice("87", "2025-07-14", 600, "k-class", berthId: "enc:0226015B0512B198")));

			Assert.That(picker.TryStartRun(), Is.True, picker.Problem);

			Assert.That(_scene.View.PickerVisible, Is.False, "starting closes the picker");
			Assert.That(runner.RunId, Is.GreaterThan(first), "a new run");
			Assert.That(runner.IsPaused, Is.False, "the new run goes");
			Assert.That(_scene.Helm.Mapper.Enabled, Is.True, "with the helm");
			Assert.That(runner.Scenario.environment.pack, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(runner.Scenario.vesselFile, Is.EqualTo("vessels/k-class.json"));
			Assert.That(runner.Parameters.Id, Is.EqualTo("k-class"));
			Assert.That(_scene.Bootstrap.StartRefusal, Is.Null);
			yield return _scene.FramesUntil(() => runner.HasEnvironment, 300, "the picked run to step");
			var readout = runner.Environment;
			Assert.That(readout.PackName, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(runner.ScenarioStartUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm"), Is.EqualTo("2025-07-14T14:00"), "10:00 EDT");
		}
	}
}
