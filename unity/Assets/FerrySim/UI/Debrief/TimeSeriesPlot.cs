using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>How a series is drawn. The second series of a plot is dashed so no plot relies on color alone.</summary>
	public enum SeriesStyle
	{
		PrimarySolid = 0,
		SecondaryDashed = 1,
		PortSolid = 2,
		StarboardDashed = 3,
		NeutralThin = 4,
	}

	/// <summary>One plotted series in display units, sharing the plot's time column.</summary>
	public sealed class PlotSeriesData
	{
		public PlotSeriesData(string name, IReadOnlyList<double> values, SeriesStyle style, bool stepped = false)
		{
			Name = name;
			Values = values;
			Style = style;
			Stepped = stepped;
		}

		public string Name { get; }

		/// <summary>Values in display units; NaN leaves a gap.</summary>
		public IReadOnlyList<double> Values { get; }

		public SeriesStyle Style { get; }

		/// <summary>Held from each sample to the next (control inputs), instead of joined by straight lines.</summary>
		public bool Stepped { get; }
	}

	/// <summary>
	/// A debrief plot against time: grid, series, shaded event spans, and a cursor line on its own layer so
	/// scrubbing repaints only the cursor. Dragging on the plot scrubs.
	/// </summary>
	public sealed class TimeSeriesPlot : VisualElement, IPaletteAware
	{
		internal const float LeftMargin = 46f;
		internal const float RightMargin = 8f;
		internal const float TopMargin = 4f;
		internal const float BottomMarginWithAxis = 18f;
		internal const float BottomMarginPlain = 4f;

		private readonly Label _title;
		private readonly VisualElement _legend;
		private readonly VisualElement _canvas;
		private readonly SeriesLayer _seriesLayer;
		private readonly CursorLayer _cursorLayer;
		private readonly List<Label> _yLabels = new List<Label>();
		private readonly List<Label> _xLabels = new List<Label>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private bool _dragging;

		public TimeSeriesPlot(string title, string plotName)
		{
			name = plotName;
			AddToClassList("fs-plot");
			var header = new VisualElement { pickingMode = PickingMode.Ignore };
			header.AddToClassList("fs-plot-header");
			_title = PaletteRoles.Label(title, PaletteRoles.Text, "fs-caption", "fs-strong");
			header.Add(_title);
			_legend = new VisualElement { pickingMode = PickingMode.Ignore };
			_legend.AddToClassList("fs-plot-legend");
			header.Add(_legend);
			Add(header);

			_canvas = new VisualElement();
			_canvas.AddToClassList("fs-plot-canvas");
			_seriesLayer = new SeriesLayer(this);
			_cursorLayer = new CursorLayer(this);
			_canvas.Add(_seriesLayer);
			_canvas.Add(_cursorLayer);
			Add(_canvas);
			_canvas.RegisterCallback<GeometryChangedEvent>(_ => LayoutAxisLabels());
			_canvas.RegisterCallback<PointerDownEvent>(OnPointerDown);
			_canvas.RegisterCallback<PointerMoveEvent>(OnPointerMove);
			_canvas.RegisterCallback<PointerUpEvent>(OnPointerUp);
		}

		/// <summary>Raised with the time under the pointer while the user presses or drags on the plot.</summary>
		public event Action<double> Scrubbed;

		public IReadOnlyList<double> Time { get; private set; } = Array.Empty<double>();

		public IReadOnlyList<PlotSeriesData> Series { get; private set; } = Array.Empty<PlotSeriesData>();

		public IReadOnlyList<DebriefEvent> Events { get; private set; } = Array.Empty<DebriefEvent>();

		public AxisRange TimeRange { get; private set; } = new AxisRange(0.0, 1.0);

		public AxisRange ValueRange { get; private set; } = new AxisRange(0.0, 1.0);

		public double ValueStep { get; private set; } = 0.2;

		/// <summary>Heading-style data: breaks lines where the value wraps through 0/360.</summary>
		public bool Compass { get; private set; }

		public bool ShowTimeAxis { get; set; }

		/// <summary>Optional vertical reference line (for example the maneuver start); NaN for none.</summary>
		public double MarkTime { get; set; } = double.NaN;

		public double CursorTime { get; private set; } = double.NaN;

		public string LegendText { get; private set; } = string.Empty;

		internal InstrumentPalette Palette => _palette;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			_seriesLayer.ApplyPalette(_palette);
			_cursorLayer.ApplyPalette(_palette);
			foreach (var label in _yLabels)
			{
				label.style.color = _palette.Scale;
			}

			foreach (var label in _xLabels)
			{
				label.style.color = _palette.Scale;
			}

			RebuildLegend();
		}

		/// <param name="fixedRange">A fixed value axis with its tick step, or null to fit the data.</param>
		public void SetData(IReadOnlyList<double> time, IReadOnlyList<PlotSeriesData> series, AxisRange timeRange, AxisRange? fixedRange, double fixedStep, bool compass)
		{
			Time = time ?? Array.Empty<double>();
			Series = series ?? Array.Empty<PlotSeriesData>();
			TimeRange = timeRange;
			Compass = compass;
			if (fixedRange.HasValue)
			{
				ValueRange = fixedRange.Value;
				ValueStep = fixedStep;
			}
			else
			{
				var data = new List<IReadOnlyList<double>>();
				foreach (var s in Series)
				{
					data.Add(s.Values);
				}

				var range = PlotScale.DataRange(data);
				ValueRange = PlotScale.NiceRange(range.Min, range.Max, 4, out var step);
				ValueStep = step;
			}

			RebuildLegend();
			RebuildAxisLabels();
			_seriesLayer.MarkDirtyRepaint();
			_cursorLayer.MarkDirtyRepaint();
		}

		public void SetEvents(IReadOnlyList<DebriefEvent> events)
		{
			Events = events ?? Array.Empty<DebriefEvent>();
			_seriesLayer.MarkDirtyRepaint();
		}

		public void SetCursor(double time)
		{
			if (time.Equals(CursorTime))
			{
				return;
			}

			CursorTime = time;
			_cursorLayer.MarkDirtyRepaint();
		}

		internal Rect PlotRect(Rect canvas) =>
			new Rect(canvas.xMin + LeftMargin, canvas.yMin + TopMargin,
				Mathf.Max(1f, canvas.width - LeftMargin - RightMargin),
				Mathf.Max(1f, canvas.height - TopMargin - (ShowTimeAxis ? BottomMarginWithAxis : BottomMarginPlain)));

		private void RebuildLegend()
		{
			_legend.Clear();
			var parts = new List<string>();
			foreach (var series in Series)
			{
				var swatch = new LegendSwatch(series.Style);
				swatch.ApplyPalette(_palette);
				_legend.Add(swatch);
				var label = PaletteRoles.Label(series.Name, PaletteRoles.Dim, "fs-legend-label");
				label.style.color = _palette.TextDim;
				_legend.Add(label);
				parts.Add(series.Name);
			}

			LegendText = string.Join(", ", parts);
		}

		private void RebuildAxisLabels()
		{
			foreach (var label in _yLabels)
			{
				label.RemoveFromHierarchy();
			}

			foreach (var label in _xLabels)
			{
				label.RemoveFromHierarchy();
			}

			_yLabels.Clear();
			_xLabels.Clear();
			var decimals = PlotScale.TickDecimals(ValueStep);
			foreach (var tick in PlotScale.Ticks(ValueRange, ValueStep))
			{
				var label = PaletteRoles.Label(tick.ToString("F" + decimals, CultureInfo.InvariantCulture), null, "fs-axis-label");
				label.style.color = _palette.Scale;
				label.userData = tick;
				_canvas.Add(label);
				_yLabels.Add(label);
			}

			if (ShowTimeAxis)
			{
				var step = PlotScale.NiceStep(TimeRange.Span, 8);
				var timeDecimals = PlotScale.TickDecimals(step);
				foreach (var tick in PlotScale.Ticks(TimeRange, step))
				{
					var label = PaletteRoles.Label(tick.ToString("F" + timeDecimals, CultureInfo.InvariantCulture) + " s", null, "fs-axis-label");
					label.style.color = _palette.Scale;
					label.userData = tick;
					_canvas.Add(label);
					_xLabels.Add(label);
				}
			}

			LayoutAxisLabels();
		}

		private void LayoutAxisLabels()
		{
			var canvas = _canvas.contentRect;
			if (canvas.width < 1f || float.IsNaN(canvas.width))
			{
				return;
			}

			var plot = PlotRect(canvas);
			foreach (var label in _yLabels)
			{
				var y = PlotScale.ToPixel((double)label.userData, ValueRange, plot.yMax, plot.yMin);
				label.style.position = Position.Absolute;
				label.style.left = plot.xMin - 5f;
				label.style.top = y;
				label.style.translate = new Translate(Length.Percent(-100f), Length.Percent(-50f));
			}

			foreach (var label in _xLabels)
			{
				var x = PlotScale.ToPixel((double)label.userData, TimeRange, plot.xMin, plot.xMax);
				label.style.position = Position.Absolute;
				label.style.left = x;
				label.style.top = plot.yMax + 2f;
				label.style.translate = new Translate(Length.Percent(-50f), 0f);
			}
		}

		private void OnPointerDown(PointerDownEvent evt)
		{
			_dragging = true;
			_canvas.CapturePointer(evt.pointerId);
			Scrub(evt.localPosition.x);
		}

		private void OnPointerMove(PointerMoveEvent evt)
		{
			if (_dragging && _canvas.HasPointerCapture(evt.pointerId))
			{
				Scrub(evt.localPosition.x);
			}
		}

		private void OnPointerUp(PointerUpEvent evt)
		{
			_dragging = false;
			_canvas.ReleasePointer(evt.pointerId);
		}

		private void Scrub(float x)
		{
			var plot = PlotRect(_canvas.contentRect);
			var time = PlotScale.FromPixel(x, TimeRange, plot.xMin, plot.xMax);
			Scrubbed?.Invoke(Math.Max(TimeRange.Min, Math.Min(TimeRange.Max, time)));
		}

		internal static Color SeriesColor(InstrumentPalette palette, SeriesStyle style)
		{
			switch (style)
			{
				case SeriesStyle.SecondaryDashed:
					return palette.SeriesB;
				case SeriesStyle.PortSolid:
					return palette.Port;
				case SeriesStyle.StarboardDashed:
					return palette.Starboard;
				case SeriesStyle.NeutralThin:
					return palette.TextDim;
				default:
					return palette.SeriesA;
			}
		}

		internal static bool IsDashed(SeriesStyle style) => style == SeriesStyle.SecondaryDashed || style == SeriesStyle.StarboardDashed;

		internal static Color EventColor(InstrumentPalette palette, DebriefEventKind kind) =>
			kind == DebriefEventKind.GearShift ? palette.Shifting :
			kind == DebriefEventKind.Contact ? palette.Caution :
			palette.Alarm;

		private sealed class SeriesLayer : InstrumentElement
		{
			private readonly TimeSeriesPlot _owner;
			private readonly List<PlotPoint> _points = new List<PlotPoint>();
			private readonly List<Vector2> _pixels = new List<Vector2>();

			public SeriesLayer(TimeSeriesPlot owner)
			{
				_owner = owner;
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var plot = _owner.PlotRect(rect);
				var x = _owner.TimeRange;
				var y = _owner.ValueRange;
				VectorDraw.Rect(painter, plot, Palette.Face);

				foreach (var e in _owner.Events)
				{
					var start = PlotScale.ToPixel(e.Time, x, plot.xMin, plot.xMax);
					var endTime = double.IsNaN(e.EndTime) ? x.Max : e.EndTime;
					var end = PlotScale.ToPixel(endTime, x, plot.xMin, plot.xMax);
					var color = EventColor(Palette, e.Kind);
					if (end - start >= 1f)
					{
						VectorDraw.Rect(painter, new Rect(start, plot.yMin, end - start, plot.height), new Color(color.r, color.g, color.b, 0.08f));
					}

					VectorDraw.Line(painter, new Vector2(start, plot.yMin), new Vector2(start, plot.yMax), new Color(color.r, color.g, color.b, 0.7f), 1.2f);
				}

				foreach (var tick in PlotScale.Ticks(y, _owner.ValueStep))
				{
					var py = PlotScale.ToPixel(tick, y, plot.yMax, plot.yMin);
					VectorDraw.Line(painter, new Vector2(plot.xMin, py), new Vector2(plot.xMax, py), Math.Abs(tick) < 1e-9 ? Palette.Bezel : Palette.Grid, 1f);
				}

				if (_owner.ShowTimeAxis)
				{
					var step = PlotScale.NiceStep(x.Span, 8);
					foreach (var tick in PlotScale.Ticks(x, step))
					{
						var px = PlotScale.ToPixel(tick, x, plot.xMin, plot.xMax);
						VectorDraw.Line(painter, new Vector2(px, plot.yMin), new Vector2(px, plot.yMax), Palette.Grid, 1f);
					}
				}

				if (InstrumentFormat.IsFinite(_owner.MarkTime))
				{
					var mx = PlotScale.ToPixel(_owner.MarkTime, x, plot.xMin, plot.xMax);
					VectorDraw.DashedLine(painter, new Vector2(mx, plot.yMin), new Vector2(mx, plot.yMax), Palette.TextDim, 1f, 2f, 3f);
				}

				var buckets = Mathf.Max(16, Mathf.RoundToInt(plot.width));
				foreach (var series in _owner.Series)
				{
					DrawSeries(painter, plot, series, buckets);
				}

				VectorDraw.RectOutline(painter, plot, Palette.Bezel, 1f);
			}

			private void DrawSeries(Painter2D painter, Rect plot, PlotSeriesData series, int buckets)
			{
				var time = _owner.Time;
				var values = series.Values;
				var count = Mathf.Min(time.Count, values.Count);
				var color = SeriesColor(Palette, series.Style);
				var width = series.Style == SeriesStyle.NeutralThin ? 1.2f : 2f;
				var runStart = -1;
				for (var i = 0; i <= count; i++)
				{
					var breakHere = i == count || double.IsNaN(values[i]) ||
						(_owner.Compass && runStart >= 0 && i > runStart && Math.Abs(values[i] - values[i - 1]) > 180.0);
					if (breakHere)
					{
						if (runStart >= 0)
						{
							DrawRun(painter, plot, series, runStart, i, buckets, color, width);
						}

						runStart = i < count && !double.IsNaN(values[i]) ? i : -1;
					}
					else if (runStart < 0)
					{
						runStart = i;
					}
				}
			}

			private void DrawRun(Painter2D painter, Rect plot, PlotSeriesData series, int start, int end, int buckets, Color color, float width)
			{
				_points.Clear();
				PlotDecimation.Decimate(_owner.Time, series.Values, start, end, _owner.TimeRange.Min, _owner.TimeRange.Max, buckets, _points);
				_pixels.Clear();
				for (var i = 0; i < _points.Count; i++)
				{
					var pixel = new Vector2(
						PlotScale.ToPixel(_points[i].X, _owner.TimeRange, plot.xMin, plot.xMax),
						Mathf.Clamp(PlotScale.ToPixel(_points[i].Y, _owner.ValueRange, plot.yMax, plot.yMin), plot.yMin, plot.yMax));
					if (series.Stepped && _pixels.Count > 0)
					{
						_pixels.Add(new Vector2(pixel.x, _pixels[_pixels.Count - 1].y));
					}

					_pixels.Add(pixel);
				}

				if (_pixels.Count == 1)
				{
					VectorDraw.Circle(painter, _pixels[0], width, color);
					return;
				}

				if (IsDashed(series.Style))
				{
					VectorDraw.DashedPolyline(painter, _pixels, 0, _pixels.Count, color, width, 6f, 4f);
				}
				else
				{
					VectorDraw.Polyline(painter, _pixels, 0, _pixels.Count, color, width);
				}
			}
		}

		private sealed class CursorLayer : InstrumentElement
		{
			private readonly TimeSeriesPlot _owner;

			public CursorLayer(TimeSeriesPlot owner)
			{
				_owner = owner;
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				if (!InstrumentFormat.IsFinite(_owner.CursorTime))
				{
					return;
				}

				var plot = _owner.PlotRect(rect);
				var x = PlotScale.ToPixel(_owner.CursorTime, _owner.TimeRange, plot.xMin, plot.xMax);
				VectorDraw.Line(painter, new Vector2(x, plot.yMin), new Vector2(x, plot.yMax), Palette.Cursor, 1.5f);
			}
		}
	}

	/// <summary>A short line sample showing a series' color and dash style in a legend.</summary>
	public sealed class LegendSwatch : InstrumentElement
	{
		private readonly SeriesStyle _style;

		public LegendSwatch(SeriesStyle style)
		{
			_style = style;
			AddToClassList("fs-legend-swatch");
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var y = rect.center.y;
			var color = TimeSeriesPlot.SeriesColor(Palette, _style);
			if (TimeSeriesPlot.IsDashed(_style))
			{
				VectorDraw.DashedLine(painter, new Vector2(rect.xMin, y), new Vector2(rect.xMax, y), color, 2f, 5f, 3f);
			}
			else
			{
				VectorDraw.Line(painter, new Vector2(rect.xMin, y), new Vector2(rect.xMax, y), color, _style == SeriesStyle.NeutralThin ? 1.2f : 2f);
			}
		}
	}
}
