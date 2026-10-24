using System;
using FerrySim.Core.Contracts;
using FerrySim.Controls.Profiles;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	/// <summary>
	/// Turns the "Helm" action map into lever and helm positions, and nothing else. Absolute axes (gamepad
	/// sticks and triggers, joystick and HID axes) set a lever or the helm directly; step actions (keyboard,
	/// d-pad) move a lever one notch per press, clicking through neutral, ahead and astern. Whichever device
	/// moved a control last wins. The values follow <see cref="ControlInputs"/>: +1 full ahead, −1 full astern,
	/// +1 helm hard over to starboard.
	/// </summary>
	public sealed class HelmInputMapper : IDisposable
	{
		public const string MapName = "Helm";

		/// <summary>Notches each way from neutral: five gives 0.2 per press.</summary>
		public const int DefaultLeverNotches = 5;

		private readonly InputActionMap _map;
		private readonly InputAction _port;
		private readonly InputAction _starboard;
		private readonly InputAction _both;
		private readonly InputAction _helm;
		private readonly InputAction _portStep;
		private readonly InputAction _starboardStep;
		private readonly InputAction _bothStep;
		private readonly InputAction _horn;
		private readonly InputAction _pause;
		private readonly InputAction _reset;
		private readonly InputAction _cameraCycle;
		private readonly int _notches;
		private double _portLever;
		private double _starboardLever;
		private double _helmOrder;
		private int _portStepHeld;
		private int _starboardStepHeld;
		private int _bothStepHeld;
		private bool _disposed;
		private DeviceProfileRouter _profileRouter;
		private readonly object[] _targetOwners = new object[3];

		public HelmInputMapper(InputActionAsset actions, int leverNotches = DefaultLeverNotches)
		{
			if (actions == null)
			{
				throw new ArgumentNullException(nameof(actions));
			}

			if (leverNotches < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(leverNotches), "At least one notch each way is needed.");
			}

			NeutralDeadzoneProcessor.Register();
			_notches = leverNotches;
			_map = actions.FindActionMap(MapName, throwIfNotFound: true);
			_port = Find("PortLever");
			_starboard = Find("StarboardLever");
			_both = Find("BothLevers");
			_helm = Find("Helm");
			_portStep = Find("PortLeverStep");
			_starboardStep = Find("StarboardLeverStep");
			_bothStep = Find("BothLeversStep");
			_horn = Find("Horn");
			_pause = Find("Pause");
			_reset = Find("Reset");
			_cameraCycle = Find("CameraCycle");

			_port.performed += OnPort;
			_port.canceled += OnPort;
			_starboard.performed += OnStarboard;
			_starboard.canceled += OnStarboard;
			_both.performed += OnBoth;
			_both.canceled += OnBoth;
			_helm.performed += OnHelm;
			_helm.canceled += OnHelm;
			_portStep.performed += OnPortStep;
			_portStep.canceled += OnPortStep;
			_starboardStep.performed += OnStarboardStep;
			_starboardStep.canceled += OnStarboardStep;
			_bothStep.performed += OnBothStep;
			_bothStep.canceled += OnBothStep;
			_horn.performed += OnHorn;
			_horn.canceled += OnHorn;
			_pause.performed += OnPause;
			_reset.performed += OnReset;
			_cameraCycle.performed += OnCameraCycle;
		}

		/// <summary>The horn button went down (true) or up (false).</summary>
		public event Action<bool> HornChanged;

		public event Action PauseRequested;

		/// <summary>Reset was held long enough (one second by default) to restart the run.</summary>
		public event Action ResetRequested;

		public event Action CameraCycleRequested;

		public InputActionMap Map => _map;

		public int LeverNotches => _notches;

		public double PortLever => _portLever;

		public double StarboardLever => _starboardLever;

		public double Helm => _helmOrder;

		public bool HornActive { get; private set; }

		/// <summary>The current lever and helm positions as the core's normalized inputs.</summary>
		public ControlInputs Current => new ControlInputs(_portLever, _starboardLever, _helmOrder);

		public void Enable()
		{
			_map.Enable();
			_profileRouter?.Resync();
		}

		public void Disable() => _map.Disable();

		/// <summary>
		/// Back to where the devices are now: stepped levers return to neutral, and absolute axes that are held
		/// somewhere (a throttle quadrant left at half ahead) are read again. Called when a run starts.
		/// </summary>
		public void ResyncWithDevices()
		{
			Array.Clear(_targetOwners, 0, _targetOwners.Length);
			_portLever = ReadAbsolute(_port, ProfileTarget.PortLever);
			_starboardLever = ReadAbsolute(_starboard, ProfileTarget.StarboardLever);
			var both = _both.ReadValue<float>();
			if (both != 0f)
			{
				if (_profileRouter?.OwnsInput(_both.activeControl, ProfileTarget.PortLever) != true) _portLever = Clamp(both);
				if (_profileRouter?.OwnsInput(_both.activeControl, ProfileTarget.StarboardLever) != true) _starboardLever = Clamp(both);
			}

			_helmOrder = ReadAbsolute(_helm, ProfileTarget.Helm);
			_profileRouter?.Resync();
		}

		/// <summary>
		/// The next notch above (<paramref name="direction"/> +1) or below (−1) <paramref name="value"/>, with
		/// <paramref name="notches"/> notches each way from neutral. A lever between notches (set by an analog
		/// device) moves to the nearest notch in that direction; the ends clamp at ±1.
		/// </summary>
		public static double StepNotch(double value, int direction, int notches)
		{
			const double Epsilon = 1e-9;
			var scaled = Clamp(value) * notches;
			var index = direction > 0 ? Math.Floor(scaled + Epsilon) + 1.0 : Math.Ceiling(scaled - Epsilon) - 1.0;
			index = Math.Max(-notches, Math.Min(notches, index));
			// Integer over integer: neutral is exactly 0 and the ends exactly ±1.
			return index / notches;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_profileRouter?.Dispose();
			_port.performed -= OnPort;
			_port.canceled -= OnPort;
			_starboard.performed -= OnStarboard;
			_starboard.canceled -= OnStarboard;
			_both.performed -= OnBoth;
			_both.canceled -= OnBoth;
			_helm.performed -= OnHelm;
			_helm.canceled -= OnHelm;
			_portStep.performed -= OnPortStep;
			_portStep.canceled -= OnPortStep;
			_starboardStep.performed -= OnStarboardStep;
			_starboardStep.canceled -= OnStarboardStep;
			_bothStep.performed -= OnBothStep;
			_bothStep.canceled -= OnBothStep;
			_horn.performed -= OnHorn;
			_horn.canceled -= OnHorn;
			_pause.performed -= OnPause;
			_reset.performed -= OnReset;
			_cameraCycle.performed -= OnCameraCycle;
			_map.Disable();
		}

		internal void AttachProfileRouter(DeviceProfileRouter router)
		{
			if (_profileRouter != null) throw new InvalidOperationException("A profile router is already attached.");
			_profileRouter = router;
		}
		internal void DetachProfileRouter(DeviceProfileRouter router)
		{
			if (_profileRouter == router) _profileRouter = null;
		}
		internal void ApplyProfileInput(ProfileTarget target, double value, object owner)
		{
			SetOwner(target, owner);
			switch (target)
			{
				case ProfileTarget.PortLever: _portLever = Clamp(value); break;
				case ProfileTarget.StarboardLever: _starboardLever = Clamp(value); break;
				case ProfileTarget.BothLevers: _portLever = _starboardLever = Clamp(value); break;
				case ProfileTarget.Helm: _helmOrder = Clamp(value); break;
			}
		}
		internal void ApplyProfileDelta(ProfileTarget target, double delta, object owner)
		{
			SetOwner(target, owner);
			switch (target)
			{
				case ProfileTarget.PortLever: _portLever = Clamp(_portLever + delta); break;
				case ProfileTarget.StarboardLever: _starboardLever = Clamp(_starboardLever + delta); break;
				case ProfileTarget.BothLevers:
					_portLever = Clamp(_portLever + delta); _starboardLever = Clamp(_starboardLever + delta); break;
				case ProfileTarget.Helm: _helmOrder = Clamp(_helmOrder + delta); break;
			}
		}
		private void SetOwner(ProfileTarget target, object owner)
		{
			if (target == ProfileTarget.PortLever || target == ProfileTarget.BothLevers) _targetOwners[0] = owner;
			if (target == ProfileTarget.StarboardLever || target == ProfileTarget.BothLevers) _targetOwners[1] = owner;
			if (target == ProfileTarget.Helm) _targetOwners[2] = owner;
		}
		internal void ReleaseProfileInput(object owner)
		{
			// Only release commands still owned by this route. A later keyboard/device order survives.
			for (var index = 0; index < _targetOwners.Length; index++)
			{
				if (!ReferenceEquals(_targetOwners[index], owner)) continue;
				var target = index == 0 ? ProfileTarget.PortLever : index == 1 ? ProfileTarget.StarboardLever : ProfileTarget.Helm;
				var action = index == 0 ? _port : index == 1 ? _starboard : _helm;
				var value = ReadAbsolute(action, target);
				var source = action.activeControl;
				if (index < 2 && _both.ReadValue<float>() != 0f && _profileRouter?.OwnsInput(_both.activeControl, target) != true)
				{
					value = Clamp(_both.ReadValue<float>()); source = _both.activeControl;
				}
				ApplyProfileInput(target, value, source);
			}
		}

		private bool ProfileOwns(InputAction.CallbackContext context, ProfileTarget target) => _profileRouter?.OwnsInput(context.control, target) == true;
		private double ReadAbsolute(InputAction action, ProfileTarget target) => _profileRouter?.OwnsInput(action.activeControl, target) == true
			? 0.0 : Clamp(action.ReadValue<float>());

		private InputAction Find(string name) => _map.FindAction(name, throwIfNotFound: true);

		private void OnPort(InputAction.CallbackContext context)
		{
			if (!ProfileOwns(context, ProfileTarget.PortLever)) { _portLever = Clamp(context.ReadValue<float>()); SetOwner(ProfileTarget.PortLever, context.control); }
		}

		private void OnStarboard(InputAction.CallbackContext context)
		{
			if (!ProfileOwns(context, ProfileTarget.StarboardLever)) { _starboardLever = Clamp(context.ReadValue<float>()); SetOwner(ProfileTarget.StarboardLever, context.control); }
		}

		private void OnBoth(InputAction.CallbackContext context)
		{
			var value = Clamp(context.ReadValue<float>());
			if (!ProfileOwns(context, ProfileTarget.PortLever)) { _portLever = value; SetOwner(ProfileTarget.PortLever, context.control); }
			if (!ProfileOwns(context, ProfileTarget.StarboardLever)) { _starboardLever = value; SetOwner(ProfileTarget.StarboardLever, context.control); }
		}

		private void OnHelm(InputAction.CallbackContext context)
		{
			if (!ProfileOwns(context, ProfileTarget.Helm)) { _helmOrder = Clamp(context.ReadValue<float>()); SetOwner(ProfileTarget.Helm, context.control); }
		}

		private void OnPortStep(InputAction.CallbackContext context)
		{
			if (TryStep(context, ref _portStepHeld, out var direction))
			{
				_portLever = StepNotch(_portLever, direction, _notches);
				SetOwner(ProfileTarget.PortLever, context.control);
			}
		}

		private void OnStarboardStep(InputAction.CallbackContext context)
		{
			if (TryStep(context, ref _starboardStepHeld, out var direction))
			{
				_starboardLever = StepNotch(_starboardLever, direction, _notches);
				SetOwner(ProfileTarget.StarboardLever, context.control);
			}
		}

		private void OnBothStep(InputAction.CallbackContext context)
		{
			if (TryStep(context, ref _bothStepHeld, out var direction))
			{
				_portLever = StepNotch(_portLever, direction, _notches);
				SetOwner(ProfileTarget.PortLever, context.control);
				_starboardLever = StepNotch(_starboardLever, direction, _notches);
				SetOwner(ProfileTarget.StarboardLever, context.control);
			}
		}

		/// <summary>One step per press: the step action's sign when it goes from released (or the other way) to pressed.</summary>
		private static bool TryStep(InputAction.CallbackContext context, ref int held, out int direction)
		{
			var value = context.canceled ? 0f : context.ReadValue<float>();
			var now = value > 0.5f ? 1 : value < -0.5f ? -1 : 0;
			direction = now;
			var stepped = now != 0 && now != held;
			held = now;
			return stepped;
		}

		private void OnHorn(InputAction.CallbackContext context)
		{
			var active = context.performed;
			if (active != HornActive)
			{
				HornActive = active;
				HornChanged?.Invoke(active);
			}
		}

		private void OnPause(InputAction.CallbackContext context) => PauseRequested?.Invoke();

		private void OnReset(InputAction.CallbackContext context) => ResetRequested?.Invoke();

		private void OnCameraCycle(InputAction.CallbackContext context) => CameraCycleRequested?.Invoke();

		private static double Clamp(double value) => double.IsNaN(value) ? 0.0 : Math.Max(-1.0, Math.Min(1.0, value));
	}

	/// <summary>
	/// Neutral noise filtering for axes with no device-level deadzone. Unlike axisDeadzone, it preserves
	/// the absolute lever/helm order outside the neutral band. Configure min on each trigger/twist binding.
	/// Stick bindings already have device processors and must not use this a second time.
	/// </summary>
	public sealed class NeutralDeadzoneProcessor : InputProcessor<float>
	{
		public float min = 0.05f;

		public override float Process(float value, InputControl control) => Math.Abs(value) <= min ? 0f : value;

#if UNITY_EDITOR
		[UnityEditor.InitializeOnLoadMethod]
#endif
		[UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
		internal static void Register()
		{
			if (InputSystem.TryGetProcessor("ferryNeutralDeadzone") != typeof(NeutralDeadzoneProcessor))
			{
				InputSystem.RegisterProcessor<NeutralDeadzoneProcessor>("ferryNeutralDeadzone");
			}
		}
	}
}
