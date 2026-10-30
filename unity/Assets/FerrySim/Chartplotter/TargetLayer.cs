using System;
using Unity.Profiling;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// The moving layer over the chart, repainted every frame from arrays the view fills: own ship (a symbol, or its
	/// outline at true size when zoomed in) with its heading line and a dashed COG/SOG vector, its track, every present
	/// target (a triangle, or its outline when zoomed in; a hollow circle when stopped; a hollow square when the pack holds
	/// it moored all day) with its COG vector, the alarm colouring, the selection ring with the name, and a dashed
	/// visibility ring in restricted visibility. Everything is written straight into meshes by the <see cref="MeshWriter"/>
	/// (no Painter2D tessellation), so a frame of a few hundred targets costs a few hundred quads. It allocates nothing, and it repaints only when something visible moved by half a pixel, turned by half a degree or changed state.
	/// </summary>
	public sealed class TargetLayer : VisualElement, IDisposable
	{
		/// <summary>A hull is drawn at true size once it is this long on screen, px.</summary>
		public const float OutlineMinimumPixels = 16f;

		private readonly Label _selectionLabel;
		private static readonly ProfilerMarker PaintMarker = new ProfilerMarker("FerrySim.Chartplotter.TargetLayer");
		private readonly MeshWriter _writer = new MeshWriter();
		private ChartPalette _palette = ChartPalette.Day;

		// Own ship.
		private bool _ownShown;
		private float _ownX, _ownY, _ownHeadingDx, _ownHeadingDy, _ownCourseDx, _ownCourseDy, _ownVectorPixels, _ownLengthPixels, _ownBeamPixels, _ownScale;
		private bool _ownCourseShown;
		private float[] _outlineForward = Array.Empty<float>();
		private float[] _outlineStarboard = Array.Empty<float>();
		private int _outlineCount;

		// Track.
		private float[] _trackX = Array.Empty<float>();
		private float[] _trackY = Array.Empty<float>();
		private int _trackCount;

		// Targets.
		private int _targetCount;
		private bool[] _present = Array.Empty<bool>();
		private float[] _x = Array.Empty<float>();
		private float[] _y = Array.Empty<float>();
		private float[] _headingDx = Array.Empty<float>();
		private float[] _headingDy = Array.Empty<float>();
		private bool[] _headingKnown = Array.Empty<bool>();
		private float[] _courseDx = Array.Empty<float>();
		private float[] _courseDy = Array.Empty<float>();
		private float[] _vectorPixels = Array.Empty<float>();
		private float[] _lengthPixels = Array.Empty<float>();
		private float[] _beamPixels = Array.Empty<float>();
		private bool[] _alarm = Array.Empty<bool>();
		private bool[] _stopped = Array.Empty<bool>();
		private bool[] _moored = Array.Empty<bool>();
		private int _selected = -1;
		private float _visibilityPixels;
		private float _centreX, _centreY;
		private bool _centred = true;

		// What the last paint showed, so a frame that changed nothing visible is not repainted.
		private bool[] _paintedPresent = Array.Empty<bool>();
		private float[] _paintedX = Array.Empty<float>();
		private float[] _paintedY = Array.Empty<float>();
		private float[] _paintedHeadingDx = Array.Empty<float>();
		private float[] _paintedHeadingDy = Array.Empty<float>();
		private float[] _paintedCourseDx = Array.Empty<float>();
		private float[] _paintedCourseDy = Array.Empty<float>();
		private float[] _paintedVector = Array.Empty<float>();
		private float[] _paintedLength = Array.Empty<float>();
		private bool[] _paintedAlarm = Array.Empty<bool>();
		private bool[] _paintedStopped = Array.Empty<bool>();
		private bool[] _paintedMoored = Array.Empty<bool>();
		private float _paintedOwnX, _paintedOwnY, _paintedOwnHdx, _paintedOwnHdy, _paintedOwnCdx, _paintedOwnCdy, _paintedOwnVector, _paintedOwnLength, _paintedVisibility, _paintedCentreX, _paintedCentreY, _paintedTrackX, _paintedTrackY;
		private bool _paintedOwnShown, _paintedOwnCourse, _paintedCentred, _paintedAny;
		private int _paintedTargetCount, _paintedTrackCount, _paintedSelected;

		/// <summary>A target or vector moves this far on screen before the layer repaints, px.</summary>
		public const float RepaintPixels = 0.5f;

		/// <summary>Cosine of the heading or course change that repaints: half a degree.</summary>
		private const float RepaintDirectionCosine = 0.99996192f;

		public TargetLayer()
		{
			pickingMode = PickingMode.Ignore;
			style.position = Position.Absolute;
			style.left = 0;
			style.top = 0;
			style.right = 0;
			style.bottom = 0;
			_selectionLabel = new Label(string.Empty) { pickingMode = PickingMode.Ignore };
			_selectionLabel.AddToClassList("fs-label");
			_selectionLabel.style.position = Position.Absolute;
			_selectionLabel.style.fontSize = 11;
			_selectionLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
			_selectionLabel.style.display = DisplayStyle.None;
			Add(_selectionLabel);
			generateVisualContent += OnGenerateVisualContent;
		}

		/// <summary>Paints so far; it rises every frame the view refreshes.</summary>
		public int PaintCount { get; private set; }

		public int TargetsDrawn { get; private set; }

		/// <summary>Vertices written by the last paint.</summary>
		public int LastVertices { get; private set; }

		public bool OwnShipShown => _ownShown;

		public float OwnX => _ownX;

		public float OwnY => _ownY;

		public bool IsPresent(int target) => target >= 0 && target < _targetCount && _present[target];

		public float TargetX(int target) => _x[target];

		public float TargetY(int target) => _y[target];

		public void SetPalette(ChartPalette palette)
		{
			_palette = palette ?? ChartPalette.Day;
			_selectionLabel.style.color = _palette.Text;
		}

		/// <summary>Makes room for a roster; allocates only when the roster grows.</summary>
		public void EnsureTargets(int count)
		{
			if (count <= _present.Length)
			{
				return;
			}

			_present = new bool[count];
			_x = new float[count];
			_y = new float[count];
			_headingDx = new float[count];
			_headingDy = new float[count];
			_headingKnown = new bool[count];
			_courseDx = new float[count];
			_courseDy = new float[count];
			_vectorPixels = new float[count];
			_lengthPixels = new float[count];
			_beamPixels = new float[count];
			_alarm = new bool[count];
			_stopped = new bool[count];
			_moored = new bool[count];
			_paintedPresent = new bool[count];
			_paintedX = new float[count];
			_paintedY = new float[count];
			_paintedHeadingDx = new float[count];
			_paintedHeadingDy = new float[count];
			_paintedCourseDx = new float[count];
			_paintedCourseDy = new float[count];
			_paintedVector = new float[count];
			_paintedLength = new float[count];
			_paintedAlarm = new bool[count];
			_paintedStopped = new bool[count];
			_paintedMoored = new bool[count];
			_paintedAny = false;
		}

		public void EnsureTrack(int capacity)
		{
			if (capacity > _trackX.Length)
			{
				_trackX = new float[capacity];
				_trackY = new float[capacity];
			}
		}

		/// <summary>Own ship's outline in body metres (x forward, y starboard); null for the symbol only.</summary>
		public void SetOwnOutline(float[] forward, float[] starboard, int count)
		{
			_outlineForward = forward ?? Array.Empty<float>();
			_outlineStarboard = starboard ?? Array.Empty<float>();
			_outlineCount = count;
		}

		public void BeginFrame(int targetCount, float centreX, float centreY, bool centred, float visibilityPixels)
		{
			_targetCount = targetCount;
			_centreX = centreX;
			_centreY = centreY;
			_centred = centred;
			_visibilityPixels = visibilityPixels;
			_ownShown = false;
			_trackCount = 0;
			_selected = -1;
			for (var i = 0; i < targetCount; i++)
			{
				_present[i] = false;
			}
		}

		public void SetOwnShip(float x, float y, float headingDx, float headingDy, bool courseShown, float courseDx, float courseDy, float vectorPixels, float lengthPixels, float beamPixels, float pixelsPerMetre)
		{
			_ownShown = true;
			_ownX = x;
			_ownY = y;
			_ownHeadingDx = headingDx;
			_ownHeadingDy = headingDy;
			_ownCourseShown = courseShown;
			_ownCourseDx = courseDx;
			_ownCourseDy = courseDy;
			_ownVectorPixels = vectorPixels;
			_ownLengthPixels = lengthPixels;
			_ownBeamPixels = beamPixels;
			_ownScale = pixelsPerMetre;
		}

		public void AddTrackPoint(float x, float y)
		{
			if (_trackCount < _trackX.Length)
			{
				_trackX[_trackCount] = x;
				_trackY[_trackCount] = y;
				_trackCount++;
			}
		}

		public void SetTarget(int index, float x, float y, bool headingKnown, float headingDx, float headingDy, float courseDx, float courseDy, float vectorPixels, float lengthPixels, float beamPixels, bool alarm, bool stopped, bool moored)
		{
			_present[index] = true;
			_x[index] = x;
			_y[index] = y;
			_headingKnown[index] = headingKnown;
			_headingDx[index] = headingDx;
			_headingDy[index] = headingDy;
			_courseDx[index] = courseDx;
			_courseDy[index] = courseDy;
			_vectorPixels[index] = vectorPixels;
			_lengthPixels[index] = lengthPixels;
			_beamPixels[index] = beamPixels;
			_alarm[index] = alarm;
			_stopped[index] = stopped;
			_moored[index] = moored;
		}

		/// <summary>Marks the selected target and shows its name (an existing string) beside it.</summary>
		/// <summary>The selection label's text as drawn (evidence for the roster-change tests).</summary>
		public string SelectionTextShown => _selectionLabel.text;

		public void SetSelection(int index, string text)
		{
			_selected = index;
			if (index < 0 || index >= _targetCount || !_present[index])
			{
				if (_selectionLabel.style.display != DisplayStyle.None)
				{
					_selectionLabel.style.display = DisplayStyle.None;
				}

				return;
			}

			if (!ReferenceEquals(_selectionLabel.text, text) && _selectionLabel.text != text)
			{
				_selectionLabel.text = text;
			}

			_selectionLabel.style.left = _x[index] + 12f;
			_selectionLabel.style.top = _y[index] - 8f;
			if (_selectionLabel.style.display != DisplayStyle.Flex)
			{
				_selectionLabel.style.display = DisplayStyle.Flex;
			}
		}

		/// <summary>Repaints only when something visible changed since the last paint: a move of half a pixel, a turn of half a degree, a flag, the selection or the track.</summary>
		public void EndFrame()
		{
			if (!Changed())
			{
				SkippedPaints++;
				return;
			}

			Remember();
			MarkDirtyRepaint();
		}

		/// <summary>Frames whose paint was skipped because nothing visible changed.</summary>
		public int SkippedPaints { get; private set; }

		private static bool Moved(float a, float b) => Math.Abs(a - b) >= RepaintPixels;

		private static bool Turned(float dx0, float dy0, float dx1, float dy1) => dx0 * dx1 + dy0 * dy1 < RepaintDirectionCosine;

		private bool Changed()
		{
			if (!_paintedAny || _paintedTargetCount != _targetCount || _paintedSelected != _selected || _paintedCentred != _centred || _paintedOwnShown != _ownShown || _paintedOwnCourse != _ownCourseShown ||
				_paintedTrackCount != _trackCount || Moved(_paintedVisibility, _visibilityPixels) || Moved(_paintedCentreX, _centreX) || Moved(_paintedCentreY, _centreY))
			{
				return true;
			}

			if (_ownShown && (Moved(_paintedOwnX, _ownX) || Moved(_paintedOwnY, _ownY) || Turned(_paintedOwnHdx, _paintedOwnHdy, _ownHeadingDx, _ownHeadingDy) ||
				(_ownCourseShown && Turned(_paintedOwnCdx, _paintedOwnCdy, _ownCourseDx, _ownCourseDy)) || Moved(_paintedOwnVector, _ownVectorPixels) || Moved(_paintedOwnLength, _ownLengthPixels)))
			{
				return true;
			}

			if (_trackCount > 0 && (Moved(_paintedTrackX, _trackX[0]) || Moved(_paintedTrackY, _trackY[0])))
			{
				return true;
			}

			for (var i = 0; i < _targetCount; i++)
			{
				if (_present[i] != _paintedPresent[i])
				{
					return true;
				}

				if (!_present[i])
				{
					continue;
				}

				if (Moved(_paintedX[i], _x[i]) || Moved(_paintedY[i], _y[i]) || _paintedAlarm[i] != _alarm[i] || _paintedStopped[i] != _stopped[i] || _paintedMoored[i] != _moored[i] ||
					Moved(_paintedVector[i], _vectorPixels[i]) || Moved(_paintedLength[i], _lengthPixels[i]) ||
					Turned(_paintedHeadingDx[i], _paintedHeadingDy[i], _headingDx[i], _headingDy[i]) || Turned(_paintedCourseDx[i], _paintedCourseDy[i], _courseDx[i], _courseDy[i]))
				{
					return true;
				}
			}

			return false;
		}

		private void Remember()
		{
			_paintedAny = true;
			_paintedTargetCount = _targetCount;
			_paintedSelected = _selected;
			_paintedCentred = _centred;
			_paintedOwnShown = _ownShown;
			_paintedOwnCourse = _ownCourseShown;
			_paintedTrackCount = _trackCount;
			_paintedVisibility = _visibilityPixels;
			_paintedCentreX = _centreX;
			_paintedCentreY = _centreY;
			_paintedOwnX = _ownX;
			_paintedOwnY = _ownY;
			_paintedOwnHdx = _ownHeadingDx;
			_paintedOwnHdy = _ownHeadingDy;
			_paintedOwnCdx = _ownCourseDx;
			_paintedOwnCdy = _ownCourseDy;
			_paintedOwnVector = _ownVectorPixels;
			_paintedOwnLength = _ownLengthPixels;
			if (_trackCount > 0)
			{
				_paintedTrackX = _trackX[0];
				_paintedTrackY = _trackY[0];
			}

			for (var i = 0; i < _targetCount; i++)
			{
				_paintedPresent[i] = _present[i];
				_paintedX[i] = _x[i];
				_paintedY[i] = _y[i];
				_paintedHeadingDx[i] = _headingDx[i];
				_paintedHeadingDy[i] = _headingDy[i];
				_paintedCourseDx[i] = _courseDx[i];
				_paintedCourseDy[i] = _courseDy[i];
				_paintedVector[i] = _vectorPixels[i];
				_paintedLength[i] = _lengthPixels[i];
				_paintedAlarm[i] = _alarm[i];
				_paintedStopped[i] = _stopped[i];
				_paintedMoored[i] = _moored[i];
			}
		}

		private void OnGenerateVisualContent(MeshGenerationContext context)
		{
			PaintCount++;
			var rect = contentRect;
			if (rect.width < 1f || rect.height < 1f)
			{
				return;
			}

			PaintMarker.Begin();
			_writer.Begin(context);
			if (_visibilityPixels > 0f)
			{
				DashedCircle(_ownX, _ownY, _visibilityPixels, _palette.Visibility, 1f);
			}

			if (!_centred)
			{
				// An off-centre view: a small cross marks the screen centre the chart is held at.
				Color32 dim = _palette.TextDim;
				_writer.Line(_centreX - 6f, _centreY, _centreX + 6f, _centreY, 1f, dim);
				_writer.Line(_centreX, _centreY - 6f, _centreX, _centreY + 6f, 1f, dim);
			}

			if (_trackCount > 1)
			{
				Color32 track = _palette.Track;
				for (var i = 1; i < _trackCount; i++)
				{
					_writer.Line(_trackX[i - 1], _trackY[i - 1], _trackX[i], _trackY[i], 1f, track);
				}
			}

			TargetsDrawn = 0;
			for (var i = 0; i < _targetCount; i++)
			{
				if (!_present[i])
				{
					continue;
				}

				TargetsDrawn++;
				DrawTarget(i);
			}

			if (_ownShown)
			{
				DrawOwnShip();
			}

			_writer.End();
			PaintMarker.End();
			LastVertices = _writer.TotalVertices;
		}

		private void DrawTarget(int i)
		{
			var x = _x[i];
			var y = _y[i];
			Color32 color = _alarm[i] ? _palette.TargetAlarm : _palette.Target;
			// The COG vector: a line from the reference point along the course, as long as the vector time.
			if (_vectorPixels[i] > 0.5f && !_stopped[i])
			{
				_writer.Line(x, y, x + _courseDx[i] * _vectorPixels[i], y + _courseDy[i] * _vectorPixels[i], 1.2f, color);
			}

			if (_moored[i])
			{
				_writer.SquareOutline(x, y, 5f, 1.5f, color);
			}
			else if (_stopped[i] || !_headingKnown[i])
			{
				_writer.CircleOutline(x, y, 5f, 1.5f, color, 12);
			}
			else if (_lengthPixels[i] >= OutlineMinimumPixels)
			{
				// True outline: a pointed bow on a rectangle, to the hull's length and beam, translucent fill and an outline.
				HullShape(x, y, _headingDx[i], _headingDy[i], _lengthPixels[i], _beamPixels[i], color);
			}
			else
			{
				var fx = _headingDx[i];
				var fy = _headingDy[i];
				var sx = -fy;
				var sy = fx;
				_writer.Triangle(x + fx * 7f, y + fy * 7f, x - fx * 5f + sx * 4.5f, y - fy * 5f + sy * 4.5f, x - fx * 5f - sx * 4.5f, y - fy * 5f - sy * 4.5f, color);
			}

			if (i == _selected)
			{
				_writer.CircleOutline(x, y, 10f, 1.5f, _palette.Selection, 16);
			}
		}

		/// <summary>A pointed hull: bow at half the length ahead, shoulders at 60% of that, a square stern.</summary>
		private void HullShape(float x, float y, float fx, float fy, float lengthPixels, float beamPixels, Color32 color)
		{
			var sx = -fy;
			var sy = fx;
			var half = lengthPixels * 0.5f;
			var beam = beamPixels * 0.5f;
			var bowX = x + fx * half; var bowY = y + fy * half;
			var srX = x + fx * half * 0.6f + sx * beam; var srY = y + fy * half * 0.6f + sy * beam;
			var slX = x + fx * half * 0.6f - sx * beam; var slY = y + fy * half * 0.6f - sy * beam;
			var qrX = x - fx * half + sx * beam; var qrY = y - fy * half + sy * beam;
			var qlX = x - fx * half - sx * beam; var qlY = y - fy * half - sy * beam;
			var fill = new Color32(color.r, color.g, color.b, 90);
			_writer.Triangle(bowX, bowY, srX, srY, slX, slY, fill);
			_writer.Quad(srX, srY, qrX, qrY, qlX, qlY, slX, slY, fill);
			_writer.Line(bowX, bowY, srX, srY, 1.5f, color);
			_writer.Line(srX, srY, qrX, qrY, 1.5f, color);
			_writer.Line(qrX, qrY, qlX, qlY, 1.5f, color);
			_writer.Line(qlX, qlY, slX, slY, 1.5f, color);
			_writer.Line(slX, slY, bowX, bowY, 1.5f, color);
		}

		private void DrawOwnShip()
		{
			var x = _ownX;
			var y = _ownY;
			var fx = _ownHeadingDx;
			var fy = _ownHeadingDy;
			var sx = -fy;
			var sy = fx;
			Color32 own = _palette.OwnShip;
			if (_ownCourseShown && _ownVectorPixels > 0.5f)
			{
				_writer.DashedLine(x, y, x + _ownCourseDx * _ownVectorPixels, y + _ownCourseDy * _ownVectorPixels, 2f, 6f, 4f, _palette.CourseVector);
			}

			// The heading line runs to the viewport edge.
			_writer.Line(x, y, x + fx * 4000f, y + fy * 4000f, 1f, _palette.HeadingLine);
			if (_ownLengthPixels >= OutlineMinimumPixels && _outlineCount >= 3)
			{
				// The vessel file's contact outline at true size: a fan of triangles from the reference point, then the outline.
				var fill = new Color32(own.r, own.g, own.b, 80);
				var px = x + fx * (_outlineForward[_outlineCount - 1] * _ownScale) + sx * (_outlineStarboard[_outlineCount - 1] * _ownScale);
				var py = y + fy * (_outlineForward[_outlineCount - 1] * _ownScale) + sy * (_outlineStarboard[_outlineCount - 1] * _ownScale);
				for (var i = 0; i < _outlineCount; i++)
				{
					var cx = x + fx * (_outlineForward[i] * _ownScale) + sx * (_outlineStarboard[i] * _ownScale);
					var cy = y + fy * (_outlineForward[i] * _ownScale) + sy * (_outlineStarboard[i] * _ownScale);
					_writer.Triangle(x, y, px, py, cx, cy, fill);
					_writer.Line(px, py, cx, cy, 1.5f, own);
					px = cx;
					py = cy;
				}
			}
			else if (_ownLengthPixels >= OutlineMinimumPixels)
			{
				HullShape(x, y, fx, fy, _ownLengthPixels, _ownBeamPixels, own);
			}
			else
			{
				// The conventional own-ship mark: a double circle on the reference point, with the heading line.
				_writer.CircleOutline(x, y, 4f, 1.5f, own, 12);
				_writer.CircleOutline(x, y, 8f, 1.5f, own, 16);
			}
		}

		private void DashedCircle(float cx, float cy, float radius, Color32 color, float width)
		{
			const int segments = 72;
			for (var i = 0; i < segments; i += 2)
			{
				var a0 = 2f * Mathf.PI * i / segments;
				var a1 = 2f * Mathf.PI * (i + 1) / segments;
				_writer.Line(cx + radius * Mathf.Sin(a0), cy - radius * Mathf.Cos(a0), cx + radius * Mathf.Sin(a1), cy - radius * Mathf.Cos(a1), width, color);
			}
		}

		public void Dispose() => _writer.Dispose();
	}
}
