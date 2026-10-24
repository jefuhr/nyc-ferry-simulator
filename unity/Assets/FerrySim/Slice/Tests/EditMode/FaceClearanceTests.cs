using System;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// Scenario starts must be clear of every face: the core never pushes a hull placed overlapping a face.
	/// Failure modes guarded: an outline built from the wrong polylines or in the wrong order (a self-crossing
	/// ring misses overlaps), the body-to-world rotation backwards (port and starboard swapped), a short face lying
	/// wholly inside the hull counted as clear, and a hull just off a face passing the margin.
	/// </summary>
	public class FaceClearanceTests
	{
		private static readonly VesselParameters Vessel = SliceTestData.LoadTClass();

		[Test]
		public void TheOutline_IsTheContactSectionsRing_AroundTheReferencePoint()
		{
			var outline = FaceClearance.BodyOutline(Vessel);

			Assert.That(outline.Max(p => p.North), Is.EqualTo(13.76).Within(1e-9), "bow fender line, x forward");
			Assert.That(outline.Min(p => p.North), Is.EqualTo(-12.25).Within(1e-9), "transom");
			Assert.That(outline.Max(p => p.East), Is.EqualTo(4.0).Within(1e-9), "starboard sheer");
			Assert.That(outline.Min(p => p.East), Is.EqualTo(-4.0).Within(1e-9), "port sheer");
			// A simple ring encloses the plan area; a ring with a polyline the wrong way round crosses itself and
			// its signed area mostly cancels.
			var area = 0.0;
			for (var i = 0; i < outline.Count; i++)
			{
				var a = outline[i];
				var b = outline[(i + 1) % outline.Count];
				area += a.North * b.East - b.North * a.East;
			}

			Assert.That(Math.Abs(0.5 * area), Is.InRange(150.0, 26.0 * 8.0), "a closed outline of about the hull's plan area");
		}

		[Test]
		public void TheWorldOutline_TurnsWithTheHeading()
		{
			// Heading east (90°): forward is east, starboard is south.
			var outline = FaceClearance.WorldOutline(Vessel, 100.0, 200.0, Math.PI / 2.0);

			Assert.That(outline.Max(p => p.East), Is.EqualTo(213.76).Within(1e-9), "the bow is east of the reference point");
			Assert.That(outline.Min(p => p.North), Is.EqualTo(96.0).Within(1e-9), "the starboard side is south");
		}

		[Test]
		public void AHullFarFromTheFaces_IsClear()
		{
			var face = SliceTestData.Face(1, 0.0, 30.0, 25.0, 30.0);

			var result = FaceClearance.Check(Vessel, 10.0, 0.0, 0.0, new[] { face });

			Assert.That(result.Overlaps, Is.False);
			Assert.That(result.Distance, Is.EqualTo(26.0).Within(1e-9), "starboard sheer at east 4 m, face at east 30 m");
			Assert.That(result.NearestSegmentId, Is.EqualTo(1));
			Assert.That(result.IsClear(), Is.True);
		}

		[Test]
		public void AHullAcrossAFace_Overlaps_AndNamesIt()
		{
			var far = SliceTestData.Face(1, 500.0, 500.0, 520.0, 500.0);
			// Runs east from inside the hull across the starboard sheer.
			var crossed = SliceTestData.Face(2, 0.0, 2.0, 0.0, 10.0);

			var result = FaceClearance.Check(Vessel, 0.0, 0.0, 0.0, new[] { far, crossed });

			Assert.That(result.Overlaps, Is.True);
			Assert.That(result.Distance, Is.EqualTo(0.0));
			Assert.That(result.NearestSegmentId, Is.EqualTo(2));
			Assert.That(result.IsClear(), Is.False);
		}

		[Test]
		public void AShortFaceInsideTheHull_Overlaps()
		{
			var inside = SliceTestData.Face(3, -1.0, 0.5, 1.0, 0.5);

			Assert.That(FaceClearance.Check(Vessel, 0.0, 0.0, 0.0, new[] { inside }).Overlaps, Is.True);
		}

		[Test]
		public void AHullHalfAMetreOffAFace_IsNotClear()
		{
			// The starboard sheer runs at east 4.0 m; the face is 0.5 m outboard of it.
			var face = SliceTestData.Face(4, -5.0, 4.5, 5.0, 4.5);

			var result = FaceClearance.Check(Vessel, 0.0, 0.0, 0.0, new[] { face });

			Assert.That(result.Overlaps, Is.False);
			Assert.That(result.Distance, Is.EqualTo(0.5).Within(1e-9));
			Assert.That(result.IsClear(), Is.False);
			Assert.That(result.IsClear(0.25), Is.True);
		}

		[Test]
		public void NoFaces_IsClear()
		{
			var result = FaceClearance.Check(Vessel, 0.0, 0.0, 0.0, Array.Empty<ContactSegment>());

			Assert.That(result.IsClear(), Is.True);
			Assert.That(result.NearestSegmentId, Is.EqualTo(-1));
			Assert.That(double.IsPositiveInfinity(result.Distance), Is.True);
		}
	}
}
