using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The track over the ground, north up, in metres or feet from the starting point, drawn at equal
	/// scale on both axes so turns are not distorted. Events are marked where they happened and the boat
	/// is drawn to scale (never smaller than a visible minimum) at the cursor. Clicking near the track
	/// moves the cursor to that moment.
	/// </summary>
	public sealed class TrackPlot : VisualElement, IPaletteAware
	{
		private const float Inset = 10f;

		private readonly Label _title;
		private readonly TrackLayer _trackLayer;
		private readonly VesselLayer _vesselLayer;
		private readonly VisualElement _canvas;
		private readonly List<Label> _gridLabels = new List<Label>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private DistanceUnit _unit = DistanceUnit.Feet;

		public TrackPlot()
		{
			name = "track-plot";
			AddToClassList("fs-track");
			_title = PaletteRoles.Label("TRACK", PaletteRoles.Text, "fs-caption", "fs-strong");
			Add(_title);
			_canvas = new VisualElement();
			_canvas.AddToClassList("fs-track-canvas");
			_trackLayer = new TrackLayer(this);
			_vesselLayer = new VesselLayer(this);
			_canvas.Add(_trackLayer);
			_canvas.Add(_vesselLayer);
			Add(_canvas);
			_canvas.RegisterCallback<GeometryChangedEvent>(_ => Relayout());
			_canvas.RegisterCallback<PointerDownEvent>(OnPointerDown);
		}

		public event Action<double> Scrubbed;

		public TelemetryRecording Recording { get; private set; }

		public IReadOnlyList<DebriefEvent> Events { get; private set; } = Array.Empty<DebriefEvent>();

		/// <summary>Boat length overall for drawing it to scale, metres; NaN draws the minimum size.</summary>
		public double VesselLength { get; private set; } = double.NaN;

		public int CursorIndex { get; private set; } = -1;

		/// <summary>Plot ranges in metres east and north of the first sample, after equal scaling.</summary>
		public AxisRange EastRange { get; private set; }

		public AxisRange NorthRange { get; private set; }

		public string TitleText => _title.text;

		public DistanceUnit DistanceUnit
		{
			get => _unit;
			set
			{
				_unit = value;
				UpdateTitle();
				Relayout();
			}
		}

		internal InstrumentPalette Palette => _palette;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			_trackLayer.ApplyPalette(_palette);
			_vesselLayer.ApplyPalette(_palette);
			foreach (var label in _gridLabels)
			{
				label.style.color = _palette.Scale;
			}
		}

		public void SetData(TelemetryRecording recording, IReadOnlyList<DebriefEvent> events, double vesselLength)
		{
			Recording = recording;
			Events = events ?? Array.Empty<DebriefEvent>();
			VesselLength = vesselLength;
			CursorIndex = recording != null ? 0 : -1;
			UpdateTitle();
			Relayout();
		}

		public void SetEvents(IReadOnlyList<DebriefEvent> events)
		{
			Events = events ?? Array.Empty<DebriefEvent>();
			_trackLayer.MarkDirtyRepaint();
		}

		public void SetCursorIndex(int index)
		{
			if (index == CursorIndex)
			{
				return;
			}

			CursorIndex = index;
			_vesselLayer.MarkDirtyRepaint();
		}

		internal Rect PlotRect(Rect canvas) =>
			new Rect(canvas.xMin + Inset, canvas.yMin + Inset, Mathf.Max(1f, canvas.width - 2f * Inset), Mathf.Max(1f, canvas.height - 2f * Inset));

		internal Vector2 ToPixel(Rect plot, double east, double north) =>
			new Vector2(PlotScale.ToPixel(east, EastRange, plot.xMin, plot.xMax), PlotScale.ToPixel(north, NorthRange, plot.yMax, plot.yMin));

		internal double EastOf(int index) => Recording.East[index] - Recording.East[0];

		internal double NorthOf(int index) => Recording.North[index] - Recording.North[0];

		internal double GridStepMetres
		{
			get
			{
				var span = DisplayUnits.Distance(Math.Max(EastRange.Span, NorthRange.Span), _unit);
				return DisplayUnits.DistanceToMetres(PlotScale.NiceStep(span, 6), _unit);
			}
		}

		private void UpdateTitle()
		{
			PaletteRoles.SetText(_title, $"TRACK ({DisplayUnits.Symbol(_unit)} from start, north up)");
		}

		private void Relayout()
		{
			var canvas = _canvas.contentRect;
			if (Recording == null || canvas.width < 1f || float.IsNaN(canvas.width))
			{
				_trackLayer.MarkDirtyRepaint();
				_vesselLayer.MarkDirtyRepaint();
				return;
			}

			var east = new double[Recording.Count];
			var north = new double[Recording.Count];
			for (var i = 0; i < Recording.Count; i++)
			{
				east[i] = EastOf(i);
				north[i] = NorthOf(i);
			}

			var eastRange = PlotScale.DataRange(new IReadOnlyList<double>[] { east });
			var northRange = PlotScale.DataRange(new IReadOnlyList<double>[] { north });
			var pad = 0.08 * Math.Max(Math.Max(eastRange.Span, northRange.Span), PlotScale.MinimumTrackSpan);
			var plot = PlotRect(canvas);
			PlotScale.EqualAspect(
				new AxisRange(eastRange.Min - pad, eastRange.Max + pad),
				new AxisRange(northRange.Min - pad, northRange.Max + pad),
				plot.width, plot.height, out var equalEast, out var equalNorth);
			EastRange = equalEast;
			NorthRange = equalNorth;
			RebuildGridLabels(plot);
			_trackLayer.MarkDirtyRepaint();
			_vesselLayer.MarkDirtyRepaint();
		}

		private void RebuildGridLabels(Rect plot)
		{
			foreach (var label in _gridLabels)
			{
				label.RemoveFromHierarchy();
			}

			_gridLabels.Clear();
			var step = GridStepMetres;
			var stepDisplay = DisplayUnits.Distance(step, _unit);
			var decimals = PlotScale.TickDecimals(stepDisplay);
			foreach (var tick in PlotScale.Ticks(EastRange, step))
			{
				var label = GridLabel(DisplayUnits.Distance(tick, _unit), decimals);
				label.style.left = PlotScale.ToPixel(tick, EastRange, plot.xMin, plot.xMax);
				label.style.top = plot.yMax - 2f;
				label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-100f));
			}

			foreach (var tick in PlotScale.Ticks(NorthRange, step))
			{
				var label = GridLabel(DisplayUnits.Distance(tick, _unit), decimals);
				label.style.left = plot.xMin + 3f;
				label.style.top = PlotScale.ToPixel(tick, NorthRange, plot.yMax, plot.yMin);
				label.style.translate = new Translate(0f, Length.Percent(-50f));
			}
		}

		private Label GridLabel(double value, int decimals)
		{
			var label = PaletteRoles.Label(value.ToString("F" + decimals, CultureInfo.InvariantCulture), null, "fs-axis-label");
			label.style.position = Position.Absolute;
			label.style.color = _palette.Scale;
			_canvas.Insert(1, label);
			_gridLabels.Add(label);
			return label;
		}

		private void OnPointerDown(PointerDownEvent evt)
		{
			if (Recording == null)
			{
				return;
			}

			var plot = PlotRect(_canvas.contentRect);
			var target = new Vector2(evt.localPosition.x, evt.localPosition.y);
			var best = -1;
			var bestDistance = float.MaxValue;
			for (var i = 0; i < Recording.Count; i++)
			{
				var distance = (ToPixel(plot, EastOf(i), NorthOf(i)) - target).sqrMagnitude;
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = i;
				}
			}

			if (best >= 0 && bestDistance <= 24f * 24f)
			{
				Scrubbed?.Invoke(Recording.Time[best]);
			}
		}

		private sealed class TrackLayer : InstrumentElement
		{
			private readonly TrackPlot _owner;
			private readonly List<Vector2> _pixels = new List<Vector2>();

			public TrackLayer(TrackPlot owner)
			{
				_owner = owner;
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var plot = _owner.PlotRect(rect);
				VectorDraw.Rect(painter, plot, Palette.Face);
				var recording = _owner.Recording;
				if (recording == null)
				{
					VectorDraw.RectOutline(painter, plot, Palette.Bezel, 1f);
					return;
				}

				var step = _owner.GridStepMetres;
				foreach (var tick in PlotScale.Ticks(_owner.EastRange, step))
				{
					var x = PlotScale.ToPixel(tick, _owner.EastRange, plot.xMin, plot.xMax);
					VectorDraw.Line(painter, new Vector2(x, plot.yMin), new Vector2(x, plot.yMax), Math.Abs(tick) < 1e-9 ? Palette.Bezel : Palette.Grid, 1f);
				}

				foreach (var tick in PlotScale.Ticks(_owner.NorthRange, step))
				{
					var y = PlotScale.ToPixel(tick, _owner.NorthRange, plot.yMax, plot.yMin);
					VectorDraw.Line(painter, new Vector2(plot.xMin, y), new Vector2(plot.xMax, y), Math.Abs(tick) < 1e-9 ? Palette.Bezel : Palette.Grid, 1f);
				}

				// Thin the polyline to about one point per pixel of travel.
				_pixels.Clear();
				for (var i = 0; i < recording.Count; i++)
				{
					var pixel = _owner.ToPixel(plot, _owner.EastOf(i), _owner.NorthOf(i));
					if (_pixels.Count == 0 || i == recording.Count - 1 || (pixel - _pixels[_pixels.Count - 1]).sqrMagnitude >= 1f)
					{
						_pixels.Add(pixel);
					}
				}

				VectorDraw.Polyline(painter, _pixels, 0, _pixels.Count, Palette.SeriesA, 2f);
				var start = _pixels[0];
				var end = _pixels[_pixels.Count - 1];
				VectorDraw.Circle(painter, start, 5f, Palette.Text);
				VectorDraw.Circle(painter, start, 3f, Palette.Face);
				VectorDraw.Rect(painter, new Rect(end.x - 4f, end.y - 4f, 8f, 8f), Palette.Text);

				foreach (var e in _owner.Events)
				{
					var index = recording.IndexAtOrBefore(e.Time);
					var p = _owner.ToPixel(plot, _owner.EastOf(index), _owner.NorthOf(index));
					var color = TimeSeriesPlot.EventColor(Palette, e.Kind);
					VectorDraw.Quad(painter, p + new Vector2(0f, -6f), p + new Vector2(6f, 0f), p + new Vector2(0f, 6f), p + new Vector2(-6f, 0f), color);
				}

				// North arrow, top right.
				var arrow = new Vector2(plot.xMax - 14f, plot.yMin + 8f);
				VectorDraw.Triangle(painter, arrow, arrow + new Vector2(-6f, 14f), arrow + new Vector2(6f, 14f), Palette.TextDim);
				VectorDraw.RectOutline(painter, plot, Palette.Bezel, 1f);
			}
		}

		private sealed class VesselLayer : InstrumentElement
		{
			private const float MinimumLengthPixels = 18f;

			private readonly TrackPlot _owner;

			public VesselLayer(TrackPlot owner)
			{
				_owner = owner;
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var recording = _owner.Recording;
				var index = _owner.CursorIndex;
				if (recording == null || index < 0 || index >= recording.Count)
				{
					return;
				}

				var plot = _owner.PlotRect(rect);
				var centre = _owner.ToPixel(plot, _owner.EastOf(index), _owner.NorthOf(index));
				var pixelsPerMetre = plot.width / (float)Math.Max(1e-6, _owner.EastRange.Span);
				var length = InstrumentFormat.IsFinite(_owner.VesselLength) ? (float)_owner.VesselLength * pixelsPerMetre : 0f;
				length = Mathf.Max(MinimumLengthPixels, length);
				var halfLength = length * 0.5f;
				var halfBeam = length * 0.16f;
				var heading = (float)recording.Heading[index];
				var sin = Mathf.Sin(heading);
				var cos = Mathf.Cos(heading);

				// Body x forward, y starboard -> world north/east -> screen (y down).
				Vector2 Point(float forward, float starboard)
				{
					var north = forward * cos - starboard * sin;
					var east = forward * sin + starboard * cos;
					return new Vector2(centre.x + east, centre.y - north);
				}

				painter.fillColor = Palette.Needle;
				painter.BeginPath();
				painter.MoveTo(Point(halfLength, 0f));
				painter.LineTo(Point(halfLength * 0.45f, halfBeam));
				painter.LineTo(Point(-halfLength, halfBeam));
				painter.LineTo(Point(-halfLength, -halfBeam));
				painter.LineTo(Point(halfLength * 0.45f, -halfBeam));
				painter.ClosePath();
				painter.Fill(FillRule.NonZero);
				painter.strokeColor = Palette.Console;
				painter.lineWidth = 1.5f;
				painter.Stroke();
			}
		}
	}
}
