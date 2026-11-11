using System;
using System.Collections;
using System.IO;
using FerrySim.Audio.Clips;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Audio.Tests.PlayMode
{
	/// <summary>Real harbor composition, additive scene lifecycle, mute-independent rendering and byte-identical recording.</summary>
	public class HarborAudioTests
	{
		private const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		private HarborBootstrap _bootstrap;
		private FerryAudioRig _audio;
		private float _listenerVolume;
		private double? _savedDepthAlarmMetres;
		private InputSettings _savedInputSettings, _testInputSettings;
		private HideFlags _savedInputFlags;
		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static string Evidence => Path.Combine(Repo, "artifacts/sound-gaps/playmode");

		[UnitySetUp]
		public IEnumerator Load()
		{
			_listenerVolume = AudioListener.volume;
			// Full-master assertions exercise the graph without sounding the Mac's speakers.
			AudioListener.volume = 0;
			// Keep the real Input System runtime for a serialized real scene. InputTestFixture's replacement runtime
			// cannot retain that scene's existing action states. A transient settings clone routes injected keys in batch mode.
			_savedInputSettings = InputSystem.settings;
			_savedInputFlags = _savedInputSettings.hideFlags;
			// InputManager deletes its previous HideAndDontSave settings on replacement. Preserve the temporary
			// original for restoration instead of leaving the manager pointing at a destroyed clone after teardown.
			if (_savedInputFlags == HideFlags.HideAndDontSave) _savedInputSettings.hideFlags = HideFlags.DontSave;
			_testInputSettings = Object.Instantiate(_savedInputSettings);
			_testInputSettings.hideFlags = HideFlags.HideAndDontSave;
			_testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
			_testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
			InputSystem.settings = _testInputSettings;
			var harbor = Path.Combine(Repo, "data/processed/harbor");
			foreach (var part in new[] { "runtime", "env/days", "traffic/2025-07-16", "3dtiles", "3dtiles-buildings" })
				if (!Directory.Exists(Path.Combine(harbor, part))) Assert.Ignore("Real harbor data not copied: " + Path.Combine(harbor, part));
			Directory.CreateDirectory(Evidence);
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			Assert.Fail("This test loads the checked-in editor scene.");
#endif
			yield return null;
			_bootstrap = Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(_bootstrap, Is.Not.Null);
			Assert.That(_bootstrap.Problem, Is.Null);
			_audio = _bootstrap.Runner.GetComponentInChildren<FerryAudioRig>();
			Assert.That(_audio, Is.Not.Null, "the runtime bootstrap creates the rig");
			_bootstrap.Runner.RecordRuns = false;
		}

		[UnityTearDown]
		public IEnumerator Unload()
		{
			Time.timeScale = 1;
			if (_bootstrap != null) _bootstrap.Runner.EndRun();
			if (_savedDepthAlarmMetres.HasValue && _bootstrap != null && _bootstrap.WheelhouseUI.View != null)
				_bootstrap.WheelhouseUI.View.Settings.DepthAlarmMetres = _savedDepthAlarmMetres.Value;
			_savedDepthAlarmMetres = null;
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
			AudioListener.volume = _listenerVolume;
			if (_savedInputSettings != null) { InputSystem.settings = _savedInputSettings; _savedInputSettings.hideFlags = _savedInputFlags; }
			if (_testInputSettings != null) Object.Destroy(_testInputSettings);
		}

		private IEnumerator Start(bool fog = false, bool alarm = false)
		{
			var catalog = _bootstrap.Services.Catalog;
			var choice = new ScenarioChoice("87", "2025-07-16", 738, catalog.Vessels[0].Id, traffic: true);
			Assert.That(catalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			settings.visibilityMetres = fog ? 500 : MeteorologicalVisibility.Unlimited;
			if (alarm)
			{
				// Raise the real C1 alarm through the player's setting, which overrides the class-file default.
				_savedDepthAlarmMetres = _bootstrap.WheelhouseUI.View.Settings.DepthAlarmMetres;
				_bootstrap.WheelhouseUI.View.Settings.DepthAlarmMetres = 1000;
			}
			Assert.That(_bootstrap.TryStart(settings, out problem), Is.True, problem);
			_bootstrap.WheelhouseUI.View.PickerVisible = false;
			var started = Time.realtimeSinceStartupAsDouble;
			while (_bootstrap.ViewState != HarborViewState.Ready)
			{
				Assert.That(_bootstrap.Problem, Is.Null);
				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(90), "initial harbor view");
				yield return null;
			}
			_bootstrap.Runner.enabled = false;
			// OnDisable ends the runner; initialize a fresh deterministic start after preventing automatic frame advance.
			Assert.That(_bootstrap.TryStart(settings, out problem), Is.True, problem);
			_bootstrap.WheelhouseUI.View.PickerVisible = false;
			_bootstrap.Runner.Pause();
			for (var i = 0; i < 15; i++) _bootstrap.RefreshView(Time.realtimeSinceStartupAsDouble);
			Assert.That(_bootstrap.ViewState, Is.EqualTo(HarborViewState.Ready));
			// The bootstrap re-enables on start, so keep automatic updates frozen with timeScale while driving manually.
			Time.timeScale = 0;
			_bootstrap.Runner.Resume();
			_bootstrap.Runner.AdvanceFrame(.02);
			_audio.Tick(.02);
			if (alarm)
			{
				Assert.That(_bootstrap.Runner.HasInstruments, Is.True);
				Assert.That(_bootstrap.Runner.Instruments.SounderOn(AlarmGroup.Navigation), Is.True);
				Assert.That(_audio.AlarmSource.isPlaying, Is.True, "real runner drives the alarm voice even at master zero");
			}
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator RigMountsHeldHornFogScheduleAndListeningPositionAreWired()
		{
			yield return Start(true);
			var runner = _bootstrap.Runner;
			var fog = _bootstrap.Context.Sun.GetComponent<Atmosphere>().FogSignal;
			var dimensions = GreyboxDimensions.From(runner.Parameters);
			Assert.That(_audio.PortMount.localPosition.x, Is.EqualTo((float)(-.25 * dimensions.Beam)).Within(1e-5));
			Assert.That(_audio.StarboardMount.localPosition.x, Is.EqualTo((float)(.25 * dimensions.Beam)).Within(1e-5));
			Assert.That(_audio.HornSource.transform.localPosition, Is.EqualTo(fog.transform.localPosition));
			Assert.That(_audio.HornSource.clip, Is.SameAs(fog.Source.clip));
			Assert.That(fog.Source.clip, Is.SameAs(AudioClipProvider.Shared.Get("horn").Clip));
			Assert.That(runner.GetComponentsInChildren<AudioSource>(true).Length, Is.EqualTo(AudioVoiceBudget.Total));
			Assert.That(_audio.SourceCount, Is.LessThanOrEqualTo(32));
			_audio.Bus.Master = 0;
			var keyboard = InputSystem.AddDevice<Keyboard>();
			try
			{
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.H)); InputSystem.Update(); _audio.Tick(.08);
				Assert.That(keyboard.hKey.isPressed, Is.True, "injected H event was processed");
				Assert.That(_audio.HornState, Is.EqualTo(HornState.Sustain), "H reaches the rig through HelmInputController.HornChanged");
				InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
			}
			finally { InputSystem.RemoveDevice(keyboard); }
			Assert.That(_audio.HornState, Is.EqualTo(HornState.Release));
			Assert.That(_audio.HornSource.isPlaying, Is.True, "held horn plays even at master zero");
			Assert.That(_audio.PortIdle.isPlaying, Is.True);
			runner.Pause(); _audio.Tick(.1);
			Assert.That(_audio.PortIdle.isPlaying, Is.False, "pausing freezes the source graph");
			runner.Resume(); _audio.Tick(.01);
			Assert.That(_audio.PortIdle.isPlaying, Is.True);
			_audio.Tick(.15);
			Assert.That(_audio.HornState, Is.EqualTo(HornState.Off));
			Assert.That(_audio.HornSource.isPlaying, Is.False);
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse; _audio.Tick(.016);
			Assert.That(_audio.PortIdle.GetComponent<AudioLowPassFilter>().cutoffFrequency, Is.EqualTo(850));
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase; _audio.Tick(.016);
			Assert.That(_audio.PortIdle.GetComponent<AudioLowPassFilter>().cutoffFrequency, Is.EqualTo(22000));
			Assert.That(fog.Recorder.Events, Has.Count.EqualTo(1));
			Assert.That(fog.Recorder.Events[0].Step, Is.Zero);
			for (var i = 0; i < 12000; i++) runner.AdvanceFrame(.01);
			fog.Refresh(); _audio.Tick(.016);
			Assert.That(fog.Recorder.Events, Has.Count.EqualTo(2));
			Assert.That(fog.Recorder.Events[1].Step, Is.EqualTo(FogSignalSchedule.IntervalSteps));
			Assert.That(_audio.TrafficSoundingCount, Is.LessThanOrEqualTo(AudioVoiceBudget.Traffic));
			File.WriteAllText(Path.Combine(Evidence, "wiring.txt"), "sources=" + _audio.SourceCount + "\nhorn shares fog clip=true\nhorn plays at volume 0=true\nfog steps=0,12000\n");
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator MuteKeyWorksWithHelmHeldButDoesNotToggleWhileTypingInThePicker()
		{
			yield return Start();
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var field = new TextField("audio typing fixture");
			try
			{
				_bootstrap.WheelhouseUI.View.PickerVisible = true;
				_bootstrap.RefreshControls();
				_bootstrap.WheelhouseUI.Document.rootVisualElement.focusController.focusedElement?.Blur();
				_audio.Bus.Muted = false;
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.V)); InputSystem.Update();
				Assert.That(keyboard.vKey.isPressed, Is.True, "injected V event was processed");
				Assert.That(_audio.Bus.Muted, Is.True, "independent Audio map works while the picker holds Helm");
				InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
				_bootstrap.WheelhouseUI.Document.rootVisualElement.Add(field); field.Focus();
				yield return null;
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.V)); InputSystem.Update();
				Assert.That(_audio.Bus.Muted, Is.True, "typing a landing name cannot change mute");
			}
			finally { field.RemoveFromHierarchy(); InputSystem.RemoveDevice(keyboard); }
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator PreparedMonoClipsKeepTheirLevelAfterUnityImport()
		{
			yield return Start();
			var lines = "slot,prepared_rms,imported_rms,ratio\n";
			foreach (var id in new[] { "engine-idle", "clutch-engage", "clutch-disengage", "alarm-steering" })
			{
				var resolved = AudioClipProvider.Shared.Get(id);
				Assert.That(resolved.IsPlaceholder, Is.False, id);
				var clip = resolved.Clip;
				Assert.That(clip.loadType, Is.EqualTo(AudioClipLoadType.DecompressOnLoad), id);
				var imported = new float[clip.samples];
				Assert.That(clip.GetData(imported, 0), Is.True, id);
				var bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "FerrySim/Audio/Clips", id + ".wav"));
				// audio-prep writes a canonical 44-byte PCM16 mono header, independently checked by its tests.
				Assert.That(bytes.Length, Is.EqualTo(44 + imported.Length * 2), id);
				double preparedEnergy = 0, importedEnergy = 0;
				for (var i = 0; i < imported.Length; i++)
				{
					var prepared = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768.0;
					preparedEnergy += prepared * prepared;
					importedEnergy += imported[i] * imported[i];
				}
				var preparedRms = Math.Sqrt(preparedEnergy / imported.Length);
				var importedRms = Math.Sqrt(importedEnergy / imported.Length);
				var ratio = importedRms / preparedRms;
				lines += FormattableString.Invariant($"{id},{preparedRms:R},{importedRms:R},{ratio:R}\n");
				File.WriteAllText(Path.Combine(Evidence, "import-levels.csv"), lines);
				Assert.That(ratio, Is.InRange(.85, 1.15), id + " codec tolerance; importing must not peak-normalize the prepared mono signal");
			}
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator RefreshAndStepObserverAllocateNothingWithCalibratedPositiveControl()
		{
			yield return Start(fog: true, alarm: true);
			_audio.Bus.Master = 0; _audio.HoldHorn(true); _audio.Tick(.1);
			var runner = _bootstrap.Runner;
			for (var i = 0; i < 30; i++) { runner.AdvanceFrame(.01); _audio.Tick(.01); }
			var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			var chars = new char[16];
			recorder.enabled = true; var positive = new string(chars); recorder.enabled = false;
			var control = recorder.sampleBlockCount; GC.KeepAlive(positive);
			Assert.That(control, Is.EqualTo(1), "exactly one deliberate string allocation");
			var allocations = 0;
			try
			{
				for (var i = 0; i < 240; i++)
				{
					var telemetry = runner.Telemetry; var state = runner.State;
					recorder.enabled = true;
					_audio.AfterStep(i, state, telemetry); _audio.Tick(.016);
					recorder.enabled = false; allocations += recorder.sampleBlockCount;
				}
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
			Assert.That(_audio.AlarmSource.isPlaying, Is.True, "alarm stayed sounding across the measurement");
			File.WriteAllText(Path.Combine(Evidence, "gc-alloc.txt"), "positive control=" + control + "\n240 Tick + AfterStep calls with alarm sounding=" + allocations + " GC.Alloc samples\n");
			Assert.That(allocations, Is.Zero);
			runner.Pause(); _audio.Tick(.016); Assert.That(_audio.AlarmSource.isPlaying, Is.False);
			runner.Resume(); _audio.Tick(.016); Assert.That(_audio.AlarmSource.isPlaying, Is.True);
			runner.SilenceAlarms(); _audio.Tick(.016); Assert.That(_audio.AlarmSource.isPlaying, Is.False);
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator RecordingsAndStateDigestsAreByteIdenticalAtFullZeroMutedAndDisabledAudio()
		{
			yield return Start(fog: true, alarm: true);
			var runner = _bootstrap.Runner;
			var settings = runner.Scenario;
			var folder = Path.Combine(Evidence, "recordings", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
			byte[] first = null; string firstInputs = null, digest = null;
			System.Collections.Generic.IReadOnlyList<string> firstRows = null;
			var lines = "fixed 600 frames at 1/60 s; same controls; AudioListener output muted for speaker safety\n";
			foreach (var mode in new[] { "full", "zero", "muted", "disabled" })
			{
				runner.EndRun(); runner.RecordRuns = true; runner.RecordingDirectory = Path.Combine(folder, mode);
				_audio.GraphEnabled = mode != "disabled"; _audio.Bus.Master = AudioLaunchOptions.MasterVolume(new[] { "-ferrysim-audio-volume", mode == "zero" ? "0" : "1" });
				_audio.Bus.Muted = mode == "muted";
				Assert.That(_bootstrap.TryStart(settings, out var problem), Is.True, problem);
				_bootstrap.WheelhouseUI.View.PickerVisible = false;
				runner.Driver.Controls = new ConstantControls(new ControlInputs(.4, .4, .1));
				// The production recorder's artifact filenames contain wall time and run number. A second production
				// RunRecorder with stable artifact names proves complete byte parity without normalizing any state data.
				var canonicalTelemetry = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
				var canonicalInputs = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
				var header = RunRecorder.BuildHeader(settings, runner.Parameters, runner.Driver.Simulation, runner.Factory.GetType().FullName,
					runner.State, runner.RunEnvironment, runner.TelemetrySampleHz);
				header.AddRange(RunRecorder.TrafficHeader(runner.Traffic));
				var canonical = new RunRecorder(canonicalTelemetry, canonicalInputs, runner.TelemetrySampleHz, header);
				runner.Driver.AddObserver(canonical);
				runner.Resume();
				for (var i = 0; i < 600; i++) { runner.AdvanceFrame(1.0 / 60); _audio.Tick(1.0 / 60); }
				Assert.That(_audio.AlarmSource.isPlaying, Is.EqualTo(mode != "disabled"), mode + " alarm voice");
				canonical.Finish(runner.Driver.StepCount, runner.State, runner.Telemetry);
				runner.Driver.RemoveObserver(canonical);
				var bytes = System.Text.Encoding.UTF8.GetBytes(canonicalTelemetry.ToString());
				var inputs = canonicalInputs.ToString();
				File.WriteAllBytes(Path.Combine(runner.RecordingDirectory, "canonical.csv"), bytes);
				File.WriteAllText(Path.Combine(runner.RecordingDirectory, "canonical.inputs.csv"), inputs);
				runner.EndRun();
				Assert.That(_audio.AlarmSource.isPlaying, Is.False, "run end stops alarm immediately");
				Assert.That(runner.RecordingError, Is.Null);
				var path = runner.LastRecordingPath; var recorded = RecordedRun.Load(path);
				if (first == null) { first = bytes; firstInputs = inputs; digest = recorded.StateDigest; firstRows = recorded.Rows; }
				else
				{
					Assert.That(bytes, Is.EqualTo(first), mode + " complete stable-name RunRecorder bytes"); Assert.That(inputs, Is.EqualTo(firstInputs));
					Assert.That(recorded.Rows, Is.EqualTo(firstRows), "actual runner recording rows"); Assert.That(recorded.StateDigest, Is.EqualTo(digest));
				}
				lines += mode + ": " + recorded.StateDigest + " bytes=" + bytes.Length + " rows=" + recorded.Rows.Count + "\n";
			}
			File.WriteAllText(Path.Combine(Evidence, "recording-equivalence.txt"), lines);
		}
	}
}
