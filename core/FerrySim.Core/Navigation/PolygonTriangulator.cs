using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Navigation
{
	/// <summary>
	/// Ear-clipping triangulation of a polygon with holes, after Mapbox's earcut (ISC licence): the outer ring and the
	/// holes become one linked ring by bridging each hole to the outer ring, ears are clipped (with a z-order index when
	/// the ring is large), and local self-intersections and remaining knots are cured by splitting. It is deterministic
	/// for the same input and tolerant of repeated points, collinear runs and degenerate rings (which yield no
	/// triangles rather than an exception). Triangles index the vertices in ring order, exterior first then each hole.
	/// It allocates while working, so a chart tessellates once, at connect, never per frame.
	/// </summary>
	public static class PolygonTriangulator
	{
		/// <summary>Triangulates rings of world points (exterior first, then holes) into vertex indices, three per triangle.</summary>
		public static int[] Triangulate(IReadOnlyList<IReadOnlyList<HorizontalVector>> rings, out double[] vertices)
		{
			if (rings == null || rings.Count == 0)
			{
				vertices = Array.Empty<double>();
				return Array.Empty<int>();
			}

			var total = 0;
			for (var r = 0; r < rings.Count; r++)
			{
				total += rings[r].Count;
			}

			vertices = new double[total * 2];
			var holes = new int[Math.Max(0, rings.Count - 1)];
			var offset = 0;
			for (var r = 0; r < rings.Count; r++)
			{
				if (r > 0)
				{
					holes[r - 1] = offset;
				}

				var ring = rings[r];
				for (var i = 0; i < ring.Count; i++)
				{
					// x east, y north: the orientation rule only needs a consistent pair.
					vertices[2 * (offset + i)] = ring[i].East;
					vertices[2 * (offset + i) + 1] = ring[i].North;
				}

				offset += ring.Count;
			}

			return Triangulate(vertices, holes);
		}

		/// <summary>Triangulates flat x/y pairs; <paramref name="holeStarts"/> are the vertex indices where each hole ring begins.</summary>
		public static int[] Triangulate(double[] data, int[] holeStarts)
		{
			var triangles = new List<int>();
			var hasHoles = holeStarts != null && holeStarts.Length > 0;
			var outerLength = hasHoles ? holeStarts[0] * 2 : data.Length;
			var outer = LinkedList(data, 0, outerLength, true);
			if (outer == null || outer.Next == outer.Prev)
			{
				return triangles.ToArray();
			}

			if (hasHoles)
			{
				outer = EliminateHoles(data, holeStarts, outer);
			}

			double minX = 0, minY = 0, invSize = 0;
			if (data.Length > 80 * 2)
			{
				minX = data[0];
				minY = data[1];
				double maxX = minX, maxY = minY;
				for (var i = 2; i < outerLength; i += 2)
				{
					var x = data[i];
					var y = data[i + 1];
					if (x < minX) minX = x;
					if (y < minY) minY = y;
					if (x > maxX) maxX = x;
					if (y > maxY) maxY = y;
				}

				invSize = Math.Max(maxX - minX, maxY - minY);
				invSize = invSize != 0 ? 32767 / invSize : 0;
			}

			EarcutLinked(outer, triangles, minX, minY, invSize, 0);
			return triangles.ToArray();
		}

		private sealed class Node
		{
			public readonly int I;
			public readonly double X;
			public readonly double Y;
			public Node Prev;
			public Node Next;
			public int Z;
			public Node PrevZ;
			public Node NextZ;
			public bool Steiner;

			public Node(int i, double x, double y)
			{
				I = i;
				X = x;
				Y = y;
			}
		}

		private static Node LinkedList(double[] data, int start, int end, bool clockwise)
		{
			Node last = null;
			if (clockwise == SignedArea(data, start, end) > 0)
			{
				for (var i = start; i < end; i += 2)
				{
					last = InsertNode(i, data[i], data[i + 1], last);
				}
			}
			else
			{
				for (var i = end - 2; i >= start; i -= 2)
				{
					last = InsertNode(i, data[i], data[i + 1], last);
				}
			}

			if (last != null && Equals(last, last.Next))
			{
				RemoveNode(last);
				last = last.Next;
			}

			return last;
		}

		private static Node FilterPoints(Node start, Node end = null)
		{
			if (start == null)
			{
				return null;
			}

			end ??= start;
			var p = start;
			bool again;
			do
			{
				again = false;
				if (!p.Steiner && (Equals(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
				{
					RemoveNode(p);
					p = end = p.Prev;
					if (p == p.Next)
					{
						break;
					}

					again = true;
				}
				else
				{
					p = p.Next;
				}
			}
			while (again || p != end);

			return end;
		}

		private static void EarcutLinked(Node ear, List<int> triangles, double minX, double minY, double invSize, int pass)
		{
			if (ear == null)
			{
				return;
			}

			if (pass == 0 && invSize != 0)
			{
				IndexCurve(ear, minX, minY, invSize);
			}

			var stop = ear;
			while (ear.Prev != ear.Next)
			{
				var prev = ear.Prev;
				var next = ear.Next;
				if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
				{
					triangles.Add(prev.I / 2);
					triangles.Add(ear.I / 2);
					triangles.Add(next.I / 2);
					RemoveNode(ear);
					ear = next.Next;
					stop = next.Next;
					continue;
				}

				ear = next;
				if (ear == stop)
				{
					if (pass == 0)
					{
						EarcutLinked(FilterPoints(ear), triangles, minX, minY, invSize, 1);
					}
					else if (pass == 1)
					{
						ear = CureLocalIntersections(FilterPoints(ear), triangles);
						EarcutLinked(ear, triangles, minX, minY, invSize, 2);
					}
					else if (pass == 2)
					{
						SplitEarcut(ear, triangles, minX, minY, invSize);
					}

					break;
				}
			}
		}

		private static bool IsEar(Node ear)
		{
			var a = ear.Prev;
			var b = ear;
			var c = ear.Next;
			if (Area(a, b, c) >= 0)
			{
				return false;
			}

			var ax = a.X; var bx = b.X; var cx = c.X; var ay = a.Y; var by = b.Y; var cy = c.Y;
			var x0 = Math.Min(ax, Math.Min(bx, cx));
			var y0 = Math.Min(ay, Math.Min(by, cy));
			var x1 = Math.Max(ax, Math.Max(bx, cx));
			var y1 = Math.Max(ay, Math.Max(by, cy));
			var p = c.Next;
			while (p != a)
			{
				if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
				{
					return false;
				}

				p = p.Next;
			}

			return true;
		}

		private static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
		{
			var a = ear.Prev;
			var b = ear;
			var c = ear.Next;
			if (Area(a, b, c) >= 0)
			{
				return false;
			}

			var ax = a.X; var bx = b.X; var cx = c.X; var ay = a.Y; var by = b.Y; var cy = c.Y;
			var x0 = Math.Min(ax, Math.Min(bx, cx));
			var y0 = Math.Min(ay, Math.Min(by, cy));
			var x1 = Math.Max(ax, Math.Max(bx, cx));
			var y1 = Math.Max(ay, Math.Max(by, cy));
			var minZ = ZOrder(x0, y0, minX, minY, invSize);
			var maxZ = ZOrder(x1, y1, minX, minY, invSize);
			var p = ear.PrevZ;
			var n = ear.NextZ;
			while (p != null && p.Z >= minZ && n != null && n.Z <= maxZ)
			{
				if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p != a && p != c && PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
				{
					return false;
				}

				p = p.PrevZ;
				if (n.X >= x0 && n.X <= x1 && n.Y >= y0 && n.Y <= y1 && n != a && n != c && PointInTriangle(ax, ay, bx, by, cx, cy, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
				{
					return false;
				}

				n = n.NextZ;
			}

			while (p != null && p.Z >= minZ)
			{
				if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && p != a && p != c && PointInTriangle(ax, ay, bx, by, cx, cy, p.X, p.Y) && Area(p.Prev, p, p.Next) >= 0)
				{
					return false;
				}

				p = p.PrevZ;
			}

			while (n != null && n.Z <= maxZ)
			{
				if (n.X >= x0 && n.X <= x1 && n.Y >= y0 && n.Y <= y1 && n != a && n != c && PointInTriangle(ax, ay, bx, by, cx, cy, n.X, n.Y) && Area(n.Prev, n, n.Next) >= 0)
				{
					return false;
				}

				n = n.NextZ;
			}

			return true;
		}

		private static Node CureLocalIntersections(Node start, List<int> triangles)
		{
			var p = start;
			do
			{
				var a = p.Prev;
				var b = p.Next.Next;
				if (!Equals(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
				{
					triangles.Add(a.I / 2);
					triangles.Add(p.I / 2);
					triangles.Add(b.I / 2);
					RemoveNode(p);
					RemoveNode(p.Next);
					p = start = b;
				}

				p = p.Next;
			}
			while (p != start);

			return FilterPoints(p);
		}

		private static void SplitEarcut(Node start, List<int> triangles, double minX, double minY, double invSize)
		{
			var a = start;
			do
			{
				var b = a.Next.Next;
				while (b != a.Prev)
				{
					if (a.I != b.I && IsValidDiagonal(a, b))
					{
						var c = SplitPolygon(a, b);
						a = FilterPoints(a, a.Next);
						c = FilterPoints(c, c.Next);
						EarcutLinked(a, triangles, minX, minY, invSize, 0);
						EarcutLinked(c, triangles, minX, minY, invSize, 0);
						return;
					}

					b = b.Next;
				}

				a = a.Next;
			}
			while (a != start);
		}

		private static Node EliminateHoles(double[] data, int[] holeStarts, Node outer)
		{
			var queue = new List<Node>();
			for (var i = 0; i < holeStarts.Length; i++)
			{
				var start = holeStarts[i] * 2;
				var end = i < holeStarts.Length - 1 ? holeStarts[i + 1] * 2 : data.Length;
				var list = LinkedList(data, start, end, false);
				if (list == null)
				{
					continue;
				}

				if (list == list.Next)
				{
					list.Steiner = true;
				}

				queue.Add(GetLeftmost(list));
			}

			queue.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
			for (var i = 0; i < queue.Count; i++)
			{
				outer = EliminateHole(queue[i], outer);
			}

			return outer;
		}

		private static Node EliminateHole(Node hole, Node outer)
		{
			var bridge = FindHoleBridge(hole, outer);
			if (bridge == null)
			{
				return outer;
			}

			var bridgeReverse = SplitPolygon(bridge, hole);
			FilterPoints(bridgeReverse, bridgeReverse.Next);
			return FilterPoints(bridge, bridge.Next);
		}

		private static Node FindHoleBridge(Node hole, Node outer)
		{
			var p = outer;
			var hx = hole.X;
			var hy = hole.Y;
			var qx = double.NegativeInfinity;
			Node m = null;
			do
			{
				if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
				{
					var x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
					if (x <= hx && x > qx)
					{
						qx = x;
						m = p.X < p.Next.X ? p : p.Next;
						if (x == hx)
						{
							return m;
						}
					}
				}

				p = p.Next;
			}
			while (p != outer);

			if (m == null)
			{
				return null;
			}

			var stop = m;
			var mx = m.X;
			var my = m.Y;
			var tanMin = double.PositiveInfinity;
			p = m;
			do
			{
				if (hx >= p.X && p.X >= mx && hx != p.X && PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
				{
					var tan = Math.Abs(hy - p.Y) / (hx - p.X);
					if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
					{
						m = p;
						tanMin = tan;
					}
				}

				p = p.Next;
			}
			while (p != stop);

			return m;
		}

		private static bool SectorContainsSector(Node m, Node p) => Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;

		private static void IndexCurve(Node start, double minX, double minY, double invSize)
		{
			var p = start;
			do
			{
				if (p.Z == 0)
				{
					p.Z = ZOrder(p.X, p.Y, minX, minY, invSize);
				}

				p.PrevZ = p.Prev;
				p.NextZ = p.Next;
				p = p.Next;
			}
			while (p != start);

			p.PrevZ.NextZ = null;
			p.PrevZ = null;
			SortLinked(p);
		}

		private static void SortLinked(Node list)
		{
			var inSize = 1;
			int numMerges;
			do
			{
				var p = list;
				list = null;
				Node tail = null;
				numMerges = 0;
				while (p != null)
				{
					numMerges++;
					var q = p;
					var pSize = 0;
					for (var i = 0; i < inSize; i++)
					{
						pSize++;
						q = q.NextZ;
						if (q == null)
						{
							break;
						}
					}

					var qSize = inSize;
					while (pSize > 0 || (qSize > 0 && q != null))
					{
						Node e;
						if (pSize != 0 && (qSize == 0 || q == null || p.Z <= q.Z))
						{
							e = p;
							p = p.NextZ;
							pSize--;
						}
						else
						{
							e = q;
							q = q.NextZ;
							qSize--;
						}

						if (tail != null)
						{
							tail.NextZ = e;
						}
						else
						{
							list = e;
						}

						e.PrevZ = tail;
						tail = e;
					}

					p = q;
				}

				tail.NextZ = null;
				inSize *= 2;
			}
			while (numMerges > 1);
		}

		private static int ZOrder(double x, double y, double minX, double minY, double invSize)
		{
			var ix = (int)((x - minX) * invSize);
			var iy = (int)((y - minY) * invSize);
			ix = (ix | (ix << 8)) & 0x00FF00FF;
			ix = (ix | (ix << 4)) & 0x0F0F0F0F;
			ix = (ix | (ix << 2)) & 0x33333333;
			ix = (ix | (ix << 1)) & 0x55555555;
			iy = (iy | (iy << 8)) & 0x00FF00FF;
			iy = (iy | (iy << 4)) & 0x0F0F0F0F;
			iy = (iy | (iy << 2)) & 0x33333333;
			iy = (iy | (iy << 1)) & 0x55555555;
			return ix | (iy << 1);
		}

		private static Node GetLeftmost(Node start)
		{
			var p = start;
			var leftmost = start;
			do
			{
				if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y))
				{
					leftmost = p;
				}

				p = p.Next;
			}
			while (p != start);

			return leftmost;
		}

		private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
			(cx - px) * (ay - py) >= (ax - px) * (cy - py) &&
			(ax - px) * (by - py) >= (bx - px) * (ay - py) &&
			(bx - px) * (cy - py) >= (cx - px) * (by - py);

		private static bool IsValidDiagonal(Node a, Node b) =>
			a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b) &&
			((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0)) ||
			(Equals(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0));

		private static double Area(Node p, Node q, Node r) => (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);

		private static bool Equals(Node p1, Node p2) => p1.X == p2.X && p1.Y == p2.Y;

		private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
		{
			var o1 = Math.Sign(Area(p1, q1, p2));
			var o2 = Math.Sign(Area(p1, q1, q2));
			var o3 = Math.Sign(Area(p2, q2, p1));
			var o4 = Math.Sign(Area(p2, q2, q1));
			if (o1 != o2 && o3 != o4)
			{
				return true;
			}

			if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
			if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
			if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
			if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
			return false;
		}

		private static bool OnSegment(Node p, Node q, Node r) =>
			q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

		private static bool IntersectsPolygon(Node a, Node b)
		{
			var p = a;
			do
			{
				if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
				{
					return true;
				}

				p = p.Next;
			}
			while (p != a);

			return false;
		}

		private static bool LocallyInside(Node a, Node b) =>
			Area(a.Prev, a, a.Next) < 0 ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0 : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

		private static bool MiddleInside(Node a, Node b)
		{
			var p = a;
			var inside = false;
			var px = (a.X + b.X) / 2;
			var py = (a.Y + b.Y) / 2;
			do
			{
				if (((p.Y > py) != (p.Next.Y > py)) && p.Next.Y != p.Y && (px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X))
				{
					inside = !inside;
				}

				p = p.Next;
			}
			while (p != a);

			return inside;
		}

		private static Node SplitPolygon(Node a, Node b)
		{
			var a2 = new Node(a.I, a.X, a.Y);
			var b2 = new Node(b.I, b.X, b.Y);
			var an = a.Next;
			var bp = b.Prev;
			a.Next = b;
			b.Prev = a;
			a2.Next = an;
			an.Prev = a2;
			b2.Next = a2;
			a2.Prev = b2;
			bp.Next = b2;
			b2.Prev = bp;
			return b2;
		}

		private static Node InsertNode(int i, double x, double y, Node last)
		{
			var p = new Node(i, x, y);
			if (last == null)
			{
				p.Prev = p;
				p.Next = p;
			}
			else
			{
				p.Next = last.Next;
				p.Prev = last;
				last.Next.Prev = p;
				last.Next = p;
			}

			return p;
		}

		private static void RemoveNode(Node p)
		{
			p.Next.Prev = p.Prev;
			p.Prev.Next = p.Next;
			if (p.PrevZ != null)
			{
				p.PrevZ.NextZ = p.NextZ;
			}

			if (p.NextZ != null)
			{
				p.NextZ.PrevZ = p.PrevZ;
			}
		}

		private static double SignedArea(double[] data, int start, int end)
		{
			var sum = 0.0;
			for (int i = start, j = end - 2; i < end; i += 2)
			{
				sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]);
				j = i;
			}

			return sum;
		}

		/// <summary>The signed area of the triangles (positive when they are consistently wound), for checks.</summary>
		public static double TriangleArea(double[] vertices, int[] triangles)
		{
			var area = 0.0;
			for (var t = 0; t + 2 < triangles.Length; t += 3)
			{
				var ax = vertices[2 * triangles[t]]; var ay = vertices[2 * triangles[t] + 1];
				var bx = vertices[2 * triangles[t + 1]]; var by = vertices[2 * triangles[t + 1] + 1];
				var cx = vertices[2 * triangles[t + 2]]; var cy = vertices[2 * triangles[t + 2] + 1];
				area += Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
			}

			return area;
		}

		/// <summary>The absolute area of a polygon with holes (exterior less holes), for checks.</summary>
		public static double RingArea(IReadOnlyList<IReadOnlyList<HorizontalVector>> rings)
		{
			var total = 0.0;
			for (var r = 0; r < rings.Count; r++)
			{
				var ring = rings[r];
				var sum = 0.0;
				for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
				{
					sum += ring[j].East * ring[i].North - ring[i].East * ring[j].North;
				}

				total += (r == 0 ? 1 : -1) * Math.Abs(sum) * 0.5;
			}

			return total;
		}
	}
}
