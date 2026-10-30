using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: triangles that do not cover the polygon (area lost or doubled), a hole filled in, an exception on a
	// degenerate or repeated-point ring, a large ring (the hashed path) mis-triangulated, and a result that differs between runs.
	public class PolygonTriangulatorTests
	{
		private static HorizontalVector P(double north, double east) => new HorizontalVector(north, east);

		private static IReadOnlyList<HorizontalVector> Ring(params double[] northEast)
		{
			var ring = new List<HorizontalVector>();
			for (var i = 0; i < northEast.Length; i += 2)
			{
				ring.Add(P(northEast[i], northEast[i + 1]));
			}

			return ring;
		}

		[Test]
		public void Square_GivesTwoTrianglesOfTheRightArea()
		{
			var rings = new[] { Ring(0, 0, 0, 10, 10, 10, 10, 0) };
			var triangles = PolygonTriangulator.Triangulate(rings, out var vertices);
			Assert.That(triangles.Length, Is.EqualTo(6));
			Assert.That(PolygonTriangulator.TriangleArea(vertices, triangles), Is.EqualTo(100.0).Within(1e-9));
		}

		[Test]
		public void SquareWithHole_LeavesTheHoleOut()
		{
			var rings = new[] { Ring(0, 0, 0, 10, 10, 10, 10, 0), Ring(3, 3, 7, 3, 7, 7, 3, 7) };
			var triangles = PolygonTriangulator.Triangulate(rings, out var vertices);
			var expected = PolygonTriangulator.RingArea(rings);
			Assert.That(expected, Is.EqualTo(84.0).Within(1e-9));
			Assert.That(PolygonTriangulator.TriangleArea(vertices, triangles), Is.EqualTo(expected).Within(1e-9));
			Assert.That(triangles.Length / 3, Is.EqualTo(8), "8 triangles for a square ring");
		}

		[Test]
		public void ConcaveRing_WithTheFirstPointRepeated()
		{
			var rings = new[] { Ring(0, 0, 0, 10, 4, 10, 4, 4, 10, 4, 10, 0, 0, 0) };
			var triangles = PolygonTriangulator.Triangulate(rings, out var vertices);
			Assert.That(PolygonTriangulator.TriangleArea(vertices, triangles), Is.EqualTo(64.0).Within(1e-9));
		}

		[Test]
		public void DegenerateRings_GiveNoTriangles_AndNoException()
		{
			Assert.That(PolygonTriangulator.Triangulate(new[] { Ring(0, 0, 1, 1, 2, 2) }, out _), Is.Empty, "collinear");
			Assert.That(PolygonTriangulator.Triangulate(new[] { Ring(0, 0, 0, 0, 0, 0) }, out _), Is.Empty, "one point repeated");
			Assert.That(PolygonTriangulator.Triangulate(new[] { Ring(0, 0, 1, 1) }, out _), Is.Empty, "two points");
			Assert.That(PolygonTriangulator.Triangulate(new IReadOnlyList<HorizontalVector>[0], out _), Is.Empty, "nothing");
		}

		[Test]
		public void LargeRing_UsesTheHashedPath_AndKeepsTheArea()
		{
			var ring = new List<HorizontalVector>();
			const int count = 400;
			for (var i = 0; i < count; i++)
			{
				var angle = 2.0 * Math.PI * i / count;
				var radius = 1000.0 + 150.0 * Math.Sin(7.0 * angle);
				ring.Add(P(radius * Math.Cos(angle), radius * Math.Sin(angle)));
			}

			var hole = new List<HorizontalVector>();
			for (var i = 0; i < 40; i++)
			{
				var angle = -2.0 * Math.PI * i / 40;
				hole.Add(P(200.0 * Math.Cos(angle) + 300.0, 200.0 * Math.Sin(angle)));
			}

			var rings = new[] { (IReadOnlyList<HorizontalVector>)ring, hole };
			var first = PolygonTriangulator.Triangulate(rings, out var vertices);
			var second = PolygonTriangulator.Triangulate(rings, out _);
			Assert.That(first, Is.EqualTo(second), "deterministic");
			var expected = PolygonTriangulator.RingArea(rings);
			Assert.That(PolygonTriangulator.TriangleArea(vertices, first), Is.EqualTo(expected).Within(expected * 1e-9));
		}
	}
}
