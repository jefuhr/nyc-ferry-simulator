using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.SettingsHud.PlayMode
{
	/// <summary>
	/// The HUD in a rendered panel. Failure modes guarded: the classic preset drawing anything the v0.4 panel did not; a
	/// widget placed somewhere other than its placement says once laid out; a drag writing layout properties every frame
	/// instead of only on the drop; a pop-out showing a stale or missing picture, or nothing at all without one; settings and
	/// a saved layout lost when the UI is torn down and built again from the same files.
	/// </summary>
	public class HudPlayModeTests
	{
		private HudTestRig _rig;
		private string _folder;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
			if (_folder != null && Directory.Exists(_folder))
			{
				Directory.Delete(_folder, true);
			}

			_folder = null;
		}

		[UnityTest]
		public IEnumerator TheClassicPreset_IsExactlyTheV04Panel()
		{
			_rig = new HudTestRig(1280, 720);
			yield return HudTestRig.Frames();
			var view = _rig.View;
			Assert.That(view.HudLayout.Current.Name, Is.EqualTo(HudPresets.Classic));
			Assert.That(view.ClassicPanelShown, Is.True);
			Assert.That(view.HudLayer.ShownWidgets, Is.Empty);
			Assert.That(view.Panel.worldBound, Is.EqualTo(new Rect(0, 420, 1280, 300)), "the panel's v0.4 place");
			var withLayer = _rig.Capture();

			// The same frame with the HUD layer removed altogether: only the v0.4 panel's own pixels remain.
			view.HudLayer.style.display = DisplayStyle.None;
			yield return HudTestRig.Frames();
			var bare = _rig.Capture();
			var a = withLayer.GetPixels32();
			var b = bare.GetPixels32();
			var differing = 0;
			for (var i = 0; i < a.Length; i++)
			{
				if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
				{
					differing++;
				}
			}

			Assert.That(differing, Is.Zero, "the classic preset adds or moves nothing");
			var vesselWidth = view.Panel.VesselName.layout.width;

			// And with everything v0.5 added taken out of the view (the layer, the menu and their style sheets), so it has
			// exactly the v0.4 view's children and sheets: the panel's pixels and text measurements are unchanged.
			view.HudLayer.RemoveFromHierarchy();
			view.Options.RemoveFromHierarchy();
			view.styleSheets.Remove(Resources.Load<StyleSheet>(WheelhouseView.HudStyleSheetResource));
			view.styleSheets.Remove(Resources.Load<StyleSheet>(WheelhouseView.OptionsStyleSheetResource));
			yield return HudTestRig.Frames();
			var v04 = _rig.Capture().GetPixels32();
			differing = 0;
			for (var i = 0; i < a.Length; i++)
			{
				if (a[i].r != v04[i].r || a[i].g != v04[i].g || a[i].b != v04[i].b || a[i].a != v04[i].a)
				{
					differing++;
				}
			}

			Assert.That(differing, Is.Zero, "the classic preset draws exactly what the v0.4 view draws");
			Assert.That(view.Panel.VesselName.layout.width, Is.EqualTo(vesselWidth), "text measures the same");
			TestContext.WriteLine($"vessel name '{view.Panel.VesselName.text}' measures {vesselWidth} px");
		}

		[UnityTest]
		public IEnumerator EveryShownWidget_IsWhereItsPlacementSays_InEveryPreset()
		{
			_rig = new HudTestRig(1280, 720);
			var view = _rig.View;
			foreach (var preset in HudWidgetRegistry.Default.PresetNames)
			{
				view.HudLayout.Choose(preset);
				yield return HudTestRig.Frames();
				foreach (var widget in view.HudLayer.ShownWidgets)
				{
					var placement = view.HudLayout.Current.Get(widget.Id);
					var expected = HudGeometry.VisualRect(placement, widget.Definition.Size, new Vector2(1280, 720));
					var actual = widget.Element.worldBound;
					Assert.That(actual.width, Is.EqualTo(expected.width).Within(0.6f), $"{preset} {widget.Id} width (Hud.uss and the registry agree)");
					Assert.That(actual.height, Is.EqualTo(expected.height).Within(0.6f), $"{preset} {widget.Id} height");
					Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.6f), $"{preset} {widget.Id} x");
					Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.6f), $"{preset} {widget.Id} y");
				}
			}
		}

		[UnityTest]
		public IEnumerator ADrag_PreviewsWithTranslate_AndWritesThePlacementOnlyOnTheDrop()
		{
			_rig = new HudTestRig(1280, 720);
			var view = _rig.View;
			view.HudLayout.Choose(HudPresets.Minimal);
			yield return HudTestRig.Frames();
			view.HudEditing = true;
			yield return HudTestRig.Frames();
			var heading = view.HudLayer.Widget(HudWidgetIds.Heading);
			var element = heading.Element;
			var start = element.worldBound;
			var margin = element.style.marginLeft;
			var grab = start.center;

			Pointer(element, EventType.MouseDown, grab);
			Pointer(element, EventType.MouseDrag, grab + new Vector2(-40, -150));
			yield return HudTestRig.Frames(2);
			Assert.That(element.style.marginLeft, Is.EqualTo(margin), "no layout property is written while dragging");
			Assert.That(element.worldBound.position.x, Is.EqualTo(start.x - 40).Within(0.6f), "the preview follows the pointer");
			Assert.That(element.worldBound.position.y, Is.EqualTo(start.y - 150).Within(0.6f));

			Pointer(element, EventType.MouseUp, grab + new Vector2(-40, -150));
			yield return HudTestRig.Frames(2);
			var placement = view.HudLayout.Current.Get(HudWidgetIds.Heading);
			Assert.That(placement.Anchor, Is.EqualTo(HudAnchor.BottomLeft), "its centre ended in the bottom-left ninth");
			Assert.That(element.worldBound.position.x, Is.EqualTo(start.x - 40).Within(0.6f));
			Assert.That(element.worldBound.position.y, Is.EqualTo(start.y - 150).Within(0.6f));
			Assert.That(element.style.translate.value.x.unit, Is.EqualTo(LengthUnit.Percent), "the preview translate is gone");
			Assert.That(view.HudLayout.Modified, Is.True);

			// Saving from the bar's commit method writes the layout and chooses it.
			Assert.That(view.HudLayer.EditBar.Save("dragged"), Is.True, view.HudLayer.EditBar.Status);
			Assert.That(view.Layouts.Find("dragged").widgets.Single().id, Is.EqualTo(HudWidgetIds.Heading));
			Assert.That(view.Settings.HudLayout, Is.EqualTo("dragged"));
		}

		[UnityTest]
		public IEnumerator PopOuts_ShowTheirScreen_OrNoSignal()
		{
			_rig = new HudTestRig(1280, 720);
			var view = _rig.View;
			view.HudLayout.Choose(HudPresets.Everything);
			var texture = new Texture2D(8, 8);
			var screens = new OneScreen("radar", texture);
			try
			{
				yield return HudTestRig.Frames();
				var radar = (ScreenWidget)view.HudLayer.Widget("screen:radar");
				var plotter = (ScreenWidget)view.HudLayer.Widget("screen:plotter");
				Assert.That(radar.HasSignal, Is.False, "nothing supplies a screen yet");
				Assert.That(radar.NoSignal.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
				view.ScreenSource = screens;
				yield return HudTestRig.Frames(2);
				Assert.That(radar.HasSignal, Is.True);
				Assert.That(radar.Picture.image, Is.SameAs(texture));
				Assert.That(radar.NoSignal.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
				Assert.That(plotter.HasSignal, Is.False);
				screens.Texture = null;
				yield return HudTestRig.Frames(2);
				Assert.That(radar.HasSignal, Is.False, "a screen that goes away shows NO SIGNAL again");
				Assert.That(radar.Picture.image, Is.Null);
			}
			finally
			{
				Object.Destroy(texture);
			}
		}

		[UnityTest]
		public IEnumerator SettingsAndASavedLayout_SurviveTheUIBeingBuiltAgainFromTheSameFiles()
		{
			_folder = Path.Combine(Path.GetTempPath(), "ferrysim-hud-restart-" + System.Guid.NewGuid().ToString("N"));
			var settingsPath = Path.Combine(_folder, SettingsStore.FileName);
			var layoutsPath = Path.Combine(_folder, HudLayoutStore.FileName);
			_rig = new HudTestRig(1280, 720, Loaded(new SettingsStore(settingsPath)), Loaded(new HudLayoutStore(layoutsPath)));
			yield return HudTestRig.Frames();
			var view = _rig.View;
			view.Options.DistanceUnit.Select((int)DistanceUnit.Metres);
			view.Options.MasterVolume.Adjust(-4);
			view.HudLayout.Choose(HudPresets.Minimal);
			view.HudEditing = true;
			view.HudLayout.Session.SetVisible(HudWidgetIds.Compass, true);
			Assert.That(view.HudLayer.EditBar.Save("bridge"), Is.True, view.HudLayer.EditBar.Status);
			view.HudEditing = false;
			_rig.Dispose();
			_rig = null;
			yield return null;

			_rig = new HudTestRig(1280, 720, Loaded(new SettingsStore(settingsPath)), Loaded(new HudLayoutStore(layoutsPath)));
			yield return HudTestRig.Frames();
			view = _rig.View;
			Assert.That(view.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
			Assert.That(view.Settings.MasterVolume, Is.EqualTo(0.8f).Within(1e-6f));
			Assert.That(view.HudLayout.Current.Name, Is.EqualTo("bridge"));
			Assert.That(view.HudLayer.ShownWidgets.Select(w => w.Id), Does.Contain(HudWidgetIds.Compass));
			Assert.That(view.ClassicPanelShown, Is.False);
			Assert.That(view.Panel.Depth.Unit.text, Is.EqualTo("m"));
		}

		private static SettingsStore Loaded(SettingsStore store)
		{
			Assert.That(store.Load(), Is.True, store.LastError);
			return store;
		}

		private static HudLayoutStore Loaded(HudLayoutStore store)
		{
			Assert.That(store.Load(), Is.True, store.LastError);
			return store;
		}

		/// <summary>A mouse event at a panel position, sent through the panel so picking and pointer capture work as for a real one.</summary>
		private static void Pointer(VisualElement anyElement, EventType type, Vector2 position)
		{
			var panel = anyElement.panel;
			var e = new Event { type = type, mousePosition = position, button = 0, clickCount = 1, delta = Vector2.zero };
			EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(e) : type == EventType.MouseUp ? (EventBase)PointerUpEvent.GetPooled(e) : PointerMoveEvent.GetPooled(e);
			using (evt)
			{
				panel.visualTree.SendEvent(evt);
			}
		}

		private sealed class OneScreen : IHudScreenSource
		{
			private readonly string _id;

			public OneScreen(string id, Texture texture)
			{
				_id = id;
				Texture = texture;
			}

			public Texture Texture { get; set; }

			public bool TryGetScreen(string screenId, out Texture texture)
			{
				texture = screenId == _id ? Texture : null;
				return texture != null;
			}
		}
	}
}
