using System;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// The hulls. Failure modes guarded: a hull not where the core's replay puts it at the learner's drawn time, or not on the
	/// drawn water; a hull shown outside its vessel's segments, outside the slice's area or past the view range, or the left-out
	/// vessel shown; a static vessel that moves; a hull not sized from AIS (or the class default without it); the public hull
	/// component not saying who and what it is; garbage per frame; no hull for a vessel whose curve bulges into the area while its
	/// reports and the lines between them stay out.
	/// </summary>
	public class TrafficViewTests
	{
		private TrafficTestPacks _packs;
		private GameObject _host;
		private GameObject _world;
		private SliceDatum _datum;

		// The slice's area: about 1.1 km either side of the origin north and south, 0.84 km east and west.
		private const double HalfLatitude = 0.01;
		private const double HalfLongitude = 0.01;

		[SetUp]
		public void SetUp() => _packs = new TrafficTestPacks();

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			if (_world != null)
			{
				Object.DestroyImmediate(_world);
			}

			if (_datum != null)
			{
				Object.DestroyImmediate(_datum);
			}

			_packs.Dispose();
		}

		private static TrafficTestPacks.Track Moored()
		{
			var barge = new TrafficTestPacks.Track { Mmsi = 366000003, Name = "BARGE", Class = TrafficClass.Tanker, Length = 90.0, Beam = 20.0, Moving = false };
			for (var k = 0; k < 20; k++)
			{
				barge.Reports.Add(new[] { 42000.0 + 120.0 * k, -400.0 + (k % 3), 300.0 - (k % 2), 0.0, double.NaN, Angles.ToRadians(80.0) });
			}

			return barge;
		}

		/// <summary>The runner on the test day, a view in a world frame over a water surface whose datum is the test's small area.</summary>
		private TrafficView View(string leaveOut, params TrafficTestPacks.Track[] tracks)
		{
			var origin = LocalTangentPlane.World.ToGeodetic(0.0, 0.0);
			return ViewOver(origin.LatitudeDegrees - HalfLatitude, origin.LatitudeDegrees + HalfLatitude, origin.LongitudeDegrees - HalfLongitude, origin.LongitudeDegrees + HalfLongitude,
				TrafficView.DefaultAreaMargin, leaveOut, tracks);
		}

		/// <summary>As <see cref="View"/>, over a datum with the given corners (degrees) and the given area margin (m).</summary>
		private TrafficView ViewOver(double south, double north, double west, double east, float margin, string leaveOut, params TrafficTestPacks.Track[] tracks)
		{
			_packs.WriteTraffic(TrafficTestPacks.Date, tracks);
			_host = new GameObject("traffic view runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			runner.EnvironmentPacks = _packs.EnvironmentPacks;
			runner.TrafficPacks = _packs.TrafficPacks;
			runner.MaxStepsPerFrame = 100;
			runner.Initialize(TrafficTestPacks.Scenario(leaveOut: leaveOut), new ConstantControls(ControlInputs.Neutral));
			_world = new GameObject("traffic view world (test)");
			_datum = ScriptableObject.CreateInstance<SliceDatum>();
			_datum.Assign(new SliceDatumValues
			{
				SliceName = "traffic-test", SouthLatitude = south, NorthLatitude = north, WestLongitude = west, EastLongitude = east,
				GeoidModel = "test", GeoidSouthWest = -31.8, GeoidSouthEast = -31.7, GeoidNorthWest = -31.9, GeoidNorthEast = -31.8, GeoidCentre = -31.8,
			});
			var waterObject = new GameObject("Water", typeof(MeshFilter), typeof(MeshRenderer));
			waterObject.transform.SetParent(_world.transform, false);
			var water = waterObject.AddComponent<WaterSurface>();
			water.Configure(_datum);
			water.WaterLevelNavd88 = 0.4;
			var view = _world.AddComponent<TrafficView>();
			view.AreaMargin = margin;
			view.Connect(runner, _world.transform, water, null);
			return view;
		}

		// Failure modes: no lights, the wrong Rule 23 length set, stopped treated as moored,
		// or a static vessel showing underway lights. Daylight and fog are checked through the real sky.
		[Test]
		public void HullLights_FollowLengthStatusDaylightAndFog()
		{
			var tug = TrafficTestPacks.Track.Straight(366000001, 43100.0, 600.0, -200.0, 4.0, 0.0, 12);
			var stopped = TrafficTestPacks.Track.Straight(366000002, 43100.0, 200.0, -200.0, 0.0, 0.0, 12);
			stopped.Length = 60.0;
			var view = View(ScenarioTraffic.Off, tug, stopped, Moored());
			var sky = _world.AddComponent<SunController>();
			view.Sun = sky;
			sky.SetUtc(new DateTime(2025, 7, 14, 4, 0, 0, DateTimeKind.Utc));
			view.Place();
			Assert.That(view.HullFor(tug.Mmsi).NavigationLights.Lights.Count, Is.EqualTo(4));
			Assert.That(view.HullFor(stopped.Mmsi).NavigationLights.Lights.Count, Is.EqualTo(5));
			Assert.That(view.HullFor(stopped.Mmsi).Status, Is.EqualTo(TrafficHullStatus.Stopped));
			Assert.That(view.HullFor(tug.Mmsi).NavigationLights.On, Is.True);
			Assert.That(view.HullFor(366000003).NavigationLights.Lights.Count, Is.Zero);
			sky.SetUtc(new DateTime(2025, 7, 14, 16, 0, 0, DateTimeKind.Utc));
			view.Place();
			Assert.That(view.HullFor(tug.Mmsi).NavigationLights.On, Is.False);
			sky.VisibilityMetres = 463;
			view.Place();
			Assert.That(view.HullFor(tug.Mmsi).NavigationLights.On, Is.True);
			view.NavigationLightsEnabled = false;
			view.Place();
			Assert.That(view.HullFor(tug.Mmsi).NavigationLights.On, Is.False);
		}

		// Failure mode: a cached steady-state light is allocation-free, but switching the sky,
		// restricted visibility or the paired benchmark switch allocates in the added light path.
		[Test]
		public void HullLightTransitions_AllocateNothingUnderWay()
		{
			var tug = TrafficTestPacks.Track.Straight(366000001, 43100.0, 600.0, -200.0, 4.0, 0.0, 12);
			var stopped = TrafficTestPacks.Track.Straight(366000002, 43100.0, 200.0, -200.0, 0.0, 0.0, 12);
			stopped.Length = 60.0;
			var view = View(ScenarioTraffic.Off, tug, stopped, Moored());
			var sky = _world.AddComponent<SunController>();
			view.Sun = sky;
			var night = new DateTime(2025, 7, 14, 4, 0, 0, DateTimeKind.Utc);
			var day = new DateTime(2025, 7, 14, 16, 0, 0, DateTimeKind.Utc);
			void Transitions()
			{
				view.Runner.AdvanceFrame(1.0 / 60.0);
				view.NavigationLightsEnabled = true;
				sky.VisibilityMetres = 0;
				sky.SetUtc(night);
				view.Place();
				sky.SetUtc(day);
				view.Place();
				sky.VisibilityMetres = 463;
				view.Place();
				view.NavigationLightsEnabled = false;
				view.Place();
			}

			Transitions();
			Assert.That(Transitions, Is.Not.AllocatingGCMemory(), "warmed runner, hull placement and every light on/off transition; recording off");
		}

		[Test]
		public void AVesselWhoseCurveBulgesIntoTheArea_GetsAHull_AndIsDrawnThere_ThoughNoReportOrLineDoes()
		{
			// The wave 2b review's case, against an area of [0, 1000] m both ways (a datum on those corners, no margin): reports at
			// (200, 1001) and (1200, 1001), 100 s apart from the run's start, end velocities (10, −10.2) and (10, 10.2) m/s. The
			// replay's curve bulges 254 m into the area; the reports and the line between them stay 1 m east of it.
			var speed = Math.Sqrt(10.0 * 10.0 + 10.2 * 10.2);
			var curve = new TrafficTestPacks.Track { Mmsi = 366000010, Name = "CURVE" };
			curve.Reports.Add(new[] { TrafficTestPacks.StartPackTime, 200.0, 1001.0, speed, Angles.WrapTwoPi(Math.Atan2(-10.2, 10.0)), double.NaN });
			curve.Reports.Add(new[] { TrafficTestPacks.StartPackTime + 100.0, 1200.0, 1001.0, speed, Math.Atan2(10.2, 10.0), double.NaN });
			// The same reports without velocities: the straight line along east 1001, never inside.
			var straight = new TrafficTestPacks.Track { Mmsi = 366000011, Name = "STRAIGHT" };
			straight.Reports.Add(new[] { TrafficTestPacks.StartPackTime, 200.0, 1001.0, double.NaN, double.NaN, double.NaN });
			straight.Reports.Add(new[] { TrafficTestPacks.StartPackTime + 100.0, 1200.0, 1001.0, double.NaN, double.NaN, double.NaN });
			var southWest = LocalTangentPlane.World.ToGeodetic(0.0, 0.0);
			var view = ViewOver(southWest.LatitudeDegrees, LocalTangentPlane.World.ToGeodetic(1000.0, 0.0).LatitudeDegrees,
				southWest.LongitudeDegrees, LocalTangentPlane.World.ToGeodetic(0.0, 1000.0).LongitudeDegrees, 0f, ScenarioTraffic.Off, curve, straight);
			WaterSurface.SliceBounds(_datum, out var minNorth, out var maxNorth, out var minEast, out var maxEast);
			Assert.That(minNorth, Is.EqualTo(0.0).Within(0.5), "the area, to half a metre");
			Assert.That(maxNorth, Is.EqualTo(1000.0).Within(0.5));
			Assert.That(minEast, Is.EqualTo(0.0).Within(0.5));
			Assert.That(maxEast, Is.EqualTo(1000.0).Within(0.5), "the reports, at 1001, lie east of it");
			Assert.That(view.HullFor(366000011), Is.Null, "the straight line never enters: no hull");
			var hull = view.HullFor(366000010);
			Assert.That(hull, Is.Not.Null, "the curve enters: a hull");
			var runner = view.Runner;
			// Half-second frames, then single steps, so the hull is drawn at 50 s itself.
			while (TrafficView.RenderTime(runner.Driver) < 50.0)
			{
				runner.AdvanceFrame(TrafficView.RenderTime(runner.Driver) < 49.4 ? 0.5 : 0.01);
			}

			view.Place();
			Assert.That(view.PlacedTime, Is.EqualTo(50.0).Within(0.011), "drawn at 50 s");
			Assert.That(runner.Traffic.Replay.TrySample(hull.VesselIndex, runner.Traffic.PackTime(view.PlacedTime), out var sample), Is.True);
			Assert.That(sample.Motion, Is.EqualTo(TrafficMotion.Hermite));
			Assert.That(sample.East, Is.LessThan(maxEast - 200.0), "well inside the area");
			Assert.That(hull.Shown && hull.gameObject.activeSelf, Is.True, "and drawn");
			Assert.That(hull.transform.localPosition.x, Is.EqualTo((float)sample.East));
			Assert.That(hull.transform.localPosition.z, Is.EqualTo((float)sample.North));
		}

		[Test]
		public void Hulls_AreWhereTheReplaySays_AtTheLearnersDrawnTime_OnTheDrawnWater()
		{
			var tug = TrafficTestPacks.Track.Straight(366000001, 43100.0, 600.0, -200.0, 4.0, Angles.ToRadians(200.0), 12);
			var view = View(ScenarioTraffic.Off, tug, Moored());
			var runner = view.Runner;
			var traffic = runner.Traffic;
			Assert.That(view.Hulls.Count, Is.EqualTo(2));
			for (var frame = 0; frame < 40; frame++)
			{
				runner.AdvanceFrame(0.0173);
				view.Place();
				var time = TrafficView.RenderTime(runner.Driver);
				var steps = runner.Driver.StepCount;
				Assert.That(time, Is.EqualTo(SimulationClock.TimeAtStep(steps - 1) + runner.Driver.InterpolationFactor * SimulationClock.FixedTimeStep), "between the last two physics states, as the vessel is drawn");
				Assert.That(view.PlacedTime, Is.EqualTo(time));
				foreach (var hull in view.Hulls)
				{
					Assert.That(traffic.Replay.TrySample(hull.VesselIndex, traffic.PackTime(time), out var sample), Is.True);
					var at = hull.transform.localPosition;
					Assert.That(hull.gameObject.activeSelf && hull.Shown, Is.True);
					Assert.That(at.z, Is.EqualTo((float)sample.North), $"{hull.Mmsi} north");
					Assert.That(at.x, Is.EqualTo((float)sample.East), $"{hull.Mmsi} east");
					Assert.That(at.y, Is.EqualTo((float)view.GetComponentInChildren<WaterSurface>().SurfaceUp(sample.North, sample.East)).Within(1e-3), "floating on the drawn water");
					Assert.That(Mathf.DeltaAngle(hull.transform.localEulerAngles.y, (float)Angles.ToDegrees(sample.Heading)), Is.EqualTo(0f).Within(1e-3f));
					Assert.That(hull.Heading, Is.EqualTo(sample.Heading));
				}
			}

			var moving = view.HullFor(366000001);
			Assert.That((moving.Mmsi, moving.Class, moving.ClassName, moving.Length, moving.Beam), Is.EqualTo((366000001, TrafficClass.TugTow, "tug-tow", 30.0, 10.0)));
			Assert.That(moving.SpeedOverGround, Is.EqualTo(4.0).Within(1e-4));
			Assert.That(moving.Status, Is.EqualTo(TrafficHullStatus.Underway));
			Assert.That(moving.Height, Is.GreaterThan(4.0));
			var bounds = moving.GetComponent<MeshFilter>().sharedMesh.bounds;
			Assert.That(bounds.size.z, Is.EqualTo(30f).Within(1e-4f), "the AIS length, bow to stern");
			Assert.That(bounds.size.x, Is.EqualTo(10f).Within(1e-4f), "the AIS beam");
			var barge = view.HullFor(366000003);
			Assert.That(barge.Status, Is.EqualTo(TrafficHullStatus.Moored));
			Assert.That((barge.North, barge.East), Is.EqualTo((runner.Traffic.Pack.Vessel(366000003).StaticNorth, runner.Traffic.Pack.Vessel(366000003).StaticEast)), "a static vessel at its median");
			Assert.That(barge.SpeedOverGround, Is.Zero);
			Assert.That(Angles.ToDegrees(barge.Heading), Is.EqualTo(80.0).Within(1e-4));
		}

		[Test]
		public void OnlyTheRunsTrafficInsideTheAreaAndRange_IsDrawn()
		{
			var inside = TrafficTestPacks.Track.Straight(366000001, 43100.0, 600.0, -200.0, 4.0, Angles.ToRadians(200.0), 12);
			var far = TrafficTestPacks.Track.Straight(366000004, 43100.0, 0.0, 6000.0, 4.0, 0.0, 12);
			// Crosses the area between two reports outside it, 3.3 km apart, 300 s apart: a hull is built and shown in the middle.
			var crossing = TrafficTestPacks.Track.Straight(366000005, 43080.0, 1700.0, 300.0, 11.0, Math.PI, 2, 300.0);
			// Its segment ends 30 s into the run.
			var leaving = TrafficTestPacks.Track.Straight(366000006, 42990.0, -300.0, -300.0, 1.0, 0.0, 5);
			var ferry = TrafficTestPacks.Track.Straight(367000001, 43140.0, 30.0, 20.0, 0.0, 0.0, 5);
			ferry.Ferry = true;
			var view = View(ScenarioTraffic.Automatic, inside, far, crossing, leaving, ferry, Moored());
			var runner = view.Runner;
			Assert.That(view.HullFor(366000004), Is.Null, "never near the slice: no hull");
			Assert.That(view.HullFor(366000005), Is.Not.Null, "its line between reports crosses the area");
			Assert.That(runner.Traffic.Removed.Mmsi, Is.EqualTo(367000001));
			for (var frame = 0; frame < 10; frame++)
			{
				runner.AdvanceFrame(1.0);
			}

			view.Place();
			Assert.That(view.HullFor(367000001).Shown, Is.False, "the learner's own ferry, left out");
			Assert.That(view.HullFor(366000006).Shown, Is.True);
			Assert.That(view.HullFor(366000005).Shown, Is.True, "at 43210 s it is 270 m north, inside the area");
			for (var frame = 0; frame < 25; frame++)
			{
				runner.AdvanceFrame(1.0);
				view.Place();
			}

			Assert.That(view.HullFor(366000006).Shown, Is.False, "its segment ended at 43230 s");
			Assert.That(view.HullFor(366000006).gameObject.activeSelf, Is.False);
			Assert.That(view.HullFor(367000001).Shown, Is.False);
			// A camera 1.5 km south of the origin: with a 1 km range neither the tug (1.64 km away) nor the barge (1.14 km) is drawn;
			// with 1.2 km the barge is.
			var camera = new GameObject("traffic view camera (test)").AddComponent<Camera>();
			try
			{
				camera.transform.SetParent(_world.transform, false);
				camera.transform.localPosition = new Vector3(0f, 10f, -1500f);
				view.Connect(runner, _world.transform, view.GetComponentInChildren<WaterSurface>(), camera);
				view.ViewRange = 1000f;
				view.Place();
				Assert.That(view.HullFor(366000001).Shown, Is.False, "the tug is 1.64 km from the camera");
				Assert.That(view.HullFor(366000003).Shown, Is.False, "the barge 1.14 km");
				view.ViewRange = 1200f;
				view.Place();
				Assert.That(view.HullFor(366000001).Shown, Is.False);
				Assert.That(view.HullFor(366000003).Shown, Is.True, "inside 1.2 km");
			}
			finally
			{
				Object.DestroyImmediate(camera.gameObject);
			}
		}

		[Test]
		public void Hulls_AreSizedFromAis_OrTheClassDefault()
		{
			var sized = TrafficTestPacks.Track.Straight(366000001, 43100.0, 100.0, 100.0, 1.0, 0.0, 12);
			sized.Class = TrafficClass.Cargo;
			sized.Length = 180.0;
			sized.Beam = 28.0;
			var unsized = TrafficTestPacks.Track.Straight(366000002, 43100.0, -100.0, -100.0, 1.0, 0.0, 12);
			unsized.Class = TrafficClass.Pleasure;
			unsized.Length = double.NaN;
			unsized.Beam = double.NaN;
			var view = View(ScenarioTraffic.Off, sized, unsized);
			var ship = view.HullFor(366000001);
			Assert.That((ship.Length, ship.Beam, ship.LengthFromAis, ship.BeamFromAis), Is.EqualTo((180.0, 28.0, true, true)));
			var size = ship.GetComponent<MeshFilter>().sharedMesh.bounds.size;
			Assert.That((size.z, size.x), Is.EqualTo((180f, 28f)));
			var boat = view.HullFor(366000002);
			Assert.That((boat.Length, boat.Beam, boat.LengthFromAis, boat.BeamFromAis), Is.EqualTo((10.0, 3.5, false, false)), "the pleasure craft default");
			Assert.That(boat.GetComponent<MeshRenderer>().sharedMaterial, Is.Not.SameAs(ship.GetComponent<MeshRenderer>().sharedMaterial), "a colour per class");
		}

		[Test]
		public void Placing_MakesNoGarbagePerFrame()
		{
			var tracks = Enumerable.Range(0, 60).Select(i => TrafficTestPacks.Track.Straight(366100000 + i, 43150.0 + i, -900.0 + 30 * i, 600.0 - 20 * i, 2.0 + 0.05 * i, 0.1 * i, 80)).ToList();
			tracks.Add(Moored());
			var view = View(ScenarioTraffic.Off, tracks.ToArray());
			var runner = view.Runner;
			for (var frame = 0; frame < 10; frame++)
			{
				runner.AdvanceFrame(1.0 / 60.0);
				view.Place();
			}

			void Frame()
			{
				runner.AdvanceFrame(1.0 / 60.0);
				view.Place();
			}

			Assert.That(Frame, Is.Not.AllocatingGCMemory());
			Assert.That(view.ShownCount, Is.GreaterThan(40));
			Assert.That(view.LastMilliseconds, Is.GreaterThan(0.0));
		}
	}
}
