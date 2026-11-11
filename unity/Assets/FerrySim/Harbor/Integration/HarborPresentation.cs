using System;
using FerrySim.Audio;
using FerrySim.Controls;
using FerrySim.Prompts;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Harbor
{
	/// <summary>Harbor-only preferences, alarm actions and interior hints. Never changes recorded controls or physics.</summary>
	[DefaultExecutionOrder(90)]
	public sealed class HarborPresentation : MonoBehaviour
	{
		private VesselSimulationRunner _runner;
		private WheelhouseUI _ui;
		private CameraRig _rig;
		private WheelhouseCameraController _camera;
		private FerryAudioRig _audio;
		private WheelhouseView _view;
		private WheelhouseControlsView _interior;
		private long _runId = long.MinValue;
		private InputBindingService _bindings;
		private InputActionAsset _actions;
		private InputActionMap _alarms;
		private bool _typing;
		private bool _volumeOverride;
		private float _launchVolume;
		public KeybindPopups Hints { get; private set; }
		public InputActionMap AlarmMap => _alarms;

		public void Configure(VesselSimulationRunner runner, WheelhouseUI ui, CameraRig rig)
		{
			_runner = runner; _ui = ui; _rig = rig;
			_camera = rig.GetComponent<WheelhouseCameraController>();
			_bindings = InputBindingService.Instance;
			_actions = _bindings.CreateInstance();
			_alarms = _actions.FindActionMap("Alarms", true);
			_alarms.FindAction("Acknowledge", true).performed += Acknowledge;
			_alarms.FindAction("Silence", true).performed += Silence;
			_alarms.FindAction("LampTest", true).performed += LampOn;
			_alarms.FindAction("LampTest", true).canceled += LampOff;
			var args = Environment.GetCommandLineArgs();
			_volumeOverride = HasAudioOverride(args);
			_launchVolume = AudioLaunchOptions.MasterVolume(args);
			Hints = gameObject.AddComponent<KeybindPopups>();
			_runner.RunStarted += NewRun;
			Refresh();
		}

		public static bool HasAudioOverride(string[] args) => Array.IndexOf(args, AudioLaunchOptions.VolumeArgument) >= 0 ||
			Array.IndexOf(args, "-runTests") >= 0 || Array.IndexOf(args, "-ferrysim-smoke") >= 0;

		private void Acknowledge(InputAction.CallbackContext context) => ((IInstrumentSource)_runner)?.AcknowledgeAlarms();
		private void Silence(InputAction.CallbackContext context) => ((IInstrumentSource)_runner)?.SilenceAlarms();
		private void LampOn(InputAction.CallbackContext context) => ((IInstrumentSource)_runner)?.SetLampTest(true);
		private void LampOff(InputAction.CallbackContext context) => ((IInstrumentSource)_runner)?.SetLampTest(false);
		private void NewRun() { ((IInstrumentSource)_runner)?.SetLampTest(false); ApplySettings(); _runId = long.MinValue; }
		private void LateUpdate() => Refresh();

		public void Refresh()
		{
			if (_runner == null) return;
			var view = _ui != null ? _ui.View : null;
			if (!ReferenceEquals(view, _view))
			{
				DetachView(); _view = view; _typing = false;
				if (view != null)
				{
					view.Settings.Changed += SettingsChanged;
					view.TextEntryChanged += TypingChanged;
					view.ConnectBindings(_bindings);
					HarborPresentationOptions.Parse(Environment.GetCommandLineArgs()).Apply(view);
					Hints.AttachTo(_ui.Document);
				}
				ApplySettings();
			}
			var audio = _runner.GetComponentInChildren<FerryAudioRig>();
			if (audio != _audio)
			{
				if (_audio != null) _audio.MuteChangedFromKey -= SaveMute;
				_audio = audio;
				if (_audio != null) _audio.MuteChangedFromKey += SaveMute;
				ApplySettings();
			}
			if (_runId != _runner.RunId)
			{
				_runId = _runner.RunId;
				_interior = _runner.GetComponentInChildren<WheelhouseControlsView>();
				Hints.Bind(_rig.GetComponent<Camera>(), _interior, _bindings);
			}
			if (_view == null) return;
			Hints.Palette = _view.Palette;
			Hints.Suppressed = _view.OptionsVisible || _view.PickerVisible;
			var allowed = _rig.CurrentMode != CameraRig.Mode.FreeFly && !Hints.Suppressed && !_typing;
			if (allowed != _alarms.enabled) { if (allowed) _alarms.Enable(); else _alarms.Disable(); }
		}

		private void TypingChanged(bool typing) => _typing = typing;
		private void SaveMute(bool muted) { if (_view != null) _view.Settings.Muted = muted; }
		private void SettingsChanged(SettingsSection section) => ApplySettings();
		private void ApplySettings()
		{
			if (_view == null) return;
			var settings = _view.Settings;
			((IInstrumentSource)_runner)?.SetShallowWaterThreshold(settings.DepthAlarmMetres);
			if (_camera != null) _camera.LookSensitivity = settings.LookSensitivity;
			Hints.Mode = settings.KeybindHints;
			if (_audio == null) return;
			_audio.Bus.Master = _volumeOverride ? _launchVolume : settings.MasterVolume;
			_audio.Bus.Muted = settings.Muted;
			_audio.Bus.Alarms = settings.AlarmVolume;
		}

		private void DetachView()
		{
			if (_view == null) return;
			_view.Settings.Changed -= SettingsChanged;
			_view.TextEntryChanged -= TypingChanged;
		}
		private void OnDestroy()
		{
			DetachView();
			if (_audio != null) _audio.MuteChangedFromKey -= SaveMute;
			if (_runner != null) { _runner.RunStarted -= NewRun; ((IInstrumentSource)_runner).SetLampTest(false); }
			Hints?.Unbind();
			if (_actions != null) { _actions.Disable(); _bindings.Unregister(_actions); Destroy(_actions); }
		}
	}

	/// <summary>Presentation overrides are applied only to batch stores; volume never enters the saved store.</summary>
	public sealed class HarborPresentationOptions
	{
		public bool? ConsoleScreens { get; private set; }
		public string HudPreset { get; private set; }
		public static HarborPresentationOptions Parse(string[] args)
		{
			var result = new HarborPresentationOptions();
			if (Array.IndexOf(args, "-ferrysim-route") >= 0 || Array.IndexOf(args, "-ferrysim-smoke") >= 0)
			{ result.ConsoleScreens = false; result.HudPreset = "classic"; }
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == "-ferrysim-console-screens")
				{
					if (++i >= args.Length || (args[i] != "0" && args[i] != "1")) throw new ArgumentException("-ferrysim-console-screens requires 0 or 1");
					result.ConsoleScreens = args[i] == "1";
				}
				else if (args[i] == "-ferrysim-hud-preset")
				{
					if (++i >= args.Length || (args[i] != "classic" && args[i] != "console" && args[i] != "minimal" && args[i] != "everything"))
						throw new ArgumentException("-ferrysim-hud-preset requires classic, console, minimal or everything");
					result.HudPreset = args[i];
				}
			}
			return result;
		}
		public void Apply(WheelhouseView view)
		{
			if (ConsoleScreens.HasValue) view.Settings.ConsoleScreens = ConsoleScreens.Value;
			if (HudPreset != null) view.HudLayout.Choose(HudPreset);
		}
	}
}
