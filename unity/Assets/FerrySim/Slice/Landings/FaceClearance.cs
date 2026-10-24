using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;

namespace FerrySim.Slice
{
	/// <summary>How a hull outline at one pose sits against the landing faces.</summary>
	public readonly struct FaceClearanceResult
	{
		public FaceClearanceResult(double distance, int nearestSegmentId, bool overlaps)
		{
			Distance = distance;
			NearestSegmentId = nearestSegmentId;
			Overlaps = overlaps;
		}

		/// <summary>Smallest distance between the hull outline and any face, m; 0 when they overlap; +∞ with no faces.</summary>
		public double Distance { get; }

		/// <summary>Id of the nearest face, or -1 with no faces.</summary>
		public int NearestSegmentId { get; }

		/// <summary>True when a face crosses the outline or lies inside it.</summary>
		public bool Overlaps { get; }

		/// <summary>True when the outline is at least <paramref name="margin"/> metres from every face.</summary>
		public bool IsClear(double margin = FaceClearance.MinimumMetres) => !Overlaps && Distance >= margin;
	}

	/// <summary>
	/// Whether a scenario starts clear of every landing face. The core captures contact only when the hull crosses
	/// a face from its water side, so a hull placed overlapping a face at reset is never pushed by it: scenarios
	/// must start clear. Uses the vessel file's contact outline (the same polylines the core touches with).
	/// </summary>
	public static class FaceClearance
	{
		/// <summary>Clearance a scenario start needs from every face, m.</summary>
		public const double MinimumMetres = 1.0;

		/// <summary>
		/// The closed hull outline in body axes (x forward, y starboard, from the reference point): starboard sheer
		/// aft to forward, bow fender line starboard to port, port sheer forward to aft, stern fender line port to
		/// starboard.
		/// </summary>
		public static IReadOnlyList<HorizontalVector> BodyOutline(VesselParameters vessel)
		{
			var contact = vessel?.Contact ?? throw new ArgumentException("The vessel file has no contact section, so it has no hull outline.", nameof(vessel));
			var ring = new List<HorizontalVector>();
			Append(ring, contact.StarboardDemihull, false);
			Append(ring, contact.BowFenderLine, true);
			Append(ring, contact.PortDemihull, true);
			Append(ring, contact.SternFenderLine, false);
			if (ring.Count > 1 && Same(ring[0], ring[ring.Count - 1]))
			{
				ring.RemoveAt(ring.Count - 1);
			}

			return ring;
		}

		/// <summary>The outline at a pose, in world (north, east) metres.</summary>
		public static HorizontalVector[] WorldOutline(VesselParameters vessel, double north, double east, double heading)
		{
			var body = BodyOutline(vessel);
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var world = new HorizontalVector[body.Count];
			for (var i = 0; i < body.Count; i++)
			{
				var x = body[i].North;
				var y = body[i].East;
				world[i] = new HorizontalVector(north + x * cos - y * sin, east + x * sin + y * cos);
			}

			return world;
		}

		public static FaceClearanceResult Check(VesselParameters vessel, double north, double east, double heading, IReadOnlyList<ContactSegment> faces)
		{
			var outline = WorldOutline(vessel, north, east, heading);
			var best = double.PositiveInfinity;
			var nearest = -1;
			var overlaps = false;
			foreach (var face in faces)
			{
				var inside = Inside(outline, face.Start) || Inside(outline, face.End);
				var distance = inside ? 0.0 : double.PositiveInfinity;
				for (var i = 0; i < outline.Length && distance > 0.0; i++)
				{
					distance = Math.Min(distance, SegmentDistance(outline[i], outline[(i + 1) % outline.Length], face.Start, face.End));
				}

				if (distance < best || (distance == 0.0 && !overlaps))
				{
					best = distance;
					nearest = face.Id;
				}

				overlaps |= distance == 0.0;
			}

			return new FaceClearanceResult(best, nearest, overlaps);
		}

		private static void Append(List<HorizontalVector> ring, List<OutlinePoint> polyline, bool reversed)
		{
			if (polyline == null || polyline.Count == 0)
			{
				throw new ArgumentException("The vessel file's contact outline is missing a polyline.");
			}

			for (var k = 0; k < polyline.Count; k++)
			{
				var point = polyline[reversed ? polyline.Count - 1 - k : k];
				var vertex = new HorizontalVector(point.X.Value, point.Y.Value);
				if (ring.Count == 0 || !Same(ring[ring.Count - 1], vertex))
				{
					ring.Add(vertex);
				}
			}
		}

		private static bool Same(HorizontalVector a, HorizontalVector b) => (a - b).Magnitude < 1e-9;

		/// <summary>Even-odd rule: whether a point lies inside the closed outline.</summary>
		private static bool Inside(HorizontalVector[] ring, HorizontalVector point)
		{
			var inside = false;
			for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
			{
				var a = ring[i];
				var b = ring[j];
				if ((a.East > point.East) != (b.East > point.East) &&
					point.North < (b.North - a.North) * (point.East - a.East) / (b.East - a.East) + a.North)
				{
					inside = !inside;
				}
			}

			return inside;
		}

		private static double SegmentDistance(HorizontalVector a, HorizontalVector b, HorizontalVector c, HorizontalVector d)
		{
			if (Crosses(a, b, c, d))
			{
				return 0.0;
			}

			return Math.Min(Math.Min(PointDistance(a, c, d), PointDistance(b, c, d)), Math.Min(PointDistance(c, a, b), PointDistance(d, a, b)));
		}

		private static double PointDistance(HorizontalVector p, HorizontalVector a, HorizontalVector b)
		{
			var ab = b - a;
			var lengthSquared = ab.North * ab.North + ab.East * ab.East;
			var t = lengthSquared > 0.0 ? ((p.North - a.North) * ab.North + (p.East - a.East) * ab.East) / lengthSquared : 0.0;
			t = Math.Max(0.0, Math.Min(1.0, t));
			var closest = new HorizontalVector(a.North + t * ab.North, a.East + t * ab.East);
			return (p - closest).Magnitude;
		}

		private static bool Crosses(HorizontalVector a, HorizontalVector b, HorizontalVector c, HorizontalVector d)
		{
			var d1 = Cross(c, d, a);
			var d2 = Cross(c, d, b);
			var d3 = Cross(a, b, c);
			var d4 = Cross(a, b, d);
			return ((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0)) && ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0));
		}

		private static double Cross(HorizontalVector origin, HorizontalVector to, HorizontalVector point) =>
			(to.North - origin.North) * (point.East - origin.East) - (to.East - origin.East) * (point.North - origin.North);
	}
}
