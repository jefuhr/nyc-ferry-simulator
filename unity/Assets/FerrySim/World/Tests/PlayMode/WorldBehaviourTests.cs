using System.Collections;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.World.Tests.PlayMode
{
	/// <summary>
	/// The running world. Failure modes guarded: objects that drift or jump when the origin shifts,
	/// precision loss far from the world origin, the scene check silently skipped, the tide moving
	/// the water but leaving the barges behind, a chase camera that loses its target across an origin
	/// shift, and a wheelhouse view that is not at its mount.
	/// </summary>
	public class WorldBehaviourTests
	{
		private WorldTestScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new WorldTestScene();
			yield return _scene.Load();
		}

		[UnityTest]
		public IEnumerator GeoreferenceCheck_RanOnStartAndPassed()
		{
			yield return null;

			Assert.That(_scene.Check.LastResult, Is.Not.Null, "the scene check did not run on Start");
			Assert.That(_scene.Check.LastResult.Passed, Is.True, _scene.Check.LastResult.ToString());
		}

		[UnityTest]
		public IEnumerator OriginShift_FollowsTheCamera_AndTheWorldStaysOnTheGlobe()
		{
			// Fly to Hunters Point South, about 6.5 km from the world origin and 5.5 km from Pier 11.
			var hunters = _scene.Barge("4");
			var eye = _scene.OnWater(hunters.GtfsNorth - 150.0, hunters.GtfsEast - 150.0, 10f);
			yield return _scene.LookFrom(eye, hunters.transform.localPosition);
			for (var i = 0; i < 4; i++)
			{
				yield return null;
			}

			var georeference = _scene.Georeference;
			var originEcef = new double3(georeference.ecefX, georeference.ecefY, georeference.ecefZ);
			var cameraEcef = _scene.Camera.GetComponent<CesiumGlobeAnchor>().positionGlobeFixed;
			var worldOriginEcef = WorldFrame.LocalToEcef().c3.xyz;
			Assert.That(math.length(originEcef - worldOriginEcef), Is.GreaterThan(5000.0), "the georeference origin did not move with the camera");
			Assert.That(math.length(originEcef - cameraEcef), Is.LessThan(CesiumDistance() + 1.0), "origin should stay near the camera");
			Assert.That(_scene.Camera.transform.position.magnitude, Is.LessThan((float)CesiumDistance() + 1f), "camera coordinates should stay small");

			// Everything is still where the core says it is.
			var result = _scene.Check.Run();
			Assert.That(result.Passed, Is.True, result.ToString());
			_scene.Frame.MeasurePinError(out var pinPosition, out var pinRotation);
			Assert.That(pinPosition, Is.LessThan(0.001));

			// The nearby barge: its Unity position equals Cesium's placement of its built world-frame
			// position (the Landings group sits at the frame origin while the level is zero).
			var local = (float3)hunters.transform.localPosition;
			var ecef = math.mul(WorldFrame.LocalToEcef(), new double4(local, 1.0)).xyz;
			var expected = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
			var actual = (float3)hunters.transform.position;
			Assert.That(math.length((double3)actual - expected), Is.LessThan(0.05), "barge drifted on the origin shift");
			Assert.That(hunters.transform.position.magnitude, Is.LessThan(1000f), "near the camera, so small coordinates and full precision");
		}

		[UnityTest]
		public IEnumerator Tide_RaisesTheWaterAndFloatsTheBarges()
		{
			// Compare in world-frame coordinates, which an origin shift does not change.
			var frame = _scene.Frame.transform;
			var pier = _scene.Barge("87");
			var deck = _scene.Bridge("Brooklyn Bridge").transform.Find("Deck");
			yield return null;
			var waterBefore = frame.InverseTransformPoint(_scene.Water.transform.position);
			var bargeBefore = frame.InverseTransformPoint(pier.transform.position);
			var deckBefore = frame.InverseTransformPoint(deck.position);

			_scene.Water.WaterLevelNavd88 = 1.5;
			yield return null;

			var waterRise = frame.InverseTransformPoint(_scene.Water.transform.position) - waterBefore;
			var bargeRise = frame.InverseTransformPoint(pier.transform.position) - bargeBefore;
			Assert.That(waterRise.y, Is.EqualTo(1.5f).Within(0.001f));
			Assert.That(bargeRise.y, Is.EqualTo(1.5f).Within(0.001f), "barges must rise with the tide");
			Assert.That(new Vector2(bargeRise.x, bargeRise.z).magnitude, Is.LessThan(0.001f));
			Assert.That(Vector3.Distance(frame.InverseTransformPoint(deck.position), deckBefore), Is.LessThan(0.001f), "bridges do not float");
		}

		[UnityTest]
		public IEnumerator ChaseCamera_FollowsAMovingTarget_AcrossOriginShifts()
		{
			var target = new GameObject("ChaseTarget").transform;
			target.SetParent(_scene.Frame.transform, false);
			var pier = _scene.Barge("87");
			var start = _scene.OnWater(pier.FaceStartNorth + pier.OutwardNormalNorth * 200.0, pier.FaceStartEast + pier.OutwardNormalEast * 200.0, 0f);
			target.localPosition = start;
			target.localRotation = Quaternion.Euler(0f, 45f, 0f);
			_scene.Rig.ChaseTarget = target;
			_scene.Rig.CurrentMode = CameraRig.Mode.Chase;
			var chase = _scene.Rig.Chase;
			var expectedDistance = Mathf.Sqrt(chase.Distance * chase.Distance + chase.Height * chase.Height);

			// Drive the target 1.5 km north-east in 60 frames: several origin shifts on the way.
			for (var i = 1; i <= 60; i++)
			{
				target.localPosition = start + new Vector3(0.7071f, 0f, 0.7071f) * (25f * i);
				yield return null;
				Assert.That(Vector3.Distance(_scene.Camera.transform.position, target.position), Is.EqualTo(expectedDistance).Within(0.5f), "frame " + i);
			}

			// Behind the target (it heads north-east) and looking at it.
			var toTarget = (target.position - _scene.Camera.transform.position).normalized;
			Assert.That(Vector3.Dot(_scene.Camera.transform.forward, toTarget), Is.GreaterThan(0.99f));
			Assert.That(Vector3.Dot(Vector3.ProjectOnPlane(toTarget, Vector3.up).normalized, Vector3.ProjectOnPlane(target.forward, Vector3.up).normalized), Is.GreaterThan(0.99f));
			Object.Destroy(target.gameObject);
		}

		[UnityTest]
		public IEnumerator WheelhouseCamera_SitsAtTheMount()
		{
			var mount = _scene.Rig.WheelhouseMount;
			Assert.That(mount, Is.Not.Null);
			_scene.Rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			yield return null;
			yield return null;

			Assert.That(Vector3.Distance(_scene.Camera.transform.position, mount.transform.position), Is.LessThan(0.01f));
			Assert.That(Vector3.Dot(_scene.Camera.transform.forward, mount.transform.forward), Is.GreaterThan(0.9999f));
			Assert.That(_scene.Camera.fieldOfView, Is.EqualTo(mount.FieldOfView));
		}

		private double CesiumDistance() => _scene.Camera.GetComponent<CesiumOriginShift>().distance;
	}
}
