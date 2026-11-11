using System;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;
using FerrySim.Controls;

namespace FerrySim.World
{
	/// <summary>
	/// Puts the camera at a <see cref="WheelhouseCameraMount"/> each frame, with head look on the right
	/// mouse button (middle button resets it) inside the mount's limits.
	/// </summary>
	[DefaultExecutionOrder(CameraAnchor.ControllerOrder)]
	[DisallowMultipleComponent]
	[RequireComponent(typeof(Camera))]
	public sealed class WheelhouseCameraController : MonoBehaviour
	{
		[SerializeField] private WheelhouseCameraMount mount;
		[Range(0.01f, 2f)]
		[SerializeField] private float lookSensitivity = 0.15f;

		/// <summary>Smallest and largest head-look sensitivity a setting may ask for, degrees per unit of look input.</summary>
		public const float MinLookSensitivity = 0.01f;
		public const float MaxLookSensitivity = 2f;

		/// <summary>
		/// Head-look sensitivity, degrees per unit of look input. A viewer preference, never recorded. Values are
		/// clamped to the setting's range; a value that is not a positive finite number is ignored.
		/// </summary>
		public float LookSensitivity
		{
			get => lookSensitivity;
			set
			{
				if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return;
				lookSensitivity = Mathf.Clamp(value, MinLookSensitivity, MaxLookSensitivity);
			}
		}

		private float _yaw;
		private float _pitch;
		private CesiumGlobeAnchor _anchor;
		private CameraActionReader _input;
		private VesselSimulationRunner _runner;
		private Transform _mountParent;
		private VesselParameters _parameters;
		private readonly ContactSegment[] _faces = new ContactSegment[1024];
		private readonly BowLookState _bow = new BowLookState();
		private float _bowPitch;
		private float _bowYaw;
		private double _bowForward;
		private float _shownYaw;
		private float _shownPitch;
		private static readonly string[] ScreenIds = { "RadarScreen", "PlotterScreen", "PortEngineScreen", "StarboardEngineScreen" };
		private readonly Transform[] _screens = new Transform[4];
		private readonly Transform[] _focusEyes = new Transform[4];
		private Transform _interior;
		private int _focusIndex = -1;
		private Vector3 _shownOffset;
		private Quaternion _shownFocusRotation = Quaternion.identity;

		/// <summary>The focused screen mount id, or null at the seat/bow. Presentation only.</summary>
		public string FocusedScreen => _focusIndex < 0 ? null : ScreenIds[_focusIndex];
		/// <summary>Raised once when focus changes, including null on recentre, bow view or a new run.</summary>
		public event Action<string> FocusChanged;

		/// <summary>Per-session preference, shared across camera/scene replacements; never recorded.</summary>
		public static bool AutomaticBowEnabled { get; set; } = true;
		public bool ShowingBow => _focusIndex < 0 && _bow.ShowBow;
		public float BowPitch => _bowPitch;
		public void ToggleBowView()
		{
			if (_focusIndex >= 0) { SetFocus(-1); _bow.HandLook(); }
			_bow.ToggleManual();
		}

		/// <summary>Seat, radar, plotter, port engine, starboard engine, seat. Missing interiors are a no-op.</summary>
		public void CycleFocus()
		{
			if (!CacheFocusMounts()) return;
			_bow.HandLook();
			SetFocus(_focusIndex == ScreenIds.Length - 1 ? -1 : _focusIndex + 1);
		}

		public void Recentre() { SetFocus(-1); _bow.HandLook(); SetHeadLook(0, 0); }

		private void SetFocus(int index)
		{
			if (_focusIndex == index) return;
			_focusIndex = index;
			_yaw = _pitch = 0;
			FocusChanged?.Invoke(FocusedScreen);
		}

		private bool CacheFocusMounts()
		{
			if (mount == null || mount.transform.parent == null) return false;
			var interior = mount.transform.parent.Find(WheelhouseControlsView.RootName);
			if (interior == null || !interior.gameObject.activeInHierarchy) return false;
			if (_interior == interior && _screens[0] != null && _focusEyes[0] != null) return true;
			for (var i = 0; i < ScreenIds.Length; i++)
			{
				_screens[i] = interior.Find(ScreenIds[i]);
				_focusEyes[i] = interior.Find(ScreenIds[i] + "FocusEye");
				if (_screens[i] == null || _focusEyes[i] == null) return false;
			}
			_interior = interior;
			return true;
		}
		public void ToggleAutomaticBow() => AutomaticBowEnabled = !AutomaticBowEnabled;

		public WheelhouseCameraMount Mount
		{
			get => mount;
			set
			{
				mount = value;
				BindRunner();
			}
		}

		/// <summary>Head look relative to the mount, degrees (yaw right positive, pitch down positive).</summary>
		public Vector2 HeadLook
		{
			get => new Vector2(_yaw, _pitch);
			set { _bow.HandLook(); SetHeadLook(value.x, value.y); }
		}

		private CesiumGlobeAnchor Anchor => _anchor != null ? _anchor : _anchor = GetComponent<CesiumGlobeAnchor>();

		/// <summary>Moves the camera to the mount now. Returns false without a mount.</summary>
		public bool Snap()
		{
			_shownYaw = _yaw; _shownPitch = _pitch;
			FocusPose(out _shownOffset, out _shownFocusRotation);
			return Follow();
		}

		private void BindRunner()
		{
			if (_runner != null) _runner.RunStarted -= ResetBow;
			_mountParent = mount != null ? mount.transform.parent : null;
			_runner = mount != null ? mount.GetComponentInParent<VesselSimulationRunner>() : null;
			if (_runner != null) _runner.RunStarted += ResetBow;
			ResetBow();
		}

		private void Awake() => BindRunner();
		private void OnEnable() => _input = new CameraActionReader();
		private void OnDisable() { _input?.Dispose(); _input = null; }
		private void OnDestroy() { if (_runner != null) _runner.RunStarted -= ResetBow; }
		private void ResetBow()
		{
			_bow.Reset(); _parameters = null;
			SetFocus(-1); _interior = null;
			_shownOffset = Vector3.zero; _shownFocusRotation = Quaternion.identity;
			_yaw = _pitch = _shownYaw = _shownPitch = 0;
		}

		private void RefreshApproach()
		{
			if (mount != null && mount.transform.parent != _mountParent) BindRunner();
			if (_runner == null || _runner.Parameters == null || mount == null) return;
			var dimensions = GreyboxDimensions.From(_runner.Parameters);
			if (!ReferenceEquals(_parameters, _runner.Parameters))
			{
				_parameters = _runner.Parameters;
				_bowForward = dimensions.LengthOverall * .45;
			}
			// Use the model eye, including a changed seated/standing pose. Keep the legacy fallback's
			// double subtraction before float conversion so its direction remains exactly unchanged.
			var eye = mount.transform.localPosition;
			var greyboxEye = new Vector3(0, (float)dimensions.BridgeEyeHeight, (float)dimensions.BridgeEyeForward);
			var offset = eye.Equals(greyboxEye)
				? new Vector3(0, (float)(dimensions.Freeboard - dimensions.BridgeEyeHeight), (float)(_bowForward - dimensions.BridgeEyeForward))
				: new Vector3(-eye.x, (float)(dimensions.Freeboard - eye.y), (float)(_bowForward - eye.z));
			// The mount already has a down tilt; express the eye-to-deck ray in its frame before adding head look.
			var direction = Quaternion.Inverse(mount.transform.localRotation) * offset;
			_bowYaw = Mathf.Clamp(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, mount.YawLimits.x, mount.YawLimits.y);
			_bowPitch = Mathf.Clamp(Mathf.Atan2(-direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg,
				mount.PitchLimits.x, mount.PitchLimits.y);
			var state = _runner.State;
			var distance = BowLookState.FaceDistance(mount.ApproachFaces, _faces, state.North, state.East, state.Heading, _bowForward);
			_bow.UpdateApproach(distance, System.Math.Sqrt(state.Surge * state.Surge + state.Sway * state.Sway), AutomaticBowEnabled && _focusIndex < 0);
		}

		private void SetHeadLook(float yaw, float pitch)
		{
			if (_focusIndex >= 0)
			{
				var cone = Vector2.ClampMagnitude(new Vector2(yaw, pitch), 15);
				_yaw = cone.x; _pitch = cone.y; return;
			}
			if (mount == null)
			{
				_yaw = yaw;
				_pitch = pitch;
				return;
			}

			_yaw = Mathf.Clamp(yaw, mount.YawLimits.x, mount.YawLimits.y);
			_pitch = Mathf.Clamp(pitch, mount.PitchLimits.x, mount.PitchLimits.y);
		}

		private void LateUpdate()
		{
			RefreshApproach();
			if (_input != null)
			{
				if (_input[ActionIds.BowView].WasPressedThisFrame()) ToggleBowView();
				if (_input[ActionIds.FocusCycle].WasPressedThisFrame()) CycleFocus();
				if (_input[ActionIds.BowAutoToggle].WasPressedThisFrame()) ToggleAutomaticBow();
				if (_input[ActionIds.LookHold].IsPressed())
				{
					if (ShowingBow) { _yaw = _shownYaw; _pitch = _shownPitch; }
					_bow.HandLook();
					var delta = _input[ActionIds.LookDelta].ReadValue<Vector2>();
					SetHeadLook(_yaw + delta.x * lookSensitivity, _pitch - delta.y * lookSensitivity);
				}
				if (_input[ActionIds.Recentre].WasPressedThisFrame())
				{
					Recentre();
				}
			}

			AdvancePose(Time.unscaledDeltaTime);
		}

		/// <summary>Advances the render-only 0.25 s easing. Exposed for deterministic presentation tests/capture.</summary>
		public void AdvancePresentation(float deltaTime) { RefreshApproach(); AdvancePose(deltaTime); }

		private void AdvancePose(float deltaTime)
		{
			if (_focusIndex >= 0 && (_screens[_focusIndex] == null || _focusEyes[_focusIndex] == null)) Recentre();
			var showBow = ShowingBow;
			var blend = 1f - Mathf.Exp(-Mathf.Max(0, deltaTime) / .25f);
			_shownYaw = Mathf.Lerp(_shownYaw, showBow ? _bowYaw : _yaw, blend);
			_shownPitch = Mathf.Lerp(_shownPitch, showBow ? _bowPitch : _pitch, blend);
			FocusPose(out var position, out var rotation);
			_shownOffset = Vector3.Lerp(_shownOffset, position, blend);
			_shownFocusRotation = Quaternion.Slerp(_shownFocusRotation, rotation, blend);
			Follow();
		}

		private void FocusPose(out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero; rotation = Quaternion.identity;
			if (mount == null || _focusIndex < 0 || _focusEyes[_focusIndex] == null || _screens[_focusIndex] == null) return;
			var eye = _focusEyes[_focusIndex];
			position = mount.transform.InverseTransformPoint(eye.position);
			var up = mount.transform.parent != null ? mount.transform.parent.up : Vector3.up;
			rotation = Quaternion.Inverse(mount.transform.rotation) * Quaternion.LookRotation(_screens[_focusIndex].position - eye.position, up);
		}

		private bool Follow()
		{
			if (mount == null)
			{
				return false;
			}

			var head = Quaternion.Euler(_shownPitch, _shownYaw, 0f);
			transform.SetPositionAndRotation(mount.transform.TransformPoint(_shownOffset), mount.transform.rotation * _shownFocusRotation * head);
			GetComponent<Camera>().fieldOfView = mount.FieldOfView;
			CameraAnchor.Commit(Anchor);
			return true;
		}
	}
}
