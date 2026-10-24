using System;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// Bow-on scenario starts off a stop's primary landing face. Failure modes guarded: the start not straight out from
	/// the middle of the face or the heading not straight in, a face the pipeline split into subsegments approached
	/// off one piece instead of the whole face, and a bent face given a single heading as if it were straight.
	/// </summary>
	public class LandingApproachTests
	{
		private const string Label = "test-contacts.json";

		private static LandingSurfaceTable Surfaces => LandingSurfaceTable.Parse(SliceTestData.SurfacesJson(), "surfaces");

		[Test]
		public void AOneSegmentFace_IsApproachedOffItsMiddle_HeadingStraightIn()
		{
			var vessel = SliceTestData.LoadTClass();
			// South Williamsburg's NW long side as merged.
			var face = Face("8", (963.0241368959863, 3771.9112337790434, 983.9753242661793, 3786.6043392838224));

			var settings = LandingApproach.BowOn(face, vessel, 40.0, "test", "vessels/t-class.json", "2026-06-15T16:00:00Z");

			var segment = face.Segments[0].Segment;
			var outward = settings.StartPosition - (segment.Start + segment.End) * 0.5;
			var bow = FaceClearance.BodyOutline(vessel).Max(p => p.North);
			Assert.That(outward.Magnitude, Is.EqualTo(bow + 40.0).Within(1e-6), "the reference point is the bow's reach plus the clearance off the middle");
			Assert.That(Cos(outward, segment.WaterNormal), Is.EqualTo(1.0).Within(1e-9), "straight out along the water normal");
			Assert.That(settings.startHeadingDegrees, Is.EqualTo(125.04204857689795).Within(1e-9), "heading in, against the normal");
			Assert.That(settings.vesselFile, Is.EqualTo("vessels/t-class.json"));
			Assert.That(settings.environment.calm, Is.True);
		}

		[Test]
		public void AFaceSplitIntoStraightSubsegments_IsApproachedOffTheMiddleOfTheWholeFace()
		{
			var vessel = SliceTestData.LoadTClass();
			var face = Face("19", (0.0, 0.0, 4.0, 3.0), (4.0, 3.0, 8.0, 6.0), (8.0, 6.0, 16.0, 12.0));

			var settings = LandingApproach.BowOn(face, vessel, 20.0, "test", "vessels/t-class.json", "2026-06-15T16:00:00Z");

			var outward = settings.StartPosition - new HorizontalVector(8.0, 6.0);
			Assert.That(Cos(outward, face.Segments[0].Segment.WaterNormal), Is.EqualTo(1.0).Within(1e-9), "off the middle of the whole face, (8, 6), not of a piece of it");
			Assert.That(outward.Magnitude, Is.EqualTo(FaceClearance.BodyOutline(vessel).Max(p => p.North) + 20.0).Within(1e-6));
		}

		[Test]
		public void ABentFace_IsRefused()
		{
			var face = Face("4", (0.0, 0.0, 10.0, 0.0), (10.0, 0.0, 20.0, 2.0));

			var error = Assert.Throws<ArgumentException>(() => LandingApproach.BowOn(face, SliceTestData.LoadTClass(), 20.0, "test", "vessels/t-class.json", "2026-06-15T16:00:00Z"));
			Assert.That(error.Message, Does.Contain("bends").And.Contain("Stop 4"));
		}

		/// <summary>A primary face from joined (start north, start east, end north, end east) pieces, through the loader.</summary>
		private static LandingFace Face(string stopId, params (double, double, double, double)[] pieces)
		{
			var segments = pieces.Select((piece, i) =>
			{
				var segment = SliceTestData.Segment(100 + i, stopId, piece.Item1, piece.Item2, piece.Item3, piece.Item4);
				segment[LandingGeometry.PrimaryFaceField] = true;
				return segment;
			}).ToArray();
			return LandingGeometry.Parse(SliceTestData.Json(segments), Label, Surfaces).PrimaryFace(stopId);
		}

		private static double Cos(HorizontalVector a, HorizontalVector b) => (a.North * b.North + a.East * b.East) / (a.Magnitude * b.Magnitude);
	}
}
