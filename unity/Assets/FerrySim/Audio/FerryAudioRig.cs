using System;
using FerrySim.Audio.Clips;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Traffic;
using FerrySim.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FerrySim.Audio
{
	/// <summary>Presentation only. Reads the panel's telemetry source and never writes controls, state, recordings or replay.</summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(200)]
	public sealed class FerryAudioRig : MonoBehaviour, IStepObserver
	{
		private VesselSimulationRunner _runner;
		private HelmInputController _helm;
		private CameraRig _camera;
		private SunController _sun;
		private Atmosphere _atmosphere;
		private TrafficView _traffic;
		private Func<UIDocument> _document;
		private FixedStepDriver _driver;
		private EngineVoices _port, _starboard;
		private AudioVoice _horn, _wash, _wind, _gull, _fender, _creak;
		private TrafficSignals _signals;
		private AlarmSounder _alarm;
		private InputBindingService _bindings;
		private InputActionAsset _actions;
		private AudioInputMapper _keys;
		private readonly HornEnvelope _envelope = new HornEnvelope();
		private readonly ContactSounds _contacts = new ContactSounds();
		private GullTiming _gulls;
		private ResolvedClip _gullClip, _fenderClip, _creakClip;
		private bool _configured, _subscribed;
		private double _idleSpeed, _ratedSpeed, _gearRatio;
		private float _fenderGain, _creakGain;

		public AudioBus Bus { get; } = new AudioBus();
		/// <summary>Raised only after an accepted mute key press; the settings owner persists the value.</summary>
		public event Action<bool> MuteChangedFromKey;
		public AudioSource AlarmSource => _alarm?.Source;
		public bool GraphEnabled { get; set; } = true;
		public HornState HornState => _envelope.State;
		public AudioSource HornSource => _horn?.Source;
		public AudioSource PortIdle => _port?.Idle;
		public AudioSource StarboardIdle => _starboard?.Idle;
		public Transform PortMount => _port?.Mount;
		public Transform StarboardMount => _starboard?.Mount;
		public int TrafficSoundingCount => _signals?.SoundingCount ?? 0;
		public int SourceCount => AudioVoiceBudget.Total - (Fog != null ? 0 : 1);
		public int DroppedClutchEvents => (_port?.Changes.Dropped ?? 0) + (_starboard?.Changes.Dropped ?? 0);
		private FogSignal Fog => _atmosphere != null ? _atmosphere.FogSignal : null;

		/// <summary>The two bootstraps call this once, without changing scene assets or assembly-layer ownership.</summary>
		public static FerryAudioRig Attach(VesselSimulationRunner runner, HelmInputController helm, CameraRig camera,
			SunController sun, TrafficView traffic, Func<UIDocument> document = null)
		{
			if (runner == null) throw new ArgumentNullException(nameof(runner));
			var rig = runner.GetComponentInChildren<FerryAudioRig>(true);
			if (rig != null) return rig;
			var host = new GameObject("Ferry audio"); host.transform.SetParent(runner.transform, false);
			rig = host.AddComponent<FerryAudioRig>(); rig.Configure(runner, helm, camera, sun, traffic, document); return rig;
		}

		private void Configure(VesselSimulationRunner runner, HelmInputController helm, CameraRig camera, SunController sun, TrafficView traffic, Func<UIDocument> document)
		{
			_runner = runner; _helm = helm; _camera = camera; _sun = sun; _traffic = traffic;
			_document = document;
			Bus.Master = AudioLaunchOptions.MasterVolume(System.Environment.GetCommandLineArgs());
			_port = new EngineVoices(transform, "port"); _starboard = new EngineVoices(transform, "starboard");
			_horn = new AudioVoice(transform, "held horn at masthead", "horn", range: 3000); _horn.Source.priority = 0;
			_wash = new AudioVoice(transform, "water wash", "water-wash");
			_wind = new AudioVoice(transform, "wind", "wind");
			_gull = new AudioVoice(transform, "landing gull", "gull", range: 500);
			_fender = new AudioVoice(transform, "fender contact", "fender");
			_creak = new AudioVoice(transform, "barge creak", "barge-creak");
			_signals = new TrafficSignals(transform);
			_alarm = new AlarmSounder(transform);
			_gullClip = AudioClipProvider.Shared.Get("gull"); _fenderClip = AudioClipProvider.Shared.Get("fender"); _creakClip = AudioClipProvider.Shared.Get("barge-creak");
			if (_helm != null && _helm.Actions != null)
			{
				_actions = Instantiate(_helm.Actions);
				_bindings = InputBindingService.Instance; _bindings.Register(_actions);
				_keys = new AudioInputMapper(_actions); _keys.MuteRequested += OnMuteRequested;
			}
			_configured = true;
			if (isActiveAndEnabled) Subscribe();
		}

		private void OnEnable() { if (_configured) Subscribe(); }
		private void Subscribe()
		{
			if (_subscribed) return;
			_subscribed = true;
			_runner.RunStarted += OnRunStarted; _runner.RunEnded += OnRunEnded;
			if (_helm != null) _helm.HornChanged += HoldHorn;
			_keys?.Enable();
			if (_runner.IsInitialized) OnRunStarted();
		}

		private void OnRunStarted()
		{
			DetachDriver(); Stop();
			_driver = _runner.Driver; _driver?.AddObserver(this);
			_port.Changes.Reset(_runner.Telemetry.Port.Gear); _starboard.Changes.Reset(_runner.Telemetry.Starboard.Gear);
			_contacts.Reset(); _signals.Reset(); _gulls = new GullTiming(0xf377a11u);
			_envelope.Reset();
			_idleSpeed = _runner.Parameters.Engines.IdleSpeed.Value;
			_ratedSpeed = _runner.Parameters.Machinery.RatedEngineSpeed.Value;
			_gearRatio = _runner.Parameters.Machinery.GearRatio.Value;
			var dimensions = GreyboxDimensions.From(_runner.Parameters);
			// Engine-room locations are rendering estimates, not vessel parameter changes or crew measurements.
			_port.Mount.localPosition = new Vector3((float)(-.25 * dimensions.Beam), -.4f, (float)(-.22 * dimensions.LengthOverall));
			_starboard.Mount.localPosition = new Vector3((float)(.25 * dimensions.Beam), -.4f, (float)(-.22 * dimensions.LengthOverall));
			var layout = VesselLightLayout.FromModel(_runner.transform, VesselLightLayout.ForGreybox(dimensions));
			_horn.Source.transform.localPosition = layout.Masthead;
			_wash.Source.transform.localPosition = new Vector3(0, 0, (float)(-.4 * dimensions.LengthOverall));
			_wind.Source.transform.localPosition = new Vector3(0, (float)dimensions.BridgeEyeHeight, (float)dimensions.BridgeEyeForward);
			_fender.Source.transform.localPosition = _creak.Source.transform.localPosition = new Vector3((float)(.5 * dimensions.Beam), 0, 0);
			_gull.Source.transform.localPosition = new Vector3(0, 15, 25);
			_alarm.Source.transform.localPosition = new Vector3(0, (float)dimensions.BridgeEyeHeight, (float)dimensions.BridgeEyeForward);
			_atmosphere = _sun != null ? _sun.GetComponent<Atmosphere>() : null;
			HoldHorn(_helm != null && _helm.HornActive);
		}

		public void HoldHorn(bool held) => _envelope.Hold(held);
		public void ToggleMute() => Bus.Muted = !Bus.Muted;
		private void OnMuteRequested()
		{
			var document = _document?.Invoke();
			var focus = document != null ? document.rootVisualElement?.focusController?.focusedElement as VisualElement : null;
			for (var element = focus; element != null; element = element.parent) if (element is TextField) return;
			ToggleMute(); MuteChangedFromKey?.Invoke(Bus.Muted);
		}
		private void LateUpdate() => Tick(Time.unscaledDeltaTime);

		/// <summary>All ongoing audio work, even at master level zero. No per-frame allocation, geodesy, scene search or simulation writes.</summary>
		public void Tick(double elapsedSeconds)
		{
			if (!_configured) return;
			if (_atmosphere == null && _sun != null) _atmosphere = _sun.GetComponent<Atmosphere>();
			var active = GraphEnabled && _runner.IsRunActive && _runner.HasTelemetry && _runner.Hold == RunHold.None;
			var paused = _runner.IsPaused;
			var wheelhouse = _camera != null && _camera.CurrentMode == CameraRig.Mode.Wheelhouse;
			var ear = ListeningPosition.ForWheelhouse(wheelhouse);
			var telemetry = _runner.Telemetry;
			var engines = Bus.Gain(AudioGroup.Engines);
			var port = EngineSoundMapping.Evaluate(telemetry.Port.EngineSpeed, _idleSpeed, _ratedSpeed, telemetry.Port.ShaftSpeed, _gearRatio, telemetry.Port.Throttle, telemetry.Port.Gear);
			var starboard = EngineSoundMapping.Evaluate(telemetry.Starboard.EngineSpeed, _idleSpeed, _ratedSpeed, telemetry.Starboard.ShaftSpeed, _gearRatio, telemetry.Starboard.Throttle, telemetry.Starboard.Gear);
			_port.Refresh(port, ear, engines, active, paused); _starboard.Refresh(starboard, ear, engines, active, paused);
			if (!active) _envelope.Reset();
			else if (!paused) _envelope.Advance(elapsedSeconds);
			var hornGain = Bus.Gain(AudioGroup.HornAndSignals);
			_horn.Level(hornGain * _envelope.Gain); _horn.Loop(active && _envelope.State != HornState.Off, paused);
			var fog = Fog;
			if (fog != null) fog.Source.volume = active && !paused ? hornGain * AudioClipProvider.Shared.Get("horn").Slot.baseGain *
				(float)TrafficSignalTiming.Envelope(fog.BlastSeconds) : 0;
			var listener = _camera != null ? _camera.transform.position : _runner.transform.position;
			_signals.Refresh(_traffic, listener, _runner.RenderTime, active && MeteorologicalVisibility.IsRestricted(RunVisibility.Of(_runner)), paused, hornGain);
			_alarm.Refresh(_runner, active, paused, wheelhouse, Bus.Gain(AudioGroup.Alarms));
			RefreshAmbience(active, paused, ear);
		}

		private void RefreshAmbience(bool active, bool paused, in ListeningPosition ear)
		{
			var gain = Bus.Gain(AudioGroup.Ambience);
			var telemetry = _runner.Telemetry;
			_wash.Level(gain * ear.WaterGain * EngineSoundMapping.Clamp(Math.Abs(telemetry.SpeedThroughWater) / 10));
			var state = _runner.State;
			var wind = _runner.HasEnvironment ? _runner.Environment.Wind : default;
			var cos = Math.Cos(state.Heading); var sin = Math.Sin(state.Heading);
			var north = wind.North - (state.Surge * cos - state.Sway * sin);
			var east = wind.East - (state.Surge * sin + state.Sway * cos);
			_wind.Level(gain * ear.WindGain * EngineSoundMapping.Clamp(Math.Sqrt(north * north + east * east) / 15));
			_wash.Loop(active, paused); _wind.Loop(active, paused);
			_gull.Level(gain); _fender.Level(gain * _fenderGain); _creak.Level(gain * _creakGain);
			_gull.Pause(paused); _fender.Pause(paused); _creak.Pause(paused);
			if (!active) { _gull.Stop(); _fender.Stop(); _creak.Stop(); return; }
			if (paused) return;
			var time = state.Time;
			var nearLanding = false;
			var stops = _runner.HarborRuntime?.Stops;
			if (stops != null) for (var i = 0; i < stops.Count; i++)
			{
				var n = stops[i].Position.North - state.North; var e = stops[i].Position.East - state.East;
				if (n * n + e * e <= 500 * 500) { nearLanding = true; break; }
			}
			if (_gulls != null && _gulls.Due(time, nearLanding)) _gull.Play(_gullClip, gain);
			var contact = _contacts.Update(_runner.Contact, time);
			var forceGain = (float)EngineSoundMapping.Clamp(Math.Sqrt(Math.Max(0, _runner.Contact.LastContactForce) / 100000));
			if ((contact & ContactSound.Fender) != 0) { _fenderGain = forceGain; _fender.Play(_fenderClip, gain * forceGain); }
			if ((contact & ContactSound.Creak) != 0) { _creakGain = forceGain; _creak.Play(_creakClip, gain * forceGain); }
		}

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }
		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{ if (GraphEnabled) { _port.Changes.Observe(telemetry.Port.Gear); _starboard.Changes.Observe(telemetry.Starboard.Gear); } }
		private void OnRunEnded() { DetachDriver(); Stop(); }
		private void DetachDriver() { _driver?.RemoveObserver(this); _driver = null; }
		private void Stop()
		{
			_port?.Stop(); _starboard?.Stop(); _horn?.Stop(); _wash?.Stop(); _wind?.Stop(); _gull?.Stop(); _fender?.Stop(); _creak?.Stop(); _signals?.Stop(); _alarm?.Reset();
		}
		private void OnDisable()
		{
			if (!_configured) return;
			if (_subscribed)
			{
				_runner.RunStarted -= OnRunStarted; _runner.RunEnded -= OnRunEnded;
				if (_helm != null) _helm.HornChanged -= HoldHorn;
				_subscribed = false;
			}
			_keys?.Disable(); DetachDriver(); Stop();
			if (Fog != null) Fog.Source.volume = 0;
		}
		private void OnDestroy()
		{
			_keys?.Dispose();
			_bindings?.Unregister(_actions);
			if (_actions == null) return;
			if (Application.isPlaying) Destroy(_actions); else DestroyImmediate(_actions);
		}
	}
}
