using System;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	/// <summary>A baked local ENU frame, independently anchored or a static child of a regional anchor.</summary>
	public sealed class NavigationAnchor
	{
		private readonly CesiumGlobeAnchor _anchor;
		private readonly LocalTangentPlane _plane;
		private readonly double3 _zero, _up, _east, _north;
		private readonly Transform _transform;
		public double North { get; }
		public double East { get; }
		public double Height { get; private set; }

		public NavigationAnchor(GameObject host, CesiumGeoreference georeference, double north, double east, double height, NavigationAnchor region = null)
		{
			North = north; East = east; _transform = host.transform;
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			_plane = new LocalTangentPlane(ll);
			_zero = georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, 0));
			_up = georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, 1)) - _zero;
			var longitude = math.radians(ll.LongitudeDegrees);
			_east = new double3(-Math.Sin(longitude), Math.Cos(longitude), 0);
			_north = math.normalize(math.cross(_up, _east));
			if (region == null)
			{
				_anchor = host.AddComponent<CesiumGlobeAnchor>();
				_anchor.detectTransformChanges = false;
				_anchor.adjustOrientationForGlobeWhenMoving = false;
				_anchor.positionGlobeFixed = _zero + _up * height;
				_anchor.rotationEastUpNorth = quaternion.identity;
			}
			else
			{
				// Both subtraction and basis rotation happen in doubles, including curvature.
				// Each child retains its own local ENU orientation for meshes and light sectors.
				_transform.SetParent(region._transform, false);
				_transform.localPosition = (Vector3)(float3)region.Direction(_zero + _up * height - region._zero - region._up * region.Height);
				_transform.localRotation = Quaternion.LookRotation((Vector3)(float3)region.Direction(_north), (Vector3)(float3)region.Direction(_up));
			}
			Height = height;
		}

		/// <summary>Build-time geodesy subtracts ECEF doubles inside ToNed before converting to local floats.</summary>
		public Vector3 Local(double north, double east, double ellipsoidalHeight)
		{
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			_plane.ToNed(ll, ellipsoidalHeight, out var n, out var e, out var d);
			return new Vector3((float)e, (float)(-d - Height), (float)n);
		}

		private double3 Direction(double3 ecef) => new double3(math.dot(ecef, _east), math.dot(ecef, _up), math.dot(ecef, _north));

		public void SetHeight(double height)
		{
			if (height == Height) return;
			if (_anchor == null) throw new InvalidOperationException("Regional navigation children are static; floating objects need their own anchor.");
			_anchor.positionGlobeFixed = _zero + _up * height;
			Height = height;
		}
	}
}
