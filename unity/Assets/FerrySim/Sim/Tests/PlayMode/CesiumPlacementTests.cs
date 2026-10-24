using System.Collections;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// The Cesium placement, against a real <see cref="CesiumGeoreference"/> at the shared origin. Failure modes
	/// guarded: the vessel placed on the flat tangent plane (floating above the curved water kilometres out),
	/// heights taken from a hard-coded geoid offset instead of the water surface, an attitude not relative to the
	/// local east-up-north frame, and the vessel jumping when the georeference origin moves (CesiumOriginShift).
	/// </summary>
	public class CesiumPlacementTests
	{
		private GameObject _root;

		[TearDown]
		public void TearDown()
		{
			if (_root != null)
			{
				Object.Destroy(_root);
			}
		}

		[UnityTest]
		public IEnumerator Placement_FollowsTheGlobe_AndSurvivesAnOriginShift()
		{
			_root = new GameObject("Georeference");
			var georeference = _root.AddComponent<CesiumGeoreference>();
			georeference.SetOriginLongitudeLatitudeHeight(WorldOrigin.LongitudeDegrees, WorldOrigin.LatitudeDegrees, 0.0);
			var vessel = new GameObject("Vessel");
			vessel.transform.SetParent(_root.transform, false);
			vessel.AddComponent<CesiumGlobeAnchor>();
			var placement = vessel.AddComponent<CesiumVesselPlacement>();
			var water = new ConstantWaterSurface(-31.8005, 0.4);
			placement.WaterSurface = water;
			yield return null;

			// 3.6 km out the ellipsoid has dropped about a metre below the tangent plane.
			var pose = new VesselPose(1500.0, 3300.0, 0.3, Angles.ToRadians(30.0), Angles.ToRadians(1.0), Angles.ToRadians(-2.0));
			placement.Place(vessel.transform, pose, 0.0);

			var geodetic = LocalTangentPlane.World.ToGeodetic(pose.North, pose.East, out var drop);
			var height = water.EllipsoidalHeight(pose.North, pose.East, 0.0) - pose.Down;
			LocalTangentPlane.World.ToNed(geodetic, height, out var north, out var east, out var down);
			var position = vessel.transform.localPosition;
			Assert.That(drop, Is.GreaterThan(0.5), "the test point is far enough out for curvature to matter");
			Assert.That(position.x, Is.EqualTo((float)east).Within(0.01f), "east");
			Assert.That(position.z, Is.EqualTo((float)north).Within(0.01f), "north");
			Assert.That(position.y, Is.EqualTo((float)-down).Within(0.01f), "height follows the curved water surface");
			Assert.That(position.y, Is.EqualTo((float)(height - drop)).Within(0.02f));

			var anchor = placement.Anchor;
			Assert.That(anchor.longitudeLatitudeHeight.x, Is.EqualTo(geodetic.LongitudeDegrees).Within(1e-9));
			Assert.That(anchor.longitudeLatitudeHeight.y, Is.EqualTo(geodetic.LatitudeDegrees).Within(1e-9));
			Assert.That(anchor.longitudeLatitudeHeight.z, Is.EqualTo(height).Within(1e-4));
			var local = (Quaternion)anchor.rotationEastUpNorth;
			var forward = local * Vector3.forward;
			Assert.That(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, Is.EqualTo(30f).Within(0.01f), "heading in the local frame");
			Assert.That(Mathf.Asin(forward.y) * Mathf.Rad2Deg, Is.EqualTo(1f).Within(0.01f), "bow up");

			// Move the origin onto the vessel, as CesiumOriginShift does when the camera travels.
			georeference.SetOriginLongitudeLatitudeHeight(geodetic.LongitudeDegrees, geodetic.LatitudeDegrees, water.EllipsoidalHeight(pose.North, pose.East, 0.0));
			var shifted = vessel.transform.localPosition;
			Assert.That(shifted.magnitude, Is.EqualTo(0.3f).Within(0.01f), "Cesium re-placed the anchored vessel: 0.3 m below the water at the new origin");
			Assert.That(shifted.y, Is.EqualTo(-0.3f).Within(0.01f));

			// The next frame's placement agrees with Cesium's re-placement.
			yield return null;
			placement.Place(vessel.transform, pose, 0.0);
			Assert.That(Vector3.Distance(vessel.transform.localPosition, shifted), Is.LessThan(0.005f));
			var up = vessel.transform.localRotation * Vector3.up;
			Assert.That(Vector3.Angle(up, Vector3.up), Is.LessThan(2.5f), "attitude is local: only the 1° pitch and 2° roll tilt it");
			var shiftedForward = vessel.transform.localRotation * Vector3.forward;
			Assert.That(Mathf.Atan2(shiftedForward.x, shiftedForward.z) * Mathf.Rad2Deg, Is.EqualTo(30f).Within(0.05f), "heading is still 30° true");
		}
	}
}
