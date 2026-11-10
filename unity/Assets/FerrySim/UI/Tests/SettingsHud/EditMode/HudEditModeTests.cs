using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// HUD edit mode, through its explicit commit methods (EditMode tests have no rendered panel and no pointer).
	/// Failure modes guarded: a drop landing somewhere other than where the widget was let go; snapping that pulls from
	/// too far, misses an anchor or another widget's edge, or flips the anchor so the offset no longer means what it
	/// says; a scale escaping its limits; reset touching other widgets; a save writing the whole preset, under a preset's
	/// name, or not choosing the saved layout; a layout and the settings not surviving a restart; edit mode pausing
	/// anything.
	/// </summary>
	public class HudEditModeTests
	{
		private static readonly Vector2 Screen = new Vector2(1280, 720);
		private readonly HudWidgetRegistry _registry = HudWidgetRegistry.Default;
		private TempFolder _folder;

		[SetUp]
		public void SetUp() => _folder = new TempFolder();

		[TearDown]
		public void TearDown() => _folder.Dispose();

		private HudEditSession Session(string preset = HudPresets.Minimal) =>
			new HudEditSession(_registry, _registry.Preset(preset), Screen, id => _registry.Find(id).Size);

		[Test]
		public void Drag_MovesTheWidgetByThePointerDelta_AndOnlyWritesItOnDrop()
		{
			var session = Session();
			var before = session.Layout.Get(HudWidgetIds.Heading);
			var start = session.VisualRect(HudWidgetIds.Heading);
			session.BeginDrag(HudWidgetIds.Heading, new Vector2(400, 650));
			var preview = session.DragTo(new Vector2(380, 450));
			Assert.That(preview, Is.EqualTo(new Vector2(-20, -200)), "the preview translation");
			Assert.That(session.Layout.Get(HudWidgetIds.Heading), Is.EqualTo(before), "nothing is written while dragging");
			session.Drop(new Vector2(380, 450));
			var moved = session.VisualRect(HudWidgetIds.Heading);
			Assert.That(moved.position, Is.EqualTo(start.position + new Vector2(-20, -200)));
			Assert.That(session.Dragging, Is.False);
		}

		[Test]
		public void CancelDrag_LeavesTheWidgetWhereItWas()
		{
			var session = Session();
			var before = session.Layout.Get(HudWidgetIds.Speed);
			session.BeginDrag(HudWidgetIds.Speed, new Vector2(900, 650));
			session.DragTo(new Vector2(100, 100));
			session.CancelDrag();
			Assert.That(session.Layout.Get(HudWidgetIds.Speed), Is.EqualTo(before));
		}

		[Test]
		public void ADrop_TakesTheNearestOfTheNineAnchors()
		{
			var session = Session();
			var start = session.VisualRect(HudWidgetIds.Heading);
			// Into the top-right ninth of the frame, clear of every snap line.
			var grab = start.center;
			session.BeginDrag(HudWidgetIds.Heading, grab);
			session.Drop(new Vector2(1060, 120));
			var placed = session.Layout.Get(HudWidgetIds.Heading);
			Assert.That(placed.Anchor, Is.EqualTo(HudAnchor.TopRight));
			var rect = session.VisualRect(HudWidgetIds.Heading);
			Assert.That(rect.center.x, Is.EqualTo(1060).Within(0.01f));
			Assert.That(rect.center.y, Is.EqualTo(120).Within(0.01f));
		}

		[Test]
		public void ADrop_NearAnAnchorEdge_SnapsToTheMargin()
		{
			var session = Session();
			var rect = session.VisualRect(HudWidgetIds.Depth);
			session.BeginDrag(HudWidgetIds.Depth, rect.position);
			// 5 px from the left margin and 4 px from the top margin: both within the snap distance.
			session.Drop(new Vector2(HudGeometry.EdgeMargin + 5, HudGeometry.EdgeMargin + 4));
			var snapped = session.VisualRect(HudWidgetIds.Depth);
			Assert.That(snapped.position, Is.EqualTo(new Vector2(HudGeometry.EdgeMargin, HudGeometry.EdgeMargin)));
			var placement = session.Layout.Get(HudWidgetIds.Depth);
			Assert.That(placement.Anchor, Is.EqualTo(HudAnchor.TopLeft));
			Assert.That(placement.Offset, Is.EqualTo(new Vector2(HudGeometry.EdgeMargin, HudGeometry.EdgeMargin)));
		}

		[Test]
		public void ADrop_NearTheCentre_SnapsToTheCentreLine()
		{
			var session = Session();
			var rect = session.VisualRect(HudWidgetIds.Depth);
			session.BeginDrag(HudWidgetIds.Depth, rect.center);
			session.Drop(new Vector2(Screen.x / 2 + 6, 300));
			Assert.That(session.VisualRect(HudWidgetIds.Depth).center.x, Is.EqualTo(Screen.x / 2));
			Assert.That(session.Layout.Get(HudWidgetIds.Depth).Anchor, Is.EqualTo(HudAnchor.MiddleCenter));
		}

		[Test]
		public void ADrop_NearAnotherWidgetsEdge_SnapsAgainstIt_ButNotFromFurtherThanTheSnapDistance()
		{
			var session = Session();
			var rudder = session.VisualRect(HudWidgetIds.Rudder);
			var depth = session.VisualRect(HudWidgetIds.Depth);
			// Depth's left edge 5 px right of the rudder's left edge: the two left edges line up.
			session.BeginDrag(HudWidgetIds.Depth, depth.position);
			session.Drop(new Vector2(rudder.xMin + 5, rudder.yMin - 195));
			var snapped = session.VisualRect(HudWidgetIds.Depth);
			Assert.That(snapped.xMin, Is.EqualTo(rudder.xMin));

			// 20 px away: left alone.
			session.BeginDrag(HudWidgetIds.Depth, snapped.position);
			session.Drop(new Vector2(rudder.xMin + 20, 300));
			Assert.That(session.VisualRect(HudWidgetIds.Depth).xMin, Is.EqualTo(rudder.xMin + 20));
		}

		[Test]
		public void HudGeometry_SnapsToAnotherRectsEdges()
		{
			var others = new[] { new Rect(100, 100, 200, 50) };
			var snapped = HudGeometry.Snap(new Rect(305, 157, 80, 40), Screen, others);
			Assert.That(snapped.position, Is.EqualTo(new Vector2(300, 150)), "left to its right edge, top to its bottom edge");
			var aligned = HudGeometry.Snap(new Rect(103, 400, 80, 40), Screen, others);
			Assert.That(aligned.xMin, Is.EqualTo(100), "left edges line up");
			var free = HudGeometry.Snap(new Rect(330, 400, 80, 40), Screen, others);
			Assert.That(free.position, Is.EqualTo(new Vector2(330, 400)));
		}

		[Test]
		public void Scale_StaysWithinTheWidgetsLimits_AndKeepsTheAnchorPointStill()
		{
			var session = Session();
			var definition = _registry.Find(HudWidgetIds.Rudder);
			var anchorPoint = HudGeometry.AnchorPoint(session.Layout.Get(HudWidgetIds.Rudder), Screen);
			session.SetScale(HudWidgetIds.Rudder, 99f);
			Assert.That(session.Layout.Get(HudWidgetIds.Rudder).Scale, Is.EqualTo(definition.MaxScale));
			session.SetScale(HudWidgetIds.Rudder, 0.01f);
			Assert.That(session.Layout.Get(HudWidgetIds.Rudder).Scale, Is.EqualTo(definition.MinScale));
			session.SetScale(HudWidgetIds.Rudder, 1f);
			session.StepScale(HudWidgetIds.Rudder, 2);
			Assert.That(session.Layout.Get(HudWidgetIds.Rudder).Scale, Is.EqualTo(1f + 2 * HudEditSession.ScaleStep).Within(1e-5f));
			Assert.That(HudGeometry.AnchorPoint(session.Layout.Get(HudWidgetIds.Rudder), Screen), Is.EqualTo(anchorPoint));
			var rect = session.VisualRect(HudWidgetIds.Rudder);
			Assert.That(rect.width, Is.EqualTo(definition.Size.x * (1f + 2 * HudEditSession.ScaleStep)).Within(0.01f));
		}

		[Test]
		public void TheClassicPanel_CanBeHiddenButNotMovedOrScaled()
		{
			var session = Session(HudPresets.Classic);
			var before = session.Layout.Get(HudWidgetIds.ClassicPanel);
			Assert.That(session.BeginDrag(HudWidgetIds.ClassicPanel, new Vector2(640, 600)), Is.False);
			session.SetScale(HudWidgetIds.ClassicPanel, 1.5f);
			Assert.That(session.Layout.Get(HudWidgetIds.ClassicPanel), Is.EqualTo(before));
			session.SetVisible(HudWidgetIds.ClassicPanel, false);
			Assert.That(session.Layout.Get(HudWidgetIds.ClassicPanel).Visible, Is.False);
		}

		[Test]
		public void HideAndShow_ChangeOnlyThatWidget_AndRaisePlacementChanged()
		{
			var session = Session();
			string changed = null;
			session.PlacementChanged += id => changed = id;
			session.SetVisible(HudWidgetIds.Compass, true);
			Assert.That(changed, Is.EqualTo(HudWidgetIds.Compass));
			Assert.That(session.Layout.Get(HudWidgetIds.Compass).Visible, Is.True);
			Assert.That(session.Layout.Get(HudWidgetIds.Compass).Anchor, Is.EqualTo(_registry.Find(HudWidgetIds.Compass).DefaultAnchor), "a widget shown for the first time takes its default place");
			session.SetVisible(HudWidgetIds.Heading, false);
			Assert.That(session.Layout.Get(HudWidgetIds.Heading).Visible, Is.False);
			Assert.That(session.Layout.Get(HudWidgetIds.Speed).Visible, Is.True);
		}

		[Test]
		public void ResetWidget_RestoresItsPresetPlacement_AndResetLayoutRestoresAll()
		{
			var session = Session();
			var preset = _registry.Preset(HudPresets.Minimal);
			session.SetScale(HudWidgetIds.Heading, 1.3f);
			session.SetScale(HudWidgetIds.Speed, 1.3f);
			session.SetVisible(HudWidgetIds.Clock, true);
			session.ResetWidget(HudWidgetIds.Heading);
			Assert.That(session.Layout.Get(HudWidgetIds.Heading), Is.EqualTo(preset.Get(HudWidgetIds.Heading)));
			Assert.That(session.Layout.Get(HudWidgetIds.Speed).Scale, Is.EqualTo(1.3f), "reset one leaves the others");
			session.ResetLayout();
			foreach (var placement in preset.Placements)
			{
				Assert.That(session.Layout.Get(placement.Id), Is.EqualTo(placement), placement.Id);
			}
		}

		[Test]
		public void SaveAs_WritesOnlyTheDifferences_ChoosesTheLayout_AndRefusesPresetNames()
		{
			var settings = new SettingsStore(_folder.File(SettingsStore.FileName));
			var layouts = new HudLayoutStore(_folder.File(HudLayoutStore.FileName));
			settings.Load();
			layouts.Load();
			var hud = new HudController(_registry, settings, layouts);
			Assert.That(hud.Current.Name, Is.EqualTo(HudPresets.Classic));
			hud.Choose(HudPresets.Minimal);
			hud.BeginEdit(Screen, id => _registry.Find(id).Size);
			Assert.That(hud.Editing, Is.True);
			hud.Session.SetVisible(HudWidgetIds.Clock, true);
			hud.Session.SetScale(HudWidgetIds.Depth, 1.2f);

			Assert.That(hud.SaveAs("Classic", out var refusal), Is.False);
			Assert.That(refusal, Is.Not.Empty);
			Assert.That(hud.SaveAs("  ", out refusal), Is.False);
			Assert.That(hud.SaveAs("harbor", out var problem), Is.True, problem);
			var saved = layouts.Find("harbor");
			Assert.That(saved.basedOn, Is.EqualTo(HudPresets.Minimal));
			Assert.That(saved.widgets.Select(w => w.id), Is.EquivalentTo(new[] { HudWidgetIds.Clock, HudWidgetIds.Depth }));
			Assert.That(settings.HudLayout, Is.EqualTo("harbor"));
			Assert.That(hud.Current.Name, Is.EqualTo("harbor"));
			Assert.That(hud.Choices, Is.EqualTo(new[] { HudPresets.Console, HudPresets.Classic, HudPresets.Minimal, HudPresets.Everything, "harbor" }));
		}

		[Test]
		public void LeavingEditModeWithoutSaving_KeepsTheChangesForTheSession_ButNotOnDisk()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var hud = new HudController(_registry, settings, layouts);
			var raised = new System.Collections.Generic.List<bool>();
			hud.EditModeChanged += raised.Add;
			hud.BeginEdit(Screen, id => _registry.Find(id).Size);
			hud.Session.SetVisible(HudWidgetIds.Compass, true);
			hud.EndEdit();
			Assert.That(raised, Is.EqualTo(new[] { true, false }));
			Assert.That(hud.Current.Get(HudWidgetIds.Compass).Visible, Is.True);
			Assert.That(layouts.Layouts, Is.Empty);
			Assert.That(hud.Modified, Is.True);
		}

		[Test]
		public void SettingsAndASavedLayout_SurviveARestart()
		{
			var settingsPath = _folder.File(SettingsStore.FileName);
			var layoutsPath = _folder.File(HudLayoutStore.FileName);
			{
				var settings = new SettingsStore(settingsPath);
				var layouts = new HudLayoutStore(layoutsPath);
				settings.Load();
				layouts.Load();
				var hud = new HudController(_registry, settings, layouts);
				settings.DistanceUnit = DistanceUnit.Metres;
				settings.MasterVolume = 0.3f;
				hud.Choose(HudPresets.Everything);
				hud.BeginEdit(Screen, id => _registry.Find(id).Size);
				hud.Session.SetVisible("screen:radar", false);
				Assert.That(hud.SaveAs("bridge", out var problem), Is.True, problem);
				hud.EndEdit();
				hud.Dispose();
			}

			var reloadedSettings = new SettingsStore(settingsPath);
			var reloadedLayouts = new HudLayoutStore(layoutsPath);
			Assert.That(reloadedSettings.Load(), Is.True, reloadedSettings.LastError);
			Assert.That(reloadedLayouts.Load(), Is.True, reloadedLayouts.LastError);
			var restarted = new HudController(_registry, reloadedSettings, reloadedLayouts);
			Assert.That(reloadedSettings.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
			Assert.That(reloadedSettings.MasterVolume, Is.EqualTo(0.3f));
			Assert.That(restarted.Current.Name, Is.EqualTo("bridge"));
			Assert.That(restarted.Current.Get("screen:radar").Visible, Is.False);
			Assert.That(restarted.Current.Get("screen:plotter").Visible, Is.True);
		}

		[Test]
		public void DeletingTheChosenLayout_FallsBackToItsPreset()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var hud = new HudController(_registry, settings, layouts);
			hud.Choose(HudPresets.Minimal);
			hud.BeginEdit(Screen, id => _registry.Find(id).Size);
			hud.Session.SetVisible(HudWidgetIds.Clock, true);
			hud.SaveAs("mine", out _);
			hud.EndEdit();
			Assert.That(hud.DeleteSaved("mine"), Is.True);
			Assert.That(settings.HudLayout, Is.EqualTo(HudPresets.Minimal));
			Assert.That(hud.Current.Name, Is.EqualTo(HudPresets.Minimal));
			Assert.That(hud.DeleteSaved(HudPresets.Minimal), Is.False, "presets cannot be deleted");
		}

		[Test]
		public void AChosenLayoutThatNoLongerExists_FallsBackToTheFirstLaunchPreset()
		{
			var settings = SettingsStore.InMemory();
			settings.HudLayout = "gone";
			UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("gone"));
			var hud = new HudController(_registry, settings, HudLayoutStore.InMemory());
			Assert.That(hud.Current.Name, Is.EqualTo(HudPresets.FirstLaunch));
		}
	}
}
