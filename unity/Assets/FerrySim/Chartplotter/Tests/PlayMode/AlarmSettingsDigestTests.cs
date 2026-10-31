using System;
using System.Collections;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// The CPA alarm keys are display settings only (v0.3.1): the pattern of the harbor displays' recording-digest test,
	/// on the checked-in harbor scene with its real services, the real chart and the traffic day. Two recorded runs of
	/// the same Pier 11 start with the chartplotter shown: one left alone, one with the limits stepped and the alarm
	/// muted and unmuted while it runs. Failure mode: a setting reaching the recording (a different state digest, row
	/// count or step count), or the view not holding the settings across the second run of the session.
	/// </summary>
	public class AlarmSettingsDigestTests
	{
		private const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		private const string TrafficDay = "2025-07-16";
		private const int BusiestLocalMinute = 738;
		private const string Pier11StopId = "87";

		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

		private static string Folder => Path.Combine(Repo, "artifacts", "plotter-cpa-controls");

		[UnityTearDown]
		public IEnumerator Unload()
		{
			Time.timeScale = 1f;
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				foreach (var root in scene.GetRootGameObjects())
					foreach (var runner in root.GetComponentsInChildren<VesselSimulationRunner>(true))
						runner.EndRun();
				yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		[UnityTest, Explicit("Needs the copied harbor data and chart; run by exact name."), Category("HarborRealData"), Timeout(1200000)]
		public IEnumerator RecordingDigestIsIdentical_WithAndWithoutAlarmKeyChangesMidRun()
		{
			var harbor = Path.Combine(Repo, "data", "processed", "harbor");
			foreach (var required in new[] { "runtime", "env/days", "3dtiles", "3dtiles-buildings", "traffic/" + TrafficDay, "chart" })
			{
				if (!Directory.Exists(Path.Combine(harbor, required))) Assert.Ignore("The harbor data is not copied: missing " + Path.Combine(harbor, required) + ".");
			}

			Directory.CreateDirectory(Folder);
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			Assert.Fail("This test loads the checked-in editor scene.");
			yield break;
#endif
			yield return null;
			var bootstrap = Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(bootstrap, Is.Not.Null, "Harbor.unity has one HarborBootstrap");
			Assert.That(bootstrap.Problem, Is.Null, "the real services connected");
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner;
			var displays = services.Displays;
			var ui = bootstrap.WheelhouseUI;
			yield return null;
			ui.View.PickerVisible = false;
			var folder = Path.Combine(Folder, "recordings", Guid.NewGuid().ToString("N"));
			runner.RecordRuns = true;
			runner.RecordingDirectory = folder;
			var catalog = services.HarborCatalog;
			var choice = new ScenarioChoice(Pier11StopId, TrafficDay, BusiestLocalMinute, catalog.Vessels[0].Id, traffic: true);
			// The runner's own Update advances unscaled frame time times the time scale: zero, so only the test's exact frames step.
			Time.timeScale = 0f;
			string pathA = null, pathB = null;
			long stepsA = -1;
			displays.ChartplotterShown = true;
			foreach (var keys in new[] { false, true })
			{
				Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
				Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
				ui.View.PickerVisible = false;
				var layers = Object.FindObjectsByType<HarborTileset>(FindObjectsSortMode.None);
				var started = Time.realtimeSinceStartupAsDouble;
				while (bootstrap.ViewState != HarborViewState.Ready)
				{
					Assert.That(bootstrap.Problem, Is.Null);
					foreach (var layer in layers) Assert.That(layer.Failure, Is.Null, layer.LayerId);
					Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(120), "the initial harbor view did not settle");
					yield return null;
				}

				var waited = Time.realtimeSinceStartupAsDouble;
				while (!displays.Connected)
				{
					Assert.That(displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), displays.Load.Error);
					Assert.That(Time.realtimeSinceStartupAsDouble - waited, Is.LessThan(180));
					yield return null;
				}

				yield return null;
				Assert.That(displays.ChartplotterVisible, Is.True);
				var view = displays.Chartplotter.View;
				if (keys)
				{
					// The second run of the session starts with the defaults the first one left (nothing changed them).
					Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(Core.Navigation.CpaAlarmLimits.DefaultCpa));
					Assert.That(view.AlarmMuted, Is.False);
				}

				runner.Driver.Controls = new ConstantControls(new ControlInputs(0.4, 0.4, 0.1));
				runner.Resume();
				var stepsBefore = runner.Driver.StepCount;
				for (var frame = 0; frame < 600; frame++)
				{
					if (keys)
					{
						// Key changes spread over the run: wider CPA, longer TCPA, mute, unmute, narrower again.
						if (frame == 60) view.StepCpaLimit(1);
						if (frame == 120) view.StepTcpaLimit(1);
						if (frame == 180) view.ToggleAlarmMute();
						if (frame == 300) view.StepCpaLimit(1);
						if (frame == 420) view.ToggleAlarmMute();
						if (frame == 480) { view.StepCpaLimit(-1); view.StepCpaLimit(-1); view.StepTcpaLimit(-1); }
					}

					runner.AdvanceFrame(1.0 / 60);
					yield return null;
				}

				var steps = runner.Driver.StepCount - stepsBefore;
				Assert.That(steps, Is.InRange(990, 1000), "about ten simulated seconds");
				if (stepsA < 0) stepsA = steps; else Assert.That(steps, Is.EqualTo(stepsA), "the same frames step the same count with and without the key changes");
				Assert.That(runner.Hold, Is.EqualTo(RunHold.None));
				Assert.That(view.Ranking.Considered, Is.GreaterThan(0), "the plotter watched the run's traffic");
				if (keys)
				{
					Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(Core.Navigation.CpaAlarmLimits.DefaultCpa), "back at the default after the key sequence");
					Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(Core.Navigation.CpaAlarmLimits.DefaultTcpa));
					Assert.That(view.AlarmMuted, Is.False);
					TestContext.WriteLine("alarms considered during the keyed run: " + view.Ranking.AlarmCount + " at the end, readout " + view.LimitsText);
				}

				runner.EndRun();
				Assert.That(runner.LastRecordingPath, Is.Not.Null, runner.RecordingError);
				if (keys) pathB = runner.LastRecordingPath; else pathA = runner.LastRecordingPath;
			}

			var a = RecordedRun.Load(pathA);
			var b = RecordedRun.Load(pathB);
			Assert.That(a.StateDigest, Is.Not.Null.And.Not.Empty);
			Assert.That(b.StateDigest, Is.EqualTo(a.StateDigest), "the alarm keys never change a recording");
			Assert.That(b.Rows.Count, Is.EqualTo(a.Rows.Count));
			Assert.That(b.StepCount, Is.EqualTo(a.StepCount));
			File.WriteAllText(Path.Combine(Folder, "recording-digest-alarm-keys.txt"), "without key changes " + pathA + "\n  state_digest " + a.StateDigest + "\nwith key changes " + pathB + "\n  state_digest " + b.StateDigest + "\nrows " + a.Rows.Count + " / " + b.Rows.Count + "\nsteps " + a.StepCount + " / " + b.StepCount + "\n");
			TestContext.WriteLine("state digests: " + a.StateDigest + " (no keys) / " + b.StateDigest + " (keys)");
		}
	}
}
