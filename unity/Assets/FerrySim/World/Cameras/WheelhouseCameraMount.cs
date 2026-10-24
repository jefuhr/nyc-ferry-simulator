using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Eye point for the wheelhouse view. The bridge task parents this object to the vessel at the
	/// helm (position = the operator's eye, forward = the bow); until then it waits near Pier 11.
	/// <see cref="WheelhouseCameraController"/> puts the camera here and adds head look.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class WheelhouseCameraMount : MonoBehaviour
	{
		[Range(20f, 110f)]
		[SerializeField] private float fieldOfView = 65f;
		[Tooltip("Head-look yaw limits, degrees (left negative).")]
		[SerializeField] private Vector2 yawLimits = new Vector2(-170f, 170f);
		[Tooltip("Head-look pitch limits, degrees (up negative).")]
		[SerializeField] private Vector2 pitchLimits = new Vector2(-60f, 45f);

		public float FieldOfView => fieldOfView;

		public Vector2 YawLimits => yawLimits;

		public Vector2 PitchLimits => pitchLimits;

		private void OnDrawGizmos()
		{
			Gizmos.color = Color.cyan;
			Gizmos.DrawWireSphere(transform.position, 0.3f);
			Gizmos.DrawRay(transform.position, transform.forward * 3f);
		}
	}
}
