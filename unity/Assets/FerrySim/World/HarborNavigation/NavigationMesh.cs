using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	/// <summary>Build-time extrusion of closed source outlines, never of the contact segment set.</summary>
	public static class NavigationMesh
	{
		public static Mesh Extrude(HarborGeometry geometry, NavigationAnchor anchor, double bottomHeight, double topHeight)
		{
			var vertices = new List<Vector3>();
			var triangles = new List<int>();
			foreach (var polygon in geometry.Polygons)
			{
				if (polygon.Rings.Count != 1) throw new ArgumentException("Navigation extrusion requires a simple outline; holes must not be silently filled.");
				AddRing(polygon.Rings[0], anchor, bottomHeight, topHeight, vertices, triangles);
			}
			// Chart line spans/piers get an explicitly estimated 1 m wide ribbon, with each source segment retained.
			foreach (var line in geometry.Lines)
			{
				for (var i = 1; i < line.Count; i++)
				{
					var a = line[i - 1]; var b = line[i];
					var dn = b.North - a.North; var de = b.East - a.East;
					var length = Math.Sqrt(dn * dn + de * de);
					if (length < 1e-6) continue;
					var n = -.5 * de / length; var e = .5 * dn / length;
					AddRing(new[] { new HorizontalVector(a.North + n, a.East + e), new HorizontalVector(b.North + n, b.East + e),
						new HorizontalVector(b.North - n, b.East - e), new HorizontalVector(a.North - n, a.East - e),
						new HorizontalVector(a.North + n, a.East + e) }, anchor, bottomHeight, topHeight, vertices, triangles);
				}
			}
			var mesh = new Mesh { name = "Closed navigation outline" };
			if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
			mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
			return mesh;
		}

		private static void AddRing(IReadOnlyList<HorizontalVector> source, NavigationAnchor anchor, double bottom, double top, List<Vector3> vertices, List<int> triangles)
		{
			if (source.Count < 4 || source[0].North != source[source.Count - 1].North || source[0].East != source[source.Count - 1].East)
				throw new ArgumentException("A closed source outline is required.");
			var ring = new List<Vector3>();
			for (var i = 0; i < source.Count - 1; i++)
			{
				var point = anchor.Local(source[i].North, source[i].East, top);
				if (ring.Count == 0 || (ring[ring.Count - 1] - point).sqrMagnitude > 1e-10f) ring.Add(point);
			}
			var area = 0.0;
			for (var i = 0; i < ring.Count; i++) { var a = ring[i]; var b = ring[(i + 1) % ring.Count]; area += (double)a.x * b.z - (double)b.x * a.z; }
			if (area < 0) ring.Reverse();
			var offset = vertices.Count;
			foreach (var v in ring) vertices.Add(v);
			foreach (var v in ring) vertices.Add(v - Vector3.up * (float)(top - bottom));
			var remaining = new List<int>();
			for (var i = 0; i < ring.Count; i++) remaining.Add(i);
			while (remaining.Count > 2)
			{
				var clipped = false;
				for (var i = 0; i < remaining.Count; i++)
				{
					var a = remaining[(i + remaining.Count - 1) % remaining.Count]; var b = remaining[i]; var c = remaining[(i + 1) % remaining.Count];
					var turn = Cross(ring[a], ring[b], ring[c]);
					if (Math.Abs(turn) < 1e-7) { remaining.RemoveAt(i); clipped = true; break; }
					if (turn < 0) continue;
					var blocked = false;
					foreach (var j in remaining)
					{
						if (j == a || j == b || j == c) continue;
						if (Cross(ring[a], ring[b], ring[j]) >= 0 && Cross(ring[b], ring[c], ring[j]) >= 0 && Cross(ring[c], ring[a], ring[j]) >= 0) { blocked = true; break; }
					}
					if (blocked) continue;
					triangles.Add(offset + a); triangles.Add(offset + c); triangles.Add(offset + b);
					triangles.Add(offset + ring.Count + a); triangles.Add(offset + ring.Count + b); triangles.Add(offset + ring.Count + c);
					remaining.RemoveAt(i); clipped = true; break;
				}
				if (!clipped) throw new ArgumentException("Source outline cannot be triangulated without crossing its boundary.");
			}
			for (var i = 0; i < ring.Count; i++)
			{
				var j = (i + 1) % ring.Count;
				triangles.Add(offset + i); triangles.Add(offset + j); triangles.Add(offset + ring.Count + j);
				triangles.Add(offset + i); triangles.Add(offset + ring.Count + j); triangles.Add(offset + ring.Count + i);
			}
		}
		private static double Cross(Vector3 a, Vector3 b, Vector3 c) => (double)(b.x - a.x) * (c.z - a.z) - (double)(b.z - a.z) * (c.x - a.x);
	}
}
