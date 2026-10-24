using System;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// The simulator's world frame inside Unity: the tangent plane through
	/// <see cref="WorldOrigin"/> at ellipsoidal height 0, mapped as docs/conventions.md says
	/// (x = east, y = up = -down, z = north). The game object carries a <see cref="CesiumGlobeAnchor"/>
	/// pinned to that plane, so when <see cref="CesiumOriginShift"/> moves the georeference origin to
	/// follow the camera, Cesium moves and rotates this object and everything under it stays put on the
	/// globe. Children therefore use world-frame coordinates directly as local positions: water,
	/// greybox structures, and the vessel the bridge task drives.
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	[RequireComponent(typeof(CesiumGlobeAnchor))]
	public sealed class WorldFrame : MonoBehaviour
	{
		private const double SemiMajorAxis = 6378137.0;
		private const double Flattening = 1.0 / 298.257223563;
		private const double EccentricitySquared = Flattening * (2.0 - Flattening);

		/// <summary>Local position, in this frame, of a world (north, east, down) point.</summary>
		public static Vector3 ToLocal(double north, double east, double down) =>
			new Vector3((float)east, (float)(-down), (float)north);

		/// <summary>Local position, in this frame, of a WGS84 point at an ellipsoidal height (metres).</summary>
		public static Vector3 LocalFromGeodetic(double latitudeDegrees, double longitudeDegrees, double ellipsoidalHeight)
		{
			LocalTangentPlane.World.ToNed(new GeodeticPosition(latitudeDegrees, longitudeDegrees), ellipsoidalHeight, out var north, out var east, out var down);
			return ToLocal(north, east, down);
		}

		/// <summary>Unity world position of a world (north, east, down) point, valid after any origin shift.</summary>
		public Vector3 ToUnityPosition(double north, double east, double down) => transform.TransformPoint(ToLocal(north, east, down));

		/// <summary>Unity world rotation for a heading (radians, clockwise from true north): a yaw about this frame's up.</summary>
		public Quaternion HeadingToUnityRotation(double headingRadians) =>
			transform.rotation * Quaternion.Euler(0f, (float)(headingRadians * 180.0 / Math.PI), 0f);

		/// <summary>
		/// Transform from this frame's local coordinates to ECEF: columns east, up and north at the world
		/// origin, then the origin itself. WGS84, the same ellipsoid as LocalTangentPlane and Cesium.
		/// </summary>
		public static double4x4 LocalToEcef()
		{
			var latitude = WorldOrigin.LatitudeDegrees * Math.PI / 180.0;
			var longitude = WorldOrigin.LongitudeDegrees * Math.PI / 180.0;
			var sinLat = Math.Sin(latitude);
			var cosLat = Math.Cos(latitude);
			var sinLon = Math.Sin(longitude);
			var cosLon = Math.Cos(longitude);
			var primeVertical = SemiMajorAxis / Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);

			var east = new double4(-sinLon, cosLon, 0.0, 0.0);
			var up = new double4(cosLat * cosLon, cosLat * sinLon, sinLat, 0.0);
			var north = new double4(-sinLat * cosLon, -sinLat * sinLon, cosLat, 0.0);
			var origin = new double4(primeVertical * cosLat * cosLon, primeVertical * cosLat * sinLon, primeVertical * (1.0 - EccentricitySquared) * sinLat, 1.0);
			return new double4x4(east, up, north, origin);
		}

		/// <summary>The scene's world frame, or null.</summary>
		public static WorldFrame Find() => FindFirstObjectByType<WorldFrame>();

		/// <summary>Pins the globe anchor to the world origin. Idempotent; runs on enable.</summary>
		public void Pin()
		{
			var anchor = GetComponent<CesiumGlobeAnchor>();
			anchor.adjustOrientationForGlobeWhenMoving = false;
			anchor.detectTransformChanges = false;
			anchor.localToGlobeFixedMatrix = LocalToEcef();
		}

		/// <summary>
		/// How far the anchor has drifted from the world origin: position in metres and rotation in
		/// degrees. Both should be zero (to rounding) at any georeference origin.
		/// </summary>
		public void MeasurePinError(out double positionMetres, out double rotationDegrees)
		{
			var actual = GetComponent<CesiumGlobeAnchor>().localToGlobeFixedMatrix;
			var expected = LocalToEcef();
			positionMetres = math.length(actual.c3.xyz - expected.c3.xyz);
			var worst = math.max(math.length(actual.c0.xyz - expected.c0.xyz), math.max(math.length(actual.c1.xyz - expected.c1.xyz), math.length(actual.c2.xyz - expected.c2.xyz)));
			rotationDegrees = 2.0 * Math.Asin(Math.Min(1.0, 0.5 * worst)) * 180.0 / Math.PI;
		}

		private void OnEnable() => Pin();
	}
}
