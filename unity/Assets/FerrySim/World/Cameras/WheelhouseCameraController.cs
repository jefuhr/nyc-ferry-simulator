using CesiumForUnity;
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

		public WheelhouseCameraMount Mount
		{
			get => mount;
			set => mount = value;
		}

		/// <summary>Head look relative to the mount, degrees (yaw right positive, pitch down positive).</summary>
		public Vector2 HeadLook
		{
			get => new Vector2(_yaw, _pitch);
			set => SetHeadLook(value.x, value.y);
		}

		private CesiumGlobeAnchor Anchor => _anchor != null ? _anchor : _anchor = GetComponent<CesiumGlobeAnchor>();

		/// <summary>Moves the camera to the mount now. Returns false without a mount.</summary>
		public bool Snap() => Follow();

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
			var mouse = Mouse.current;
			if (mouse != null)
			{
				if (mouse.rightButton.isPressed)
				{
					var delta = mouse.delta.ReadValue();
					SetHeadLook(_yaw + delta.x * lookSensitivity, _pitch - delta.y * lookSensitivity);
				}

				if (mouse.middleButton.wasPressedThisFrame)
				{
					SetHeadLook(0f, 0f);
				}
			}

			Follow();
		}

		private bool Follow()
		{
			if (mount == null)
			{
				return false;
			}

			var head = Quaternion.Euler(_pitch, _yaw, 0f);
			transform.SetPositionAndRotation(mount.transform.position, mount.transform.rotation * head);
			GetComponent<Camera>().fieldOfView = mount.FieldOfView;
			CameraAnchor.Commit(Anchor);
			return true;
		}
	}
}
