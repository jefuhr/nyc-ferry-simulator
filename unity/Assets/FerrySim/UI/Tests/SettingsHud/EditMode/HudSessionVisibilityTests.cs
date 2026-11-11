using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UIElements;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// Showing or hiding a widget for the session only (a display key popping a console screen out onto the HUD).
	/// Failure modes guarded: the toggle counting as an edit (the layout marked modified, the setting or the layouts file
	/// changed, or the toggle written by a later save); the toggle surviving a new choice of layout or leaking into edit
	/// mode; a listener not told, or told when nothing changed; the layer drawing the layout's visibility instead of the
	/// effective one; an unknown widget or a disposed controller doing anything.
	/// </summary>
	public class HudSessionVisibilityTests
	{
		private static readonly string Radar = HudWidgetIds.Screen("radar");
		private static readonly string Plotter = HudWidgetIds.Screen("plotter");
		private SettingsStore _settings;
		private HudLayoutStore _layouts;
		private HudController _hud;
		private List<string> _changes;

		[SetUp]
		public void SetUp()
		{
			_settings = SettingsStore.InMemory();
			_layouts = HudLayoutStore.InMemory();
			_settings.HudLayout = HudPresets.Classic;
			_hud = new HudController(HudWidgetRegistry.Default, _settings, _layouts);
			_changes = new List<string>();
			_hud.LayoutChanged += id => _changes.Add(id ?? "<all>");
		}

		[TearDown]
		public void TearDown() => _hud.Dispose();

		[Test]
		public void ShowingAHiddenWidget_IsNotAnEdit()
		{
			Assert.That(_hud.IsShown(Radar), Is.False, "classic hides the radar pop-out");
			Assert.That(_hud.SetSessionShown(Radar, true), Is.True);
			Assert.That(_hud.IsShown(Radar), Is.True);
			Assert.That(_hud.IsShown(Plotter), Is.False, "only the one asked for");
			Assert.That(_hud.Current.IsVisible(Radar), Is.False, "the layout itself is untouched");
			Assert.That(_hud.Modified, Is.False);
			Assert.That(_settings.HudLayout, Is.EqualTo(HudPresets.Classic));
			Assert.That(_layouts.Layouts, Is.Empty);
			Assert.That(_changes, Is.EqualTo(new[] { Radar }));

			// The same again tells nobody.
			Assert.That(_hud.SetSessionShown(Radar, true), Is.True);
			Assert.That(_changes, Has.Count.EqualTo(1));
		}

		[Test]
		public void Toggle_FlipsTheEffectiveVisibility_AndBackToTheLayoutsOwn()
		{
			_hud.ToggleSessionShown(Radar);
			Assert.That(_hud.IsShown(Radar), Is.True);
			_hud.ToggleSessionShown(Radar);
			Assert.That(_hud.IsShown(Radar), Is.False);
			Assert.That(_changes, Is.EqualTo(new[] { Radar, Radar }));

			// A widget the layout shows can be hidden for the session, and shown again.
			_settings.HudLayout = HudPresets.Everything;
			_changes.Clear();
			Assert.That(_hud.IsShown(Radar), Is.True);
			_hud.ToggleSessionShown(Radar);
			Assert.That(_hud.IsShown(Radar), Is.False);
			Assert.That(_hud.Current.IsVisible(Radar), Is.True);
			_hud.ToggleSessionShown(Radar);
			Assert.That(_hud.IsShown(Radar), Is.True);
			Assert.That(_hud.Modified, Is.False);
		}

		[Test]
		public void ChoosingALayout_ClearsTheSessionToggles()
		{
			_hud.SetSessionShown(Radar, true);
			Assert.That(_hud.IsShown(Radar), Is.True);
			_hud.Choose(HudPresets.Minimal);
			Assert.That(_hud.IsShown(Radar), Is.False);

			_hud.SetSessionShown(Radar, true);
			_hud.Choose(HudPresets.Minimal);
			Assert.That(_hud.IsShown(Radar), Is.False, "choosing the same layout again goes back to what it says");
		}

		[Test]
		public void EditMode_ShowsTheLayoutItself_AndRefusesSessionToggles()
		{
			_hud.SetSessionShown(Radar, true);
			_changes.Clear();
			_hud.BeginEdit(new Vector2(1280, 720), null);
			Assert.That(_hud.IsShown(Radar), Is.False, "edit mode shows what would be saved");
			Assert.That(_changes, Does.Contain(Radar));
			Assert.That(_hud.SetSessionShown(Radar, true), Is.False);
			Assert.That(_hud.IsShown(Radar), Is.False);
			_hud.EndEdit();
			Assert.That(_hud.Modified, Is.False);
		}

		[Test]
		public void ASave_DoesNotWriteASessionToggle()
		{
			_hud.SetSessionShown(Radar, true);
			Assert.That(_hud.IsShown(Radar), Is.True);
			Assert.That(_hud.SaveAs("mine", out var problem), Is.True, problem);
			Assert.That(_hud.IsShown(Radar), Is.False, "what is shown after a save is what was saved");
			var reopened = new HudController(HudWidgetRegistry.Default, _settings, _layouts);
			try
			{
				Assert.That(_settings.HudLayout, Is.EqualTo("mine"));
				Assert.That(reopened.Current.IsVisible(Radar), Is.False);
				Assert.That(reopened.IsShown(Radar), Is.False);
			}
			finally
			{
				reopened.Dispose();
			}
		}

		[Test]
		public void AnUnknownWidget_OrADisposedController_DoesNothing()
		{
			Assert.That(_hud.SetSessionShown("no-such-widget", true), Is.False);
			Assert.That(_hud.SetSessionShown(null, true), Is.False);
			Assert.That(_hud.IsShown("no-such-widget"), Is.False);
			Assert.That(_changes, Is.Empty);
			_hud.Dispose();
			Assert.That(_hud.SetSessionShown(Radar, true), Is.False);
			Assert.That(_hud.IsShown(Radar), Is.False);
			Assert.That(_changes, Is.Empty);
		}

		[Test]
		public void AToggle_OnAWarmView_AllocatesNothing()
		{
			// A display key toggles a pop-out during a run: once the widget exists, showing and hiding it must cost no garbage.
			var view = new WheelhouseView(_settings, _layouts);
			try
			{
				var hud = view.HudLayout;
				for (var i = 0; i < 6; i++)
				{
					hud.ToggleSessionShown(Radar);
					hud.ToggleSessionShown(Plotter);
				}

				Assert.That(hud.IsShown(Radar), Is.False);
				Assert.That(() => hud.ToggleSessionShown(Radar), Is.Not.AllocatingGCMemory(), "show");
				Assert.That(hud.IsShown(Radar), Is.True);
				Assert.That(() => hud.ToggleSessionShown(Radar), Is.Not.AllocatingGCMemory(), "hide");
				Assert.That(() => { hud.SetSessionShown(Plotter, true); }, Is.Not.AllocatingGCMemory(), "show a second widget");
			}
			finally
			{
				view.Dispose();
			}
		}

		[Test]
		public void TheLayer_DrawsTheEffectiveVisibility_AtTheLayoutsPlace()
		{
			var view = new WheelhouseView(_settings, _layouts);
			try
			{
				var hud = view.HudLayout;
				var layer = view.HudLayer;
				Assert.That(layer.Widget(Radar)?.Element.style.display.value ?? DisplayStyle.None, Is.EqualTo(DisplayStyle.None));

				hud.SetSessionShown(Radar, true);
				var widget = layer.Widget(Radar);
				Assert.That(widget, Is.Not.Null);
				Assert.That(widget.Element.style.display.value, Is.EqualTo(DisplayStyle.Flex));
				Assert.That(layer.ShownWidgets, Has.Member(widget));
				var place = hud.Current.Get(Radar);
				Assert.That(widget.Element.style.marginLeft.value.value, Is.EqualTo(place.Offset.x));
				Assert.That(widget.Element.style.marginTop.value.value, Is.EqualTo(place.Offset.y));

				hud.SetSessionShown(Radar, false);
				Assert.That(widget.Element.style.display.value, Is.EqualTo(DisplayStyle.None));
				Assert.That(layer.ShownWidgets, Has.No.Member(widget));

				// The classic panel is a widget like any other here.
				Assert.That(layer.ClassicPanelShown, Is.True);
				hud.SetSessionShown(HudWidgetIds.ClassicPanel, false);
				Assert.That(layer.ClassicPanelShown, Is.False);
				hud.Choose(HudPresets.Classic);
				Assert.That(layer.ClassicPanelShown, Is.True);
			}
			finally
			{
				view.Dispose();
			}
		}
	}
}
