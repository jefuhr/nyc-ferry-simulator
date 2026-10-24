using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.World.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// Greybox geometry helpers. Guards against triangulations that miss or double-cover area, wrong
	/// winding (faces culled from above), a rectangle fitted along the wrong axis, and a closing
	/// GeoJSON point counted twice.
	/// </summary>
	public class PolygonGeometryTests
	{
		private static readonly PlanePoint[] LShape =
		{
			new PlanePoint(0, 0), new PlanePoint(30, 0), new PlanePoint(30, 10), new PlanePoint(10, 10), new PlanePoint(10, 25), new PlanePoint(0, 25),
		};

		[Test]
		public void CleanRing_DropsClosingPointAndDuplicates()
		{
			var ring = new[] { new PlanePoint(0, 0), new PlanePoint(1, 0), new PlanePoint(1, 0), new PlanePoint(1, 1), new PlanePoint(0, 0) };

			var cleaned = PolygonGeometry.CleanRing(ring);

			Assert.That(cleaned.Count, Is.EqualTo(3));
		}

		[Test]
		public void Triangulate_LShape_CoversExactAreaWithCounterClockwiseTriangles()
		{
			var triangles = PolygonGeometry.Triangulate(LShape);

			Assert.That(triangles.Count, Is.EqualTo((LShape.Length - 2) * 3));
			var total = 0.0;
			for (var i = 0; i < triangles.Count; i += 3)
			{
				var area = PolygonGeometry.SignedArea(new[] { LShape[triangles[i]], LShape[triangles[i + 1]], LShape[triangles[i + 2]] });
				Assert.That(area, Is.GreaterThan(0.0), "triangle " + i / 3 + " is not counter-clockwise");
				total += area;
			}

			Assert.That(total, Is.EqualTo(PolygonGeometry.SignedArea(LShape)).Within(1e-9));
			Assert.That(total, Is.EqualTo(30 * 10 + 10 * 15).Within(1e-9));
		}

		[Test]
		public void Triangulate_RejectsClockwiseRing()
		{
			var clockwise = LShape.Reverse().ToArray();

			Assert.Throws<ArgumentException>(() => PolygonGeometry.Triangulate(clockwise));
		}

		[Test]
		public void MinimumAreaRectangle_RecoversRotatedRectangle()
		{
			// A 400 m x 26 m span whose long axis bears 137.9 deg, like the Brooklyn Bridge.
			var bearing = 137.9 * Math.PI / 180.0;
			var axis = new PlanePoint(Math.Sin(bearing), Math.Cos(bearing));
			var across = new PlanePoint(-axis.North, axis.East);
			var centre = new PlanePoint(1560, 650);
			var corners = new List<PlanePoint>();
			foreach (var s in new[] { -1, 1 })
			{
				foreach (var t in new[] { -1, 1 })
				{
					corners.Add(centre + axis * (200.0 * s) + across * (13.0 * t));
				}
			}

			var rectangle = PolygonGeometry.MinimumAreaRectangle(corners);

			Assert.That(rectangle.Length, Is.EqualTo(400.0).Within(1e-6));
			Assert.That(rectangle.Width, Is.EqualTo(26.0).Within(1e-6));
			Assert.That(rectangle.Centre.East, Is.EqualTo(centre.East).Within(1e-6));
			Assert.That(rectangle.Centre.North, Is.EqualTo(centre.North).Within(1e-6));
			Assert.That(Math.Abs(rectangle.Axis.Dot(axis)), Is.EqualTo(1.0).Within(1e-9), "long axis must follow the span");
		}

		[Test]
		public void ExtrudedPrism_FacesPointOutward()
		{
			var ring = PolygonGeometry.CounterClockwise(LShape);
			var mesh = GreyboxBuilder.ExtrudedPrism(ring, 1.2f, -1.5f, "test");
			try
			{
				var vertices = mesh.vertices;
				var triangles = mesh.triangles;
				var centre = mesh.bounds.center;
				var upFacing = 0;
				for (var i = 0; i < triangles.Length; i += 3)
				{
					var a = vertices[triangles[i]];
					var b = vertices[triangles[i + 1]];
					var c = vertices[triangles[i + 2]];

					// Unity front faces wind clockwise, so the face normal is (b - a) x (c - a).
					var normal = Vector3.Cross(b - a, c - a).normalized;
					var middle = (a + b + c) / 3f;
					if (normal.y > 0.99f)
					{
						upFacing++;
						Assert.That(middle.y, Is.EqualTo(1.2f).Within(1e-5f), "up-facing triangle below the deck");
					}
					else if (normal.y < -0.99f)
					{
						Assert.That(middle.y, Is.EqualTo(-1.5f).Within(1e-5f), "down-facing triangle above the bottom");
					}
					else
					{
						// Wall: the normal points away from the inside. The L-shape's notch makes the centre a poor
						// reference, so test against a point just inside the wall instead.
						var inside = new PlanePoint(middle.x - normal.x * 0.01, middle.z - normal.z * 0.01);
						Assert.That(Contains(ring, inside), Is.True, "wall normal points inward at " + middle);
					}
				}

				Assert.That(upFacing, Is.EqualTo(ring.Count - 2));
				Assert.That(centre.y, Is.EqualTo(-0.15f).Within(1e-5f));
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(mesh);
			}
		}

		private static bool Contains(IReadOnlyList<PlanePoint> ring, PlanePoint point)
		{
			var inside = false;
			for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
			{
				var a = ring[i];
				var b = ring[j];
				if ((a.North > point.North) != (b.North > point.North) &&
					point.East < (b.East - a.East) * (point.North - a.North) / (b.North - a.North) + a.East)
				{
					inside = !inside;
				}
			}

			return inside;
		}
	}
}
