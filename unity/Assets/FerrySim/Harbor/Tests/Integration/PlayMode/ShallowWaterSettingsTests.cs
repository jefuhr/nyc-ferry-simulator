using System.Collections;
using System.IO;
using FerrySim.Controls;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	// Failure modes: saved threshold never reaches C1, changes are ignored, a new model loses
	// preferences on reset/class switch, or a second composition keeps the old view binding.
	public class ShallowWaterSettingsTests
	{
		private float _volume;
		[SetUp]
		public void SetUp()
		{
			_volume = AudioListener.volume; AudioListener.volume = 0;
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			InputBindingService.SetSession(new InputBindingService(defaults)); Object.Destroy(defaults);
			UserSettingsFiles.ResetSession();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return HarborSceneFixture.Unload();
			Time.timeScale = 1; AudioListener.volume = _volume;
			UserSettingsFiles.ResetSession(); InputBindingService.ResetSession();
		}

		private static void Start(HarborBootstrap bootstrap, int vessel)
		{
			var catalog = ((HarborIntegrationServices)bootstrap.Services).HarborCatalog;
			var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay,
				HarborSceneFixture.BusiestLocalMinute, catalog.Vessels[vessel].Id, traffic: false);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			bootstrap.Runner.RecordRuns = false;
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			bootstrap.Runner.Pause();
			bootstrap.Runner.Driver.StepOnce();
		}

		private static void Phase(HarborBootstrap bootstrap, AlarmPhase expected)
		{
			Assert.That(bootstrap.Runner.HasInstruments, Is.True);
			Assert.That(bootstrap.Runner.Telemetry.DepthUnderKeel, Is.InRange(0, 1000));
			var readout = bootstrap.Runner.Instruments;
			var index = -1;
			for (var i = 0; i < readout.AlarmCount; i++) if (readout.AlarmId(i) == "navigation.shallow-water") index = i;
			Assert.That(index, Is.GreaterThanOrEqualTo(0));
			Assert.That(readout.Phase(index), Is.EqualTo(expected));
		}

		[UnityTest]
		public IEnumerator SavedThreshold_ReachesStartChangeResetClassSwitchAndSecondComposition()
		{
			HarborSceneFixture.RequireData();
			var file = Path.Combine(HarborSceneFixture.Repo, "artifacts/alarm-unify/settings-composition.json");
			var saved = new SettingsStore(file); saved.DepthAlarmMetres = 1000; saved.ConsoleScreens = false;
			for (var composition = 0; composition < 2; composition++)
			{
				UserSettingsFiles.SetSession(saved, HudLayoutStore.InMemory());
				yield return HarborSceneFixture.Load(false);
				var bootstrap = HarborSceneFixture.Bootstrap();
				Assert.That(bootstrap.WheelhouseUI.View.Settings, Is.SameAs(saved));
				Assert.That(saved.DepthAlarmMetres, Is.EqualTo(1000));
				Start(bootstrap, 0); Phase(bootstrap, AlarmPhase.Unacknowledged);
				bootstrap.Runner.AcknowledgeAlarms();
				saved.DepthAlarmMetres = 0;
				bootstrap.Runner.Driver.StepOnce(); Phase(bootstrap, AlarmPhase.Normal);
				saved.DepthAlarmMetres = 1000;
				bootstrap.Runner.Driver.StepOnce(); Phase(bootstrap, AlarmPhase.Unacknowledged);
				bootstrap.Runner.ResetRun(); bootstrap.Runner.Pause(); bootstrap.Runner.Driver.StepOnce();
				Phase(bootstrap, AlarmPhase.Unacknowledged);
				Start(bootstrap, 1); Phase(bootstrap, AlarmPhase.Unacknowledged);
				yield return HarborSceneFixture.Unload();
				saved = new SettingsStore(file);
				Assert.That(saved.Load(), Is.True, saved.LastError);
			}
		}
	}
}
