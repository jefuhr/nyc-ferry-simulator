using System;
using System.Diagnostics;
using FerrySim.Core.Contracts;
using FerrySim.Core.Navigation;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	public enum ChartOrientation
	{
		NorthUp = 0,
		CourseUp = 1,
	}

	/// <summary>
	/// The chartplotter: a header (span, orientation, centring; depth unit, datum and safety contour), the chart
	/// viewport with the static canvas under the moving layer, the CPA list beside it, and a footer with the notice and
	/// own ship's heading, course and speed. <see cref="Refresh"/> runs once a frame from the display host: it reads
	/// own ship and the targets, decides whether the static chart needs regenerating (only on a view change), moves the
	/// canvas by its transform, fills the moving layer and the list, and allocates nothing while the settings hold.
	/// </summary>
	public sealed class ChartplotterView : VisualElement
	{
		/// <summary>Spans offered by the zoom keys, nautical miles across the viewport.</summary>
		public static readonly double[] SpanLadder = { 0.25, 0.5, 1.0, 2.0, 4.0, 8.0 };

		public const double MinimumSpan = 0.25;
		public const double MaximumSpan = 8.0;

		/// <summary>Below this speed over ground a course is not meaningful, so course-up follows the heading and the COG vector is hidden (0.5 kn).</summary>
		public const double MinimumSpeedForCourse = 0.5 * Units.MetresPerSecondPerKnot;

		/// <summary>A target under this speed over ground is drawn as stopped (the replay's own slow limit, 0.5 m/s).</summary>
		public const double StoppedSpeed = TrafficReplay.SlowSpeed;

		/// <summary>Visibility under a nautical mile is restricted: the ring is drawn.</summary>
		public const double RestrictedVisibilityMetres = ChartView.MetresPerNauticalMile;

		/// <summary>Track fixes kept, and their spacing: every 10 s or 20 m, about 100 minutes under way.</summary>
		public const int TrackFixes = 600;
		public const double TrackFixMetres = 20.0;
		public const double TrackFixSeconds = 10.0;

		/// <summary>The label counter-rotation is updated once the chart has turned this far, degrees.</summary>
		public const float LabelRotationStep = 2f;

		/// <summary>Labels of each pool turned per frame when the chart has turned: bounded work, never a whole-frame burst.</summary>
		public const int LabelsRotatedPerFrame = 16;

		private readonly VisualElement _header;
		private readonly VisualElement _viewport;
		private readonly VisualElement _footer;
		private readonly Label _modeLabel;
		private readonly Label _depthLabel;
		private readonly Label _noticeLabel;
		private readonly Label _headingLabel;
		private readonly Label _courseLabel;
		private readonly Label _speedLabel;
		private readonly Label _statusLabel;
		private readonly Label _scaleLabel;
		private readonly VisualElement _scaleBar;
		private readonly FixRing _track = new FixRing(TrackFixes, TrackFixMetres, TrackFixSeconds);
		private readonly TargetRanking _ranking = new TargetRanking(CpaList.DefaultRows);
		private readonly Stopwatch _watch = new Stopwatch();
		private ChartTessellation _chart;
		private ChartPaletteKind _paletteKind = ChartPaletteKind.Day;
		private ChartPalette _palette = ChartPalette.Day;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private double _span = 1.0;
		private ChartOrientation _orientation = ChartOrientation.NorthUp;
		private bool _centred = true;
		private double _panNorth, _panEast;
		private double _safety = SafetyContour.DefaultMetres;
		private double _vectorMinutes = 6.0;
		private CpaAlarmLimits _limits = CpaAlarmLimits.Default;
		private double _visibility;
		private int _selected = -1;
		private string _selectedText = string.Empty;
		private int _selectedTextFor = -1;
		private ITargetSource _targetsSeen;
		private long _runId = long.MinValue;
		private long _generation = long.MinValue;
		private bool _staticDirty = true;
		private double _renderedSpan = double.NaN;
		private float _labelRotation;
		private double _upBearing;
		private double _ownNorth, _ownEast, _ownHeading;
		private bool _ownKnown;
		private VesselParameters _outlineFor;
		private float[] _outlineForward = Array.Empty<float>();
		private float[] _outlineStarboard = Array.Empty<float>();
		private int _outlineCount;
		private double _ownLength = 26.0, _ownBeam = 8.0;
		private bool _modeTextDirty = true, _depthTextDirty = true;
		private double _scaleBarMetres = ChartView.MetresPerCable;

		/// <summary>Diagnostics for the cost tests only: hold the canvas unrotated, or skip the label repositioning, to attribute the cost of each.</summary>
		internal bool DiagnosticRotateCanvas = true;

		internal bool DiagnosticRepositionLabels = true;

		public ChartplotterView()
		{
			AddToClassList("fs-view");
			pickingMode = PickingMode.Ignore;
			style.flexDirection = FlexDirection.Column;
			style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
			style.overflow = Overflow.Hidden;

			_header = Bar(22);
			_modeLabel = Caption(string.Empty, 11, true);
			_depthLabel = Caption(string.Empty, 11, false);
			_header.Add(_modeLabel);
			_header.Add(_depthLabel);
			Add(_header);

			var body = new VisualElement { pickingMode = PickingMode.Ignore };
			body.style.flexDirection = FlexDirection.Row;
			body.style.flexGrow = 1;
			_viewport = new VisualElement { pickingMode = PickingMode.Ignore };
			_viewport.style.flexGrow = 1;
			_viewport.style.overflow = Overflow.Hidden;
			LabelLayer = new ChartLabelLayer();
			Canvas = new ChartCanvas(LabelLayer);
			_viewport.Add(Canvas);
			_viewport.Add(LabelLayer);
			Targets = new TargetLayer();
			_viewport.Add(Targets);
			_statusLabel = Caption("NO POSITION", 14, true);
			_statusLabel.style.position = Position.Absolute;
			_statusLabel.style.left = Length.Percent(50);
			_statusLabel.style.top = Length.Percent(50);
			_statusLabel.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
			_statusLabel.style.display = DisplayStyle.None;
			_viewport.Add(_statusLabel);
			_scaleBar = new VisualElement { pickingMode = PickingMode.Ignore };
			_scaleBar.style.position = Position.Absolute;
			_scaleBar.style.left = 10;
			_scaleBar.style.bottom = 8;
			_scaleBar.style.height = 3;
			_scaleBar.style.width = 40;
			_viewport.Add(_scaleBar);
			_scaleLabel = Caption("1 CABLE", 10, false);
			_scaleLabel.style.position = Position.Absolute;
			_scaleLabel.style.left = 10;
			_scaleLabel.style.bottom = 12;
			_viewport.Add(_scaleLabel);
			body.Add(_viewport);
			List = new CpaList();
			body.Add(List);
			Add(body);

			_footer = Bar(20);
			_noticeLabel = Caption("NOT FOR NAVIGATION", 11, true);
			var readouts = new VisualElement { pickingMode = PickingMode.Ignore };
			readouts.style.flexDirection = FlexDirection.Row;
			_headingLabel = Caption("HDG ---", 11, false);
			_courseLabel = Caption("COG ---", 11, false);
			_speedLabel = Caption("SOG --- kn", 11, false);
			_courseLabel.style.marginLeft = 10;
			_speedLabel.style.marginLeft = 10;
			readouts.Add(_headingLabel);
			readouts.Add(_courseLabel);
			readouts.Add(_speedLabel);
			_footer.Add(_noticeLabel);
			_footer.Add(readouts);
			Add(_footer);
			ApplyPalette();
		}

		public ChartCanvas Canvas { get; }

		/// <summary>The unrotated layer the chart's labels live in.</summary>
		public ChartLabelLayer LabelLayer { get; }

		public TargetLayer Targets { get; }

		public CpaList List { get; }

		/// <summary>The viewport the chart is drawn in.</summary>
		public VisualElement Viewport => _viewport;

		/// <summary>The current view: own ship (or the pan point) at the viewport centre, at the span, with the orientation.</summary>
		public ChartView CurrentView { get; private set; }

		public bool HasView { get; private set; }

		public TargetRanking Ranking => _ranking;

		public FixRing Track => _track;

		/// <summary>True while any target is a CPA alarm.</summary>
		public bool Alarm => _ranking.Alarm;

		/// <summary>Static chart generations requested so far (one per view change).</summary>
		public int StaticRenders => Canvas.RequestCount;

		public double LastRefreshMilliseconds { get; private set; }

		public string ModeText => _modeLabel.text;

		public string DepthText => _depthLabel.text;

		public string HeadingText => _headingLabel.text;

		public string CourseText => _courseLabel.text;

		public string SpeedText => _speedLabel.text;

		public bool NoPositionShown => _statusLabel.style.display == DisplayStyle.Flex;

		public ChartTessellation Chart => _chart;

		public ChartPaletteKind Palette
		{
			get => _paletteKind;
			set
			{
				if (_paletteKind == value) return;
				_paletteKind = value;
				_palette = ChartPalette.For(value);
				ApplyPalette();
				_staticDirty = true;
			}
		}

		public void CyclePalette() => Palette = (ChartPaletteKind)(((int)_paletteKind + 1) % 3);

		public DistanceUnit DistanceUnit
		{
			get => _unit;
			set
			{
				if (_unit == value) return;
				_unit = value;
				_staticDirty = true;
				_depthTextDirty = true;
			}
		}

		/// <summary>Nautical miles across the viewport, clamped to [0.25, 8].</summary>
		public double SpanNauticalMiles
		{
			get => _span;
			set
			{
				var clamped = Math.Max(MinimumSpan, Math.Min(MaximumSpan, double.IsNaN(value) ? _span : value));
				if (clamped == _span) return;
				_span = clamped;
				_modeTextDirty = true;
			}
		}

		public void ZoomIn() => Step(-1);

		public void ZoomOut() => Step(1);

		private void Step(int direction)
		{
			var index = 0;
			var best = double.PositiveInfinity;
			for (var i = 0; i < SpanLadder.Length; i++)
			{
				var distance = Math.Abs(Math.Log(SpanLadder[i] / _span));
				if (distance < best)
				{
					best = distance;
					index = i;
				}
			}

			// From a span off the ladder, zooming moves to the next rung in that direction.
			if (SpanLadder[index] == _span || (direction < 0 && SpanLadder[index] > _span) || (direction > 0 && SpanLadder[index] < _span))
			{
				index += direction;
			}

			SpanNauticalMiles = SpanLadder[Math.Max(0, Math.Min(SpanLadder.Length - 1, index))];
		}

		public ChartOrientation Orientation
		{
			get => _orientation;
			set
			{
				if (_orientation == value) return;
				_orientation = value;
				_modeTextDirty = true;
			}
		}

		public void ToggleOrientation() => Orientation = _orientation == ChartOrientation.NorthUp ? ChartOrientation.CourseUp : ChartOrientation.NorthUp;

		/// <summary>True while the view follows own ship; false after a pan, until <see cref="Centre"/>.</summary>
		public bool Centred => _centred;

		/// <summary>Recentres on own ship.</summary>
		public void Centre()
		{
			if (_centred) return;
			_centred = true;
			_modeTextDirty = true;
		}

		/// <summary>Moves the chart by a screen offset (px, right and down), holding the view on the new centre until <see cref="Centre"/>.</summary>
		public void Pan(double dxPixels, double dyPixels)
		{
			if (!HasView) return;
			CurrentView.ToWorld(CurrentView.ScreenCentreX - dxPixels, CurrentView.ScreenCentreY - dyPixels, out _panNorth, out _panEast);
			if (_centred)
			{
				_centred = false;
				_modeTextDirty = true;
			}
		}

		/// <summary>The requested safety contour, m below chart datum; the chart draws the next deeper charted contour.</summary>
		public double SafetyContourMetres
		{
			get => _safety;
			set
			{
				if (!(value > 0.0) || double.IsInfinity(value) || value == _safety) return;
				_safety = value;
				_staticDirty = true;
				_depthTextDirty = true;
			}
		}

		/// <summary>The safety contour depth actually drawn, m; NaN before the first render.</summary>
		public double SafetyContourDrawnMetres => Canvas.HasRenderedView ? Canvas.SafetyContourDrawn : double.NaN;

		/// <summary>The length of the COG/SOG vectors, minutes of run at the present speed.</summary>
		public double VectorMinutes
		{
			get => _vectorMinutes;
			set
			{
				if (value > 0.0 && !double.IsInfinity(value)) _vectorMinutes = value;
			}
		}

		public CpaAlarmLimits AlarmLimits
		{
			get => _limits;
			set => _limits = value;
		}

		/// <summary>Meteorological visibility, m; 0 or more than a mile draws no ring.</summary>
		public double VisibilityMetres
		{
			get => _visibility;
			set => _visibility = double.IsNaN(value) ? 0.0 : Math.Max(0.0, value);
		}

		/// <summary>The selected target's roster index, or -1.</summary>
		public int SelectedTarget
		{
			get => _selected;
			set => _selected = value;
		}

		/// <summary>Selects the next row of the CPA list (the closest first), wrapping to none after the last.</summary>
		public void SelectNextTarget()
		{
			if (_ranking.Count == 0)
			{
				_selected = -1;
				return;
			}

			var row = -1;
			for (var i = 0; i < _ranking.Count; i++)
			{
				if (_ranking.Target(i) == _selected)
				{
					row = i;
					break;
				}
			}

			row++;
			_selected = row < _ranking.Count ? _ranking.Target(row) : -1;
		}

		public void ClearSelection() => _selected = -1;

		/// <summary>Takes the tessellated chart (built once by the host).</summary>
		public void SetChart(ChartTessellation chart)
		{
			_chart = chart;
			Canvas.SetChart(chart);
			_staticDirty = true;
			_depthTextDirty = true;
		}

		private static VisualElement Bar(float height)
		{
			var bar = new VisualElement { pickingMode = PickingMode.Ignore };
			bar.style.flexDirection = FlexDirection.Row;
			bar.style.justifyContent = Justify.SpaceBetween;
			bar.style.alignItems = Align.Center;
			bar.style.height = height;
			bar.style.paddingLeft = 8;
			bar.style.paddingRight = 8;
			return bar;
		}

		private static Label Caption(string text, int size, bool bold)
		{
			var label = new Label(text) { pickingMode = PickingMode.Ignore };
			label.AddToClassList("fs-label");
			label.style.fontSize = size;
			label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
			return label;
		}

		private void ApplyPalette()
		{
			style.backgroundColor = _palette.Panel;
			PaletteRoles.SetBorderColor(this, _palette.Bezel);
			_header.style.backgroundColor = _palette.Panel;
			_footer.style.backgroundColor = _palette.Panel;
			_viewport.style.backgroundColor = _palette.NoData;
			_modeLabel.style.color = _palette.Text;
			_depthLabel.style.color = _palette.TextDim;
			_noticeLabel.style.color = _palette.Instruments.Caution;
			_headingLabel.style.color = _palette.Text;
			_courseLabel.style.color = _palette.CourseVector;
			_speedLabel.style.color = _palette.Text;
			_statusLabel.style.color = _palette.TextDim;
			_scaleLabel.style.color = _palette.TextDim;
			_scaleBar.style.backgroundColor = _palette.TextDim;
			Targets.SetPalette(_palette);
			List.SetPalette(_palette);
		}

		/// <summary>
		/// One frame: reads own ship (null or no telemetry shows NO POSITION over the last chart) and the targets at the
		/// run time, regenerates the static chart when the view changed, and redraws the moving layer and the list.
		/// </summary>
		public void Refresh(IVesselTelemetrySource ownShip, ITargetSource targets, double time)
		{
			_watch.Restart();
			var rect = _viewport.contentRect;
			if (rect.width < 2f || rect.height < 2f || float.IsNaN(rect.width))
			{
				_watch.Stop();
				return;
			}

			var live = ownShip != null && ownShip.HasTelemetry;
			if (live && ownShip.RunId != _runId)
			{
				_runId = ownShip.RunId;
				_track.Clear();
				_selected = -1;
				_selectedTextFor = -1;
			}

			var roster = targets != null ? targets.Count : 0;
			if (targets != null && (targets.Generation != _generation || !ReferenceEquals(targets, _targetsSeen)))
			{
				// A new roster (another day, a replay, another source): nothing cached by index survives it.
				_generation = targets.Generation;
				_targetsSeen = targets;
				_selected = -1;
				_selectedTextFor = -1;
				Targets.EnsureTargets(roster);
			}

			Targets.EnsureTrack(TrackFixes);
			double groundNorth = 0.0, groundEast = 0.0, course = 0.0, speed = 0.0;
			if (live)
			{
				var state = ownShip.State;
				_ownNorth = state.North;
				_ownEast = state.East;
				_ownHeading = state.Heading;
				_ownKnown = true;
				var velocity = state.GroundVelocity;
				groundNorth = velocity.North;
				groundEast = velocity.East;
				speed = Math.Sqrt(groundNorth * groundNorth + groundEast * groundEast);
				course = speed > 0.0 ? Angles.WrapTwoPi(Math.Atan2(groundEast, groundNorth)) : _ownHeading;
				UpdateOutline(ownShip.Parameters);
				_track.TryAdd(_ownNorth, _ownEast, time);
			}
			else if (!_ownKnown && _chart != null && _chart.Stops.Count > 0)
			{
				// Nothing to centre on yet: the first landing of the catalog, so the chart is still a chart.
				_ownNorth = _chart.Stops.North[0];
				_ownEast = _chart.Stops.East[0];
				_ownHeading = 0.0;
			}

			var courseKnown = live && speed >= MinimumSpeedForCourse;
			if (live)
			{
				_upBearing = _orientation == ChartOrientation.NorthUp ? 0.0 : courseKnown ? course : _ownHeading;
			}
			else if (_orientation == ChartOrientation.NorthUp)
			{
				_upBearing = 0.0;
			}

			var centreNorth = _centred ? _ownNorth : _panNorth;
			var centreEast = _centred ? _ownEast : _panEast;
			var metresPerPixel = ChartView.MetresPerPixelFor(_span, rect.width);
			CurrentView = new ChartView(centreNorth, centreEast, metresPerPixel, _upBearing, rect.width * 0.5, rect.height * 0.5);
			HasView = true;

			RenderStaticIfNeeded(rect, centreNorth, centreEast, metresPerPixel);
			PlaceCanvas(rect, centreNorth, centreEast, metresPerPixel);
			UpdateTexts(live, courseKnown, course, speed);

			var visibilityPixels = _visibility > 0.0 && _visibility < RestrictedVisibilityMetres ? (float)(_visibility / metresPerPixel) : 0f;
			Targets.BeginFrame(roster, rect.width * 0.5f, rect.height * 0.5f, _centred, visibilityPixels);
			var view = CurrentView;
			if (live || _ownKnown)
			{
				view.ToScreen(_ownNorth, _ownEast, out var ox, out var oy);
				view.Direction(_ownHeading, out var hdx, out var hdy);
				view.Direction(course, out var cdx, out var cdy);
				var vectorPixels = (float)(speed * _vectorMinutes * 60.0 / metresPerPixel);
				Targets.SetOwnShip((float)ox, (float)oy, (float)hdx, (float)hdy, courseKnown, (float)cdx, (float)cdy, vectorPixels,
					(float)(_ownLength / metresPerPixel), (float)(_ownBeam / metresPerPixel), (float)(1.0 / metresPerPixel));
				for (var i = 0; i < _track.Count; i++)
				{
					view.ToScreen(_track.North(i), _track.East(i), out var tx, out var ty);
					Targets.AddTrackPoint((float)tx, (float)ty);
				}
			}

			_ranking.Begin();
			if (targets != null && live)
			{
				var margin = 60.0;
				for (var i = 0; i < roster; i++)
				{
					if (!targets.TrySample(i, time, out var sample))
					{
						continue;
					}

					var encounter = Encounter.Between(_ownNorth, _ownEast, groundNorth, groundEast, sample.North, sample.East, sample.VelocityNorth, sample.VelocityEast);
					_ranking.Add(i, encounter, _limits);
					view.ToScreen(sample.North, sample.East, out var x, out var y);
					if (x < -margin || y < -margin || x > rect.width + margin || y > rect.height + margin)
					{
						continue;
					}

					var targetSpeed = sample.SpeedOverGround;
					var targetCourse = targetSpeed > 0.0 ? Math.Atan2(sample.VelocityEast, sample.VelocityNorth) : 0.0;
					var heading = sample.HeadingKnown ? sample.Heading : targetCourse;
					view.Direction(heading, out var thdx, out var thdy);
					view.Direction(targetCourse, out var tcdx, out var tcdy);
					Targets.SetTarget(i, (float)x, (float)y, sample.HeadingKnown || targetSpeed >= StoppedSpeed, (float)thdx, (float)thdy, (float)tcdx, (float)tcdy,
						(float)(targetSpeed * _vectorMinutes * 60.0 / metresPerPixel), (float)(sample.Length / metresPerPixel), (float)(sample.Beam / metresPerPixel),
						_limits.IsAlarm(encounter), targetSpeed < StoppedSpeed, !sample.Moving);
				}
			}

			LandingLabelsGiveWay(rect, roster);
			List.Update(_ranking, targets);
			if (_selected >= 0 && (_selected >= roster || targets == null))
			{
				_selected = -1;
			}

			Targets.SetSelection(_selected, SelectionText(targets));
			Targets.EndFrame();
			_watch.Stop();
			LastRefreshMilliseconds = _watch.Elapsed.TotalMilliseconds;
		}

		/// <summary>Own ship and the targets keep this clearance from a landing label's box, px.</summary>
		public const float LabelClearancePixels = 14f;

		/// <summary>
		/// Landing names give way to what moves: a label whose box reaches own ship or a drawn target moves to its alternate
		/// anchor, and is hidden while both anchors are covered. Checked every frame over at most 24 labels; style changes
		/// only when a label's state flips.
		/// </summary>
		private void LandingLabelsGiveWay(Rect rect, int roster)
		{
			var pool = Canvas.StopLabelPool;
			var translate = Canvas.resolvedStyle.translate;
			for (var i = 0; i < pool.Count; i++)
			{
				var label = pool[i];
				var halfWidth = label.resolvedStyle.width * 0.5f + LabelClearancePixels;
				var halfHeight = label.resolvedStyle.height * 0.5f + LabelClearancePixels;
				var covered = Covered(pool, i, translate, halfWidth, halfHeight, roster);
				if (covered && !pool.IsAlternate(i))
				{
					pool.SetAlternate(i, true);
					covered = Covered(pool, i, translate, halfWidth, halfHeight, roster);
				}
				else if (!covered && pool.IsAlternate(i))
				{
					// Back to the preferred anchor once that is clear again.
					pool.SetAlternate(i, false);
					if (Covered(pool, i, translate, halfWidth, halfHeight, roster))
					{
						pool.SetAlternate(i, true);
						covered = Covered(pool, i, translate, halfWidth, halfHeight, roster);
					}
				}

				pool.SetHidden(i, covered);
			}
		}

		private bool Covered(ChartLabelPool pool, int i, Vector3 translate, float halfWidth, float halfHeight, int roster)
		{
			pool.ScreenPoint(i, out var x, out var y);
			x += translate.x;
			y += translate.y;
			if (Targets.OwnShipShown && Math.Abs(Targets.OwnX - x) < halfWidth && Math.Abs(Targets.OwnY - y) < halfHeight)
			{
				return true;
			}

			for (var t = 0; t < roster; t++)
			{
				if (Targets.IsPresent(t) && Math.Abs(Targets.TargetX(t) - x) < halfWidth && Math.Abs(Targets.TargetY(t) - y) < halfHeight)
				{
					return true;
				}
			}

			return false;
		}

		private string SelectionText(ITargetSource targets)
		{
			if (_selected < 0 || targets == null)
			{
				_selectedTextFor = -1;
				return string.Empty;
			}

			if (_selectedTextFor != _selected)
			{
				// Built once per selection change: the name, else the MMSI.
				_selectedTextFor = _selected;
				var name = targets.Name(_selected);
				_selectedText = string.IsNullOrEmpty(name) ? targets.Mmsi(_selected).ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
			}

			return _selectedText;
		}

		private void UpdateOutline(VesselParameters parameters)
		{
			if (parameters == null || ReferenceEquals(parameters, _outlineFor))
			{
				return;
			}

			_outlineFor = parameters;
			_ownLength = parameters.Particulars?.LengthOverall?.Value ?? 26.0;
			_ownBeam = parameters.Particulars?.Beam?.Value ?? 8.0;
			try
			{
				var outline = HullOutline.FromContact(parameters);
				_outlineForward = new float[outline.Count];
				_outlineStarboard = new float[outline.Count];
				for (var i = 0; i < outline.Count; i++)
				{
					_outlineForward[i] = (float)outline.X(i);
					_outlineStarboard[i] = (float)outline.Y(i);
				}

				_outlineCount = outline.Count;
			}
			catch (ArgumentException)
			{
				_outlineCount = 0;
			}

			Targets.SetOwnOutline(_outlineForward, _outlineStarboard, _outlineCount);
		}

		private void RenderStaticIfNeeded(Rect rect, double centreNorth, double centreEast, double metresPerPixel)
		{
			var diagonal = Math.Sqrt(rect.width * rect.width + rect.height * rect.height);
			var side = Math.Ceiling(diagonal * 1.6);
			var need = _staticDirty || !Canvas.HasRenderedView || _renderedSpan != _span || Canvas.RenderedView.ScreenCentreX * 2.0 != side;
			if (!need)
			{
				var rendered = Canvas.RenderedView;
				var dn = centreNorth - rendered.CentreNorth;
				var de = centreEast - rendered.CentreEast;
				// Regenerate once the view centre has used up most of the margin around the viewport's diagonal.
				var marginPixels = (side - diagonal) * 0.5 * 0.8;
				need = dn * dn + de * de > marginPixels * marginPixels * metresPerPixel * metresPerPixel;
			}

			if (!need)
			{
				return;
			}

			Canvas.Render(centreNorth, centreEast, metresPerPixel, side, _span, _palette, _safety, _unit);
			Canvas.style.left = (float)((rect.width - side) * 0.5);
			Canvas.style.top = (float)((rect.height - side) * 0.5);
			_labelRotation = (float)(-_upBearing * 180.0 / Math.PI);
			Canvas.LayoutLabels(_labelRotation, rect.width * 0.5f, rect.height * 0.5f);
			_staticDirty = false;
			_renderedSpan = _span;
			_depthTextDirty = true;
			var bar = _span <= 1.0 ? ChartView.MetresPerCable : _span <= 4.0 ? ChartView.MetresPerNauticalMile * 0.5 : ChartView.MetresPerNauticalMile;
			if (bar != _scaleBarMetres)
			{
				_scaleBarMetres = bar;
				PaletteRoles.SetText(_scaleLabel, bar == ChartView.MetresPerCable ? "1 CABLE" : bar == ChartView.MetresPerNauticalMile ? "1 NM" : "0.5 NM");
			}

			_scaleBar.style.width = (float)(bar / metresPerPixel);
		}

		private void PlaceCanvas(Rect rect, double centreNorth, double centreEast, double metresPerPixel)
		{
			var rendered = Canvas.RenderedView;
			// Where the current centre sits in the north-up canvas, relative to the canvas centre.
			var dx = (centreEast - rendered.CentreEast) / metresPerPixel;
			var dy = -(centreNorth - rendered.CentreNorth) / metresPerPixel;
			// Turning the chart so the up bearing points up is a counter-clockwise turn on screen: negative in UI Toolkit's clockwise-positive rotation.
			var angle = -_upBearing;
			var cos = Math.Cos(angle);
			var sin = Math.Sin(angle);
			var rx = dx * cos - dy * sin;
			var ry = dx * sin + dy * cos;
			var degrees = (float)(angle * 180.0 / Math.PI);
			Canvas.style.rotate = new Rotate(DiagnosticRotateCanvas ? degrees : 0f);
			var translate = new Translate((float)-rx, (float)-ry);
			Canvas.style.translate = translate;
			// The labels' layer moves with the canvas but never turns: the labels are repositioned for the chart's rotation in steps.
			LabelLayer.style.translate = translate;
			if (DiagnosticRepositionLabels && (Math.Abs(degrees - _labelRotation) >= LabelRotationStep || (degrees == 0f && _labelRotation != 0f)))
			{
				_labelRotation = degrees;
				Canvas.SetLabelRotation(degrees);
			}

			Canvas.StepLabelRotation(LabelsRotatedPerFrame);
		}

		private void UpdateTexts(bool live, bool courseKnown, double course, double speed)
		{
			if (_modeTextDirty)
			{
				_modeTextDirty = false;
				PaletteRoles.SetText(_modeLabel, PlotterStrings.Span(_span) + (_orientation == ChartOrientation.NorthUp ? " · NORTH UP · " : " · COURSE UP · ") + (_centred ? "CENTRED" : "PANNED"));
			}

			if (_depthTextDirty)
			{
				_depthTextDirty = false;
				var drawn = SafetyContourDrawnMetres;
				var unitText = DisplayUnits.Symbol(_unit);
				var safety = double.IsNaN(drawn) ? "---" : InstrumentFormat.Fixed(DisplayUnits.Distance(drawn, _unit), 0);
				var requested = InstrumentFormat.Fixed(DisplayUnits.Distance(_safety, _unit), _unit == DistanceUnit.Feet ? 0 : 1);
				PaletteRoles.SetText(_depthLabel, "DEPTHS " + unitText + " MLLW · SAFETY " + safety + " " + unitText + (drawn == _safety || double.IsNaN(drawn) ? string.Empty : " (SET " + requested + " " + unitText + ")"));
			}

			if (_statusLabel.style.display != (live ? DisplayStyle.None : DisplayStyle.Flex))
			{
				_statusLabel.style.display = live ? DisplayStyle.None : DisplayStyle.Flex;
			}

			PaletteRoles.SetText(_headingLabel, live ? HeadingTexts.Heading(_ownHeading) : "HDG ---");
			PaletteRoles.SetText(_courseLabel, live && courseKnown ? HeadingTexts.Course(course) : "COG ---");
			PaletteRoles.SetText(_speedLabel, live ? HeadingTexts.Speed(speed) : "SOG --- kn");
		}

		/// <summary>Bounded readout strings for the footer, built once.</summary>
		private static class HeadingTexts
		{
			private static readonly string[] Headings = new string[360];
			private static readonly string[] Courses = new string[360];
			private static readonly string[] Speeds = new string[601];

			static HeadingTexts()
			{
				for (var i = 0; i < 360; i++)
				{
					Headings[i] = "HDG " + ReadoutStrings.Compass(i);
					Courses[i] = "COG " + ReadoutStrings.Compass(i);
				}

				for (var i = 0; i < Speeds.Length; i++)
				{
					Speeds[i] = "SOG " + (i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " kn";
				}
			}

			private static int Degrees(double radians)
			{
				var degrees = (int)Math.Round(DisplayUnits.CompassDegrees(radians), MidpointRounding.AwayFromZero);
				return degrees >= 360 ? 0 : degrees < 0 ? 0 : degrees;
			}

			public static string Heading(double radians) => InstrumentFormat.IsFinite(radians) ? Headings[Degrees(radians)] : "HDG ---";

			public static string Course(double radians) => InstrumentFormat.IsFinite(radians) ? Courses[Degrees(radians)] : "COG ---";

			public static string Speed(double metresPerSecond)
			{
				if (!InstrumentFormat.IsFinite(metresPerSecond))
				{
					return "SOG --- kn";
				}

				var tenths = (int)Math.Round(DisplayUnits.Knots(Math.Abs(metresPerSecond)) * 10.0, MidpointRounding.AwayFromZero);
				return tenths < Speeds.Length ? Speeds[tenths] : Speeds[Speeds.Length - 1];
			}
		}
	}
}
