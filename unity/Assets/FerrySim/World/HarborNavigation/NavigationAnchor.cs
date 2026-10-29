using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	/// <summary>A baked local ENU frame. Only a double ECEF translation along cached up changes with tide.</summary>
	public sealed class NavigationAnchor
	{
		private readonly CesiumGlobeAnchor _anchor;
		private readonly LocalTangentPlane _plane;
		private readonly double3 _zero, _up;
		public double North { get; }
		public double East { get; }
		public double Height { get; private set; }

		public NavigationAnchor(GameObject host, CesiumGeoreference georeference, double north, double east, double height)
		{
			North = north; East = east;
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			_plane = new LocalTangentPlane(ll);
			_zero = georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, 0));
			_up = georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, 1)) - _zero;
			_anchor = host.AddComponent<CesiumGlobeAnchor>();
			_anchor.detectTransformChanges = false;
			_anchor.adjustOrientationForGlobeWhenMoving = false;
			_anchor.positionGlobeFixed = _zero + _up * height;
			_anchor.rotationEastUpNorth = quaternion.identity;
			Height = height;
		}

		/// <summary>Build-time geodesy subtracts ECEF doubles inside ToNed before converting to local floats.</summary>
		public Vector3 Local(double north, double east, double ellipsoidalHeight)
		{
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			_plane.ToNed(ll, ellipsoidalHeight, out var n, out var e, out var d);
			return new Vector3((float)e, (float)(-d - Height), (float)n);
		}

		public void SetHeight(double height)
		{
			if (height == Height) return;
			_anchor.positionGlobeFixed = _zero + _up * height;
			Height = height;
		}
	}
}
