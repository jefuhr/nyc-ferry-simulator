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
	/// The static chart: a north-up square larger than the viewport, generated from the tessellation into staged meshes
	/// only when <see cref="Render"/> is called (a view change), then moved and turned under own ship each frame by its
	/// transform alone. A generation is a resumable pass over the drawing (<see cref="Advance"/>): a view change runs it
	/// to the end at once, while a drift regeneration (own ship creeping through the rendered margin, the old chart still
	/// right) is spread over frames within a budget, so a route never pays the whole chart in one frame. The committed
	/// meshes are uploaded at each repaint from kept arrays (<see cref="MeshWriter"/>, staged). Its labels (soundings,
	/// aid light characters, landing names) are pooled children placed at the commit and decluttered deterministically.
	/// Depths are below chart datum (MLLW) in the panel's unit.
	/// </summary>
	public sealed class ChartCanvas : VisualElement, IDisposable
	{
		private const int SoundingLabels = 240;
		private const int AidLabels = 64;
		private const int StopLabels = 24;

		private enum Phase
		{
			Idle,
			Queries,
			DepthAreas,
			Land,
			Fairways,
			Contours,
			SafetyContours,
			Coastlines,
			FairwayOutlines,
			ChannelLines,
			HazardOutlines,
			Bridges,
			Barges,
			Hazards,
			Stops,
			Aids,
			Commit,
		}

		private static readonly ProfilerMarker PaintMarker = new ProfilerMarker("FerrySim.Chartplotter.StaticChart");
		private static readonly ProfilerMarker GenerateMarker = new ProfilerMarker("FerrySim.Chartplotter.StaticChart.Generate");
		private readonly LabelDeclutter _declutter = new LabelDeclutter(SoundingLabels + AidLabels + StopLabels);
		private readonly ChartLabelPool _soundingLabels;
		private readonly ChartLabelPool _aidLabels;
		private readonly ChartLabelPool _stopLabels;
		private readonly Stopwatch _watch = new Stopwatch();
		private MeshWriter _front = new MeshWriter();
		private MeshWriter _back = new MeshWriter();
		private ChartTessellation _chart;
		private ChartQueryBuffer _landQuery, _depthQuery, _contourQuery, _hazardQuery, _channelQuery;

		// The rendered state: what the committed meshes and the placed labels show.
		private ChartView _view;
		private ChartPalette _palette = ChartPalette.Day;
		private double _side = 1.0;
		private double _spanNauticalMiles = 1.0;
		private double _safety = double.NaN;
		private double _deep = double.NaN;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private double _minNorth, _minEast, _maxNorth, _maxEast;
		private bool _hasView;
		private int _committed;
		private int _uploaded;

		// The generation under way (or the last one): the pending view and its parameters, the phase and its cursors.
		private ChartView _genView;
		private ChartPalette _genPalette = ChartPalette.Day;
		private double _genSide = 1.0;
		private double _genSpan = 1.0;
		private double _genSafety = double.NaN;
		private double _genDeep = double.NaN;
		private double _genRequestedSafety = double.NaN;
		private DistanceUnit _genUnit = DistanceUnit.Feet;
		private double _genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast;
		private Phase _phase = Phase.Idle;
		private int _cursorQuery, _cursorItem;
		private double _generationMs;
		private int _generationFrames;
		private readonly int[] _phaseVertices = new int[(int)Phase.Commit + 1];
		private int _phaseVertexMark;

		// Sounding placement, two passes: the decluttered soundings of the new view (index and canvas point), then the slots,
		// a sounding keeping the label it had so a drift regeneration changes few texts.
		private readonly int[] _placedSounding = new int[SoundingLabels];
		private readonly float[] _placedX = new float[SoundingLabels];
		private readonly float[] _placedY = new float[SoundingLabels];
		private int[] _slotOfSounding = Array.Empty<int>();

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

		/// <summary>The north-up view the committed meshes were generated for (canvas-local pixels).</summary>
		public ChartView RenderedView => _view;

		public bool HasRenderedView => _hasView;

		/// <summary>True while a generation is under way (a drift regeneration spread over frames).</summary>
		public bool Generating => _phase != Phase.Idle;

		/// <summary>Mesh generations uploaded so far: it rises only on a view change (once per committed generation).</summary>
		public int RenderCount { get; private set; }

		/// <summary>Render calls so far (a generation requested, a repaint to follow at its commit).</summary>
		public int RequestCount { get; private set; }

		/// <summary>Main-thread time of the last generation's drawing, ms, summed over the frames it took.</summary>
		public double LastRenderMilliseconds { get; private set; }

		/// <summary>The same figure under its own name.</summary>
		public double LastGenerationMilliseconds => LastRenderMilliseconds;

		/// <summary>Frames the last generation took (1 for a view change).</summary>
		public int LastGenerationFrames { get; private set; }

		/// <summary>Time of the last upload of the committed meshes at a repaint, ms.</summary>
		public double LastUploadMilliseconds { get; private set; }

		/// <summary>Time of the last commit's label placement (queries not included), ms.</summary>
		public double LastPlacementMilliseconds { get; private set; }

		/// <summary>Of the last generation: the box queries, then the landing, aid and sounding label placement, then hiding the rest, ms.</summary>
		public double LastQueryMilliseconds { get; private set; }

		public double LastStopLabelMilliseconds { get; private set; }

		public double LastAidLabelMilliseconds { get; private set; }

		public double LastSoundingLabelMilliseconds { get; private set; }

		public double LastLabelEndMilliseconds { get; private set; }

		public int LastVertices { get; private set; }

		public int LastChunks { get; private set; }

		/// <summary>Vertices the last generation wrote in each drawing pass, in phase order (depth areas, land, fairways, contours, safety contours, coastlines, fairway outlines, channel lines, hazard outlines, bridges, barges, hazards, landings, aids).</summary>
		public string LastPhaseVertices
		{
			get
			{
				var text = new System.Text.StringBuilder();
				for (var phase = (int)Phase.DepthAreas; phase <= (int)Phase.Aids; phase++)
				{
					if (text.Length > 0) text.Append(' ');
					text.Append((Phase)phase).Append('=').Append(_phaseVertices[phase]);
				}

				return text.ToString();
			}
		}

		/// <summary>Managed bytes held by both staging writers' chunk arrays.</summary>
		public long StagedBytes => _front.StagedBytes + _back.StagedBytes;

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
			_phase = Phase.Idle;
			if (chart == null)
			{
				_landQuery = _depthQuery = _contourQuery = _hazardQuery = _channelQuery = null;
				return;
			}

			// Once per chart: which label slot each sounding last had (-1 none).
			_slotOfSounding = new int[chart.Soundings.Count];
			for (var i = 0; i < _slotOfSounding.Length; i++) _slotOfSounding[i] = -1;
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
		/// Requests the chart for a north-up view centred on a world point, on a square canvas of <paramref name="side"/>
		/// pixels. Immediate (a view change, the default), the generation runs to its commit here: the labels are placed
		/// now and the meshes uploaded at the next repaint. Otherwise it only begins, and <see cref="Advance"/> carries it
		/// on within a budget each frame while the committed chart stays on screen.
		/// </summary>
		public void Render(double centreNorth, double centreEast, double metresPerPixel, double side, double spanNauticalMiles,
			ChartPalette palette, double requestedSafetyMetres, DistanceUnit unit, bool immediate = true)
		{
			_genPalette = palette ?? ChartPalette.Day;
			_genSide = side;
			_genSpan = spanNauticalMiles;
			_genUnit = unit;
			_genRequestedSafety = requestedSafetyMetres;
			_genView = new ChartView(centreNorth, centreEast, metresPerPixel, 0.0, side * 0.5, side * 0.5);
			_genView.WorldBounds(0, 0, side, side, out _genMinNorth, out _genMinEast, out _genMaxNorth, out _genMaxEast);
			_phase = Phase.Queries;
			_cursorQuery = 0;
			_cursorItem = 0;
			_generationMs = 0.0;
			_generationFrames = 0;
			_phaseVertexMark = 0;
			Array.Clear(_phaseVertices, 0, _phaseVertices.Length);
			RequestCount++;
			if (immediate)
			{
				Advance(double.PositiveInfinity);
			}
		}

		/// <summary>
		/// Carries the generation on for at most about <paramref name="budgetMilliseconds"/> (checked between items, so a
		/// large polygon can run over), committing when the drawing is complete. True when nothing is left to do.
		/// </summary>
		public bool Advance(double budgetMilliseconds)
		{
			if (_phase == Phase.Idle)
			{
				return true;
			}

			_watch.Restart();
			GenerateMarker.Begin();
			_generationFrames++;
			_sliceItems = 0;
			var done = false;
			while (!done)
			{
				if (_chart == null && _phase != Phase.Commit)
				{
					// No chart: nothing to draw, an empty commit.
					_back.BeginStaged();
					_phase = Phase.Commit;
				}

				switch (_phase)
				{
					case Phase.Queries:
						done = StepQueries();
						break;
					case Phase.DepthAreas:
						done = StepDepthAreas(budgetMilliseconds);
						break;
					case Phase.Land:
						done = StepPolygons(_chart.Land, _landQuery, _genPalette.Land, budgetMilliseconds);
						break;
					case Phase.Fairways:
						done = StepPolygons(_chart.Fairways, _channelQuery, Alpha(_genPalette.Channel, 0.07f), budgetMilliseconds);
						break;
					case Phase.Contours:
						done = !ShowAllContours(_genSpan) || StepLines(_chart.Contours, _contourQuery, _genPalette.Contour, 1f, false, true, budgetMilliseconds);
						break;
					case Phase.SafetyContours:
						done = StepLines(_chart.Contours, _contourQuery, _genPalette.SafetyContour, 2f, true, false, budgetMilliseconds);
						break;
					case Phase.Coastlines:
						done = StepLines(_chart.Coastlines, _landQuery, _genPalette.Coastline, 1.5f, false, false, budgetMilliseconds);
						break;
					case Phase.FairwayOutlines:
						done = StepDashedLines(_chart.FairwayOutlines, _channelQuery, _genPalette.Channel, 1f, 6f, 4f, budgetMilliseconds);
						break;
					case Phase.ChannelLines:
						done = StepDashedLines(_chart.ChannelLines, _channelQuery, _genPalette.Channel, 1.5f, 10f, 5f, budgetMilliseconds);
						break;
					case Phase.HazardOutlines:
						done = !(ShowHazards(_genSpan) && _genSpan <= 2.0) || StepLines(_chart.HazardOutlines, _hazardQuery, _genPalette.Hazard, 1f, false, false, budgetMilliseconds);
						break;
					case Phase.Bridges:
						done = StepAllLines(_chart.Bridges, _genPalette.Bridge, 2f, budgetMilliseconds);
						break;
					case Phase.Barges:
						done = StepAllLines(_chart.Barges, _genPalette.Barge, 1.5f, budgetMilliseconds);
						break;
					case Phase.Hazards:
						done = !ShowHazards(_genSpan) || StepHazards(budgetMilliseconds);
						break;
					case Phase.Stops:
						done = StepStops(budgetMilliseconds);
						break;
					case Phase.Aids:
						done = StepAids(budgetMilliseconds);
						break;
					case Phase.Commit:
						Commit();
						_phase = Phase.Idle;
						break;
					default:
						break;
				}

				if (_phase == Phase.Idle)
				{
					break;
				}

				if (done)
				{
					// This phase is complete: the next starts from its first item.
					_phaseVertices[(int)_phase] = _back.TotalVertices - _phaseVertexMark;
					_phaseVertexMark = _back.TotalVertices;
					_phase++;
					_cursorQuery = 0;
					_cursorItem = 0;
					done = false;
					continue;
				}

				// Out of budget inside a phase: resume here next frame.
				break;
			}

			GenerateMarker.End();
			_watch.Stop();
			_generationMs += _watch.Elapsed.TotalMilliseconds;
			return _phase == Phase.Idle;
		}

		/// <summary>
		/// Diagnostic seam: at most this many drawn items per budgeted slice (0, the default: the time budget alone). Tests set
		/// it so a drift generation is certain to stay pending across frames whatever the chart's size or the host's speed;
		/// the immediate path (an unbounded budget) ignores it.
		/// </summary>
		public int DiagnosticItemsPerSlice { get; set; }

		private int _sliceItems;

		private bool OverBudget(double budget)
		{
			if (DiagnosticItemsPerSlice > 0 && !double.IsPositiveInfinity(budget) && ++_sliceItems >= DiagnosticItemsPerSlice)
			{
				return true;
			}

			return _watch.Elapsed.TotalMilliseconds >= budget;
		}

		/// <summary>The box queries and the safety contour, the first slice of a generation; the writer starts staging here.</summary>
		private bool StepQueries()
		{
			_back.BeginStaged();
			_genSafety = SafetyContour.Select(_chart.ChartedContours, _genRequestedSafety);
			_genDeep = SafetyContour.DeepContour(_chart.ChartedContours, _genSafety);
			_chart.Chart[ChartLayer.Land].QueryBox(_genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast, _landQuery);
			_chart.Chart[ChartLayer.DepthAreas].QueryBox(_genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast, _depthQuery);
			_chart.Chart[ChartLayer.Contours].QueryBox(_genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast, _contourQuery);
			_chart.Chart[ChartLayer.Hazards].QueryBox(_genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast, _hazardQuery);
			_chart.Chart[ChartLayer.Channels].QueryBox(_genMinNorth, _genMinEast, _genMaxNorth, _genMaxEast, _channelQuery);
			LastQueryMilliseconds = _watch.Elapsed.TotalMilliseconds;
			return true;
		}

		/// <summary>The drawing is complete: the staged meshes become the chart, the labels are placed for the new view and a repaint is requested.</summary>
		private void Commit()
		{
			_back.EndStaged();
			var finished = _back;
			_back = _front;
			_front = finished;
			_view = _genView;
			_palette = _genPalette;
			_side = _genSide;
			_spanNauticalMiles = _genSpan;
			_unit = _genUnit;
			_safety = _genSafety;
			_deep = _genDeep;
			_minNorth = _genMinNorth;
			_minEast = _genMinEast;
			_maxNorth = _genMaxNorth;
			_maxEast = _genMaxEast;
			_hasView = true;
			_committed++;
			style.width = (float)_side;
			style.height = (float)_side;
			style.backgroundColor = _palette.NoData;
			var mark = _watch.Elapsed.TotalMilliseconds;
			PlaceLabels();
			LastPlacementMilliseconds = _watch.Elapsed.TotalMilliseconds - mark;
			LastRenderMilliseconds = _generationMs + _watch.Elapsed.TotalMilliseconds;
			LastGenerationFrames = _generationFrames;
			LastVertices = _front.TotalVertices;
			LastChunks = _front.StagedChunks;
			MarkDirtyRepaint();
		}

		private static bool ShowSoundings(double span) => span <= 2.0;

		private static bool ShowHazards(double span) => span <= 4.0;

		private static bool ShowAllContours(double span) => span <= 4.0;

		private static bool ShowAidText(double span) => span <= 1.0;

		private static float SymbolSize(double span) => span <= 1.0 ? 6f : span <= 4.0 ? 5f : 3.5f;

		/// <summary>Sounding labels the last commit gave a new text (the rest kept theirs: keyed slots).</summary>
		public int LastSoundingTextChanges { get; private set; }

		private void PlaceLabels()
		{
			_aidLabels.Begin();
			_stopLabels.Begin();
			if (_chart == null)
			{
				_soundingLabels.Begin();
				_soundingLabels.End();
				_aidLabels.End();
				_stopLabels.End();
				return;
			}

			// Spacing grows with the span so wide views keep fewer, better separated numbers.
			var spacing = Mathf.Clamp((float)(4.0 + 10.0 * _spanNauticalMiles), 4f, 40f);
			_declutter.Begin(0, 0, _side, _side, spacing, 152, 152);
			var mark = _watch.Elapsed.TotalMilliseconds;
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

			LastStopLabelMilliseconds = _watch.Elapsed.TotalMilliseconds - mark;
			mark = _watch.Elapsed.TotalMilliseconds;
			if (ShowAidText(_spanNauticalMiles))
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

			LastAidLabelMilliseconds = _watch.Elapsed.TotalMilliseconds - mark;
			mark = _watch.Elapsed.TotalMilliseconds;
			var placed = 0;
			if (ShowSoundings(_spanNauticalMiles))
			{
				var soundings = _chart.Soundings;
				var texts = _unit == DistanceUnit.Feet ? soundings.TextFeet : soundings.TextMetres;
				for (var k = 0; k < soundings.ByDepth.Length && placed < SoundingLabels; k++)
				{
					var i = soundings.ByDepth[k];
					if (!InBounds(soundings.North[i], soundings.East[i]))
					{
						continue;
					}

					_view.ToScreen(soundings.North[i], soundings.East[i], out var x, out var y);
					var box = Diagonal(EstimateWidth(texts[i], 6.5f) + 3f, 13f);
					if (_declutter.TryPlace((float)x, (float)y, box, box))
					{
						_placedSounding[placed] = i;
						_placedX[placed] = (float)x;
						_placedY[placed] = (float)y;
						placed++;
					}
				}
			}

			LastSoundingLabelMilliseconds = _watch.Elapsed.TotalMilliseconds - mark;
			mark = _watch.Elapsed.TotalMilliseconds;
			AssignSoundingSlots(placed);
			_aidLabels.End();
			_stopLabels.End();
			LastLabelEndMilliseconds = _watch.Elapsed.TotalMilliseconds - mark;
		}

		/// <summary>
		/// The placed soundings take their slots: first every sounding that had a slot last time keeps it (its text
		/// unchanged, a feet/metres change aside), then the newcomers take the slots left over, then the rest hide.
		/// A unit change sets every text, so the slots are forgotten first.
		/// </summary>
		private void AssignSoundingSlots(int placed)
		{
			var soundings = _chart.Soundings;
			var texts = _unit == DistanceUnit.Feet ? soundings.TextFeet : soundings.TextMetres;
			var color = _palette.Sounding;
			_soundingLabels.BeginKeyed();
			LastSoundingTextChanges = 0;
			for (var p = 0; p < placed; p++)
			{
				var sounding = _placedSounding[p];
				var slot = _slotOfSounding[sounding];
				if (slot >= 0 && _soundingLabels.TryReuse(slot, sounding, _placedX[p], _placedY[p], color))
				{
					if (!ReferenceEquals(_soundingLabels[slot].text, texts[sounding]))
					{
						// The unit changed since this slot last showed the sounding.
						_soundingLabels[slot].text = texts[sounding];
						LastSoundingTextChanges++;
					}

					_placedSounding[p] = -1;
				}
			}

			for (var p = 0; p < placed; p++)
			{
				var sounding = _placedSounding[p];
				if (sounding < 0)
				{
					continue;
				}

				var slot = _soundingLabels.AssignFree(sounding, texts[sounding], _placedX[p], _placedY[p], color);
				if (slot < 0)
				{
					break;
				}

				_slotOfSounding[sounding] = slot;
				LastSoundingTextChanges++;
			}

			_soundingLabels.EndKeyed();
		}

		private static float EstimateWidth(string text, float perCharacter) => text.Length * perCharacter;

		private static float Diagonal(float width, float height) => Mathf.Min(151f, Mathf.Sqrt(width * width + height * height));

		private bool InBounds(double north, double east) => north >= _minNorth && north <= _maxNorth && east >= _minEast && east <= _maxEast;

		private bool GenInBounds(double north, double east) => north >= _genMinNorth && north <= _genMaxNorth && east >= _genMinEast && east <= _genMaxEast;

		private Color AidColor(AidKind kind)
		{
			switch (kind)
			{
				case AidKind.PortBuoy:
				case AidKind.PortBeacon:
				case AidKind.PreferredChannelToPort:
					return _genPalette.AidPort;
				case AidKind.StarboardBuoy:
				case AidKind.StarboardBeacon:
				case AidKind.PreferredChannelToStarboard:
					return _genPalette.AidStarboard;
				case AidKind.SpecialBuoy:
					return _genPalette.AidSpecial;
				case AidKind.Light:
					return _genPalette.LightFlare;
				default:
					return _genPalette.AidOther;
			}
		}

		/// <summary>A repaint uploads the committed meshes (a new generation once, then the same again whenever UI Toolkit repaints the element).</summary>
		private void OnGenerateVisualContent(MeshGenerationContext context)
		{
			if (!_hasView || _chart == null)
			{
				return;
			}

			var watch = Stopwatch.StartNew();
			PaintMarker.Begin();
			_front.Upload(context);
			PaintMarker.End();
			watch.Stop();
			LastUploadMilliseconds = watch.Elapsed.TotalMilliseconds;
			if (_uploaded != _committed)
			{
				_uploaded = _committed;
				RenderCount++;
			}
		}

		private static Color32 Alpha(Color color, float alpha) => new Color(color.r, color.g, color.b, alpha);

		private bool StepDepthAreas(double budget)
		{
			var set = _chart.DepthAreas;
			Color32 drying = _genPalette.Drying, unsafeBand = _genPalette.Unsafe, caution = _genPalette.Caution, deep = _genPalette.Deep;
			while (_cursorQuery < _depthQuery.Count)
			{
				var feature = _depthQuery[_cursorQuery];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				if (_cursorItem < start) _cursorItem = start;
				for (; _cursorItem < end; _cursorItem++)
				{
					var item = _cursorItem;
					if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						var band = SafetyContour.Classify(set.Value[item], _genSafety, _genDeep);
						var color = band == DepthBand.Deep ? deep : band == DepthBand.Caution ? caution : band == DepthBand.Unsafe ? unsafeBand : drying;
						DrawItem(set, item, color);
						if (OverBudget(budget))
						{
							_cursorItem++;
							return false;
						}
					}
				}

				_cursorQuery++;
				_cursorItem = 0;
			}

			return true;
		}

		private bool StepPolygons(ChartTessellation.PolygonSet set, ChartQueryBuffer query, Color32 color, double budget)
		{
			while (_cursorQuery < query.Count)
			{
				var feature = query[_cursorQuery];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				if (_cursorItem < start) _cursorItem = start;
				for (; _cursorItem < end; _cursorItem++)
				{
					var item = _cursorItem;
					if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						DrawItem(set, item, color);
						if (OverBudget(budget))
						{
							_cursorItem++;
							return false;
						}
					}
				}

				_cursorQuery++;
				_cursorItem = 0;
			}

			return true;
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
				_genView.ToScreen(set.North[a], set.East[a], out var ax, out var ay);
				_genView.ToScreen(set.North[b], set.East[b], out var bx, out var by);
				_genView.ToScreen(set.North[c], set.East[c], out var cx, out var cy);
				// A triangle wholly beyond one edge of the square draws nothing: a harbor-wide depth area overlaps the square but most of it lies outside.
				if ((ax < 0.0 && bx < 0.0 && cx < 0.0) || (ay < 0.0 && by < 0.0 && cy < 0.0) || (ax > _genSide && bx > _genSide && cx > _genSide) || (ay > _genSide && by > _genSide && cy > _genSide))
				{
					continue;
				}

				_back.Triangle((float)ax, (float)ay, (float)bx, (float)by, (float)cx, (float)cy, color);
			}
		}

		private bool StepLines(ChartTessellation.LineSet set, ChartQueryBuffer query, Color32 color, float width, bool onlySafety, bool skipSafety, double budget)
		{
			while (_cursorQuery < query.Count)
			{
				var feature = query[_cursorQuery];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				if (_cursorItem < start) _cursorItem = start;
				for (; _cursorItem < end; _cursorItem++)
				{
					var item = _cursorItem;
					var isSafety = !double.IsNaN(_genSafety) && set.Value[item] == _genSafety;
					if ((onlySafety && !isSafety) || (skipSafety && isSafety))
					{
						continue;
					}

					if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						DrawPolyline(set, item, color, width);
						if (OverBudget(budget))
						{
							_cursorItem++;
							return false;
						}
					}
				}

				_cursorQuery++;
				_cursorItem = 0;
			}

			return true;
		}

		private bool StepAllLines(ChartTessellation.LineSet set, Color32 color, float width, double budget)
		{
			for (; _cursorItem < set.Count; _cursorItem++)
			{
				var item = _cursorItem;
				if (Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
				{
					DrawPolyline(set, item, color, width);
					if (OverBudget(budget))
					{
						_cursorItem++;
						return false;
					}
				}
			}

			return true;
		}

		private bool StepDashedLines(ChartTessellation.LineSet set, ChartQueryBuffer query, Color32 color, float width, float dash, float gap, double budget)
		{
			while (_cursorQuery < query.Count)
			{
				var feature = query[_cursorQuery];
				var start = set.FeatureItemStart[feature];
				var end = start + set.FeatureItemCount[feature];
				if (_cursorItem < start) _cursorItem = start;
				for (; _cursorItem < end; _cursorItem++)
				{
					var item = _cursorItem;
					if (!Overlaps(set.MinNorth[item], set.MinEast[item], set.MaxNorth[item], set.MaxEast[item]))
					{
						continue;
					}

					var first = set.Start[item];
					var count = set.Length[item];
					_genView.ToScreen(set.North[first], set.East[first], out var px, out var py);
					for (var i = 1; i < count; i++)
					{
						_genView.ToScreen(set.North[first + i], set.East[first + i], out var x, out var y);
						var dx = x - px;
						var dy = y - py;
						if (i < count - 1 && dx * dx + dy * dy < MinimumSegmentPixels * MinimumSegmentPixels)
						{
							continue;
						}

						if (!((px < 0.0 && x < 0.0) || (py < 0.0 && y < 0.0) || (px > _genSide && x > _genSide) || (py > _genSide && y > _genSide)))
						{
							_back.DashedLine((float)px, (float)py, (float)x, (float)y, width, dash, gap, color);
						}

						px = x;
						py = y;
					}

					if (OverBudget(budget))
					{
						_cursorItem++;
						return false;
					}
				}

				_cursorQuery++;
				_cursorItem = 0;
			}

			return true;
		}

		/// <summary>Vertices closer than this to the last drawn one are skipped: sub-pixel detail costs quads and shows nothing.</summary>
		private const double MinimumSegmentPixels = 0.75;

		private void DrawPolyline(ChartTessellation.LineSet set, int item, Color32 color, float width)
		{
			var first = set.Start[item];
			var count = set.Length[item];
			_genView.ToScreen(set.North[first], set.East[first], out var px, out var py);
			for (var i = 1; i < count; i++)
			{
				_genView.ToScreen(set.North[first + i], set.East[first + i], out var x, out var y);
				var dx = x - px;
				var dy = y - py;
				if (i < count - 1 && dx * dx + dy * dy < MinimumSegmentPixels * MinimumSegmentPixels)
				{
					continue;
				}

				// A segment wholly beyond one edge of the square draws nothing (a segment crossing the square has its ends on different sides).
				if (!((px < 0.0 && x < 0.0) || (py < 0.0 && y < 0.0) || (px > _genSide && x > _genSide) || (py > _genSide && y > _genSide)))
				{
					_back.Line((float)px, (float)py, (float)x, (float)y, width, color);
				}

				px = x;
				py = y;
			}
		}

		private bool StepHazards(double budget)
		{
			var set = _chart.Hazards;
			Color32 color = _genPalette.Hazard;
			var s = SymbolSize(_genSpan) * 0.8f;
			for (; _cursorItem < set.Count; _cursorItem++)
			{
				var i = _cursorItem;
				if (!GenInBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_genView.ToScreen(set.North[i], set.East[i], out var xd, out var yd);
				var x = (float)xd;
				var y = (float)yd;
				switch (set.Kind[i])
				{
					case HazardKind.Rock:
						// A cross: the chart's rock mark.
						_back.Line(x - s, y, x + s, y, 1.2f, color);
						_back.Line(x, y - s, x, y + s, 1.2f, color);
						break;
					case HazardKind.Wreck:
						// A diagonal cross on a baseline: a wreck.
						_back.Line(x - s, y - s, x + s, y + s, 1.2f, color);
						_back.Line(x - s, y + s, x + s, y - s, 1.2f, color);
						_back.Line(x - s * 1.3f, y + s * 1.3f, x + s * 1.3f, y + s * 1.3f, 1.2f, color);
						break;
					default:
						// A diamond outline: an obstruction.
						_back.Line(x, y - s, x + s, y, 1.2f, color);
						_back.Line(x + s, y, x, y + s, 1.2f, color);
						_back.Line(x, y + s, x - s, y, 1.2f, color);
						_back.Line(x - s, y, x, y - s, 1.2f, color);
						break;
				}

				if (OverBudget(budget))
				{
					_cursorItem++;
					return false;
				}
			}

			return true;
		}

		private bool StepStops(double budget)
		{
			var set = _chart.Stops;
			Color32 color = _genPalette.Landing;
			for (; _cursorItem < set.Count; _cursorItem++)
			{
				var i = _cursorItem;
				if (!GenInBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_genView.ToScreen(set.North[i], set.East[i], out var x, out var y);
				_back.Disc((float)x, (float)y, 3f, color, 10);
				_back.CircleOutline((float)x, (float)y, 5.5f, 1f, color, 12);
				if (OverBudget(budget))
				{
					_cursorItem++;
					return false;
				}
			}

			return true;
		}

		private bool StepAids(double budget)
		{
			var set = _chart.Aids;
			var s = SymbolSize(_genSpan);
			Color32 flare = _genPalette.LightFlare;
			for (; _cursorItem < set.Count; _cursorItem++)
			{
				var i = _cursorItem;
				if (!GenInBounds(set.North[i], set.East[i]))
				{
					continue;
				}

				_genView.ToScreen(set.North[i], set.East[i], out var xd, out var yd);
				var x = (float)xd;
				var y = (float)yd;
				Color32 color = AidColor(set.Kind[i]);
				switch (set.Kind[i])
				{
					case AidKind.PortBuoy:
						_back.Rect(x - s, y - s, 2f * s, 2f * s, color);
						break;
					case AidKind.StarboardBuoy:
						_back.Triangle(x, y - s * 1.2f, x + s, y + s * 0.8f, x - s, y + s * 0.8f, color);
						break;
					case AidKind.PreferredChannelToStarboard:
						_back.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, _genPalette.AidStarboard);
						_back.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, _genPalette.AidPort);
						break;
					case AidKind.PreferredChannelToPort:
						_back.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, _genPalette.AidPort);
						_back.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, _genPalette.AidStarboard);
						break;
					case AidKind.SpecialBuoy:
						_back.Triangle(x, y - s * 1.2f, x + s, y, x - s, y, color);
						_back.Triangle(x - s, y, x + s, y, x, y + s * 1.2f, color);
						break;
					case AidKind.PortBeacon:
					case AidKind.StarboardBeacon:
					case AidKind.OtherBeacon:
						// A beacon: a triangle outline standing on a base line.
						_back.Line(x - s, y + s, x + s, y + s, 1.5f, color);
						_back.Line(x - s * 0.8f, y + s, x, y - s, 1.2f, color);
						_back.Line(x + s * 0.8f, y + s, x, y - s, 1.2f, color);
						break;
					case AidKind.Light:
						_back.Disc(x, y, s * 0.6f, color, 10);
						break;
					default:
						_back.Disc(x, y, s * 0.6f, color, 10);
						_back.CircleOutline(x, y, s, 1f, color, 12);
						break;
				}

				if (set.Lit[i])
				{
					// A light flare: three short rays above the mark.
					var r0 = s * 1.3f;
					var r1 = s * 2.2f;
					_back.Line(x, y - r0, x, y - r1, 1f, flare);
					_back.Line(x - r0 * 0.7f, y - r0 * 0.7f, x - r1 * 0.7f, y - r1 * 0.7f, 1f, flare);
					_back.Line(x + r0 * 0.7f, y - r0 * 0.7f, x + r1 * 0.7f, y - r1 * 0.7f, 1f, flare);
				}

				if (OverBudget(budget))
				{
					_cursorItem++;
					return false;
				}
			}

			return true;
		}

		/// <summary>In the generation's box, and not smaller than a pixel in both directions (a smaller item draws nothing worth its triangles).</summary>
		private bool Overlaps(double minNorth, double minEast, double maxNorth, double maxEast) =>
			maxNorth >= _genMinNorth && minNorth <= _genMaxNorth && maxEast >= _genMinEast && minEast <= _genMaxEast &&
			(maxNorth - minNorth >= _genView.MetresPerPixel || maxEast - minEast >= _genView.MetresPerPixel);

		public void Dispose()
		{
			_front.Dispose();
			_back.Dispose();
		}
	}
}
