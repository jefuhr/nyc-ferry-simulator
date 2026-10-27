using System;
using FerrySim.Core.Contracts;
using FerrySim.Controls.Profiles;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	/// <summary>
	/// The wheelhouse controls for the bridge: keyboard, gamepad and HID devices through the "Helm" action map,
	/// smoothed once per physics step, handed to the runner as <see cref="ControlInputs"/>. The buttons pause and
	/// reset the run, cycle the camera and sound the horn.
	/// </summary>
	[DefaultExecutionOrder(-200)]
	[DisallowMultipleComponent]
	public sealed class HelmInputController : MonoBehaviour, IControlInputSource, IDebugReadoutSource
	{
		private const string KeyHelp = "keys: Q/A port  E/D stbd  W/S both  ←/→ helm steps  M midships  B bow  Shift+B auto bow  H horn  P pause  hold R reset  C camera";

		[Tooltip("The FerrySimControls asset (Helm map). A private copy is enabled at runtime.")]
		[SerializeField]
		private InputActionAsset actions;

		[Tooltip("Runner to pause and reset. Empty: the one on this GameObject.")]
		[SerializeField]
		private VesselSimulationRunner runner;

		[SerializeField]
		private ChaseCamera chaseCamera;

		[Tooltip("Keyboard and d-pad notches each way from neutral.")]
		[SerializeField]
		[Range(1, 20)]
		private int leverNotches = HelmInputMapper.DefaultLeverNotches;

		[Tooltip("Smoothing time constant for device noise, seconds; 0 turns smoothing off.")]
		[SerializeField]
		[Min(0f)]
		private float smoothingTimeConstant = (float)ControlInputSmoother.DefaultTimeConstant;

		private InputActionAsset _instance;
		private HelmInputMapper _mapper;
		private ControlInputSmoother _smoother;
		private DeviceProfileRouter _profileRouter;

		public DeviceProfileStore Profiles { get; private set; }

		/// <summary>The horn went on (true) or off (false).</summary>
		public event Action<bool> HornChanged;

		public HelmInputMapper Mapper => _mapper;

		public bool HornActive => _mapper != null && _mapper.HornActive;

		public string ReadoutLine => HornActive ? KeyHelp + "   HORN" : KeyHelp;

		public InputActionAsset Actions
		{
			get => actions;
			set => actions = value;
		}

		public ChaseCamera ChaseCamera
		{
			get => chaseCamera;
			set => chaseCamera = value;
		}

		public ControlInputs ControlsForStep(long step)
		{
			var raw = _mapper != null ? _mapper.Current : ControlInputs.Neutral;
			_smoother ??= new ControlInputSmoother(smoothingTimeConstant);
			if (step == 0)
			{
				// A new run starts without a smoothing transient.
				_smoother.Reset();
			}

			return _smoother.Step(raw);
		}

		private void Awake()
		{
			if (runner == null)
			{
				runner = GetComponent<VesselSimulationRunner>();
			}
		}

		private void OnEnable()
		{
			_smoother = new ControlInputSmoother(smoothingTimeConstant);
			if (actions == null)
			{
				Debug.LogWarning("[FerrySim] HelmInputController has no input actions asset; controls stay neutral.", this);
				return;
			}

			_instance = Instantiate(actions);
			_mapper = new HelmInputMapper(_instance, leverNotches);
			// One store belongs to this component for its lifetime, including disable/enable cycles.
			// An open calibration view keeps this same store and the new router subscribes to it.
			if (Profiles == null)
			{
				Profiles = new DeviceProfileStore(DeviceProfileStore.DefaultPath);
				Profiles.Load();
			}
			_profileRouter = new DeviceProfileRouter(_mapper, Profiles);
			if (!string.IsNullOrEmpty(Profiles.LastError)) Debug.LogWarning("[FerrySim] " + Profiles.LastError, this);
			_mapper.PauseRequested += OnPause;
			_mapper.ResetRequested += OnReset;
			_mapper.CameraCycleRequested += OnCameraCycle;
			_mapper.HornChanged += OnHorn;
			_mapper.Enable();
			if (runner != null)
			{
				runner.RunStarted += OnRunStarted;
			}
		}

		private void OnDisable()
		{
			_profileRouter?.Dispose();
			_profileRouter = null;
			if (runner != null)
			{
				runner.RunStarted -= OnRunStarted;
			}

			if (_mapper != null)
			{
				_mapper.PauseRequested -= OnPause;
				_mapper.ResetRequested -= OnReset;
				_mapper.CameraCycleRequested -= OnCameraCycle;
				_mapper.HornChanged -= OnHorn;
				_mapper.Dispose();
				_mapper = null;
			}

			if (_instance != null)
			{
				Destroy(_instance);
				_instance = null;
			}
		}

		private void OnValidate()
		{
			if (_smoother != null)
			{
				_smoother.TimeConstant = Math.Max(0f, smoothingTimeConstant);
			}
		}

		private void Update() => _mapper?.AdvanceHelmRepeat(Time.unscaledDeltaTime);

		private void OnRunStarted() => _mapper?.ResyncWithDevices();

		private void OnPause()
		{
			if (runner != null)
			{
				runner.TogglePause();
			}
		}

		private void OnReset()
		{
			if (runner != null)
			{
				runner.ResetRun();
			}
		}

		private void OnCameraCycle()
		{
			if (chaseCamera != null)
			{
				chaseCamera.CycleMode();
			}
		}

		private void OnHorn(bool active) => HornChanged?.Invoke(active);
	}
}
