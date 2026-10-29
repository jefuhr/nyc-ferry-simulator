using System;
using System.IO;
using CesiumForUnity;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Sim;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World.HarborNavigation.Tests
{
	public class HarborWorldTests
	{
		private GameObject _root;
		private HarborNavigationWorld _world;
		private CesiumGeoreference _geo;
		private HarborRuntimePack _pack;
		[SetUp] public void SetUp()
		{
			_pack = HarborRuntimePackJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../core/dotnet/FerrySim.Core.Tests/World/Fixtures")));
			_pack = new HarborRuntimePack(_pack.Region, _pack.Digest, _pack.EnvironmentDepthDigest, Array.Empty<HarborAid>(), _pack.Bridges, _pack.Stops, _pack.Barges, _pack.Geoid, _pack.Coverage, _pack.Footprints, _pack.Manifest, _pack.ContactsJson);
			_root = new GameObject("navigation fixture");
			_geo = _root.AddComponent<CesiumGeoreference>();
			_geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
			_world = _root.AddComponent<HarborNavigationWorld>();
			_world.Build(_pack, _geo, new SpatialWater(), null);
		}
		[TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(_root);

		[Test] public void StructureIdentityClosedOutlineAndSourceEvidenceStaySeparateFromContacts()
		{
			Assert.That(_world.Barges.Count, Is.EqualTo(_pack.Barges.Count));
			Assert.That(_world.Labels.Count, Is.EqualTo(_pack.Stops.Count));
			Assert.That(_world.Barges[0].Source, Is.SameAs(_pack.Barges[0]));
			Assert.That(_world.Barges[0].Freeboard, Is.EqualTo(1.2f));
			Assert.That(_world.Barges[0].Draft, Is.EqualTo(1.5f));
			Assert.That(_root.GetComponentsInChildren<Collider>(true), Is.Empty);
			Assert.That(_world.Queries, Is.SameAs(_pack.Queries));
			Assert.That(_world.Barges[0].GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThanOrEqualTo(8));
		}

		[Test] public void BargeSamplesItsOwnTideAndRetainsDoubleAnchorAfterOriginShift()
		{
			_world.Tick(17, null, "1", false, 0);
			var barge = _world.Barges[0];
			var expected = new SpatialWater().EllipsoidalHeight(barge.Anchor.North, barge.Anchor.East, 17);
			Assert.That(barge.Anchor.Height, Is.EqualTo(expected));
			var position = barge.GetComponent<CesiumGlobeAnchor>().positionGlobeFixed;
			_geo.SetOriginLongitudeLatitudeHeight(-73.8, 40.9, 15);
			_world.Tick(17, null, "1", false, 0);
			Assert.That(math.distance(position, barge.GetComponent<CesiumGlobeAnchor>().positionGlobeFixed), Is.LessThan(.001));
		}

		[Test] public void UnknownSpanBoundsRemainUnknownAndDisplayHeightsNeverReachQueries()
		{
			foreach (var span in _world.Spans)
			{
				Assert.That(span.Source.Record["vertical_bounds_navd88_m"].Items[0].IsNull, Is.True);
				Assert.That(span.DeckEstimated, Is.True);
				Assert.That(span.Source.Clearances, Is.Not.Null);
			}
			foreach (var footprint in _pack.Footprints) Assert.That(footprint.CollisionAuthority, Is.False);
			Assert.That(_world.Queries, Is.SameAs(_pack.Queries));
		}

		[Test] public void TickAndResidentQueriesAllocateNothingAfterWarmup()
		{
			var buffer = new HarborFootprint[128];
			var camera = new GameObject("allocation camera").AddComponent<Camera>();
			camera.transform.SetParent(_root.transform, false);
			void Tick(int i)
			{
				camera.transform.position = _world.Labels[0].transform.position + Vector3.right * (i % 2 == 0 ? 100 : 20000);
				_world.Tick(i * .037, camera, i % 2 == 0 ? "1" : "other", i % 3 == 1, i % 3 == 2 ? 200 : 0);
				_world.Queries.Query(0, 0, 1000, buffer, out _);
			}
			for (var i = 0; i < 30; i++) Tick(i);
			var count = NavigationTests.MeasureAllocations(() => { for (var i = 0; i < 100; i++) Tick(i); });
			Assert.That(count, Is.Zero, "changing water, labels, palette, camera visibility and resident queries");
		}

		[Test] public void BoundarySpansKeepEvidenceAndQueriesWithoutExtrapolatedDatum()
		{
			UnityEngine.Object.DestroyImmediate(_world);
			FerrySim.Core.Contracts.HorizontalVector P(double n, double e) => new FerrySim.Core.Contracts.HorizontalVector(n, e);
			HarborGeometry G(double min, double max) => new HarborGeometry("Polygon", new[] { new HarborPolygon(new[] { new[] { P(min, 10), P(max, 10), P(max, 20), P(min, 20), P(min, 10) } }) }, Array.Empty<System.Collections.Generic.IReadOnlyList<FerrySim.Core.Contracts.HorizontalVector>>(), Array.Empty<FerrySim.Core.Contracts.HorizontalVector>());
			var outside = new HarborSpan("outside", "outside", G(600, 700), default, HarborValue.Null);
			var edge = new HarborSpan("edge", "edge", G(10, 1100), default, HarborValue.Null);
			var footprints = new[] { new HarborFootprint("outside", "bridge", outside.Geometry, default, HarborValue.Null), new HarborFootprint("edge", "bridge", edge.Geometry, default, HarborValue.Null) };
			var pack = new HarborRuntimePack("fixture", "", "", Array.Empty<HarborAid>(), new[] { new HarborBridge("group", new[] { outside, edge }, Array.Empty<HarborPier>(), HarborValue.Null) }, Array.Empty<HarborStop>(), Array.Empty<HarborBarge>(), _pack.Geoid, _pack.Coverage, footprints, HarborValue.Null, "{}");
			_world = _root.AddComponent<HarborNavigationWorld>(); _world.Build(pack, _geo, new SpatialWater(), null);
			Assert.That(_world.OmittedSpanIds, Is.EqualTo(new[] { "outside" }));
			Assert.That(_world.EdgeAnchoredSpanIds, Is.EqualTo(new[] { "edge" }));
			Assert.That(_world.Spans[0].Drawn, Is.False);
			Assert.That(_world.Spans[0].GetComponent<CesiumGlobeAnchor>(), Is.Null);
			Assert.That(_world.Spans[0].GetComponentsInChildren<Renderer>(), Is.Empty);
			var results = new HarborFootprint[4];
			Assert.That(_world.Queries.Query(650, 15, 1, results, out var saturated), Is.EqualTo(2));
			Assert.That(saturated, Is.False);
		}

		[Test] public void LabelSelectionDistanceAndPaletteAreIndependentOfSourceNotes()
		{
			var cameraHost = new GameObject("label camera");
			try
			{
				var camera = cameraHost.AddComponent<Camera>(); camera.transform.position = _world.Labels[0].transform.position + Vector3.right * 2000;
				_world.Tick(0, camera, "other", false, 0); Assert.That(_world.Labels[0].Shown, Is.False);
				_world.Tick(0, camera, "1", true, 0); Assert.That(_world.Labels[0].Shown, Is.True);
				Assert.That(_world.Labels[0].Text.color, Is.EqualTo(FerrySim.UI.InstrumentPalette.Night.Text));
				camera.transform.position = _world.Labels[0].transform.position + Vector3.right * 100;
				_world.Tick(0, camera, "other", false, 0); Assert.That(_world.Labels[0].Shown, Is.True);
				Assert.That(_world.Labels[0].Text.color, Is.EqualTo(FerrySim.UI.InstrumentPalette.Day.Text));
				Assert.That(_world.Labels[0].Details, Does.Contain("estimated"));
			}
			finally { UnityEngine.Object.DestroyImmediate(cameraHost); }
		}

		private sealed class SpatialWater : IWaterSurface
		{
			public double EllipsoidalHeight(double north, double east, double time) => -31 + north * .001 + east * .002 + time * .01;
		}
	}
}
