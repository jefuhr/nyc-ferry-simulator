using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>The nine places a HUD widget can be anchored to, named in files as <c>top-left</c> … <c>bottom-right</c>.</summary>
	public enum HudAnchor
	{
		TopLeft,
		TopCenter,
		TopRight,
		MiddleLeft,
		MiddleCenter,
		MiddleRight,
		BottomLeft,
		BottomCenter,
		BottomRight,
	}

	public static class HudAnchors
	{
		private static readonly string[] Ids =
		{
			"top-left", "top-center", "top-right", "middle-left", "middle-center", "middle-right", "bottom-left", "bottom-center", "bottom-right",
		};

		/// <summary>The anchor's name in hud-layouts-v1.json.</summary>
		public static string Id(HudAnchor anchor) => Ids[(int)anchor];

		public static bool TryParse(string text, out HudAnchor anchor)
		{
			var index = Array.IndexOf(Ids, text);
			anchor = index >= 0 ? (HudAnchor)index : HudAnchor.MiddleCenter;
			return index >= 0;
		}

		/// <summary>Where the anchor sits as a fraction of the screen (and of the widget): (0, 0) top left … (1, 1) bottom right.</summary>
		public static Vector2 Fraction(HudAnchor anchor)
		{
			var index = (int)anchor;
			return new Vector2(index % 3 * 0.5f, index / 3 * 0.5f);
		}

		/// <summary>The anchor of the ninth of the screen that <paramref name="point"/> falls in.</summary>
		public static HudAnchor Nearest(Vector2 point, Vector2 screen)
		{
			var column = point.x < screen.x / 3f ? 0 : point.x < screen.x * 2f / 3f ? 1 : 2;
			var row = point.y < screen.y / 3f ? 0 : point.y < screen.y * 2f / 3f ? 1 : 2;
			return (HudAnchor)(row * 3 + column);
		}
	}

	/// <summary>The widget ids saved in hud-layouts-v1.json. Pop-out screens are <c>screen:</c> plus the screen id.</summary>
	public static class HudWidgetIds
	{
		public const string ClassicPanel = "classic-panel";
		public const string Compass = "compass";
		public const string Heading = "heading";
		public const string Speed = "speed";
		public const string Depth = "depth";
		public const string Rudder = "rudder";
		public const string PortEngine = "engine-port";
		public const string StarboardEngine = "engine-starboard";
		public const string Environment = "environment";
		public const string Clock = "clock";
		public const string TrafficAlarm = "traffic-alarm";
		public const string ScreenPrefix = "screen:";

		/// <summary>The console screens a pop-out can show, as <see cref="IHudScreenSource"/> names them.</summary>
		public static readonly IReadOnlyList<string> Screens = Array.AsReadOnly(new[] { "radar", "plotter", "engine-port", "engine-starboard", "alarms" });

		public static string Screen(string screenId) => ScreenPrefix + screenId;
	}

	/// <summary>The four built-in layouts.</summary>
	public static class HudPresets
	{
		/// <summary>No panel: the 3D console screens are the instruments; pop-outs only where the player adds them.</summary>
		public const string Console = "console";

		/// <summary>The v0.4 panel, as it was.</summary>
		public const string Classic = "classic";

		/// <summary>Heading, speed, depth and rudder.</summary>
		public const string Minimal = "minimal";

		/// <summary>Every widget but the classic panel.</summary>
		public const string Everything = "everything";

		/// <summary>
		/// The layout a first launch uses. It becomes <see cref="Console"/> only if a separate measurement shows the 3D
		/// console screens are readable from the helm; this is the one constant to flip.
		/// </summary>
		public const string FirstLaunch = Classic;

		public static bool IsPreset(string name) =>
			string.Equals(name, Console, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Classic, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(name, Minimal, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Everything, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Where one widget is: shown or not, its anchor, its offset from the anchor in 1280×720 reference pixels and its scale.</summary>
	public readonly struct HudPlacement : IEquatable<HudPlacement>
	{
		public HudPlacement(string id, bool visible, HudAnchor anchor, Vector2 offset, float scale)
		{
			Id = id;
			Visible = visible;
			Anchor = anchor;
			Offset = offset;
			Scale = scale;
		}

		public string Id { get; }

		public bool Visible { get; }

		public HudAnchor Anchor { get; }

		/// <summary>From the anchor point, reference pixels, y down.</summary>
		public Vector2 Offset { get; }

		public float Scale { get; }

		public HudPlacement WithVisible(bool visible) => new HudPlacement(Id, visible, Anchor, Offset, Scale);

		public HudPlacement WithOffset(Vector2 offset) => new HudPlacement(Id, Visible, Anchor, offset, Scale);

		public HudPlacement WithScale(float scale) => new HudPlacement(Id, Visible, Anchor, Offset, scale);

		public HudPlacement WithAnchor(HudAnchor anchor, Vector2 offset) => new HudPlacement(Id, Visible, anchor, offset, Scale);

		/// <summary>Same place on screen: offsets compare to a hundredth of a reference pixel, so a file round trip is equal.</summary>
		public bool Equals(HudPlacement other) =>
			Id == other.Id && Visible == other.Visible && Anchor == other.Anchor && Mathf.Abs(Scale - other.Scale) < 1e-4f &&
			(Offset - other.Offset).sqrMagnitude < 1e-4f;

		public override bool Equals(object obj) => obj is HudPlacement other && Equals(other);

		public override int GetHashCode() => (Id ?? string.Empty).GetHashCode() ^ (int)Anchor ^ (Visible ? 1 << 8 : 0);

		public override string ToString() => $"{Id} {(Visible ? "shown" : "hidden")} {HudAnchors.Id(Anchor)} ({Offset.x:0.##}, {Offset.y:0.##}) ×{Scale:0.##}";
	}

	/// <summary>What the registry knows about one widget.</summary>
	public sealed class HudWidgetDefinition
	{
		public HudWidgetDefinition(string id, string title, Vector2 size, HudAnchor anchor, Vector2 offset, float scale = 1f, float minScale = 0.6f,
			float maxScale = 1.6f, bool movable = true)
		{
			Id = id;
			Title = title;
			Size = size;
			DefaultAnchor = anchor;
			DefaultOffset = offset;
			DefaultScale = scale;
			MinScale = minScale;
			MaxScale = maxScale;
			Movable = movable;
		}

		public string Id { get; }

		/// <summary>The name edit mode shows.</summary>
		public string Title { get; }

		/// <summary>The widget's size at scale 1, reference pixels (Hud.uss fixes it).</summary>
		public Vector2 Size { get; }

		public HudAnchor DefaultAnchor { get; }

		public Vector2 DefaultOffset { get; }

		public float DefaultScale { get; }

		public float MinScale { get; }

		public float MaxScale { get; }

		/// <summary>False for the classic panel, which can be shown or hidden but stays where v0.4 put it.</summary>
		public bool Movable { get; }

		/// <summary>A pop-out of a console screen.</summary>
		public bool IsScreen => Id.StartsWith(HudWidgetIds.ScreenPrefix, StringComparison.Ordinal);

		/// <summary>The screen id a pop-out shows (<c>radar</c> for <c>screen:radar</c>); null for an instrument.</summary>
		public string ScreenId => IsScreen ? Id.Substring(HudWidgetIds.ScreenPrefix.Length) : null;

		public HudPlacement DefaultPlacement(bool visible) => new HudPlacement(Id, visible, DefaultAnchor, DefaultOffset, DefaultScale);

		public float ClampScale(float scale) => float.IsNaN(scale) ? DefaultScale : Mathf.Clamp(scale, MinScale, MaxScale);
	}

	/// <summary>A placement for every widget the registry knows, in registry order: a preset, a saved layout resolved, or one being edited.</summary>
	public sealed class HudLayout
	{
		private readonly List<HudPlacement> _placements;

		public HudLayout(string name, string basedOn, IEnumerable<HudPlacement> placements)
		{
			Name = name;
			BasedOn = basedOn;
			_placements = new List<HudPlacement>(placements);
		}

		/// <summary>The preset's or the saved layout's name.</summary>
		public string Name { get; }

		/// <summary>The preset a saved layout records its differences from; a preset is based on itself.</summary>
		public string BasedOn { get; }

		public IReadOnlyList<HudPlacement> Placements => _placements;

		public bool TryGet(string id, out HudPlacement placement)
		{
			foreach (var item in _placements)
			{
				if (item.Id == id)
				{
					placement = item;
					return true;
				}
			}

			placement = default;
			return false;
		}

		public HudPlacement Get(string id) => TryGet(id, out var placement) ? placement : throw new ArgumentException("No widget " + id + " in the layout.", nameof(id));

		public bool IsVisible(string id) => TryGet(id, out var placement) && placement.Visible;

		/// <summary>Replaces the placement with the same id; an id the layout does not have is ignored.</summary>
		public void Set(HudPlacement placement)
		{
			for (var i = 0; i < _placements.Count; i++)
			{
				if (_placements[i].Id == placement.Id)
				{
					_placements[i] = placement;
					return;
				}
			}
		}

		public HudLayout Copy() => new HudLayout(Name, BasedOn, _placements);

		public HudLayout Renamed(string name, string basedOn) => new HudLayout(name, basedOn, _placements);
	}

	/// <summary>
	/// Where a placement puts a widget in the 1280×720 reference frame, and snapping. A widget's anchor point is the
	/// same fraction of the widget as its anchor is of the screen (the bottom-centre of a bottom-centre widget), and it
	/// sits at the screen's anchor point plus the offset. Scaling is about that point, so it stays put.
	/// </summary>
	public static class HudGeometry
	{
		/// <summary>The default gap between a widget and the screen edge, which snapping pulls to.</summary>
		public const float EdgeMargin = 8f;

		/// <summary>How close, in reference pixels, an edge must come to be pulled onto another.</summary>
		public const float SnapDistance = 8f;

		public static readonly Vector2 ReferenceScreen = new Vector2(1280, 720);

		public static Vector2 AnchorPoint(HudPlacement placement, Vector2 screen) => Vector2.Scale(HudAnchors.Fraction(placement.Anchor), screen) + placement.Offset;

		/// <summary>The rectangle the widget covers on screen, y down, scale included.</summary>
		public static Rect VisualRect(HudPlacement placement, Vector2 size, Vector2 screen)
		{
			var fraction = HudAnchors.Fraction(placement.Anchor);
			var scaled = size * placement.Scale;
			var point = AnchorPoint(placement, screen);
			return new Rect(point - Vector2.Scale(fraction, scaled), scaled);
		}

		/// <summary>The placement that puts the widget at <paramref name="rect"/>, anchored to the ninth its centre is in.</summary>
		public static HudPlacement PlacementFor(HudPlacement placement, Rect rect, Vector2 screen)
		{
			var anchor = HudAnchors.Nearest(rect.center, screen);
			var fraction = HudAnchors.Fraction(anchor);
			var point = rect.position + Vector2.Scale(fraction, rect.size);
			var offset = point - Vector2.Scale(fraction, screen);
			return placement.WithAnchor(anchor, new Vector2(Mathf.Round(offset.x * 100f) / 100f, Mathf.Round(offset.y * 100f) / 100f));
		}

		/// <summary>
		/// Pulls the rectangle, without resizing it, onto the nearest line within <see cref="SnapDistance"/> on each axis:
		/// the screen margins and centre line, and the edges of the <paramref name="others"/> (edge to edge and side by side).
		/// </summary>
		public static Rect Snap(Rect rect, Vector2 screen, IReadOnlyList<Rect> others)
		{
			var dx = Best(rect.xMin, rect.center.x, rect.xMax, screen.x, others, true);
			var dy = Best(rect.yMin, rect.center.y, rect.yMax, screen.y, others, false);
			rect.position += new Vector2(dx, dy);
			return rect;
		}

		private static float Best(float min, float centre, float max, float extent, IReadOnlyList<Rect> others, bool horizontal)
		{
			var best = float.PositiveInfinity;
			void Try(float from, float to)
			{
				var delta = to - from;
				if (Mathf.Abs(delta) <= SnapDistance && Mathf.Abs(delta) < Mathf.Abs(best))
				{
					best = delta;
				}
			}

			Try(min, EdgeMargin);
			Try(max, extent - EdgeMargin);
			Try(centre, extent / 2f);
			if (others != null)
			{
				foreach (var other in others)
				{
					var otherMin = horizontal ? other.xMin : other.yMin;
					var otherMax = horizontal ? other.xMax : other.yMax;
					Try(min, otherMax);
					Try(min, otherMin);
					Try(max, otherMin);
					Try(max, otherMax);
				}
			}

			return float.IsInfinity(best) ? 0f : best;
		}
	}
}
