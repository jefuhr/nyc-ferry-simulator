using System;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>A component that adds a line to the on-screen debug readout.</summary>
	public interface IDebugReadoutSource
	{
		string ReadoutLine { get; }
	}

	/// <summary>
	/// Follows the vessel's rendered transform: a chase view from astern, the wheelhouse eye point, or an
	/// overhead view with north up. <see cref="CycleMode"/> steps through them (the camera-cycle button).
	/// Runs after the runner has placed the vessel for the frame.
	/// </summary>
	[DefaultExecutionOrder(100)]
	public sealed class ChaseCamera : MonoBehaviour, IDebugReadoutSource
	{
		public enum Mode
		{
			Chase = 0,
			Bridge = 1,
			Overhead = 2,
		}

		[SerializeField]
		private Transform target;

		[Tooltip("Optional: sizes the bridge eye point from the vessel file.")]
		[SerializeField]
		private VesselSimulationRunner runner;

		[SerializeField]
		private Mode mode = Mode.Chase;

		[SerializeField]
		private float chaseDistance = 75f;

		[SerializeField]
		private float chaseHeight = 24f;

		[SerializeField]
		private float overheadHeight = 260f;

		[Tooltip("Seconds for the chase view to close most of the gap; 0 follows rigidly.")]
		[SerializeField]
		private float followTimeConstant = 0.4f;

		private bool _snap = true;

		public Mode CurrentMode => mode;

		public Transform Target
		{
			get => target;
			set
			{
				target = value;
				_snap = true;
			}
		}

		public string ReadoutLine => "camera: " + mode.ToString().ToLowerInvariant();

		public void CycleMode()
		{
			mode = (Mode)(((int)mode + 1) % Enum.GetValues(typeof(Mode)).Length);
			_snap = true;
		}

		public void SetMode(Mode value)
		{
			mode = value;
			_snap = true;
		}

		private void LateUpdate()
		{
			if (target == null)
			{
				return;
			}

			switch (mode)
			{
				case Mode.Bridge:
					PlaceOnBridge();
					break;
				case Mode.Overhead:
					transform.SetPositionAndRotation(target.position + Vector3.up * overheadHeight, Quaternion.Euler(90f, 0f, 0f));
					break;
				default:
					FollowAstern();
					break;
			}

			_snap = false;
		}

		private void PlaceOnBridge()
		{
			var eyeHeight = 6.5f;
			var eyeForward = 7f;
			if (runner != null && runner.Parameters != null)
			{
				var dimensions = GreyboxDimensions.From(runner.Parameters);
				eyeHeight = (float)dimensions.BridgeEyeHeight;
				eyeForward = (float)dimensions.BridgeEyeForward;
			}

			transform.SetPositionAndRotation(target.TransformPoint(new Vector3(0f, eyeHeight, eyeForward)), target.rotation);
		}

		private void FollowAstern()
		{
			var forward = target.forward;
			forward.y = 0f;
			forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
			var desired = target.position - forward * chaseDistance + Vector3.up * chaseHeight;
			var teleported = (transform.position - desired).sqrMagnitude > 500f * 500f;
			if (_snap || teleported || followTimeConstant <= 0f)
			{
				transform.position = desired;
			}
			else
			{
				// Exponential approach, independent of the frame rate.
				var blend = 1f - Mathf.Exp(-Time.deltaTime / followTimeConstant);
				transform.position = Vector3.Lerp(transform.position, desired, blend);
			}

			transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * 3f - transform.position, Vector3.up);
		}
	}
}
