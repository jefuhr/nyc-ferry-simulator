using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Painter2D helpers. Dial angles are degrees clockwise from 12 o'clock (compass style), converted to
	/// screen coordinates here with y growing downward, so no drawing code depends on Painter2D's own arc
	/// angle convention. Arcs are drawn as fine polylines.
	/// </summary>
	public static class VectorDraw
	{
		private const float ArcStepDegrees = 2f;

		/// <summary>The point at <paramref name="degrees"/> clockwise from 12 o'clock on a circle.</summary>
		public static Vector2 Polar(Vector2 centre, float radius, float degrees)
		{
			var radians = degrees * Mathf.Deg2Rad;
			return new Vector2(centre.x + radius * Mathf.Sin(radians), centre.y - radius * Mathf.Cos(radians));
		}

		public static void Line(Painter2D painter, Vector2 from, Vector2 to, Color color, float width, LineCap cap = LineCap.Butt)
		{
			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineCap = cap;
			painter.BeginPath();
			painter.MoveTo(from);
			painter.LineTo(to);
			painter.Stroke();
		}

		/// <summary>A dashed straight line; dashes are drawn as separate segments.</summary>
		public static void DashedLine(Painter2D painter, Vector2 from, Vector2 to, Color color, float width, float dash, float gap)
		{
			var length = Vector2.Distance(from, to);
			if (length <= 0f || dash <= 0f)
			{
				return;
			}

			var direction = (to - from) / length;
			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineCap = LineCap.Butt;
			painter.BeginPath();
			for (var start = 0f; start < length; start += dash + gap)
			{
				painter.MoveTo(from + direction * start);
				painter.LineTo(from + direction * Mathf.Min(length, start + dash));
			}

			painter.Stroke();
		}

		/// <summary>A connected line through <paramref name="points"/>[start, start + count).</summary>
		public static void Polyline(Painter2D painter, IReadOnlyList<Vector2> points, int start, int count, Color color, float width)
		{
			if (count < 2)
			{
				return;
			}

			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineCap = LineCap.Round;
			painter.lineJoin = LineJoin.Round;
			painter.BeginPath();
			painter.MoveTo(points[start]);
			for (var i = start + 1; i < start + count; i++)
			{
				painter.LineTo(points[i]);
			}

			painter.Stroke();
		}

		/// <summary>A dashed polyline: the dash pattern runs continuously along the whole path.</summary>
		public static void DashedPolyline(Painter2D painter, IReadOnlyList<Vector2> points, int start, int count, Color color, float width, float dash, float gap)
		{
			if (count < 2)
			{
				return;
			}

			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineCap = LineCap.Butt;
			painter.BeginPath();
			var period = dash + gap;
			var phase = 0f;
			for (var i = start + 1; i < start + count; i++)
			{
				var a = points[i - 1];
				var b = points[i];
				var length = Vector2.Distance(a, b);
				if (length <= 0f)
				{
					continue;
				}

				var direction = (b - a) / length;
				var along = 0f;
				while (along < length)
				{
					var inDash = phase < dash;
					var step = Mathf.Min(length - along, inDash ? dash - phase : period - phase);
					if (inDash)
					{
						painter.MoveTo(a + direction * along);
						painter.LineTo(a + direction * (along + step));
					}

					along += step;
					phase = (phase + step) % period;
				}
			}

			painter.Stroke();
		}

		public static void Arc(Painter2D painter, Vector2 centre, float radius, float fromDegrees, float toDegrees, Color color, float width, LineCap cap = LineCap.Butt)
		{
			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineCap = cap;
			painter.lineJoin = LineJoin.Round;
			painter.BeginPath();
			var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(toDegrees - fromDegrees) / ArcStepDegrees));
			painter.MoveTo(Polar(centre, radius, fromDegrees));
			for (var i = 1; i <= steps; i++)
			{
				painter.LineTo(Polar(centre, radius, Mathf.Lerp(fromDegrees, toDegrees, i / (float)steps)));
			}

			painter.Stroke();
		}

		/// <summary>A filled ring segment between two radii.</summary>
		public static void ArcBand(Painter2D painter, Vector2 centre, float innerRadius, float outerRadius, float fromDegrees, float toDegrees, Color color)
		{
			var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(toDegrees - fromDegrees) / ArcStepDegrees));
			painter.fillColor = color;
			painter.BeginPath();
			painter.MoveTo(Polar(centre, outerRadius, fromDegrees));
			for (var i = 1; i <= steps; i++)
			{
				painter.LineTo(Polar(centre, outerRadius, Mathf.Lerp(fromDegrees, toDegrees, i / (float)steps)));
			}

			for (var i = steps; i >= 0; i--)
			{
				painter.LineTo(Polar(centre, innerRadius, Mathf.Lerp(fromDegrees, toDegrees, i / (float)steps)));
			}

			painter.ClosePath();
			painter.Fill(FillRule.NonZero);
		}

		public static void Circle(Painter2D painter, Vector2 centre, float radius, Color color)
		{
			var steps = Mathf.Clamp(Mathf.CeilToInt(radius), 12, 90);
			painter.fillColor = color;
			painter.BeginPath();
			painter.MoveTo(Polar(centre, radius, 0f));
			for (var i = 1; i < steps; i++)
			{
				painter.LineTo(Polar(centre, radius, 360f * i / steps));
			}

			painter.ClosePath();
			painter.Fill(FillRule.NonZero);
		}

		public static void Triangle(Painter2D painter, Vector2 a, Vector2 b, Vector2 c, Color color)
		{
			painter.fillColor = color;
			painter.BeginPath();
			painter.MoveTo(a);
			painter.LineTo(b);
			painter.LineTo(c);
			painter.ClosePath();
			painter.Fill(FillRule.NonZero);
		}

		public static void TriangleOutline(Painter2D painter, Vector2 a, Vector2 b, Vector2 c, Color color, float width)
		{
			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineJoin = LineJoin.Miter;
			painter.BeginPath();
			painter.MoveTo(a);
			painter.LineTo(b);
			painter.LineTo(c);
			painter.ClosePath();
			painter.Stroke();
		}

		public static void Quad(Painter2D painter, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
		{
			painter.fillColor = color;
			painter.BeginPath();
			painter.MoveTo(a);
			painter.LineTo(b);
			painter.LineTo(c);
			painter.LineTo(d);
			painter.ClosePath();
			painter.Fill(FillRule.NonZero);
		}

		public static void Rect(Painter2D painter, Rect rect, Color color)
		{
			if (rect.width <= 0f || rect.height <= 0f)
			{
				return;
			}

			Quad(painter, new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin), new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax), color);
		}

		public static void RectOutline(Painter2D painter, Rect rect, Color color, float width)
		{
			painter.strokeColor = color;
			painter.lineWidth = width;
			painter.lineJoin = LineJoin.Miter;
			painter.BeginPath();
			painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
			painter.LineTo(new Vector2(rect.xMax, rect.yMin));
			painter.LineTo(new Vector2(rect.xMax, rect.yMax));
			painter.LineTo(new Vector2(rect.xMin, rect.yMax));
			painter.ClosePath();
			painter.Stroke();
		}

		/// <summary>A filled rectangle with rounded corners.</summary>
		public static void RoundedRect(Painter2D painter, Rect rect, float radius, Color color)
		{
			radius = Mathf.Min(radius, rect.width * 0.5f, rect.height * 0.5f);
			if (radius <= 0.5f)
			{
				Rect(painter, rect, color);
				return;
			}

			painter.fillColor = color;
			painter.BeginPath();
			CornerPath(painter, rect, radius);
			painter.ClosePath();
			painter.Fill(FillRule.NonZero);
		}

		private static void CornerPath(Painter2D painter, Rect rect, float radius)
		{
			var topLeft = new Vector2(rect.xMin + radius, rect.yMin + radius);
			var topRight = new Vector2(rect.xMax - radius, rect.yMin + radius);
			var bottomRight = new Vector2(rect.xMax - radius, rect.yMax - radius);
			var bottomLeft = new Vector2(rect.xMin + radius, rect.yMax - radius);
			painter.MoveTo(Polar(topLeft, radius, 270f));
			ArcPoints(painter, topLeft, radius, 270f, 360f);
			ArcPoints(painter, topRight, radius, 0f, 90f);
			ArcPoints(painter, bottomRight, radius, 90f, 180f);
			ArcPoints(painter, bottomLeft, radius, 180f, 270f);
		}

		private static void ArcPoints(Painter2D painter, Vector2 centre, float radius, float fromDegrees, float toDegrees)
		{
			const int steps = 6;
			for (var i = 0; i <= steps; i++)
			{
				painter.LineTo(Polar(centre, radius, Mathf.Lerp(fromDegrees, toDegrees, i / (float)steps)));
			}
		}
	}
}
