using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// The logic of HUD edit mode, kept apart from UI Toolkit so it can be tested without a rendered panel: a working
	/// copy of a layout and the commands that change it. A drag only previews (the caller translates the widget by
	/// <see cref="DragTo"/>'s result) until <see cref="Drop"/>, which snaps the widget to the screen margins, the centre
	/// lines and the other shown widgets' edges, anchors it to the ninth of the screen it ended in and writes its
	/// placement. Scale stays within the widget's limits and keeps the anchor point still. Reset goes back to the
	/// preset the layout is based on. Every change raises <see cref="PlacementChanged"/> with the widget's id.
	/// </summary>
	public sealed class HudEditSession
	{
		public const float ScaleStep = 0.1f;

		private readonly HudWidgetRegistry _registry;
		private readonly Func<string, Vector2> _sizeOf;
		private readonly List<Rect> _others = new List<Rect>();
		private string _dragId;
		private Vector2 _dragStart;
		private Rect _dragRect;

		/// <param name="start">The layout to edit; the session works on a copy.</param>
		/// <param name="screen">The frame's size in reference pixels.</param>
		/// <param name="sizeOf">A widget's size at scale 1, reference pixels; null uses the registry's sizes.</param>
		public HudEditSession(HudWidgetRegistry registry, HudLayout start, Vector2 screen, Func<string, Vector2> sizeOf)
		{
			_registry = registry ?? throw new ArgumentNullException(nameof(registry));
			Layout = (start ?? throw new ArgumentNullException(nameof(start))).Copy();
			Screen = screen;
			_sizeOf = sizeOf;
		}

		/// <summary>Raised with the widget's id after its placement changed, or null after the whole layout changed.</summary>
		public event Action<string> PlacementChanged;

		/// <summary>The working copy.</summary>
		public HudLayout Layout { get; private set; }

		/// <summary>The frame's size, reference pixels; the host updates it when the screen's aspect changes.</summary>
		public Vector2 Screen { get; set; }

		/// <summary>The widget edit mode acts on (show or hide, scale, reset); the last one clicked or chosen.</summary>
		public string Selected { get; private set; }

		public bool Dragging => _dragId != null;

		/// <summary>True after any change since the session started.</summary>
		public bool Modified { get; private set; }

		public void Select(string id)
		{
			if (_registry.Find(id) != null)
			{
				Selected = id;
			}
		}

		public Vector2 SizeOf(string id)
		{
			var size = _sizeOf?.Invoke(id) ?? Vector2.zero;
			if (size.x > 0f && size.y > 0f && !float.IsNaN(size.x) && !float.IsNaN(size.y))
			{
				return size;
			}

			return _registry.Find(id)?.Size ?? Vector2.zero;
		}

		/// <summary>Where the widget is drawn, reference pixels, y down. The classic panel always spans the frame's width.</summary>
		public Rect VisualRect(string id)
		{
			var placement = Layout.Get(id);
			if (id == HudWidgetIds.ClassicPanel)
			{
				var height = SizeOf(id).y;
				return new Rect(0f, Screen.y - height, Screen.x, height);
			}

			return HudGeometry.VisualRect(placement, SizeOf(id), Screen);
		}

		/// <summary>Starts dragging a movable widget grabbed at <paramref name="pointer"/>; false for one that cannot move.</summary>
		public bool BeginDrag(string id, Vector2 pointer)
		{
			var widget = _registry.Find(id);
			if (widget == null || !widget.Movable)
			{
				return false;
			}

			_dragId = id;
			Selected = id;
			_dragStart = pointer;
			_dragRect = VisualRect(id);
			_others.Clear();
			foreach (var placement in Layout.Placements)
			{
				if (placement.Visible && placement.Id != id)
				{
					_others.Add(VisualRect(placement.Id));
				}
			}

			return true;
		}

		/// <summary>The translation to preview the widget with; nothing is written until <see cref="Drop"/>.</summary>
		public Vector2 DragTo(Vector2 pointer) => Dragging ? pointer - _dragStart : Vector2.zero;

		/// <summary>Ends the drag at <paramref name="pointer"/>: snaps, re-anchors and writes the placement.</summary>
		public void Drop(Vector2 pointer)
		{
			if (!Dragging)
			{
				return;
			}

			var id = _dragId;
			var rect = _dragRect;
			rect.position += pointer - _dragStart;
			rect = HudGeometry.Snap(rect, Screen, _others);
			_dragId = null;
			Write(HudGeometry.PlacementFor(Layout.Get(id), rect, Screen));
		}

		public void CancelDrag() => _dragId = null;

		public void SetScale(string id, float scale)
		{
			var widget = _registry.Find(id);
			if (widget == null || !widget.Movable)
			{
				return;
			}

			Selected = id;
			Write(Layout.Get(id).WithScale(widget.ClampScale(Mathf.Round(scale * 1000f) / 1000f)));
		}

		public void StepScale(string id, int steps)
		{
			if (Layout.TryGet(id, out var placement))
			{
				SetScale(id, placement.Scale + steps * ScaleStep);
			}
		}

		public void SetVisible(string id, bool visible)
		{
			if (Layout.TryGet(id, out var placement))
			{
				Selected = id;
				Write(placement.WithVisible(visible));
			}
		}

		public void ToggleVisible(string id)
		{
			if (Layout.TryGet(id, out var placement))
			{
				SetVisible(id, !placement.Visible);
			}
		}

		/// <summary>Moves a widget by whole reference pixels, for keys; re-anchors like a drop but does not snap.</summary>
		public void Nudge(string id, Vector2 delta)
		{
			var widget = _registry.Find(id);
			if (widget == null || !widget.Movable || !Layout.TryGet(id, out var placement))
			{
				return;
			}

			var rect = VisualRect(id);
			rect.position += delta;
			Write(HudGeometry.PlacementFor(placement, rect, Screen));
		}

		/// <summary>Puts one widget back where the layout's preset has it.</summary>
		public void ResetWidget(string id)
		{
			var preset = _registry.Preset(Layout.BasedOn);
			if (preset.TryGet(id, out var original))
			{
				Write(original);
			}
		}

		/// <summary>Puts every widget back where the layout's preset has it.</summary>
		public void ResetLayout()
		{
			_dragId = null;
			Layout = _registry.Preset(Layout.BasedOn).Renamed(Layout.Name, Layout.BasedOn);
			Modified = true;
			PlacementChanged?.Invoke(null);
		}

		private void Write(HudPlacement placement)
		{
			if (Layout.TryGet(placement.Id, out var current) && current.Equals(placement) && current.Offset == placement.Offset)
			{
				return;
			}

			Layout.Set(placement);
			Modified = true;
			PlacementChanged?.Invoke(placement.Id);
		}
	}
}
