using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// The generated greybox against the pipeline outputs. Failure modes guarded: GeoJSON [lon, lat]
	/// read as [lat, lon], local_coordinates [north, east] read as [east, north], a landing dropped or
	/// duplicated, the confidence flag missing from the label, bridge clearance taken as NAVD88 or as
	/// height above the water instead of above MHW, and bridges matched to the wrong ENC features.
	/// </summary>
	public class GreyboxTests
	{
		private GameObject _prefab;
		private SliceDatum _datum;

		[SetUp]
		public void SetUp()
		{
			_prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GreyboxBuilder.PrefabPath);
			_datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(GreyboxBuilder.DatumPath);
			Assert.That(_prefab, Is.Not.Null, GreyboxBuilder.PrefabPath + " is missing; run FerrySim > World > Rebuild All.");
			Assert.That(_datum, Is.Not.Null, GreyboxBuilder.DatumPath + " is missing.");
		}

		private LandingBarge[] Barges => _prefab.GetComponentsInChildren<LandingBarge>(true);

		private BridgeGreybox[] Bridges => _prefab.GetComponentsInChildren<BridgeGreybox>(true);

		[Test]
		public void EveryBarge_HasHullLabelAndFace()
		{
			Assert.That(Barges.Length, Is.GreaterThanOrEqualTo(11));
			Assert.That(Barges.Select(b => b.StopId).Distinct().Count(), Is.EqualTo(Barges.Length), "one barge per stop");
			foreach (var barge in Barges)
			{
				var hull = barge.transform.Find("Hull");
				Assert.That(hull, Is.Not.Null, barge.name);
				Assert.That(hull.GetComponent<MeshFilter>().sharedMesh.triangles.Length, Is.GreaterThan(0), barge.name);
				Assert.That(barge.transform.Find("BerthingFace"), Is.Not.Null, barge.name);
				Assert.That(barge.transform.Find("FaceNormal"), Is.Not.Null, barge.name);
				Assert.That(barge.gameObject.layer, Is.EqualTo(WorldLayers.Structure), barge.name);
				Assert.That(hull.gameObject.layer, Is.EqualTo(WorldLayers.Structure), barge.name);

				var label = barge.GetComponentInChildren<TextMesh>(true);
				Assert.That(label, Is.Not.Null, barge.name);
				Assert.That(label.text, Does.Contain(barge.StopName));
				Assert.That(label.text, Does.Contain(barge.Confidence + " confidence"));
				Assert.That(label.text, Does.Contain("needs-crew-verification"));
				Assert.That(label.GetComponent<Billboard>(), Is.Not.Null);
			}
		}

		[Test]
		public void Barges_FloatOnTheTide()
		{
			var landings = _prefab.transform.Find("Landings");

			Assert.That(landings.GetComponent<FloatOnWater>(), Is.Not.Null);
			Assert.That(Barges.All(b => b.transform.parent == landings), Is.True);
		}

		[Test]
		public void Barges_SitAtTheNavd88ZeroWaterlineNearTheirGtfsStop()
		{
			foreach (var barge in Barges)
			{
				var position = barge.transform.localPosition;
				var waterline = WaterSurface.SurfaceUpAt(_datum, position.z, position.x, 0.0);
				Assert.That(position.y, Is.EqualTo(waterline).Within(0.005), barge.name);

				// The pipeline requires a landing face within 50 m of its GTFS stop; the barge centre is a little farther.
				var gap = Math.Sqrt(Math.Pow(position.z - barge.GtfsNorth, 2) + Math.Pow(position.x - barge.GtfsEast, 2));
				Assert.That(gap, Is.LessThan(70.0), barge.name);

				var gtfs = LocalTangentPlane.World.ToLocal(new GeodeticPosition(barge.GtfsLatitude, barge.GtfsLongitude));
				Assert.That(gtfs.North, Is.EqualTo(barge.GtfsNorth).Within(1e-6), barge.name);
				Assert.That(gtfs.East, Is.EqualTo(barge.GtfsEast).Within(1e-6), barge.name);
			}
		}

		[Test]
		public void BerthingFace_LiesOnTheBargeEdgeAndFacesTheWater()
		{
			foreach (var barge in Barges)
			{
				var hull = barge.transform.Find("Hull").GetComponent<MeshFilter>().sharedMesh.bounds;
				foreach (var (north, east) in new[] { (barge.FaceStartNorth, barge.FaceStartEast), (barge.FaceEndNorth, barge.FaceEndEast) })
				{
					var relative = new Vector3((float)east - barge.transform.localPosition.x, 0f, (float)north - barge.transform.localPosition.z);
					Assert.That(relative.x, Is.InRange(hull.min.x - 0.5f, hull.max.x + 0.5f), barge.name);
					Assert.That(relative.z, Is.InRange(hull.min.z - 0.5f, hull.max.z + 0.5f), barge.name);
				}

				// The outward normal points away from the barge centre and is perpendicular to the face.
				var middleNorth = 0.5 * (barge.FaceStartNorth + barge.FaceEndNorth) - barge.transform.localPosition.z;
				var middleEast = 0.5 * (barge.FaceStartEast + barge.FaceEndEast) - barge.transform.localPosition.x;
				Assert.That(middleNorth * barge.OutwardNormalNorth + middleEast * barge.OutwardNormalEast, Is.GreaterThan(0.0), barge.name);
				var alongNorth = barge.FaceEndNorth - barge.FaceStartNorth;
				var alongEast = barge.FaceEndEast - barge.FaceStartEast;
				var length = Math.Sqrt(alongNorth * alongNorth + alongEast * alongEast);
				Assert.That(Math.Abs(alongNorth * barge.OutwardNormalNorth + alongEast * barge.OutwardNormalEast) / length, Is.LessThan(0.05), barge.name);
				var heading = Math.Atan2(barge.OutwardNormalEast, barge.OutwardNormalNorth) * 180.0 / Math.PI;
				Assert.That(Mathf.DeltaAngle((float)heading, (float)barge.FaceHeadingDegrees), Is.EqualTo(0f).Within(0.01f), barge.name);
			}
		}

		[Test]
		public void Barges_MatchThePipelineLandingFaces()
		{
			var data = LoadPipelineOrIgnore();
			Assert.That(Barges.Select(b => b.StopId).OrderBy(s => s, StringComparer.Ordinal), Is.EqualTo(data.Faces.Select(f => f.StopId).OrderBy(s => s, StringComparer.Ordinal)));

			foreach (var face in data.Faces)
			{
				var barge = Barges.Single(b => b.StopId == face.StopId);
				Assert.That(barge.StopName, Is.EqualTo(face.StopName));
				Assert.That(barge.Source, Is.EqualTo(face.Source));
				Assert.That(barge.Confidence, Is.EqualTo(face.Confidence));
				Assert.That(barge.Verification, Is.EqualTo(face.Verification));

				// Outline centroid recomputed from GeoJSON [lon, lat] through the core's tangent plane.
				var ring = face.OutlineLonLat.Select(p => LocalTangentPlane.World.ToLocal(new GeodeticPosition(p[1], p[0]))).Select(l => new PlanePoint(l.East, l.North)).ToList();
				var centroid = PolygonGeometry.Centroid(PolygonGeometry.CounterClockwise(PolygonGeometry.CleanRing(ring)));
				Assert.That(barge.transform.localPosition.x, Is.EqualTo(centroid.East).Within(0.05), face.StopId + " east");
				Assert.That(barge.transform.localPosition.z, Is.EqualTo(centroid.North).Within(0.05), face.StopId + " north");

				// The face line agrees with the pipeline's [north, east] local coordinates.
				var start = face.FaceNorthEast[0];
				var end = face.FaceNorthEast[face.FaceNorthEast.Count - 1];
				Assert.That(barge.FaceStartNorth, Is.EqualTo(start[0]).Within(0.01), face.StopId);
				Assert.That(barge.FaceStartEast, Is.EqualTo(start[1]).Within(0.01), face.StopId);
				Assert.That(barge.FaceEndNorth, Is.EqualTo(end[0]).Within(0.01), face.StopId);
				Assert.That(barge.FaceEndEast, Is.EqualTo(end[1]).Within(0.01), face.StopId);
			}
		}

		[Test]
		public void Bridges_AreTheThreeEastRiverBridges()
		{
			Assert.That(Bridges.Select(b => b.BridgeName), Is.EquivalentTo(GreyboxBuilder.BridgeNames));
			foreach (var bridge in Bridges)
			{
				Assert.That(bridge.transform.Find("Deck"), Is.Not.Null, bridge.BridgeName);
				Assert.That(bridge.transform.Find("Tower A"), Is.Not.Null, bridge.BridgeName);
				Assert.That(bridge.transform.Find("Tower B"), Is.Not.Null, bridge.BridgeName);
				Assert.That(bridge.ClearanceDatum, Is.EqualTo("MHW"), bridge.BridgeName);
				Assert.That(bridge.TowerHeightIsEstimate, Is.True, bridge.BridgeName);
			}
		}

		[Test]
		public void BridgeDeck_UndersideIsAtTheClearanceAboveMeanHighWater()
		{
			foreach (var bridge in Bridges)
			{
				Assert.That(bridge.VerticalClearance, Is.InRange(30.0, 50.0), bridge.BridgeName);
				Assert.That(bridge.MeanHighWaterNavd88, Is.EqualTo(_datum.MeanHighWaterNavd88), bridge.BridgeName);
				Assert.That(bridge.DeckUndersideNavd88, Is.EqualTo(_datum.MeanHighWaterNavd88 + bridge.VerticalClearance).Within(1e-9), bridge.BridgeName);

				var deck = bridge.transform.Find("Deck");
				var underside = deck.localPosition.y - 0.5f * deck.localScale.y;
				var centre = bridge.transform.localPosition;
				var expected = WaterSurface.SurfaceUpAt(_datum, centre.z, centre.x, bridge.DeckUndersideNavd88);
				Assert.That(underside, Is.EqualTo(expected).Within(0.05), bridge.BridgeName);

				// Above the water at mean high water by exactly the clearance.
				var mhwSurface = WaterSurface.SurfaceUpAt(_datum, centre.z, centre.x, _datum.MeanHighWaterNavd88);
				Assert.That(underside - mhwSurface, Is.EqualTo(bridge.VerticalClearance).Within(0.05), bridge.BridgeName);
			}
		}

		[Test]
		public void BridgeTowers_StandOutsideTheNavigableSpan()
		{
			foreach (var bridge in Bridges)
			{
				var deck = bridge.transform.Find("Deck");
				var a = bridge.transform.Find("Tower A");
				var b = bridge.transform.Find("Tower B");
				var gap = Mathf.Abs(b.localPosition.z - a.localPosition.z) - a.localScale.z;
				Assert.That(gap, Is.GreaterThanOrEqualTo((float)bridge.HorizontalClearance - 1f), bridge.BridgeName + ": towers intrude on the navigable width");
				Assert.That(a.localPosition.y + 0.5f * a.localScale.y, Is.GreaterThan(deck.localPosition.y + 0.5f * deck.localScale.y), bridge.BridgeName);
			}
		}

		[Test]
		public void Bridges_MatchThePipelineEncFeatures()
		{
			var data = LoadPipelineOrIgnore();
			foreach (var bridge in Bridges)
			{
				var features = data.Bridges.Where(f => f.Name == bridge.BridgeName).ToList();
				var span = features.Single(f => f.HasClearance);
				Assert.That(bridge.VerticalClearance, Is.EqualTo(span.VerticalClearance.Value), bridge.BridgeName);
				Assert.That(bridge.EncIds, Is.EquivalentTo(features.Select(f => f.Id)), bridge.BridgeName);

				// The deck centre lies on the main span's axis, inside its minimum-area rectangle widened by the approaches.
				var points = span.RingsLonLat.SelectMany(r => r).Select(p => LocalTangentPlane.World.ToLocal(new GeodeticPosition(p[1], p[0]))).Select(l => new PlanePoint(l.East, l.North)).ToList();
				var rectangle = PolygonGeometry.MinimumAreaRectangle(points);
				var centre = new PlanePoint(bridge.transform.localPosition.x, bridge.transform.localPosition.z);
				var across = new PlanePoint(-rectangle.Axis.North, rectangle.Axis.East);
				Assert.That(Math.Abs((centre - rectangle.Centre).Dot(across)), Is.LessThan(0.5), bridge.BridgeName + " off the span axis");
				Assert.That(Math.Abs((centre - rectangle.Centre).Dot(rectangle.Axis)), Is.LessThan(0.5 * rectangle.Length), bridge.BridgeName + " deck centre beyond the span");
				Assert.That(Mathf.DeltaAngle(bridge.transform.localEulerAngles.y, (float)rectangle.AxisBearingDegrees), Is.EqualTo(0f).Within(0.01f).Or.EqualTo(180f).Within(0.01f).Or.EqualTo(-180f).Within(0.01f));
			}
		}

		private static PipelineData LoadPipelineOrIgnore()
		{
			if (!PipelineData.Exists())
			{
				Assert.Ignore("Pipeline outputs are not built in this checkout; run ferrydata build --slice lower-east-river to compare.");
			}

			return PipelineData.Load();
		}
	}
}
