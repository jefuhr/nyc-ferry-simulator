using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// The widgets and the presets. Failure modes guarded: a preset naming a widget that does not exist; a preset
	/// whose widgets overlap or leave the 1280×720 frame; a saved layout re-applying the whole preset instead of only its
	/// differences (so a later preset change would not reach it); a saved layout with a widget this build does not know
	/// (from a newer build, or a removed widget) failing whole instead of dropping that one widget with a warning.
	/// </summary>
	public class HudWidgetRegistryTests
	{
		private static readonly Vector2 Screen = new Vector2(1280, 720);
		private readonly HudWidgetRegistry _registry = HudWidgetRegistry.Default;

		[Test]
		public void EveryInstrumentAndScreen_IsAWidget_WithLimitsAroundItsDefaultScale()
		{
			var expected = new[]
			{
				HudWidgetIds.ClassicPanel, HudWidgetIds.Compass, HudWidgetIds.Heading, HudWidgetIds.Speed, HudWidgetIds.Depth, HudWidgetIds.Rudder,
				HudWidgetIds.PortEngine, HudWidgetIds.StarboardEngine, HudWidgetIds.Environment, HudWidgetIds.Clock, HudWidgetIds.TrafficAlarm,
				"screen:radar", "screen:plotter", "screen:engine-port", "screen:engine-starboard", "screen:alarms",
			};
			Assert.That(_registry.Widgets.Select(widget => widget.Id), Is.EquivalentTo(expected));
			foreach (var widget in _registry.Widgets)
			{
				Assert.That(widget.MinScale, Is.LessThanOrEqualTo(widget.DefaultScale), widget.Id);
				Assert.That(widget.MaxScale, Is.GreaterThanOrEqualTo(widget.DefaultScale), widget.Id);
				Assert.That(widget.MinScale, Is.GreaterThan(0f), widget.Id);
				Assert.That(widget.Size.x, Is.GreaterThan(0f), widget.Id);
				Assert.That(widget.Title, Is.Not.Empty, widget.Id);
			}

			Assert.That(_registry.Find(HudWidgetIds.ClassicPanel).Movable, Is.False, "the v0.4 panel stays where it was");
			Assert.That(_registry.Find("screen:radar").IsScreen, Is.True);
		}

		[TestCase(HudPresets.Console)]
		[TestCase(HudPresets.Classic)]
		[TestCase(HudPresets.Minimal)]
		[TestCase(HudPresets.Everything)]
		public void EveryPreset_Resolves_ToAPlacementForEveryWidget_InsideTheFrame_WithoutOverlaps(string preset)
		{
			Assert.That(_registry.IsPreset(preset), Is.True);
			var layout = _registry.Preset(preset);
			Assert.That(layout.Name, Is.EqualTo(preset));
			Assert.That(layout.BasedOn, Is.EqualTo(preset));
			Assert.That(layout.Placements.Select(p => p.Id), Is.EquivalentTo(_registry.Widgets.Select(widget => widget.Id)));
			var visible = layout.Placements.Where(p => p.Visible).ToArray();
			foreach (var placement in visible)
			{
				var rect = HudGeometry.VisualRect(placement, _registry.Find(placement.Id).Size, Screen);
				Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(0f), placement.Id);
				Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(0f), placement.Id);
				Assert.That(rect.xMax, Is.LessThanOrEqualTo(Screen.x), placement.Id);
				Assert.That(rect.yMax, Is.LessThanOrEqualTo(Screen.y), placement.Id);
				foreach (var other in visible.Where(o => o.Id != placement.Id))
				{
					var otherRect = HudGeometry.VisualRect(other, _registry.Find(other.Id).Size, Screen);
					Assert.That(rect.Overlaps(otherRect), Is.False, $"{preset}: {placement.Id} {rect} overlaps {other.Id} {otherRect}");
				}
			}
		}

		[Test]
		public void ThePresets_ShowWhatTheySay()
		{
			Assert.That(Visible(HudPresets.Console), Is.Empty, "no panel, and pop-outs only where the player adds them");
			Assert.That(Visible(HudPresets.Classic), Is.EqualTo(new[] { HudWidgetIds.ClassicPanel }));
			Assert.That(Visible(HudPresets.Minimal), Is.EquivalentTo(new[] { HudWidgetIds.Heading, HudWidgetIds.Speed, HudWidgetIds.Depth, HudWidgetIds.Rudder }));
			Assert.That(Visible(HudPresets.Everything), Is.EquivalentTo(_registry.Widgets.Select(w => w.Id).Where(id => id != HudWidgetIds.ClassicPanel)));
			Assert.That(_registry.PresetNames, Is.EqualTo(new[] { HudPresets.Console, HudPresets.Classic, HudPresets.Minimal, HudPresets.Everything }));
		}

		[Test]
		public void ASavedLayout_AppliesOnlyItsDifferences_OverItsPreset()
		{
			var saved = new HudLayoutEntry
			{
				name = "mine",
				basedOn = HudPresets.Minimal,
				widgets = { new HudWidgetEntry { id = HudWidgetIds.Compass, visible = true, anchor = "top-right", x = -20f, y = 30f, scale = 0.8f } },
			};
			var layout = _registry.Resolve(saved);
			Assert.That(layout.Name, Is.EqualTo("mine"));
			Assert.That(layout.BasedOn, Is.EqualTo(HudPresets.Minimal));
			var compass = layout.Get(HudWidgetIds.Compass);
			Assert.That(compass.Visible, Is.True);
			Assert.That(compass.Anchor, Is.EqualTo(HudAnchor.TopRight));
			Assert.That(compass.Offset, Is.EqualTo(new Vector2(-20f, 30f)));
			Assert.That(compass.Scale, Is.EqualTo(0.8f));
			var preset = _registry.Preset(HudPresets.Minimal);
			foreach (var placement in layout.Placements.Where(p => p.Id != HudWidgetIds.Compass))
			{
				Assert.That(placement, Is.EqualTo(preset.Get(placement.Id)), placement.Id + " comes from the preset");
			}
		}

		[Test]
		public void Differences_RecordsOnlyWhatDiffersFromThePreset_AndResolvesBackToTheSameLayout()
		{
			var layout = _registry.Preset(HudPresets.Classic).Copy();
			layout.Set(layout.Get(HudWidgetIds.Depth).WithVisible(true).WithOffset(new Vector2(-8f, -320f)));
			layout.Set(layout.Get(HudWidgetIds.ClassicPanel).WithVisible(false));
			var entry = _registry.Differences("mine", layout);
			Assert.That(entry.name, Is.EqualTo("mine"));
			Assert.That(entry.basedOn, Is.EqualTo(HudPresets.Classic));
			Assert.That(entry.widgets.Select(w => w.id), Is.EquivalentTo(new[] { HudWidgetIds.Depth, HudWidgetIds.ClassicPanel }));
			var back = _registry.Resolve(entry);
			foreach (var placement in layout.Placements)
			{
				Assert.That(back.Get(placement.Id), Is.EqualTo(placement), placement.Id);
			}
		}

		[Test]
		public void AnUnknownWidgetId_IsDroppedWithAWarning_AndTheRestApplies()
		{
			var saved = new HudLayoutEntry
			{
				name = "from-the-future",
				basedOn = HudPresets.Classic,
				widgets =
				{
					new HudWidgetEntry { id = "sonar", visible = true, anchor = "top-left", x = 0f, y = 0f, scale = 1f },
					new HudWidgetEntry { id = HudWidgetIds.Clock, visible = true, anchor = "top-left", x = 8f, y = 8f, scale = 1f },
				},
			};
			LogAssert.Expect(LogType.Warning, new Regex("from-the-future.*sonar"));
			var layout = _registry.Resolve(saved);
			Assert.That(layout.TryGet("sonar", out _), Is.False);
			Assert.That(layout.Get(HudWidgetIds.Clock).Visible, Is.True);
			Assert.That(layout.Get(HudWidgetIds.ClassicPanel).Visible, Is.True);
		}

		[Test]
		public void ScaleOutsideTheWidgetsLimits_IsClampedWhenResolved()
		{
			var compass = _registry.Find(HudWidgetIds.Compass);
			var saved = new HudLayoutEntry
			{
				name = "huge",
				basedOn = HudPresets.Console,
				widgets = { new HudWidgetEntry { id = HudWidgetIds.Compass, visible = true, anchor = "middle-center", x = 0f, y = 0f, scale = 40f } },
			};
			Assert.That(_registry.Resolve(saved).Get(HudWidgetIds.Compass).Scale, Is.EqualTo(compass.MaxScale));
		}

		private string[] Visible(string preset) => _registry.Preset(preset).Placements.Where(p => p.Visible).Select(p => p.Id).ToArray();
	}
}
