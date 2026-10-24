using System;
using System.Collections.Generic;

namespace FerrySim.World.EditorTools
{
	/// <summary>A point in the horizontal world plane, metres east (X) and north (Y).</summary>
	public readonly struct PlanePoint
	{
		public PlanePoint(double east, double north)
		{
			East = east;
			North = north;
		}

		public double East { get; }

		public double North { get; }

		public static PlanePoint operator +(PlanePoint a, PlanePoint b) => new PlanePoint(a.East + b.East, a.North + b.North);

		public static PlanePoint operator -(PlanePoint a, PlanePoint b) => new PlanePoint(a.East - b.East, a.North - b.North);

		public static PlanePoint operator *(PlanePoint a, double s) => new PlanePoint(a.East * s, a.North * s);

		public double Dot(PlanePoint other) => East * other.East + North * other.North;

		public double Cross(PlanePoint other) => East * other.North - North * other.East;

		public double Length => Math.Sqrt(East * East + North * North);

		public override string ToString() => $"(E {East:F3}, N {North:F3})";
	}

	/// <summary>An oriented rectangle: centre, unit axis along its length, length and width.</summary>
	public readonly struct OrientedRectangle
	{
		public OrientedRectangle(PlanePoint centre, PlanePoint axis, double length, double width)
		{
			Centre = centre;
			Axis = axis;
			Length = length;
			Width = width;
		}

		public PlanePoint Centre { get; }

		/// <summary>Unit vector along the long side.</summary>
		public PlanePoint Axis { get; }

		public double Length { get; }

		public double Width { get; }

		/// <summary>Bearing of <see cref="Axis"/>, degrees clockwise from north.</summary>
		public double AxisBearingDegrees => Math.Atan2(Axis.East, Axis.North) * 180.0 / Math.PI;
	}

	/// <summary>Small planar helpers for greybox generation: ring cleanup, area, centroid, ear clipping, minimum-area rectangle.</summary>
	public static class PolygonGeometry
	{
		private const double Epsilon = 1e-9;

		/// <summary>Drops a closing point equal to the first and consecutive duplicates.</summary>
		public static List<PlanePoint> CleanRing(IReadOnlyList<PlanePoint> ring)
		{
			var cleaned = new List<PlanePoint>();
			foreach (var point in ring)
			{
				if (cleaned.Count == 0 || (point - cleaned[cleaned.Count - 1]).Length > 1e-6)
				{
					cleaned.Add(point);
				}
			}

			while (cleaned.Count > 1 && (cleaned[0] - cleaned[cleaned.Count - 1]).Length <= 1e-6)
			{
				cleaned.RemoveAt(cleaned.Count - 1);
			}

			return cleaned;
		}

		/// <summary>Signed area, positive when the ring runs counter-clockwise seen from above (east right, north up).</summary>
		public static double SignedArea(IReadOnlyList<PlanePoint> ring)
		{
			var area = 0.0;
			for (var i = 0; i < ring.Count; i++)
			{
				area += ring[i].Cross(ring[(i + 1) % ring.Count]);
			}

			return 0.5 * area;
		}

		/// <summary>Area centroid of a simple ring.</summary>
		public static PlanePoint Centroid(IReadOnlyList<PlanePoint> ring)
		{
			var area = SignedArea(ring);
			if (Math.Abs(area) < Epsilon)
			{
				var east = 0.0;
				var north = 0.0;
				foreach (var point in ring)
				{
					east += point.East;
					north += point.North;
				}

				return new PlanePoint(east / ring.Count, north / ring.Count);
			}

			double cx = 0.0;
			double cy = 0.0;
			for (var i = 0; i < ring.Count; i++)
			{
				var a = ring[i];
				var b = ring[(i + 1) % ring.Count];
				var cross = a.Cross(b);
				cx += (a.East + b.East) * cross;
				cy += (a.North + b.North) * cross;
			}

			return new PlanePoint(cx / (6.0 * area), cy / (6.0 * area));
		}

		/// <summary>Returns the ring in counter-clockwise order.</summary>
		public static List<PlanePoint> CounterClockwise(IReadOnlyList<PlanePoint> ring)
		{
			var result = new List<PlanePoint>(ring);
			if (SignedArea(result) < 0.0)
			{
				result.Reverse();
			}

			return result;
		}

		/// <summary>
		/// Ear-clipping triangulation of a simple counter-clockwise ring. Returns index triples, each
		/// counter-clockwise seen from above.
		/// </summary>
		public static List<int> Triangulate(IReadOnlyList<PlanePoint> ring)
		{
			if (ring.Count < 3)
			{
				throw new ArgumentException("A polygon needs at least three points.", nameof(ring));
			}

			if (SignedArea(ring) <= 0.0)
			{
				throw new ArgumentException("Triangulate expects a counter-clockwise ring.", nameof(ring));
			}

			var remaining = new List<int>();
			for (var i = 0; i < ring.Count; i++)
			{
				remaining.Add(i);
			}

			var triangles = new List<int>();
			var guard = 0;
			while (remaining.Count > 3 && guard++ < 10000)
			{
				var clipped = false;
				for (var i = 0; i < remaining.Count; i++)
				{
					var previous = remaining[(i + remaining.Count - 1) % remaining.Count];
					var current = remaining[i];
					var next = remaining[(i + 1) % remaining.Count];
					if (!IsEar(ring, remaining, previous, current, next))
					{
						continue;
					}

					triangles.Add(previous);
					triangles.Add(current);
					triangles.Add(next);
					remaining.RemoveAt(i);
					clipped = true;
					break;
				}

				if (!clipped)
				{
					// Degenerate (collinear) leftovers: drop the flattest vertex and carry on.
					var flattest = 0;
					var smallest = double.MaxValue;
					for (var i = 0; i < remaining.Count; i++)
					{
						var a = ring[remaining[(i + remaining.Count - 1) % remaining.Count]];
						var b = ring[remaining[i]];
						var c = ring[remaining[(i + 1) % remaining.Count]];
						var turn = Math.Abs((b - a).Cross(c - b));
						if (turn < smallest)
						{
							smallest = turn;
							flattest = i;
						}
					}

					remaining.RemoveAt(flattest);
				}
			}

			if (remaining.Count == 3 && (ring[remaining[1]] - ring[remaining[0]]).Cross(ring[remaining[2]] - ring[remaining[1]]) > Epsilon)
			{
				triangles.Add(remaining[0]);
				triangles.Add(remaining[1]);
				triangles.Add(remaining[2]);
			}

			return triangles;
		}

		/// <summary>Convex hull (Andrew's monotone chain), counter-clockwise, without repeated points.</summary>
		public static List<PlanePoint> ConvexHull(IEnumerable<PlanePoint> points)
		{
			var sorted = new List<PlanePoint>(points);
			sorted.Sort((a, b) => a.East != b.East ? a.East.CompareTo(b.East) : a.North.CompareTo(b.North));
			if (sorted.Count < 3)
			{
				return sorted;
			}

			var hull = new List<PlanePoint>();
			foreach (var point in sorted)
			{
				while (hull.Count >= 2 && (hull[hull.Count - 1] - hull[hull.Count - 2]).Cross(point - hull[hull.Count - 1]) <= 0.0)
				{
					hull.RemoveAt(hull.Count - 1);
				}

				hull.Add(point);
			}

			var lowerCount = hull.Count + 1;
			for (var i = sorted.Count - 2; i >= 0; i--)
			{
				var point = sorted[i];
				while (hull.Count >= lowerCount && (hull[hull.Count - 1] - hull[hull.Count - 2]).Cross(point - hull[hull.Count - 1]) <= 0.0)
				{
					hull.RemoveAt(hull.Count - 1);
				}

				hull.Add(point);
			}

			hull.RemoveAt(hull.Count - 1);
			return hull;
		}

		/// <summary>Smallest-area rectangle around the points (one side lies along a hull edge).</summary>
		public static OrientedRectangle MinimumAreaRectangle(IEnumerable<PlanePoint> points)
		{
			var hull = ConvexHull(points);
			if (hull.Count < 2)
			{
				throw new ArgumentException("Need at least two distinct points.", nameof(points));
			}

			var bestArea = double.MaxValue;
			var best = default(OrientedRectangle);
			for (var i = 0; i < hull.Count; i++)
			{
				var edge = hull[(i + 1) % hull.Count] - hull[i];
				var length = edge.Length;
				if (length < Epsilon)
				{
					continue;
				}

				var u = edge * (1.0 / length);
				var v = new PlanePoint(-u.North, u.East);
				double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
				foreach (var point in hull)
				{
					var pu = point.Dot(u);
					var pv = point.Dot(v);
					minU = Math.Min(minU, pu);
					maxU = Math.Max(maxU, pu);
					minV = Math.Min(minV, pv);
					maxV = Math.Max(maxV, pv);
				}

				var area = (maxU - minU) * (maxV - minV);
				if (area < bestArea - Epsilon)
				{
					bestArea = area;
					var centre = u * (0.5 * (minU + maxU)) + v * (0.5 * (minV + maxV));
					var extentU = maxU - minU;
					var extentV = maxV - minV;
					best = extentU >= extentV
						? new OrientedRectangle(centre, u, extentU, extentV)
						: new OrientedRectangle(centre, v, extentV, extentU);
				}
			}

			return best;
		}

		private static bool IsEar(IReadOnlyList<PlanePoint> ring, List<int> remaining, int previous, int current, int next)
		{
			var a = ring[previous];
			var b = ring[current];
			var c = ring[next];
			if ((b - a).Cross(c - b) <= Epsilon)
			{
				return false;
			}

			foreach (var index in remaining)
			{
				if (index == previous || index == current || index == next)
				{
					continue;
				}

				if (InTriangle(ring[index], a, b, c))
				{
					return false;
				}
			}

			return true;
		}

		private static bool InTriangle(PlanePoint p, PlanePoint a, PlanePoint b, PlanePoint c)
		{
			var d1 = (b - a).Cross(p - a);
			var d2 = (c - b).Cross(p - b);
			var d3 = (a - c).Cross(p - c);
			return d1 >= -Epsilon && d2 >= -Epsilon && d3 >= -Epsilon;
		}
	}
}
