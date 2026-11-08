using System;
using System.Reflection;
using CesiumForUnity;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.FrameTail.Tests
{
	public class HarborOriginShiftTests
	{
		private GameObject _root, _camera;
		private CesiumGeoreference _georeference;
		private CesiumGlobeAnchor _anchor;
		private HarborOriginShift _shift;

		[SetUp]
		public void SetUp()
		{
			_root = new GameObject("origin fixture");
			_georeference = _root.AddComponent<CesiumGeoreference>();
			_georeference.SetOriginLongitudeLatitudeHeight(-73.98, 40.72, 0);
			_camera = new GameObject("camera anchor");
			_camera.transform.SetParent(_root.transform, false);
			_anchor = _camera.AddComponent<CesiumGlobeAnchor>();
			_anchor.detectTransformChanges = false;
			_shift = _camera.AddComponent<HarborOriginShift>();
			_shift.Configure(_georeference, _anchor, 100);
		}

		[TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(_root);
		private double3 Origin => new double3(_georeference.ecefX, _georeference.ecefY, _georeference.ecefZ);

		[Test]
		public void ExactThresholdMovesTheOriginAndRetainsGlobeCoordinates()
		{
			var origin = Origin;
			_anchor.positionGlobeFixed = origin + new double3(99.99, 0, 0);
			Assert.That(_shift.Advance(), Is.False);
			Assert.That(math.distance(Origin, origin), Is.LessThan(1e-8));
			var target = origin + new double3(100, 0, 0);
			_anchor.positionGlobeFixed = target;
			Assert.That(_shift.Advance(), Is.True);
			Assert.That(math.distance(Origin, target), Is.LessThan(1e-8));
			Assert.That(math.distance(_anchor.positionGlobeFixed, target), Is.LessThan(1e-8));
			Assert.That(_camera.transform.position.magnitude, Is.LessThan(.001f));
			Assert.That(_shift.Advance(), Is.False);
		}

		[Test]
		public void DisabledAnchorCannotMoveOrigin()
		{
			var origin = Origin;
			_anchor.positionGlobeFixed = origin + new double3(120, 0, 0);
			_anchor.enabled = false;
			Assert.That(_shift.Advance(), Is.False);
			Assert.That(math.distance(Origin, origin), Is.LessThan(1e-8));
		}

		[Test]
		public void UnsupportedSubscenesAreRejectedAtConfigurationIncludingInactiveOnes()
		{
			var subscene = new GameObject("unsupported subscene");
			subscene.SetActive(false);
			subscene.transform.SetParent(_root.transform, false);
			subscene.AddComponent<CesiumSubScene>();
			Assert.Throws<InvalidOperationException>(() => _shift.Configure(_georeference, _anchor, 100));
		}

		[TestCase(-1)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		public void InvalidDistanceIsRejected(double distance) =>
			Assert.Throws<ArgumentOutOfRangeException>(() => _shift.Configure(_georeference, _anchor, distance));

		[Test]
		public void RepeatedShiftsRetainAnotherAnchorsDoublePosition()
		{
			var other = new GameObject("fixed globe anchor");
			other.transform.SetParent(_root.transform, false);
			var anchor = other.AddComponent<CesiumGlobeAnchor>();
			anchor.detectTransformChanges = false;
			var fixedPosition = Origin + new double3(500, 250, -80);
			anchor.positionGlobeFixed = fixedPosition;
			for (var i = 0; i < 20; i++)
			{
				_anchor.positionGlobeFixed = Origin + new double3(101, -50, 30);
				Assert.That(_shift.Advance(), Is.True);
				Assert.That(math.distance(anchor.positionGlobeFixed, fixedPosition), Is.LessThan(.00001));
			}
		}

		[Test]
		public void StationaryAndShiftingUpdatesAllocateNothing()
		{
			for (var i = 0; i < 10; i++) { _shift.Advance(); MoveAndShift(); }
			Action stationary = () => { for (var i = 0; i < 256; i++) _shift.Advance(); };
			Action shifting = () => { for (var i = 0; i < 32; i++) MoveAndShift(); };
			Assert.That(MeasureAllocations(stationary), Is.Zero);
			Assert.That(MeasureAllocations(shifting), Is.Zero);
		}

		private void MoveAndShift()
		{
			_anchor.positionGlobeFixed = Origin + new double3(101, 0, 0);
			_shift.Advance();
		}

		[Test]
		public void IsolatedCpuComparisonAgainstPinnedCesiumHierarchyScan()
		{
			if (Application.platform != RuntimePlatform.OSXEditor) Assert.Ignore("Darwin thread CPU measurement is Mac-only; functional and allocation tests are portable.");
			for (var i = 0; i < 12000; i++)
			{
				var child = new GameObject("fixture child");
				child.transform.SetParent(_root.transform, false);
			}
			var original = _camera.GetComponent<CesiumOriginShift>();
			if (original == null) original = _camera.AddComponent<CesiumOriginShift>();
			original.distance = 100;
			original.enabled = false;
			var method = typeof(CesiumOriginShift).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
			var originalUpdate = (Action)Delegate.CreateDelegate(typeof(Action), original, method);
			Action optimizedUpdate = () => _shift.Advance();
			for (var i = 0; i < 10; i++) { originalUpdate(); optimizedUpdate(); }
			var originalMs = CpuMilliseconds(originalUpdate, 128);
			var optimizedMs = CpuMilliseconds(optimizedUpdate, 128);
			TestContext.WriteLine("12000 children, 128 updates: pinned Cesium CPU ms=" + originalMs + "; harbor CPU ms=" + optimizedMs);
			Assert.That(originalMs, Is.GreaterThan(0));
			Assert.That(optimizedMs, Is.LessThan(originalMs * .2), "eliminating the recursive scan must reduce isolated CPU cost by at least 80%");
		}

		private static double CpuMilliseconds(Action action, int count)
		{
			var before = HarborFrameProfile.ReadThreadCpuMilliseconds();
			for (var i = 0; i < count; i++) action();
			return HarborFrameProfile.ReadThreadCpuMilliseconds() - before;
		}

		private static int MeasureAllocations(Action action)
		{
			var chars = new char[16];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true;
				var control = new string(chars);
				recorder.enabled = false;
				var calibration = recorder.sampleBlockCount;
				GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				recorder.enabled = true;
				action();
				recorder.enabled = false;
				var count = recorder.sampleBlockCount;
				TestContext.WriteLine("GC.Alloc positive control=" + calibration + "; measured samples=" + count);
				return count;
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}
	}
}
