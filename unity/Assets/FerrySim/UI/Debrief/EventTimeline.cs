using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The scrub bar under the debrief plots: the whole recording as one bar, events as marks (diamond for
	/// gear shifts, circle for contact, triangle for depth alarms, square for traffic CPA alarms and a cross for
	/// collisions with traffic, so kinds differ by shape as well as color) and a handle at the cursor. Press or
	/// drag anywhere on it to scrub.
	/// </summary>
	public sealed class EventTimeline : InstrumentElement
	{
		private const float Margin = 10f;

		private bool _dragging;

		public EventTimeline()
		{
			name = "event-timeline";
			AddToClassList("fs-timeline");
			pickingMode = PickingMode.Position;
			RegisterCallback<PointerDownEvent>(OnPointerDown);
			RegisterCallback<PointerMoveEvent>(OnPointerMove);
			RegisterCallback<PointerUpEvent>(OnPointerUp);
		}

		public event Action<double> Scrubbed;

		public AxisRange TimeRange { get; private set; } = new AxisRange(0.0, 1.0);

		public IReadOnlyList<DebriefEvent> Events { get; private set; } = Array.Empty<DebriefEvent>();

		public double CursorTime { get; private set; } = double.NaN;

		public void SetData(AxisRange timeRange, IReadOnlyList<DebriefEvent> events)
		{
			TimeRange = timeRange;
			Events = events ?? Array.Empty<DebriefEvent>();
			MarkDirtyRepaint();
		}

		public void SetCursor(double time)
		{
			if (time.Equals(CursorTime))
			{
				return;
			}

			CursorTime = time;
			MarkDirtyRepaint();
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var left = rect.xMin + Margin;
			var right = rect.xMax - Margin;
			var middle = rect.center.y;
			VectorDraw.RoundedRect(painter, new Rect(left, middle - 3f, right - left, 6f), 3f, Palette.Bezel);
			foreach (var e in Events)
			{
				var x = PlotScale.ToPixel(e.Time, TimeRange, left, right);
				var color = TimeSeriesPlot.EventColor(Palette, e.Kind);
				var y = middle - 12f;
				switch (e.Kind)
				{
					case DebriefEventKind.GearShift:
						VectorDraw.Quad(painter, new Vector2(x, y - 6f), new Vector2(x + 6f, y), new Vector2(x, y + 6f), new Vector2(x - 6f, y), color);
						break;
					case DebriefEventKind.Contact:
						VectorDraw.Circle(painter, new Vector2(x, y), 5.5f, color);
						break;
					case DebriefEventKind.TrafficAlarm:
						VectorDraw.Rect(painter, new Rect(x - 5f, y - 5f, 10f, 10f), color);
						break;
					case DebriefEventKind.TrafficCollision:
						VectorDraw.Line(painter, new Vector2(x - 6f, y - 6f), new Vector2(x + 6f, y + 6f), color, 3f);
						VectorDraw.Line(painter, new Vector2(x - 6f, y + 6f), new Vector2(x + 6f, y - 6f), color, 3f);
						break;
					case DebriefEventKind.TrafficAlongside:
						// Two hulls side by side.
						VectorDraw.Rect(painter, new Rect(x - 6f, y - 6f, 4f, 12f), color);
						VectorDraw.Rect(painter, new Rect(x + 2f, y - 6f, 4f, 12f), color);
						break;
					default:
						VectorDraw.Triangle(painter, new Vector2(x, y + 6f), new Vector2(x - 6.5f, y - 6f), new Vector2(x + 6.5f, y - 6f), color);
						break;
				}
			}

			if (!InstrumentFormat.IsFinite(CursorTime))
			{
				return;
			}

			var cx = PlotScale.ToPixel(CursorTime, TimeRange, left, right);
			VectorDraw.Rect(painter, new Rect(left, middle - 2f, cx - left, 4f), Palette.Fill);
			VectorDraw.RoundedRect(painter, new Rect(cx - 5f, middle - 11f, 10f, 22f), 3f, Palette.Cursor);
		}

		private void OnPointerDown(PointerDownEvent evt)
		{
			_dragging = true;
			this.CapturePointer(evt.pointerId);
			Scrub(evt.localPosition.x);
		}

		private void OnPointerMove(PointerMoveEvent evt)
		{
			if (_dragging && this.HasPointerCapture(evt.pointerId))
			{
				Scrub(evt.localPosition.x);
			}
		}

		private void OnPointerUp(PointerUpEvent evt)
		{
			_dragging = false;
			this.ReleasePointer(evt.pointerId);
		}

		private void Scrub(float x)
		{
			var rect = contentRect;
			var time = PlotScale.FromPixel(x, TimeRange, rect.xMin + Margin, rect.xMax - Margin);
			Scrubbed?.Invoke(Math.Max(TimeRange.Min, Math.Min(TimeRange.Max, time)));
		}
	}
}
