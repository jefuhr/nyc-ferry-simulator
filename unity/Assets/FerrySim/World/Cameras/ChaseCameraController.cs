using CesiumForUnity;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.World
{
	/// <summary>
	/// External chase camera that follows any transform: behind and above the target, turning after its
	/// heading with a lag. Hold the right mouse button to orbit; scroll to zoom. Only angles are kept
	/// between frames, never positions, so an origin shift cannot make it jump.
	/// </summary>
	[DefaultExecutionOrder(CameraAnchor.ControllerOrder)]
	[DisallowMultipleComponent]
	public sealed class ChaseCameraController : MonoBehaviour
	{
		[SerializeField] private Transform target;
		[Tooltip("Horizontal distance behind the target, metres.")]
		[Min(1f)]
		[SerializeField] private float distance = 70f;
		[Tooltip("Height above the target's origin, metres.")]
		[SerializeField] private float height = 20f;
		[Tooltip("Aim point above the target's origin, metres.")]
		[SerializeField] private float lookHeight = 3f;
		[Tooltip("Time constant of the heading lag, seconds. 0 follows rigidly.")]
		[Min(0f)]
		[SerializeField] private float headingLag = 0.8f;
		[Range(0.01f, 1f)]
		[SerializeField] private float orbitSensitivity = 0.2f;

		private float _heading;
		private float _orbit;
		private bool _hasHeading;
		private CesiumGlobeAnchor _anchor;

		/// <summary>The transform to follow; null leaves the camera where it is.</summary>
		public Transform Target
		{
			get => target;
			set
			{
				target = value;
				_hasHeading = false;
			}
		}

		public float Distance
		{
			get => distance;
			set => distance = Mathf.Max(1f, value);
		}

		public float Height
		{
			get => height;
			set => height = value;
		}

		/// <summary>Extra yaw around the target, degrees, on top of following its heading.</summary>
		public float Orbit
		{
			get => _orbit;
			set => _orbit = value;
		}

		private CesiumGlobeAnchor Anchor => _anchor != null ? _anchor : _anchor = GetComponent<CesiumGlobeAnchor>();

		/// <summary>Moves the camera to its chase pose now, with no lag. Returns false without a target.</summary>
		public bool Snap()
		{
			_hasHeading = false;
			return Follow(0f);
		}

		private void LateUpdate()
		{
			var mouse = Mouse.current;
			if (mouse != null)
			{
				if (mouse.rightButton.isPressed)
				{
					_orbit += mouse.delta.ReadValue().x * orbitSensitivity;
				}

				var scroll = mouse.scroll.ReadValue().y;
				if (scroll != 0f)
				{
					Distance = distance * (scroll > 0f ? 0.85f : 1.15f);
				}
			}

			Follow(Time.unscaledDeltaTime);
		}

		private bool Follow(float deltaTime)
		{
			if (target == null)
			{
				return false;
			}

			var forward = Vector3.ProjectOnPlane(target.forward, Vector3.up);
			var heading = forward.sqrMagnitude > 1e-8f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : _heading;
			if (!_hasHeading || headingLag <= 0f || deltaTime <= 0f)
			{
				_heading = heading;
				_hasHeading = true;
			}
			else
			{
				_heading = Mathf.LerpAngle(_heading, heading, 1f - Mathf.Exp(-deltaTime / headingLag));
			}

			var offset = Quaternion.Euler(0f, _heading + _orbit, 0f) * new Vector3(0f, height, -distance);
			var position = target.position + offset;
			var focus = target.position + Vector3.up * lookHeight;
			transform.SetPositionAndRotation(position, Quaternion.LookRotation(focus - position, Vector3.up));
			CameraAnchor.Commit(Anchor);
			return true;
		}
	}
}
