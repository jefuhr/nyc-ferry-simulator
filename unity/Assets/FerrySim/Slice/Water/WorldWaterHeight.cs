using System;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.Slice
{
	/// <summary>
	/// The bridge's <see cref="IWaterSurface"/> on the world's drawn water (<see cref="World.WaterSurface"/>): the
	/// ellipsoidal height of the surface the camera sees, the slice's GEOID18 offset under the point plus the
	/// water level above NAVD88. Assign it to <c>CesiumVesselPlacement.waterSurfaceSource</c> so the hull floats on
	/// that surface rather than on a constant from <c>geoid_offset.json</c>.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class WorldWaterHeight : MonoBehaviour, IWaterSurface
	{
		[Tooltip("The world's water surface: the drawn mesh and its NAVD88 water level.")]
		[SerializeField]
		private World.WaterSurface water;

		public World.WaterSurface Water
		{
			get => water;
			set => water = value;
		}

		/// <summary>
		/// Metres above WGS84 at world (north, east). The level is the water surface's current
		/// <see cref="World.WaterSurface.WaterLevelNavd88"/>; <paramref name="time"/> does not change it.
		/// </summary>
		public double EllipsoidalHeight(double north, double east, double time)
		{
			if (water == null)
			{
				throw new InvalidOperationException($"{name}: WorldWaterHeight has no WaterSurface, so the hull would sit on the ellipsoid instead of the water. Assign the world's water.");
			}

			return water.EllipsoidalHeight(north, east);
		}
	}
}
