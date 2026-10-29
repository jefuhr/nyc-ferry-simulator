using System;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	// Failure modes: slice clipping; filling inactive tile holes; stale scalar tide; flat-world/float-ECEF
	// placement; origin shifts; lost long-range lights; lights orphaned on disable/reconnect; frame GC.
	public partial class HarborTrafficTests
	{
		private TrafficTestPacks _packs;
		private GameObject _world;
		private VesselSimulationRunner _runner;
		private CesiumGeoreference _geo;
		private TrafficView _view;
		private Camera _camera;
		private readonly SpatialWater _water = new SpatialWater();

		private sealed class SpatialWater : IWaterSurface
		{
			public int Calls;
			public double EllipsoidalHeight(double north, double east, double time)
			{
				Calls++;
				return -32 + north * 0.0001 + east * 0.00005 + time * 0.1;
			}
		}

		[SetUp]
		public void SetUp()
		{
			_packs = new TrafficTestPacks();
			_world = new GameObject("harbor traffic fixture");
			_geo = _world.AddComponent<CesiumGeoreference>();
			_geo.SetOriginLongitudeLatitudeHeight(WorldOrigin.LongitudeDegrees, WorldOrigin.LatitudeDegrees, 0);
			_runner = new GameObject("runner").AddComponent<VesselSimulationRunner>();
			_runner.transform.SetParent(_world.transform, false);
			_runner.RecordRuns = false;
			_runner.MaxStepsPerFrame = 100;
			_runner.EnvironmentPacks = _packs.EnvironmentPacks;
			_runner.TrafficPacks = _packs.TrafficPacks;
			_camera = new GameObject("camera").AddComponent<Camera>();
			_camera.transform.SetParent(_world.transform, false);
			_view = _world.AddComponent<TrafficView>();
		}

		[TearDown]
		public void TearDown()
		{
			Object.DestroyImmediate(_world);
			_packs.Dispose();
		}

		private void Start(params TrafficTestPacks.Track[] tracks)
		{
			_packs.WriteTraffic(TrafficTestPacks.Date, tracks);
			_runner.Initialize(TrafficTestPacks.Scenario(), new ConstantControls(ControlInputs.Neutral));
			_view.ConnectHarbor(_runner, _geo, _water, new TrafficTileArea(new[] { new TrafficBounds(-10000, 32000, -12000, 28000) }), _camera);
			_view.ViewRange = 100000;
		}

		[Test]
		public void DistantHullsFollowTheirOwnWaterAndTime_AndSurviveOriginShift()
		{
			Start(TrafficTestPacks.Track.Straight(366000001, 43100, 24037, 21081, 0, 0, 12),
				TrafficTestPacks.Track.Straight(366000002, 43100, -7031, -5123, 0, 1, 12));
			_view.Place();
			CheckPositions();
			_runner.AdvanceFrame(0.5);
			_view.Place();
			Assert.That(_view.PlacedTime, Is.GreaterThan(0));
			CheckPositions();
			_geo.SetOriginLongitudeLatitudeHeight(-73.85, 40.85, 10);
			_view.Place();
			CheckPositions();
			Assert.That(_view.ShownCount, Is.EqualTo(2));
		}

		private void CheckPositions()
		{
			foreach (var hull in _view.Hulls)
			{
				var ll = LocalTangentPlane.World.ToGeodetic(hull.North, hull.East);
				var expected = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees,
					_water.EllipsoidalHeight(hull.North, hull.East, _view.PlacedTime)));
				var actual = _geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)hull.transform.localPosition);
				Assert.That(math.distance(actual, expected), Is.LessThan(0.01), hull.Mmsi.ToString());
			}
		}

		[Test]
		public void TileUnionDoesNotFillHoles_AndUsesReplayBounds()
		{
			var pack = _packs.WriteTraffic(TrafficTestPacks.Date,
				TrafficTestPacks.Track.Straight(366000001, 43100, 10000, 10000, 0, 0, 12));
			var area = new TrafficTileArea(new[] { new TrafficBounds(-1000, 1000, -1000, 1000), new TrafficBounds(9000, 11000, 9000, 11000) });
			Assert.That(area.Contains(5000, 5000), Is.False);
			Assert.That(area.Contains(10000, 10000), Is.True);
			Assert.That(area.MayEnter(new FerrySim.Core.Traffic.TrafficReplay(pack), 0), Is.True);
		}

		[Test]
		public void HarborScalePlacementAllocatesNothing()
		{
			var tracks = new TrafficTestPacks.Track[485];
			for (var i = 0; i < tracks.Length; i++)
				tracks[i] = TrafficTestPacks.Track.Straight(366100000 + i, 43100, -7000 + i % 25 * 1300, -9000 + i / 25 * 1400, 1, 0.2, 12);
			Start(tracks);
			for (var i = 0; i < 5; i++) _view.Place();
			Assert.That(() => _view.Place(), Is.Not.AllocatingGCMemory());
			Assert.That(_view.ShownCount, Is.EqualTo(485));
			Assert.That(_view.RosterCount, Is.EqualTo(485));
			Assert.That(_view.MissingTargets, Is.Zero);
		}
		[Test]
		public void LongRangeLightsSurviveHullCutoff_AndDetachOnReconnect()
		{
			var track = TrafficTestPacks.Track.Straight(366000001, 43100, 1000, 0, 0, 0, 12);
			track.Length = 90;
			Start(track);
			var sun = _world.AddComponent<SunController>();
			sun.SetUtc(new DateTime(2025, 7, 14, 6, 0, 0, DateTimeKind.Utc));
			sun.Apply();
			_view.Sun = sun;
			_view.ViewRange = 4000;
			_camera.transform.localPosition = new Vector3(0, 10, 4500);
			_view.Place();
			var hull = _view.Hulls[0];
			Assert.That(hull.DetailedHullShown, Is.True);
			_camera.transform.localPosition = new Vector3(0, 10, 6000);
			_view.Place();
			Assert.That(hull.DetailedHullShown, Is.False);
			Assert.That(hull.GetComponent<MeshRenderer>().enabled, Is.False);
			Assert.That(hull.gameObject.activeInHierarchy, Is.True, "long-range signal host stays alive");
			Assert.That(_view.ShownCount, Is.Zero);
			Assert.That(_view.LightsDrawn, Is.GreaterThan(0));
			_camera.transform.localPosition = new Vector3(0, 10, 25000);
			_view.Place();
			Assert.That(hull.gameObject.activeInHierarchy, Is.False, "past all optics ranges");
			_view.ConnectHarbor(_runner, _geo, _water, new TrafficTileArea(new[] { new TrafficBounds(-1000, 2000, -1000, 1000) }), _camera, sun);
			Assert.That(hull == null, Is.True, "old light hierarchy destroyed on reconnect");
			Assert.That(_world.GetComponentsInChildren<NavigationLights>(true).Length, Is.EqualTo(1));
		}

		[Test]
		public void MatchingGenericHullsShareMeshes_WithoutSharingVisibility()
		{
			Start(TrafficTestPacks.Track.Straight(366000001, 43100, 100, 100, 0, 0, 12),
				TrafficTestPacks.Track.Straight(366000002, 43100, 10000, 10000, 0, 0, 12));
			_view.ViewRange = 4000;
			_view.Place();
			Assert.That(_view.Hulls[0].GetComponent<MeshFilter>().sharedMesh, Is.SameAs(_view.Hulls[1].GetComponent<MeshFilter>().sharedMesh));
			Assert.That(_view.Hulls[0].Shown, Is.True);
			Assert.That(_view.Hulls[1].Shown, Is.False);
		}

		[Test]
		public void TileEligibilityKeepsHermiteBulge_WhenReportsAndStraightChordStayOutside()
		{
			var speed = Math.Sqrt(10 * 10 + 10.2 * 10.2);
			var curve = new TrafficTestPacks.Track { Mmsi = 366000010 };
			curve.Reports.Add(new[] { 43200.0, 200, 1001, speed, Angles.WrapTwoPi(Math.Atan2(-10.2, 10)), double.NaN });
			curve.Reports.Add(new[] { 43300.0, 1200, 1001, speed, Math.Atan2(10.2, 10), double.NaN });
			Start(curve);
			var area = new TrafficTileArea(new[] { new TrafficBounds(0, 1000, 0, 1000) });
			_view.ConnectHarbor(_runner, _geo, _water, area, _camera);
			Assert.That(_view.Hulls.Count, Is.EqualTo(1));
			for (var i = 0; i < 500; i++) _runner.AdvanceFrame(0.1);
			_view.Place();
			Assert.That(_view.Hulls[0].Shown, Is.True);
			Assert.That(_view.Hulls[0].East, Is.LessThan(800));
			Assert.That(_view.MissingTargets, Is.Zero);
		}

		[Test]
		public void BakedGlobePositionsStayWithinThreeMillimetresAtCornersAndCellInteriors()
		{
			var lattice = new TrafficGlobeLattice(new TrafficBounds(-12000, 32000, -12000, 28000));
			var random = new System.Random(731);
			for (var i = 0; i < 1000; i++)
			{
				var n = i < 4 ? (i < 2 ? -12000 : 32000) : -12000 + random.NextDouble() * 44000;
				var e = i < 4 ? (i % 2 == 0 ? -12000 : 28000) : -12000 + random.NextDouble() * 40000;
				lattice.Sample(n, e, -32, out var actual, out var up, out var north);
				var ll = LocalTangentPlane.World.ToGeodetic(n, e);
				var expected = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, -32));
				Assert.That(math.distance(actual, expected), Is.LessThan(0.003));
				Assert.That(math.abs(math.dot(up, north)), Is.LessThan(1e-8));
			}
		}

	}
}
