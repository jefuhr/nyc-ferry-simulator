using System;
using System.Diagnostics;
using Unity.Profiling;
using FerrySim.Core.Navigation;
using FerrySim.Core.World.Chart;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// The static chart: a north-up square larger than the viewport, drawn from the tessellation into meshes only when
	/// <see cref="Render"/> is called (a view change), then moved and turned under own ship each frame by its transform
	/// alone. Its labels (soundings, aid light characters, landing names) are pooled children placed at render time and
	/// decluttered deterministically. Depths are below chart datum (MLLW) in the panel's unit.
	/// </summary>
	public sealed class ChartCanvas : VisualElement, IDisposable
	{
		private const int SoundingLabels = 240;
		private const int AidLabels = 64;
		private const int StopLabels = 24;

		private static readonly ProfilerMarker PaintMarker = new ProfilerMarker("FerrySim.Chartplotter.StaticChart");
		private readonly MeshWriter _writer = new MeshWriter();
		private readonly LabelDeclutter _declutter = new LabelDeclutter(SoundingLabels + AidLabels + StopLabels);
		private readonly ChartLabelPool _soundingLabels;
		private readonly ChartLabelPool _aidLabels;
		private readonly ChartLabelPool _stopLabels;
		private readonly Stopwatch _watch = new Stopwatch();
		private ChartTessellation _chart;
		private ChartQueryBuffer _landQuery, _depthQuery, _contourQuery, _hazardQuery, _channelQuery;
		private ChartView _view;
		private ChartPalette _palette = ChartPalette.Day;
		private double _side = 1.0;
		private double _spanNauticalMiles = 1.0;
		private double _safety = double.NaN;
		private double _deep = double.NaN;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private double _minNorth, _minEast, _maxNorth, _maxEast;
		private bool _hasView;

		/// <param name="labelLayer">The unrotated layer the labels live in (over this canvas, under the moving layer).</param>
		public ChartCanvas(ChartLabelLayer labelLayer)
		{
			pickingMode = PickingMode.Ignore;
			usageHints = UsageHints.DynamicTransform;
			style.position = Position.Absolute;
			style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
			_soundingLabels = new ChartLabelPool(labelLayer, SoundingLabels, 11, false);
			_aidLabels = new ChartLabelPool(labelLayer, AidLabels, 10, false);
			_stopLabels = new ChartLabelPool(labelLayer, StopLabels, 12, true);
			generateVisualContent += OnGenerateVisualContent;
		}

		/// <summary>The tessellated chart drawn, or null.</summary>
		public ChartTessellation Chart => _chart;

		/// <summary>The north-up view the meshes were last generated for (canvas-local pixels).</summary>
		public ChartView RenderedView => _view;

		public bool HasRenderedView => _hasView;

		/// <summary>Mesh generations so far: it rises only on a view change.</summary>
		public int RenderCount { get; private set; }

		/// <summary>Render calls so far (label placement and a repaint request).</summary>
		public int RequestCount { get; private set; }

		/// <summary>Main-thread time of the last mesh generation, ms.</summary>
		public double LastRenderMilliseconds { get; private set; }

		public double LastPlacementMilliseconds { get; private set; }

		public int LastVertices { get; private set; }

		public int LastChunks { get; private set; }

		public ChartLabelPool SoundingLabelPool => _soundingLabels;

		public ChartLabelPool AidLabelPool => _aidLabels;

		public ChartLabelPool StopLabelPool => _stopLabels;

		public int SoundingsShown => _soundingLabels.Count;

		public int AidLabelsShown => _aidLabels.Count;

		public int StopLabelsShown => _stopLabels.Count;

		public int LabelsRefused => _declutter.Refused;

		/// <summary>The chart's safety contour depth as drawn, m.</summary>
		public double SafetyContourDrawn => _safety;

		public double DeepContourDrawn => _deep;

		/// <summary>Takes a tessellation (built once by the caller) and makes the query buffers for it.</summary>
		public void SetChart(ChartTessellation chart)
		{
			_chart = chart;
			_hasView = false;
			if (chart == null)
			{
				_landQuery = _depthQuery = _contourQuery = _hazardQuery = _channelQuery = null;
				return;
			}

			_landQuery = chart.Chart[ChartLayer.Land].CreateQueryBuffer(chart.Chart[ChartLayer.Land].Features.Count);
			_depthQuery = chart.Chart[ChartLayer.DepthAreas].CreateQueryBuffer(chart.Chart[ChartLayer.DepthAreas].Features.Count);
			_contourQuery = chart.Chart[ChartLayer.Contours].CreateQueryBuffer(chart.Chart[ChartLayer.Contours].Features.Count);
			_hazardQuery = chart.Chart[ChartLayer.Hazards].CreateQueryBuffer(chart.Chart[ChartLayer.Hazards].Features.Count);
			_channelQuery = chart.Chart[ChartLayer.Channels].CreateQueryBuffer(chart.Chart[ChartLayer.Channels].Features.Count);
		}

		/// <summary>Asks the labels to follow the chart's rotation (degrees, clockwise on screen), applied a few per frame by <see cref="StepLabelRotation"/>.</summary>
		public void SetLabelRotation(float degrees)
		{
			_soundingLabels.SetRotation(degrees);
			_aidLabels.SetRotation(degrees);
			_stopLabels.SetRotation(degrees);
		}

		/// <summary>Positions every label for the chart's rotation at once (at a render, a view change anyway).</summary>
		public void LayoutLabels(float degrees, float viewportCentreX, float viewportCentreY)
		{
			var half = (float)(_side * 0.5);
			_soundingLabels.Layout(degrees, half, viewportCentreX, viewportCentreY);
			_aidLabels.Layout(degrees, half, viewportCentreX, viewportCentreY);
			_stopLabels.Layout(degrees, half, viewportCentreX, viewportCentreY);
		}

		/// <summary>Applies a pending label rotation to at most <paramref name="perPool"/> labels of each pool: bounded work per frame.</summary>
		public void StepLabelRotation(int perPool)
		{
			_stopLabels.StepRotation(perPool);
			_aidLabels.StepRotation(perPool);
			_soundingLabels.StepRotation(perPool);
		}

		/// <summary>True while any label still waits for its rotation.</summary>
		public bool LabelRotationPending => _stopLabels.RotationPending || _aidLabels.RotationPending || _soundingLabels.RotationPending;

		/// <summary>
		/// Generates the chart for a north-up view centred on a world point, on a square canvas of <paramref name="side"/>
		/// pixels: places the labels now and regenerates the meshes at the next repaint.
		/// </summary>
		public void Render(double centreNorth, double centreEast, double metresPerPixel, double side, double spanNauticalMiles,
			ChartPalette palette, double requestedSafetyMetres, DistanceUnit unit)
		{
			_watch.Restart();
			_palette = palette ?? ChartPalette.Day;
			_side = side;
			_spanNauticalMiles = spanNauticalMiles;
			_unit = unit;
			_view = new ChartView(centreNorth, centreEast, metresPerPixel, 0.0, side * 0.5, side * 0.5);
			_hasView = true;
			style.width = (float)side;
			style.height = (float)side;
			style.backgroundColor = _palette.NoData;
			_view.WorldBounds(0, 0, side, side, out _minNorth, out _minEast, out _maxNorth, out _maxEast);
			if (_chart != null)
			{
				_safety = SafetyContour.Select(_chart.ChartedContours, requestedSafetyMetres);
				_deep = SafetyContour.DeepContour(_chart.ChartedContours, _safety);
				_chart.Chart[ChartLayer.Land].QueryBox(_minNorth, _minEast, _maxNorth, _maxEast, _landQuery);
				_chart.Chart[ChartLayer.DepthAreas].QueryBox(_minNorth, _minEast, _maxNorth, _maxEast, _depthQuery);
				_chart.Chart[ChartLayer.Contours].QueryBox(_minNorth, _minEast, _maxNorth, _maxEast, _contourQuery);
				_chart.Chart[ChartLayer.Hazards].QueryBox(_minNorth, _minEast, _maxNorth, _maxEast, _hazardQuery);
				_chart.Chart[ChartLayer.Channels].QueryBox(_minNorth, _minEast, _maxNorth, _maxEast, _channelQuery);
			}

			PlaceLabels();
			RequestCount++;
			MarkDirtyRepaint();
			_watch.Stop();
			LastPlacementMilliseconds = _watch.Elapsed.TotalMilliseconds;
		}

		private bool ShowSoundings => _spanNauticalMiles <= 2.0;

		private bool ShowHazards => _spanNauticalMiles <= 4.0;

		private bool ShowAllContours => _spanNauticalMiles <= 4.0;

		private bool ShowAidText => _spanNauticalMiles <= 1.0;

		private float SymbolSize => _spanNauticalMiles <= 1.0 ? 6f : _spanNauticalMiles <= 4.0 ? 5f : 3.5f;

		private void PlaceLabels()
		{
			_soundingLabels.Begin();
			_aidLabels.Begin();
			_stopLabels.Begin();
			if (_chart == null)
			{
				_soundingLabels.End();
				_aidLabels.End();
				_stopLabels.End();
				return;
			}

			// Spacing grows with the span so wide views keep fewer, better separated numbers.
			var spacing = Mathf.Clamp((float)(4.0 + 10.0 * _spanNauticalMiles), 4f, 40f);
			_declutter.Begin(0, 0, _side, _side, spacing, 152, 152);
			var stops = _chart.Stops;
			for (var i = 0; i < stops.Count; i++)
			{
				if (!InBounds(stops.North[i], stops.East[i]))
				{
					continue;
				}

				_view.ToScreen(stops.North[i], stops.East[i], out var x, out var y);
				var width = EstimateWidth(stops.Name[i], 7.0f) + 6f;
				var lx = (float)x + 8f + width * 0.5f;
				var ly = (float)y - 9f;
				// Square boxes of the label's diagonal: the labels stay upright while the chart turns, so the declutter must hold at any rotation.
				var box = Diagonal(width, 15f);
				// The alternate anchor mirrors the label to the lower left, for when own ship or a target sits on the first.
				if (_declutter.TryPlace(lx, ly, box, box) && !_stopLabels.Place(stops.Name[i], lx, ly, _palette.Landing, (float)x - 8f - width * 0.5f, (float)y + 11f))
				{
					break;
				}
			}

			if (ShowAidText)
			{
				var aids = _chart.Aids;
				for (var i = 0; i < aids.Count; i++)
				{
					if (!aids.Lit[i] || aids.LightText[i].Length == 0 || !InBounds(aids.North[i], aids.East[i]))
					{
						continue;
					}

					_view.ToScreen(aids.North[i], aids.East[i], out var x, out var y);
					var width = EstimateWidth(aids.LightText[i], 6.0f) + 4f;
					var lx = (float)x + 7f + width * 0.5f;
					var ly = (float)y + 9f;
					var box = Diagonal(width, 13f);
					if (_declutter.TryPlace(lx, ly, box, box) && !_aidLabels.Place(aids.LightText[i], lx, ly, _palette.Sounding))
					{
						break;
					}
				}
			}

			if (ShowSoundings)
			{
				var soundings = _chart.Soundings;
				var texts = _unit == DistanceUnit.Feet ? soundings.TextFeet : soundings.TextMetres;
				var color = _palette.Sounding;
				for (var k = 0; k < soundings.ByDepth.Length; k++)
				{
					var i = soundings.ByDepth[k];
					if (!InBounds(soundings.North[i], soundings.East[i]))
					{
						continue;
					}

					_view.ToScreen(soundings.North[i], soundings.East[i], out var x, out var y);
					var box = Diagonal(EstimateWidth(texts[i], 6.5f) + 3f, 13f);
					if (_declutter.TryPlace((float)x, (float)y, box, box) && !_soundingLabels.Place(texts[i], (float)x, (float)y, color))
					{
						break;
					}
				}
			}

			_soundingLabels.End();
			_aidLabels.End();
			_stopLabels.End();
		}

		private static float EstimateWidth(string text, float perCharacter) => text.Length * perCharacter;

		private static float Diagonal(float width, float height) => Mathf.Min(151f, Mathf.Sqrt(width * width + height * height));

		private bool InBounds(double north, double east) => north >= _minNorth && north <= _maxNorth && east >= _minEast && east <= _maxEast;

		private Color AidColor(AidKind kind)
		{
			switch (kind)
			{
				case AidKind.PortBuoy:
				case AidKind.PortBeacon:
				case AidKind.PreferredChannelToPort:
					return _palette.AidPort;
				case AidKind.StarboardBuoy:
				case AidKind.StarboardBeacon:
				case AidKind.PreferredChannelToStarboard:
					return _palette.AidStarboard;
				case AidKind.SpecialBuoy:
					return _palette.AidSpecial;
				case AidKind.Light:
					return _palette.LightFlare;
				default:
					return _palette.AidOther;
			}
		}

		private void OnGenerateVisualContent(MeshGenerationContext context)
		{
			if (!_hasView || _chart == null)
			{
				return;
			}

			_watch.Restart();
			PaintMarker.Begin();
			_writer.Begin(context);
			DrawDepthAreas();
			DrawPolygons(_chart.Land, _landQuery, _palette.Land);
			DrawPolygons(_chart.Fairways, _channelQuery, Alpha(_palette.Channel, 0.07f));
			if (ShowAllContours)
			{
				DrawLines(_chart.Contours, _contourQuery, _palette.Contour, 1f, false, true);
			}

			DrawLines(_chart.Contours, _contourQuery, _palette.SafetyContour, 2f, true, false);
			DrawLines(_chart.Coastlines, _landQuery, _palette.Coastline, 1.5f);
			DrawDashedLines(_chart.FairwayOutlines, _channelQuery, _palette.Channel, 1f, 6f, 4f);
			DrawDashedLines(_chart.ChannelLines, _channelQuery, _palette.Channel, 1.5f, 10f, 5f);
			if (ShowHazards && _spanNauticalMiles <= 2.0)
			{
				DrawLines(_chart.HazardOutlines, _hazardQuery, _palette.Hazard, 1f);
			}

			DrawAllLines(_chart.Bridges, _palette.Bridge, 2f);
			DrawAllLines(_chart.Barges, _palette.Barge, 1.5f);
			if (ShowHazards)
			{
				DrawHazards();
			}

			DrawStops();
			DrawAids();
			_writer.End();
			PaintMarker.End();
			_watch.Stop();
			RenderCount++;
			LastRenderMilliseconds = _watch.Elapsed.TotalMilliseconds;
			LastVertices = _writer.TotalVertices;
			LastChunks = _writer.Chunks;
		}

		private static Color32 Alpha(Color color, float alpha) => new Color(color.r, color.g, color.b, alpha);

		private void DrawDepthAreas()
		{
			var set = _chart.DepthAreas;
			Color32 drying = _palette.Drying, unsafeBand = _palette.Unsafe, caution = _palette.Caution, deep = _palette.Deep;
			for (var q = 0; q < _depthQuery.Count; q++)
			{
				var feature = _depthQuery[q];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				for (var item = start; item < end; item++)
				{
					if (!Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						continue;
					}

					var band = SafetyContour.Classify(set.Value[item], _safety, _deep);
					var color = band == DepthBand.Deep ? deep : band == DepthBand.Caution ? caution : band == DepthBand.Unsafe ? unsafeBand : drying;
					DrawItem(set, item, color);
				}
			}
		}

		private void DrawPolygons(ChartTessellation.PolygonSet set, ChartQueryBuffer query, Color32 color)
		{
			for (var q = 0; q < query.Count; q++)
			{
				var feature = query[q];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				for (var item = start; item < end; item++)
				{
					if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						DrawItem(set, item, color);
					}
				}
			}
		}

		private void DrawItem(ChartTessellation.PolygonSet set, int item, Color32 color)
		{
			var start = set.TriangleStart[item];
			var end = start + set.TriangleCount[item];
			for (var t = start; t < end; t += 3)
			{
				var a = set.Triangles[t];
				var b = set.Triangles[t + 1];
				var c = set.Triangles[t + 2];
				_view.ToScreen(set.North[a], set.East[a], out var ax, out var ay);
				_view.ToScreen(set.North[b], set.East[b], out var bx, out var by);
				_view.ToScreen(set.North[c], set.East[c], out var cx, out var cy);
				_writer.Triangle((float)ax, (float)ay, (float)bx, (float)by, (float)cx, (float)cy, color);
			}
		}

		private void DrawLines(ChartTessellation.LineSet set, ChartQueryBuffer query, Color32 color, float width, bool onlySafety = false, bool skipSafety = false)
		{
			for (var q = 0; q < query.Count; q++)
			{
				var feature = query[q];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				for (var item = start; item < end; item++)
				{
					var isSafety = !double.IsNaN(_safety) && set.Value[item] == _safety;
					if ((onlySafety && !isSafety) || (skipSafety && isSafety))
					{
						continue;
					}

					if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						DrawPolyline(set, item, color, width);
					}
				}
			}
		}

		private void DrawAllLines(ChartTessellation.LineSet set, Color32 color, float width)
		{
			for (var item = 0; item < set.Count; item++)
			{
				if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
				{
					DrawPolyline(set, item, color, width);
				}
			}
		}

		private void DrawDashedLines(ChartTessellation.LineSet set, ChartQueryBuffer query, Color32 color, float width, float dash, float gap)
		{
			for (var q = 0; q < query.Count; q++)
			{
				var feature = query[q];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				for (var item = start; item < end; item++)
				{
					if (!Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						continue;
					}

					var first = set.Start[item];
					var count = set.Length[item];
					_view.ToScreen(set.North[first], set.East[first], out var px, out var py);
					for (var i = 1; i < count; i++)
					{
						_view.ToScreen(set.North[first + i], set.East[first + i], out var x, out var y);
						var dx = x - px;
						var dy = y - py;
						if (i < count - 1 && dx * dx + dy * dy < MinimumSegmentPixels * MinimumSegmentPixels)
						{
							continue;
						}

						_writer.DashedLine((float)px, (float)py, (float)x, (float)y, width, dash, gap, color);
						px = x;
						py = y;
					}
				}
			}
		}

		/// <summary>Vertices closer than this to the last drawn one are skipped: sub-pixel detail costs quads and shows nothing.</summary>
		private const double MinimumSegmentPixels = 0.75;

		private void DrawPolyline(ChartTessellation.LineSet set, int item, Color32 color, float width)
		{
			var first = set.Start[item];
			var count = set.Length[item];
			_view.ToScreen(set.North[first], set.East[first], out var px, out var py);
			for (var i = 1; i < count; i++)
			{
				_view.ToScreen(set.North[first + i], set.East[first + i], out var x, out var y);
				var dx = x - px;
				var dy = y - py;
				if (i < count - 1 && dx * dx + dy * dy < MinimumSegmentPixels * MinimumSegmentPixels)
				{
					continue;
				}

				_writer.Line((float)px, (float)py, (float)x, (float)y, width, color);
				px = x;
				py = y;
			}
		}

		private void DrawHazards()
		{
			var set = _chart.Hazards;
			Color32 color = _palette.Hazard;
			var s = SymbolSize * 0.8f;
			for (var i = 0; i < set.Count; i++)
			{
				if (!InBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_view.ToScreen(set.North[i], set.East[i], out var xd, out var yd);
				var x = (float)xd;
				var y = (float)yd;
				switch (set.Kind[i])
				{
					case HazardKind.Rock:
						// A cross: the chart's rock mark.
						_writer.Line(x - s, y, x + s, y, 1.2f, color);
						_writer.Line(x, y - s, x, y + s, 1.2f, color);
						break;
					case HazardKind.Wreck:
						// A diagonal cross on a baseline: a wreck.
						_writer.Line(x - s, y - s, x + s, y + s, 1.2f, color);
						_writer.Line(x - s, y + s, x + s, y - s, 1.2f, color);
						_writer.Line(x - s * 1.3f, y + s * 1.3f, x + s * 1.3f, y + s * 1.3f, 1.2f, color);
						break;
					default:
						// A diamond outline: an obstruction.
						_writer.Line(x, y - s, x + s, y, 1.2f, color);
						_writer.Line(x + s, y, x, y + s, 1.2f, color);
						_writer.Line(x, y + s, x - s, y, 1.2f, color);
						_writer.Line(x - s, y, x, y - s, 1.2f, color);
						break;
				}
			}
		}

		private void DrawStops()
		{
			var set = _chart.Stops;
			Color32 color = _palette.Landing;
			for (var i = 0; i < set.Count; i++)
			{
				if (!InBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_view.ToScreen(set.North[i], set.East[i], out var x, out var y);
				_writer.Disc((float)x, (float)y, 3f, color, 10);
				_writer.CircleOutline((float)x, (float)y, 5.5f, 1f, color, 12);
			}
		}

		private void DrawAids()
		{
			var set = _chart.Aids;
			var s = SymbolSize;
			Color32 flare = _palette.LightFlare;
			for (var i = 0; i < set.Count; i++)
			{
				if (!InBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_view.ToScreen(set.North[i], set.East[i], out var xd, out var yd);
				var x = (float)xd;
				var y = (float)yd;
				Color32 color = AidColor(set.Kind[i]);
				switch (set.Kind[i])
				{
					case AidKind.PortBuoy:
						_writer.Rect(x - s, y - s, 2f * s, 2f * s, color);
						break;
					case AidKind.StarboardBuoy:
						_writer.Triangle(x, y - s * 1.2f, x + s, y + s * 0.8f, x - s, y + s * 0.8f, color);
						break;
					case AidKind.PreferredChannelToStarboard:
						_writer.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, _palette.AidStarboard);
						_writer.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, _palette.AidPort);
						break;
					case AidKind.PreferredChannelToPort:
						_writer.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, _palette.AidPort);
						_writer.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, _palette.AidStarboard);
						break;
					case AidKind.SpecialBuoy:
						_writer.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, color);
						_writer.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, color);
						break;
					case AidKind.PortBeacon:
					case AidKind.StarboardBeacon:
					case AidKind.OtherBeacon:
						// A beacon: a triangle outline standing on a base line.
						_writer.Line(x - s, y + s, x + s, y + s, 1.5f, color);
						_writer.Line(x - s * 0.8f, y + s, x, y - s, 1.2f, color);
						_writer.Line(x + s * 0.8f, y + s, x, y - s, 1.2f, color);
						break;
					case AidKind.Light:
						_writer.Disc(x, y, s * 0.6f, color, 10);
						break;
					default:
						_writer.Disc(x, y, s * 0.6f, color, 10);
						_writer.CircleOutline(x, y, s, 1f, color, 12);
						break;
				}

				if (set.Lit[i])
				{
					// A light flare: three short rays above the mark.
					var r0 = s * 1.3f;
					var r1 = s * 2.2f;
					_writer.Line(x, y - r0, x, y - r1, 1f, flare);
					_writer.Line(x - r0 * 0.7f, y - r0 * 0.7f, x - r1 * 0.7f, y - r1 * 0.7f, 1f, flare);
					_writer.Line(x + r0 * 0.7f, y - r0 * 0.7f, x + r1 * 0.7f, y - r1 * 0.7f, 1f, flare);
				}
			}
		}

		/// <summary>In the rendered box, and not smaller than a pixel in both directions (a smaller item draws nothing worth its triangles).</summary>
		private bool Overlaps(double minNorth, double minEast, double maxNorth, double maxEast) =>
			maxNorth >= _minNorth && minNorth <= _maxNorth && maxEast >= _minEast && minEast <= _maxEast &&
			(maxNorth - minNorth >= _view.MetresPerPixel || maxEast - minEast >= _view.MetresPerPixel);

		public void Dispose() => _writer.Dispose();
	}
}
