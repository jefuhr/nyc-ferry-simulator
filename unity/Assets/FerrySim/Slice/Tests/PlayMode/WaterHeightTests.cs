using System;
using System.Collections;
using System.Collections.Generic;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (e) The hull sits on the water that is drawn: at positions across the slice the placed hull's WGS84 ellipsoidal
	/// height equals the drawn water mesh's under it, within 1 cm. Failure modes guarded: the placement left on its
	/// constant fallback (the slice-centre geoid offset from geoid_offset.json, centimetres off away from the centre),
	/// the adapter not wired, and heights compared in the wrong frame (the mesh is measured through Cesium, the same
	/// way the hull is placed).
	/// </summary>
	public class WaterHeightTests
	{
		private const double Tolerance = 0.01;

		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator ThePlacedHull_SitsOnTheDrawnWater_WithinACentimetre_AcrossTheSlice()
		{
			Assert.That(_scene.Placement.WaterSurface, Is.InstanceOf<WorldWaterHeight>(), "the placement reads the world's water");
			Assert.That(_scene.Water.WaterLevelNavd88, Is.EqualTo(0.0), "the scene keeps the world scene's constant NAVD88 0.0 m level");
			Assert.That(ConstantWaterSurface.TryReadGeoidOffset(SimPaths.Resolve(ConstantWaterSurface.DefaultGeoidOffsetFile), out var centreOffset, out var problem), Is.True, problem);
			_scene.StepsPerFrame(2);

			var positions = new List<(string Name, double Latitude, double Longitude)>
			{
				("off Wall St/Pier 11", 40.7030, -74.0040),
				("off South Williamsburg", 40.7092, -73.9722),
				("off Hunters Point South", 40.7400, -73.9660),
			};
			var worstFallbackError = 0.0;
			foreach (var (name, latitude, longitude) in positions)
			{
				var scenario = new ScenarioSettings
				{
					name = "water height " + name,
					vesselFile = _scene.Runner.Scenario.vesselFile,
					startLatitude = latitude,
					startLongitude = longitude,
					startHeadingDegrees = 30.0,
					scenarioStartUtc = "2026-06-15T16:00:00Z",
					environment = new ScenarioEnvironment { calm = true },
				};
				_scene.Restart(scenario, new ConstantControls(ControlInputs.Neutral));
				// Let the cameras follow and Cesium shift its origin to the new place.
				yield return SliceScene.Frames(6);

				var pose = _scene.Runner.RenderPose;
				var hull = _scene.Placement.Anchor.longitudeLatitudeHeight;
				var drawn = DrawnSurfaceHeight(_scene.Runner.transform.position, _scene.Runner.transform.up);
				var expected = _scene.Placement.WaterSurface.EllipsoidalHeight(pose.North, pose.East, _scene.Runner.Driver.SimulationTime);
				var geodetic = LocalTangentPlane.World.ToGeodetic(pose.North, pose.East);
				Debug.Log($"Water height {name}: hull {hull.z:F4} m, drawn water {drawn:F4} m, difference {(hull.z - drawn) * 100.0:F2} cm; slice-centre constant would be {(centreOffset - drawn) * 100.0:F2} cm off.");

				Assert.That(Math.Abs(pose.Down), Is.LessThan(1e-6), $"{name}: at rest, no heave");
				Assert.That(hull.x, Is.EqualTo(geodetic.LongitudeDegrees).Within(1e-9), $"{name}: longitude");
				Assert.That(hull.y, Is.EqualTo(geodetic.LatitudeDegrees).Within(1e-9), $"{name}: latitude");
				Assert.That(hull.z, Is.EqualTo(expected - pose.Down).Within(1e-6), $"{name}: placed at the adapter's height");
				Assert.That(hull.z, Is.EqualTo(drawn).Within(Tolerance), $"{name}: the hull sits on the drawn water");
				worstFallbackError = Math.Max(worstFallbackError, Math.Abs(centreOffset - drawn));
			}

			Assert.That(worstFallbackError, Is.GreaterThan(Tolerance), "control: the constant fallback would miss the drawn water by more than the tolerance somewhere");
		}

		/// <summary>
		/// The imported terrain is on the drawn water's NAVD88 frame: a terrain vertex at NAVD88 height h sits where the
		/// water would at level h, within 1 cm, across the slice. Control: as imported, the far tiles stand more than half
		/// a metre off.
		/// </summary>
		[UnityTest]
		public IEnumerator TheImportedTerrain_SitsOnTheWatersNavd88Frame()
		{
			var correction = _scene.Bootstrap.TerrainCorrection;
			Assert.That(correction, Is.Not.Null, "the terrain was moved onto the water's datum");
			Assert.That(correction.MeshesCorrected, Is.GreaterThan(500), "terrain tiles and chart structures");
			Assert.That(correction.ObjectsMoved, Is.GreaterThan(0), "aids and landing strips");
			var datum = _scene.Water.Datum;
			var zero = datum.GeoidCentre;
			var originals = _scene.Bootstrap.TerrainPrefab.transform.Find("Terrain");
			var placed = _scene.Bootstrap.Terrain.transform.Find("Terrain");
			Assert.That(placed.childCount, Is.EqualTo(originals.childCount));
			var worstPlaced = 0.0;
			var worstImported = 0.0;
			var checkedVertices = 0;
			for (var tile = 0; tile < placed.childCount; tile += 5)
			{
				var before = originals.GetChild(tile).GetComponent<MeshFilter>().sharedMesh.vertices;
				var after = placed.GetChild(tile).GetComponent<MeshFilter>().sharedMesh.vertices;
				Assert.That(after.Length, Is.EqualTo(before.Length));
				for (var i = 0; i < before.Length; i += 97)
				{
					var navd88 = before[i].y - zero;
					var expected = WaterSurface.SurfaceUpAt(datum, before[i].z, before[i].x, navd88);
					worstPlaced = Math.Max(worstPlaced, Math.Abs(after[i].y - expected));
					worstImported = Math.Max(worstImported, Math.Abs(before[i].y - expected));
					checkedVertices++;
				}
			}

			Debug.Log($"Imported terrain: {correction}; {checkedVertices} vertices checked, worst {worstPlaced * 100.0:F2} cm from the water's frame (as imported: {worstImported:F2} m).");
			Assert.That(checkedVertices, Is.GreaterThan(100));
			Assert.That(worstPlaced, Is.LessThan(Tolerance));
			Assert.That(worstImported, Is.GreaterThan(0.5), "control: as imported the terrain is off the water's frame");
			yield return null;
		}

		/// <summary>
		/// The drawn water mesh's height under a Unity point: a ray down the local vertical against every triangle of
		/// the mesh, the hit taken through the Cesium georeference to ECEF and then to a WGS84 ellipsoidal height.
		/// </summary>
		private double DrawnSurfaceHeight(Vector3 point, Vector3 up)
		{
			var water = _scene.Water;
			var mesh = water.Mesh;
			Assert.That(mesh, Is.Not.Null, "the water mesh is built");
			var vertices = mesh.vertices;
			var triangles = mesh.triangles;
			var frame = water.transform;
			var origin = frame.InverseTransformPoint(point + up * 50f);
			var direction = frame.InverseTransformDirection(-up).normalized;
			for (var i = 0; i < triangles.Length; i += 3)
			{
				if (Intersect(origin, direction, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]], out var distance))
				{
					var hit = frame.TransformPoint(origin + direction * distance);
					var ecef = _scene.Georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(hit.x, hit.y, hit.z));
					return CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(ecef).z;
				}
			}

			Assert.Fail("the vertical through the hull does not meet the water mesh");
			return double.NaN;
		}

		/// <summary>Möller–Trumbore ray-triangle intersection, both faces.</summary>
		private static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
		{
			distance = 0f;
			var ab = b - a;
			var ac = c - a;
			var p = Vector3.Cross(direction, ac);
			var determinant = Vector3.Dot(ab, p);
			if (Mathf.Abs(determinant) < 1e-9f)
			{
				return false;
			}

			var inverse = 1f / determinant;
			var s = origin - a;
			var u = Vector3.Dot(s, p) * inverse;
			if (u < 0f || u > 1f)
			{
				return false;
			}

			var q = Vector3.Cross(s, ab);
			var v = Vector3.Dot(direction, q) * inverse;
			if (v < 0f || u + v > 1f)
			{
				return false;
			}

			distance = Vector3.Dot(ac, q) * inverse;
			return distance > 0f;
		}
	}
}
