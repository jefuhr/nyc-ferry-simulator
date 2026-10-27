using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

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
		[Range(0.01f, 1f)]
		[SerializeField] private float lookSensitivity = 0.15f;

		private float _yaw;
		private float _pitch;
		private CesiumGlobeAnchor _anchor;
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

		/// <summary>Per-session preference, shared across camera/scene replacements; never recorded.</summary>
		public static bool AutomaticBowEnabled { get; set; } = true;
		public bool ShowingBow => _bow.ShowBow;
		public float BowPitch => _bowPitch;
		public void ToggleBowView() => _bow.ToggleManual();
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
		private void OnDestroy() { if (_runner != null) _runner.RunStarted -= ResetBow; }
		private void ResetBow()
		{
			_bow.Reset(); _parameters = null;
			_yaw = _pitch = _shownYaw = _shownPitch = 0;
		}

		private void RefreshApproach()
		{
			if (mount != null && mount.transform.parent != _mountParent) BindRunner();
			if (_runner == null || _runner.Parameters == null || mount == null) return;
			if (!ReferenceEquals(_parameters, _runner.Parameters))
			{
				_parameters = _runner.Parameters;
				var dimensions = GreyboxDimensions.From(_parameters);
				_bowForward = dimensions.LengthOverall * .45;
				// The mount already has a down tilt; express the eye-to-deck ray in its frame before adding head look.
				var direction = Quaternion.Inverse(mount.transform.localRotation) * new Vector3(0,
					(float)(dimensions.Freeboard - dimensions.BridgeEyeHeight), (float)(_bowForward - dimensions.BridgeEyeForward));
				_bowYaw = Mathf.Clamp(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, mount.YawLimits.x, mount.YawLimits.y);
				_bowPitch = Mathf.Clamp(Mathf.Atan2(-direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg,
					mount.PitchLimits.x, mount.PitchLimits.y);
			}
			var state = _runner.State;
			var distance = BowLookState.FaceDistance(mount.ApproachFaces, _faces, state.North, state.East, state.Heading, _bowForward);
			_bow.UpdateApproach(distance, System.Math.Sqrt(state.Surge * state.Surge + state.Sway * state.Sway), AutomaticBowEnabled);
		}

		private void SetHeadLook(float yaw, float pitch)
		{
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
			var keyboard = Keyboard.current;
			if (keyboard != null && keyboard.bKey.wasPressedThisFrame)
			{
				if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) ToggleAutomaticBow();
				else ToggleBowView();
			}
			var mouse = Mouse.current;
			if (mouse != null)
			{
				if (mouse.rightButton.isPressed)
				{
					if (_bow.ShowBow) { _yaw = _shownYaw; _pitch = _shownPitch; }
					_bow.HandLook();
					var delta = mouse.delta.ReadValue();
					SetHeadLook(_yaw + delta.x * lookSensitivity, _pitch - delta.y * lookSensitivity);
				}

				if (mouse.middleButton.wasPressedThisFrame)
				{
					_bow.HandLook();
					SetHeadLook(0f, 0f);
				}
			}

			var showBow = _bow.ShowBow;
			var blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / .25f);
			_shownYaw = Mathf.Lerp(_shownYaw, showBow ? _bowYaw : _yaw, blend);
			_shownPitch = Mathf.Lerp(_shownPitch, showBow ? _bowPitch : _pitch, blend);
			Follow();
		}

		private bool Follow()
		{
			if (mount == null)
			{
				return false;
			}

			var head = Quaternion.Euler(_shownPitch, _shownYaw, 0f);
			transform.SetPositionAndRotation(mount.transform.position, mount.transform.rotation * head);
			GetComponent<Camera>().fieldOfView = mount.FieldOfView;
			CameraAnchor.Commit(Anchor);
			return true;
		}
	}
}
