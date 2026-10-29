using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.World.Chart
{
	internal static class ChartIntersection
	{
		internal static bool Intersects(ChartFeature f, double a, double b, double c, double d, double n, double e, double radius)
		{
			if (f.Geometry == null)
			{
				if (radius < 0) return true;
				var dn = Math.Max(f.MinimumNorth - n, Math.Max(0, n - f.MaximumNorth));
				var de = Math.Max(f.MinimumEast - e, Math.Max(0, e - f.MaximumEast));
				return dn * dn + de * de <= radius * radius;
			}
			var g = f.Geometry;
			for (var i = 0; i < g.Points.Count; i++) if (Point(g.Points[i], a, b, c, d, n, e, radius)) return true;
			for (var i = 0; i < g.Lines.Count; i++) if (Line(g.Lines[i], a, b, c, d, n, e, radius)) return true;
			for (var i = 0; i < g.Polygons.Count; i++)
			{
				var polygon = g.Polygons[i];
				for (var r = 0; r < polygon.Rings.Count; r++) if (Line(polygon.Rings[r], a, b, c, d, n, e, radius)) return true;
				if (radius >= 0 ? polygon.Contains(n, e) : polygon.Contains(a, b) || polygon.Contains(a, d) || polygon.Contains(c, b) || polygon.Contains(c, d)) return true;
			}
			return false;
		}
		private static bool Point(HorizontalVector p, double a, double b, double c, double d, double n, double e, double radius)
		{
			if (radius < 0) return p.North >= a && p.North <= c && p.East >= b && p.East <= d;
			var dn = p.North - n; var de = p.East - e; return dn * dn + de * de <= radius * radius;
		}
		private static bool Line(IReadOnlyList<HorizontalVector> line, double a, double b, double c, double d, double n, double e, double radius)
		{
			for (var i = 0; i < line.Count; i++) if (Point(line[i], a, b, c, d, n, e, radius)) return true;
			for (var i = 1; i < line.Count; i++)
			{
				var p = line[i - 1]; var q = line[i]; var dn = q.North - p.North; var de = q.East - p.East;
				if (radius >= 0)
				{
					var length = dn * dn + de * de; var t = length == 0 ? 0 : Math.Max(0, Math.Min(1, ((n - p.North) * dn + (e - p.East) * de) / length));
					var x = p.North + t * dn - n; var y = p.East + t * de - e; if (x * x + y * y <= radius * radius) return true;
				}
				else
				{
					var lo = 0.0; var hi = 1.0;
					if (Clip(p.North, dn, a, c, ref lo, ref hi) && Clip(p.East, de, b, d, ref lo, ref hi)) return true;
				}
			}
			return false;
		}
		private static bool Clip(double start, double delta, double min, double max, ref double lo, ref double hi)
		{
			if (delta == 0) return start >= min && start <= max;
			var t0 = (min - start) / delta; var t1 = (max - start) / delta;
			lo = Math.Max(lo, Math.Min(t0, t1)); hi = Math.Min(hi, Math.Max(t0, t1)); return lo <= hi;
		}
	}
}
