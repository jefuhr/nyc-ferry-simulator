using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// The unrotated layer the chart's labels live in: it only translates (one dynamic transform the labels inherit), so
	/// text stays upright in course-up and a moving chart costs the labels nothing per frame.
	/// </summary>
	public sealed class ChartLabelLayer : VisualElement
	{
		public ChartLabelLayer()
		{
			pickingMode = PickingMode.Ignore;
			usageHints = UsageHints.DynamicTransform;
			style.position = Position.Absolute;
			style.left = 0;
			style.top = 0;
			style.right = 0;
			style.bottom = 0;
		}
	}

	/// <summary>
	/// A fixed set of labels made once and reused on every static render: each placement sets a label's text (an existing
	/// string, so nothing is allocated), its anchor in the chart canvas's north-up pixels and its colour; the rest are
	/// hidden. Labels sit in the unrotated <see cref="ChartLabelLayer"/>, so their screen position is the anchor turned
	/// by the chart's rotation about the canvas centre: <see cref="Layout"/> does that for all of them at a render, and
	/// <see cref="SetRotation"/> plus <see cref="StepRotation"/> redo it a few per frame when the chart turns. Each label
	/// centres itself on its point by negative margins measured after layout, and is placed by a translate so no
	/// repositioning ever triggers a layout pass.
	/// </summary>
	public sealed class ChartLabelPool
	{
		private readonly Label[] _labels;
		private readonly float[] _anchorX;
		private readonly float[] _anchorY;
		private readonly float[] _alternateX;
		private readonly float[] _alternateY;
		private readonly bool[] _alternate;
		private readonly bool[] _hidden;
		private int _used;
		private float _rotation;
		private float _targetRotation;
		private float _half, _centreX, _centreY;
		private int _cursor;

		public ChartLabelPool(VisualElement parent, int capacity, int fontSize, bool bold)
		{
			_labels = new Label[capacity];
			_anchorX = new float[capacity];
			_anchorY = new float[capacity];
			_alternateX = new float[capacity];
			_alternateY = new float[capacity];
			_alternate = new bool[capacity];
			_hidden = new bool[capacity];
			for (var i = 0; i < capacity; i++)
			{
				var label = new Label(string.Empty) { pickingMode = PickingMode.Ignore };
				label.AddToClassList("fs-label");
				label.style.position = UnityEngine.UIElements.Position.Absolute;
				label.style.left = 0;
				label.style.top = 0;
				label.style.fontSize = fontSize;
				label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
				label.style.unityTextAlign = TextAnchor.MiddleCenter;
				label.style.display = DisplayStyle.None;
				label.RegisterCallback<GeometryChangedEvent>(Recentre);
				parent.Add(label);
				_labels[i] = label;
			}
		}

		/// <summary>Centres a label on its (left, top) point once its size is known: negative margins of half its size.</summary>
		private static void Recentre(GeometryChangedEvent evt)
		{
			if (evt.target is Label label && (evt.newRect.width != evt.oldRect.width || evt.newRect.height != evt.oldRect.height))
			{
				label.style.marginLeft = -evt.newRect.width * 0.5f;
				label.style.marginTop = -evt.newRect.height * 0.5f;
			}
		}

		public int Capacity => _labels.Length;

		/// <summary>Labels shown by the last <see cref="End"/>.</summary>
		public int Count => _used;

		public Label this[int index] => _labels[index];

		/// <summary>A placed label's anchor in canvas pixels (north-up, from the canvas's top-left).</summary>
		public float AnchorX(int index) => _anchorX[index];

		public float AnchorY(int index) => _anchorY[index];

		/// <summary>The index of the shown label with this text, or -1.</summary>
		public int Find(string text)
		{
			for (var i = 0; i < _used; i++)
			{
				if (_labels[i].text == text)
				{
					return i;
				}
			}

			return -1;
		}

		public void Begin() => _used = 0;

		/// <summary>Shows the next label at a canvas-local anchor, with an alternate anchor it falls back to when it must give way; false when the pool is exhausted.</summary>
		public bool Place(string text, float x, float y, Color color, float alternateX = float.NaN, float alternateY = float.NaN)
		{
			if (_used >= _labels.Length)
			{
				return false;
			}

			var label = _labels[_used];
			_anchorX[_used] = x;
			_anchorY[_used] = y;
			_alternateX[_used] = float.IsNaN(alternateX) ? x : alternateX;
			_alternateY[_used] = float.IsNaN(alternateY) ? y : alternateY;
			_alternate[_used] = false;
			_hidden[_used] = false;
			label.style.visibility = Visibility.Visible;
			_used++;
			if (!ReferenceEquals(label.text, text) && label.text != text)
			{
				label.text = text;
			}

			label.style.color = color;
			if (label.style.display != DisplayStyle.Flex)
			{
				label.style.display = DisplayStyle.Flex;
			}

			return true;
		}

		/// <summary>Hides the labels not placed this time.</summary>
		public void End()
		{
			for (var i = _used; i < _labels.Length; i++)
			{
				if (_labels[i].style.display != DisplayStyle.None)
				{
					_labels[i].style.display = DisplayStyle.None;
				}
			}
		}

		/// <summary>
		/// Positions every shown label for a chart rotation (degrees, clockwise on screen) about the canvas centre, the
		/// canvas's half side and the layer point the canvas centre rests at (the viewport centre). Used at a render.
		/// </summary>
		public void Layout(float rotationDegrees, float canvasHalf, float centreX, float centreY)
		{
			_rotation = _targetRotation = rotationDegrees;
			_half = canvasHalf;
			_centreX = centreX;
			_centreY = centreY;
			_cursor = _used;
			for (var i = 0; i < _used; i++)
			{
				Place(i);
			}
		}

		/// <summary>Asks for the labels to follow a new chart rotation; <see cref="StepRotation"/> applies it a few labels per frame.</summary>
		public void SetRotation(float rotationDegrees)
		{
			if (Mathf.Abs(rotationDegrees - _targetRotation) < 1e-3f)
			{
				return;
			}

			_targetRotation = rotationDegrees;
			_cursor = 0;
		}

		/// <summary>True while labels still wait for the requested rotation.</summary>
		public bool RotationPending => _cursor < _used;

		/// <summary>Repositions at most <paramref name="budget"/> more labels for the requested rotation; true once all have it.</summary>
		public bool StepRotation(int budget)
		{
			if (_cursor >= _used)
			{
				_rotation = _targetRotation;
				return true;
			}

			_rotation = _targetRotation;
			var end = Mathf.Min(_used, _cursor + budget);
			for (; _cursor < end; _cursor++)
			{
				Place(_cursor);
			}

			return _cursor >= _used;
		}

		/// <summary>The screen point (layer-local, before the layer's translate) a placed label is centred on.</summary>
		public void ScreenPoint(int i, out float x, out float y)
		{
			var radians = _rotation * Mathf.Deg2Rad;
			var cos = Mathf.Cos(radians);
			var sin = Mathf.Sin(radians);
			var dx = (_alternate[i] ? _alternateX[i] : _anchorX[i]) - _half;
			var dy = (_alternate[i] ? _alternateY[i] : _anchorY[i]) - _half;
			x = _centreX + dx * cos - dy * sin;
			y = _centreY + dx * sin + dy * cos;
		}

		/// <summary>Moves a label to its alternate anchor, or back; a change repositions it at once.</summary>
		public void SetAlternate(int i, bool alternate)
		{
			if (_alternate[i] == alternate)
			{
				return;
			}

			_alternate[i] = alternate;
			Place(i);
		}

		public bool IsAlternate(int i) => _alternate[i];

		/// <summary>Hides a placed label without unplacing it (it gives way to something moving), or shows it again.</summary>
		public void SetHidden(int i, bool hidden)
		{
			if (_hidden[i] == hidden)
			{
				return;
			}

			_hidden[i] = hidden;
			_labels[i].style.visibility = hidden ? Visibility.Hidden : Visibility.Visible;
		}

		public bool IsHidden(int i) => _hidden[i];

		private void Place(int i)
		{
			var radians = _rotation * Mathf.Deg2Rad;
			var cos = Mathf.Cos(radians);
			var sin = Mathf.Sin(radians);
			var dx = (_alternate[i] ? _alternateX[i] : _anchorX[i]) - _half;
			var dy = (_alternate[i] ? _alternateY[i] : _anchorY[i]) - _half;
			// A translate, not left/top: a transform change never costs a layout pass over the whole layer.
			_labels[i].style.translate = new Translate(_centreX + dx * cos - dy * sin, _centreY + dx * sin + dy * cos);
		}
	}
}
