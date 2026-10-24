using CesiumForUnity;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Camera controllers run in LateUpdate before <see cref="CesiumOriginShift"/> (execution order
	/// <see cref="ControllerOrder"/> against Cesium's 0). After moving a camera they call
	/// <see cref="Commit"/>, so its <see cref="CesiumGlobeAnchor"/> records the new pose in ECEF before
	/// the origin shift reads it. Without that, a pose set in LateUpdate would be read in the shifted
	/// frame and the camera would jump by the shift.
	/// </summary>
	public static class CameraAnchor
	{
		public const int ControllerOrder = -100;

		/// <summary>Records the transform's current pose in its globe anchor, if it has an active one.</summary>
		public static void Commit(CesiumGlobeAnchor anchor)
		{
			if (anchor != null && anchor.isActiveAndEnabled)
			{
				anchor.Sync();
			}
		}
	}
}
