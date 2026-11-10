using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// Every HUD widget (its id, default anchor, offset, scale and scale limits, and its size at scale 1) and the four
	/// presets. A widget's default placement is where the <c>everything</c> preset puts it, so a widget the player shows
	/// for the first time appears clear of the others; the other presets show a subset, some of it placed differently.
	/// Sizes are in 1280×720 reference pixels and match Hud.uss.
	/// <para>
	/// A saved layout (<see cref="HudLayoutEntry"/>) records the preset it is based on and only its differences:
	/// <see cref="Resolve"/> applies them over the preset, so a widget the layout does not mention follows the preset,
	/// and <see cref="Differences"/> writes them. A saved widget id this build does not know is dropped with a warning
	/// and the rest still applies.
	/// </para>
	/// </summary>
	public sealed class HudWidgetRegistry
	{
		private static readonly Vector2 ScreenSize = new Vector2(300, 220);
		private const float ScreenScale = 0.7f;

		private readonly List<HudWidgetDefinition> _widgets = new List<HudWidgetDefinition>();
		private readonly Dictionary<string, HudLayout> _presets = new Dictionary<string, HudLayout>(StringComparer.OrdinalIgnoreCase);
		private readonly string[] _presetNames = { HudPresets.Console, HudPresets.Classic, HudPresets.Minimal, HudPresets.Everything };

		private HudWidgetRegistry()
		{
			Add(new HudWidgetDefinition(HudWidgetIds.ClassicPanel, "Classic panel", new Vector2(1280, 300), HudAnchor.BottomCenter, Vector2.zero,
				1f, 1f, 1f, false));
			Add(new HudWidgetDefinition(HudWidgetIds.Environment, "Water and air", new Vector2(452, 64), HudAnchor.TopLeft, new Vector2(8, 8)));
			Add(new HudWidgetDefinition(HudWidgetIds.Clock, "Clock", new Vector2(208, 36), HudAnchor.TopRight, new Vector2(-8, 8)));
			Add(new HudWidgetDefinition(HudWidgetIds.TrafficAlarm, "Traffic alarm", new Vector2(460, 30), HudAnchor.TopCenter, new Vector2(0, 242)));
			Add(new HudWidgetDefinition(HudWidgetIds.PortEngine, "Port engine", new Vector2(288, 262), HudAnchor.BottomLeft, new Vector2(8, -8)));
			Add(new HudWidgetDefinition(HudWidgetIds.Depth, "Depth", new Vector2(250, 94), HudAnchor.BottomLeft, new Vector2(8, -278)));
			Add(new HudWidgetDefinition(HudWidgetIds.Heading, "Heading", new Vector2(176, 116), HudAnchor.BottomCenter, new Vector2(-186, -8)));
			Add(new HudWidgetDefinition(HudWidgetIds.Compass, "Compass", new Vector2(180, 180), HudAnchor.BottomCenter, new Vector2(0, -8)));
			Add(new HudWidgetDefinition(HudWidgetIds.Speed, "Speed", new Vector2(176, 116), HudAnchor.BottomCenter, new Vector2(186, -8)));
			Add(new HudWidgetDefinition(HudWidgetIds.Rudder, "Rudder", new Vector2(372, 86), HudAnchor.BottomCenter, new Vector2(0, -188)));
			Add(new HudWidgetDefinition(HudWidgetIds.StarboardEngine, "Starboard engine", new Vector2(288, 262), HudAnchor.BottomRight, new Vector2(-8, -8)));
			string[] titles = { "Plotter screen", "Radar screen", "Alarm screen", "Port engine screen", "Starboard engine screen" };
			string[] order = { "plotter", "radar", "alarms", "engine-port", "engine-starboard" };
			for (var i = 0; i < order.Length; i++)
			{
				Add(new HudWidgetDefinition(HudWidgetIds.Screen(order[i]), titles[i], ScreenSize, HudAnchor.TopLeft, new Vector2(8 + i * 218, 80),
					ScreenScale, 0.4f, 2f));
			}

			// Presets: every widget gets a placement; what a preset does not show is hidden at its default place.
			AddPreset(HudPresets.Console);
			AddPreset(HudPresets.Classic, Shown(HudWidgetIds.ClassicPanel));
			AddPreset(HudPresets.Minimal,
				Shown(HudWidgetIds.Rudder, HudAnchor.BottomCenter, 0, -8),
				Shown(HudWidgetIds.Heading, HudAnchor.BottomCenter, -282, -8),
				Shown(HudWidgetIds.Speed, HudAnchor.BottomCenter, 282, -8),
				Shown(HudWidgetIds.Depth, HudAnchor.BottomRight, -8, -8));
			var everything = new List<HudPlacement>();
			foreach (var widget in _widgets)
			{
				if (widget.Id != HudWidgetIds.ClassicPanel)
				{
					everything.Add(widget.DefaultPlacement(true));
				}
			}

			AddPreset(HudPresets.Everything, everything.ToArray());
			Widgets = _widgets.AsReadOnly();
			PresetNames = Array.AsReadOnly(_presetNames);
		}

		public static HudWidgetRegistry Default { get; } = new HudWidgetRegistry();

		/// <summary>Every widget, in the order they are drawn (later ones on top).</summary>
		public IReadOnlyList<HudWidgetDefinition> Widgets { get; }

		/// <summary>console, classic, minimal, everything.</summary>
		public IReadOnlyList<string> PresetNames { get; }

		public HudWidgetDefinition Find(string id)
		{
			foreach (var widget in _widgets)
			{
				if (widget.Id == id)
				{
					return widget;
				}
			}

			return null;
		}

		public bool IsPreset(string name) => name != null && _presets.ContainsKey(name);

		/// <summary>A fresh copy of a preset; an unknown name is an error.</summary>
		public HudLayout Preset(string name)
		{
			if (name == null || !_presets.TryGetValue(name, out var preset))
			{
				throw new ArgumentException("No HUD preset named " + name + ".", nameof(name));
			}

			return preset.Copy();
		}

		/// <summary>A saved layout over its preset. Unknown widget ids are dropped with a warning; scales are clamped to the widget's limits.</summary>
		public HudLayout Resolve(HudLayoutEntry saved)
		{
			if (saved == null)
			{
				throw new ArgumentNullException(nameof(saved));
			}

			var basedOn = IsPreset(saved.basedOn) ? saved.basedOn.ToLowerInvariant() : HudPresets.FirstLaunch;
			var layout = Preset(basedOn).Renamed(saved.name, basedOn);
			if (saved.widgets == null)
			{
				return layout;
			}

			foreach (var entry in saved.widgets)
			{
				var widget = entry != null ? Find(entry.id) : null;
				if (widget == null)
				{
					Debug.LogWarning($"[FerrySim] HUD layout '{saved.name}': widget '{entry?.id}' is not in this build and was dropped.");
					continue;
				}

				var current = layout.Get(widget.Id);
				if (!widget.Movable)
				{
					layout.Set(current.WithVisible(entry.visible));
					continue;
				}

				if (!HudAnchors.TryParse(entry.anchor, out var anchor) || !Finite(entry.x) || !Finite(entry.y))
				{
					Debug.LogWarning($"[FerrySim] HUD layout '{saved.name}': widget '{entry.id}' has no valid place and keeps the preset's.");
					layout.Set(current.WithVisible(entry.visible));
					continue;
				}

				layout.Set(new HudPlacement(widget.Id, entry.visible, anchor, new Vector2(entry.x, entry.y), widget.ClampScale(entry.scale)));
			}

			return layout;
		}

		/// <summary>The saved form of <paramref name="layout"/>: its preset and every placement that differs from it.</summary>
		public HudLayoutEntry Differences(string name, HudLayout layout)
		{
			var basedOn = IsPreset(layout.BasedOn) ? layout.BasedOn.ToLowerInvariant() : HudPresets.FirstLaunch;
			var preset = Preset(basedOn);
			var entry = new HudLayoutEntry { name = name, basedOn = basedOn };
			foreach (var placement in layout.Placements)
			{
				if (preset.TryGet(placement.Id, out var original) && original.Equals(placement))
				{
					continue;
				}

				entry.widgets.Add(new HudWidgetEntry
				{
					id = placement.Id,
					visible = placement.Visible,
					anchor = HudAnchors.Id(placement.Anchor),
					x = placement.Offset.x,
					y = placement.Offset.y,
					scale = placement.Scale,
				});
			}

			return entry;
		}

		private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

		private void Add(HudWidgetDefinition widget) => _widgets.Add(widget);

		private (string id, bool custom, HudAnchor anchor, Vector2 offset) Shown(string id) => (id, false, default, default);

		private (string id, bool custom, HudAnchor anchor, Vector2 offset) Shown(string id, HudAnchor anchor, float x, float y) => (id, true, anchor, new Vector2(x, y));

		private void AddPreset(string name, params (string id, bool custom, HudAnchor anchor, Vector2 offset)[] shown)
		{
			var placements = new List<HudPlacement>();
			foreach (var widget in _widgets)
			{
				var placement = widget.DefaultPlacement(false);
				foreach (var item in shown)
				{
					if (item.id == widget.Id)
					{
						placement = item.custom ? new HudPlacement(widget.Id, true, item.anchor, item.offset, widget.DefaultScale) : widget.DefaultPlacement(true);
					}
				}

				placements.Add(placement);
			}

			_presets[name] = new HudLayout(name, name, placements);
		}

		private void AddPreset(string name, HudPlacement[] shown)
		{
			var placements = new List<HudPlacement>();
			foreach (var widget in _widgets)
			{
				var placement = widget.DefaultPlacement(false);
				foreach (var item in shown)
				{
					if (item.Id == widget.Id)
					{
						placement = item;
					}
				}

				placements.Add(placement);
			}

			_presets[name] = new HudLayout(name, name, placements);
		}
	}
}
