using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Puts a vessel pose (world NED, double precision) into the Unity scene. The runner uses the first
	/// component on its GameObject that implements this (for example the Cesium placement in
	/// <c>FerrySim.Unity.Sim.Cesium</c>), and <see cref="TangentPlanePlacement"/> otherwise.
	/// </summary>
	public interface IVesselPlacement
	{
		/// <summary>Moves <paramref name="target"/> to <paramref name="pose"/> at simulation time <paramref name="time"/>, seconds.</summary>
		void Place(Transform target, in VesselPose pose, double time);
	}

	/// <summary>
	/// Flat placement in the conventions' mapping: the target's parent frame is east-up-north at the shared
	/// world origin (a CesiumGeoreference at <c>WorldOrigin</c> without origin shifting, or a plain scene),
	/// shifted by a double-precision render origin so coordinates stay small near the vessel. Earth curvature
	/// is ignored, which is what the dev scene wants and what the 2D physics assumes.
	/// </summary>
	public sealed class TangentPlanePlacement : IVesselPlacement
	{
		/// <summary>World north of the Unity origin, metres.</summary>
		public double RenderOriginNorth { get; set; }

		/// <summary>World east of the Unity origin, metres.</summary>
		public double RenderOriginEast { get; set; }

		/// <summary>Unity height of the calm water surface in the parent frame, metres.</summary>
		public double WaterSurfaceHeight { get; set; }

		public void Place(Transform target, in VesselPose pose, double time)
		{
			var offset = WorldToUnity.Position(pose.North, pose.East, pose.Down, RenderOriginNorth, RenderOriginEast);
			offset.y += (float)WaterSurfaceHeight;
			target.SetLocalPositionAndRotation(offset, WorldToUnity.Rotation(pose.Heading, pose.Pitch, pose.Roll));
		}

		/// <summary>Unity position of a world point on the calm water surface, in the same frame as placed vessels.</summary>
		public Vector3 ToUnity(double north, double east) =>
			WorldToUnity.Position(north, east, -WaterSurfaceHeight, RenderOriginNorth, RenderOriginEast);
	}
}
