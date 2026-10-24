using FerrySim.Core.Contracts;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// The conventions' mapping from the simulation world to Unity: world (north, east, down) becomes Unity
	/// (x = east, y = −down, z = north), and heading ψ is a yaw of ψ degrees about +y (both turn clockwise
	/// seen from above). Unity is left-handed, so bow-up pitch is a negative rotation about +x (starboard)
	/// and starboard-down roll is a negative rotation about +z (forward). Positions stay double until the
	/// difference from a render origin is taken, so precision holds kilometres from the world origin.
	/// </summary>
	public static class WorldToUnity
	{
		/// <summary>Unity offset of world point (north, east, down) from a render origin at (originNorth, originEast) on the calm-water surface.</summary>
		public static Vector3 Position(double north, double east, double down, double originNorth, double originEast) =>
			new Vector3((float)(east - originEast), (float)(-down), (float)(north - originNorth));

		/// <summary>Unity rotation of a vessel at the given heading, pitch and roll (radians).</summary>
		public static Quaternion Rotation(double heading, double pitch, double roll) =>
			Quaternion.Euler(
				(float)-Angles.ToDegrees(pitch),
				(float)Angles.ToDegrees(heading),
				(float)-Angles.ToDegrees(roll));

		/// <summary>World (north, east) offset of a Unity offset from the render origin; the inverse of <see cref="Position"/> on the surface.</summary>
		public static HorizontalVector ToWorldOffset(Vector3 unityOffset) => new HorizontalVector(unityOffset.z, unityOffset.x);
	}
}
