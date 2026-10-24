using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Rides the tide: keeps this object's local height equal to the water level above NAVD88, so
	/// children built with their waterline on the NAVD88-zero surface (floating landing barges) stay
	/// afloat as the level changes. Put it on a direct child of the <see cref="WorldFrame"/> with no
	/// other offset. Finds the scene's <see cref="WaterSurface"/> when none is assigned.
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	public sealed class FloatOnWater : MonoBehaviour
	{
		[SerializeField] private WaterSurface water;

		public WaterSurface Water
		{
			get
			{
				if (water == null)
				{
					var frame = GetComponentInParent<WorldFrame>();
					water = frame != null ? frame.GetComponentInChildren<WaterSurface>() : null;
				}

				return water;
			}
			set => water = value;
		}

		private void LateUpdate()
		{
			var surface = Water;
			if (surface == null)
			{
				return;
			}

			var level = (float)surface.WaterLevelNavd88;
			var position = transform.localPosition;
			if (position.x != 0f || position.y != level || position.z != 0f)
			{
				transform.localPosition = new Vector3(0f, level, 0f);
			}
		}
	}
}
