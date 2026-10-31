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
	/// repositioning ever triggers a layout pass. Two ways of filling it: in order (<see cref="Begin"/>, <see cref="Place"/>,
	/// <see cref="End"/>: the first labels of the pool, in the order offered), or keyed (<see cref="BeginKeyed"/>,
	/// <see cref="TryReuse"/>, <see cref="AssignFree"/>, <see cref="EndKeyed"/>: a label that showed the same key last
	/// time keeps its slot and its text, so a regeneration that moves the chart a little changes few texts, and the
	/// shown slots may have gaps, so iterate to <see cref="Extent"/> and check <see cref="IsShown"/>).
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
		private readonly bool[] _shown;
		private readonly bool[] _claimed;
		private readonly int[] _key;
		private int _used;
		private int _count;
		private int _freeCursor;
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
			_shown = new bool[capacity];
			_claimed = new bool[capacity];
			_key = new int[capacity];
			for (var i = 0; i < capacity; i++)
			{
				_key[i] = -1;
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

		/// <summary>Labels shown by the last <see cref="End"/> or <see cref="EndKeyed"/>.</summary>
		public int Count => _count;

		/// <summary>One past the highest slot that may be shown: iterate to here and check <see cref="IsShown"/> (keyed filling leaves gaps).</summary>
		public int Extent => _used;

		public bool IsShown(int index) => index >= 0 && index < _used && _shown[index];

		/// <summary>The key the slot last showed (keyed filling), or -1.</summary>
		public int KeyOf(int index) => _key[index];

		public Label this[int index] => _labels[index];

		/// <summary>A placed label's anchor in canvas pixels (north-up, from the canvas's top-left).</summary>
		public float AnchorX(int index) => _anchorX[index];

		public float AnchorY(int index) => _anchorY[index];

		/// <summary>The index of the shown label with this text, or -1.</summary>
		public int Find(string text)
		{
			for (var i = 0; i < _used; i++)
			{
				if (_shown[i] && _labels[i].text == text)
				{
					return i;
				}
			}

			return -1;
		}

		public void Begin()
		{
			_used = 0;
			_count = 0;
		}

		/// <summary>Shows the next label at a canvas-local anchor, with an alternate anchor it falls back to when it must give way; false when the pool is exhausted.</summary>
		public bool Place(string text, float x, float y, Color color, float alternateX = float.NaN, float alternateY = float.NaN)
		{
			if (_used >= _labels.Length)
			{
				return false;
			}

			Show(_used, -1, text, x, y, color, alternateX, alternateY);
			_used++;
			_count++;
			return true;
		}

		private void Show(int slot, int key, string text, float x, float y, Color color, float alternateX, float alternateY)
		{
			var label = _labels[slot];
			_key[slot] = key;
			_anchorX[slot] = x;
			_anchorY[slot] = y;
			_alternateX[slot] = float.IsNaN(alternateX) ? x : alternateX;
			_alternateY[slot] = float.IsNaN(alternateY) ? y : alternateY;
			_alternate[slot] = false;
			_hidden[slot] = false;
			_shown[slot] = true;
			label.style.visibility = Visibility.Visible;
			if (!ReferenceEquals(label.text, text) && label.text != text)
			{
				label.text = text;
			}

			label.style.color = color;
			if (label.style.display != DisplayStyle.Flex)
			{
				label.style.display = DisplayStyle.Flex;
			}
		}

		private void Hide(int slot)
		{
			_shown[slot] = false;
			if (_labels[slot].style.display != DisplayStyle.None)
			{
				_labels[slot].style.display = DisplayStyle.None;
			}
		}

		/// <summary>Hides the labels not placed this time.</summary>
		public void End()
		{
			for (var i = _used; i < _labels.Length; i++)
			{
				Hide(i);
			}
		}

		/// <summary>Starts a keyed filling: nothing is claimed yet; slots keep their last keys and texts until claimed or hidden.</summary>
		public void BeginKeyed()
		{
			System.Array.Clear(_claimed, 0, _claimed.Length);
			_freeCursor = 0;
		}

		/// <summary>Keeps a key in the slot it showed last time (its text unchanged), with a new anchor; false when the slot is not that key's or is already claimed.</summary>
		public bool TryReuse(int slot, int key, float x, float y, Color color)
		{
			if (slot < 0 || slot >= _labels.Length || _key[slot] != key || _claimed[slot])
			{
				return false;
			}

			_claimed[slot] = true;
			var label = _labels[slot];
			_anchorX[slot] = x;
			_anchorY[slot] = y;
			_alternateX[slot] = x;
			_alternateY[slot] = y;
			_alternate[slot] = false;
			_hidden[slot] = false;
			if (!_shown[slot])
			{
				_shown[slot] = true;
				label.style.visibility = Visibility.Visible;
				label.style.display = DisplayStyle.Flex;
			}

			label.style.color = color;
			return true;
		}

		/// <summary>Shows a key in the next unclaimed slot (hidden or holding a key not reused this time); the slot, or -1 when the pool is full.</summary>
		public int AssignFree(int key, string text, float x, float y, Color color)
		{
			for (; _freeCursor < _labels.Length; _freeCursor++)
			{
				if (!_claimed[_freeCursor])
				{
					var slot = _freeCursor++;
					_claimed[slot] = true;
					Show(slot, key, text, x, y, color, float.NaN, float.NaN);
					return slot;
				}
			}

			return -1;
		}

		/// <summary>Hides every unclaimed slot and settles the counts.</summary>
		public void EndKeyed()
		{
			_used = 0;
			_count = 0;
			for (var i = 0; i < _labels.Length; i++)
			{
				if (_claimed[i])
				{
					_used = i + 1;
					_count++;
				}
				else if (_shown[i])
				{
					Hide(i);
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
			if (!_shown[i])
			{
				return;
			}

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
