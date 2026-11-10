using CesiumForUnity;
using UnityEngine;
using FerrySim.Controls;

namespace FerrySim.World
{
	/// <summary>
	/// Debug fly camera. Hold the right mouse button to look; W/S forward and back, A/D sideways, E/Q up
	/// and down; Shift is faster, Ctrl slower; the scroll wheel changes the base speed. Reads the Camera and FreeFly actions only while enabled.
	/// </summary>
	[DefaultExecutionOrder(CameraAnchor.ControllerOrder)]
	[DisallowMultipleComponent]
	public sealed class FreeFlyCameraController : MonoBehaviour
	{
		[Tooltip("Base speed, m/s.")]
		[Min(0.1f)]
		[SerializeField] private float speed = 25f;
		[Min(1f)]
		[SerializeField] private float fastMultiplier = 6f;
		[Range(0.01f, 1f)]
		[SerializeField] private float slowMultiplier = 0.2f;
		[Tooltip("Degrees of turn per pixel of mouse movement.")]
		[Range(0.01f, 1f)]
		[SerializeField] private float lookSensitivity = 0.15f;

		private float _yaw;
		private float _pitch;
		private CesiumGlobeAnchor _anchor;
		private CameraActionReader _input;

		public float Speed
		{
			get => speed;
			set => speed = Mathf.Max(0.1f, value);
		}

		/// <summary>Yaw (degrees about up, clockwise from the frame's north) and pitch (degrees, down positive).</summary>
		public Vector2 YawPitch => new Vector2(_yaw, _pitch);

		/// <summary>Places the camera and points it; pitch is clamped to +-89 degrees.</summary>
		public void SetPose(Vector3 position, float yawDegrees, float pitchDegrees)
		{
			_yaw = yawDegrees;
			_pitch = Mathf.Clamp(pitchDegrees, -89f, 89f);
			transform.SetPositionAndRotation(position, Quaternion.Euler(_pitch, _yaw, 0f));
			CameraAnchor.Commit(Anchor);
		}

		/// <summary>Places the camera and points it at a Unity world position.</summary>
		public void SetPoseLookingAt(Vector3 position, Vector3 target)
		{
			var direction = target - position;
			var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
			var pitch = -Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg;
			SetPose(position, yaw, pitch);
		}

		private CesiumGlobeAnchor Anchor => _anchor != null ? _anchor : _anchor = GetComponent<CesiumGlobeAnchor>();

		private void OnEnable()
		{
			_input = new CameraActionReader(true);
			InputBindingService.Instance.SetFreeFly(true);
			var euler = transform.rotation.eulerAngles;
			_yaw = euler.y;
			_pitch = euler.x > 180f ? euler.x - 360f : euler.x;
		}

		private void OnDisable()
		{
			_input?.Dispose(); _input = null;
			InputBindingService.Instance.SetFreeFly(false);
		}

		private void LateUpdate()
		{
			if (_input == null) return;
			if (_input[ActionIds.LookHold].IsPressed())
			{
				var delta = _input[ActionIds.LookDelta].ReadValue<Vector2>();
				_yaw += delta.x * lookSensitivity;
				_pitch = Mathf.Clamp(_pitch - delta.y * lookSensitivity, -89f, 89f);
			}
			var scroll = _input[ActionIds.Scroll].ReadValue<float>();
			if (scroll != 0f) Speed = speed * (scroll > 0f ? 1.25f : 0.8f);
			var horizontal = _input[ActionIds.FreeFlyMove].ReadValue<Vector2>();
			var move = new Vector3(horizontal.x, _input[ActionIds.FreeFlyVertical].ReadValue<float>(), horizontal.y);
			var multiplier = _input[ActionIds.FreeFlyFast].IsPressed() ? fastMultiplier :
				_input[ActionIds.FreeFlySlow].IsPressed() ? slowMultiplier : 1f;

			var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
			var position = transform.position + rotation * move * (speed * multiplier * Time.unscaledDeltaTime);
			transform.SetPositionAndRotation(position, rotation);
			CameraAnchor.Commit(Anchor);
		}

	}
}
