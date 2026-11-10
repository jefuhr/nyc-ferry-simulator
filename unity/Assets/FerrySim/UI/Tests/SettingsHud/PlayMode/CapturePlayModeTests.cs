using System.Collections;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace FerrySim.UI.Tests.SettingsHud.PlayMode
{
	/// <summary>
	/// Captures every preset, edit mode and every options page at 1280×720 and 2560×1440, by day and by night, into
	/// artifacts/settings-options-hud/captures/, and checks the contrast of every visible text of the menu and of the
	/// edit-mode chrome against what is behind it, over a black and a white view: WCAG AA (4.5:1) by day; by night the
	/// palette's own floors (3:1 for main text, 2:1 for captions; PaletteTests). Failure modes guarded: a page or edit
	/// mode drawing text the night palette cannot carry, theme grey left on a control, a preset drawing nothing.
	/// </summary>
	public class CapturePlayModeTests
	{
		private HudTestRig _rig;
		private InputBindingService _bindings;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
			_bindings?.Dispose();
			_bindings = null;
		}

		[UnityTest]
		public IEnumerator Captures_At720p() => CaptureAll(1280, 720, "720p", checkContrast: true);

		[UnityTest]
		public IEnumerator Captures_At1440p() => CaptureAll(2560, 1440, "1440p", checkContrast: false);

		private IEnumerator CaptureAll(int width, int height, string size, bool checkContrast)
		{
			// A binding service of the defaults, not saved and not reading keys, so the Controls page lists every row.
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			_bindings = new InputBindingService(defaults);
			Object.Destroy(defaults);
			_rig = new HudTestRig(width, height, configure: ui => ui.ConfigureInput(_bindings, false));
			var view = _rig.View;
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				var tag = palette == PaletteKind.Day ? "day" : "night";
				var colors = InstrumentPalette.For(palette);
				var textFloor = palette == PaletteKind.Day ? 4.5 : 3.0;
				var otherFloor = palette == PaletteKind.Day ? 4.5 : 2.0;
				_rig.SetPalette(palette);
				foreach (var preset in HudWidgetRegistry.Default.PresetNames)
				{
					view.HudLayout.Choose(preset);
					yield return HudTestRig.Frames();
					// The console preset shows nothing over the view, by design.
					Object.Destroy(_rig.Screenshot($"hud-{preset}-{tag}-{size}.png", preset != HudPresets.Console));
				}

				view.HudLayout.Choose(HudPresets.Minimal);
				view.HudEditing = true;
				yield return HudTestRig.Frames();
				Object.Destroy(_rig.Screenshot($"hud-edit-{tag}-{size}.png"));
				if (checkContrast)
				{
					Assert.That(Contrast.CheckVisibleText(view.HudLayer.EditBar, colors, textFloor, otherFloor), Is.GreaterThan(5), "edit bar " + tag);
					foreach (var widget in view.HudLayer.ShownWidgets)
					{
						Assert.That(Contrast.CheckVisibleText(widget.EditOverlay, colors, textFloor, otherFloor), Is.EqualTo(1), "edit label of " + widget.Id + " " + tag);
					}
				}

				view.HudEditing = false;
				view.HudLayout.Choose(HudPresets.Classic);
				view.OptionsVisible = true;
				foreach (OptionsPage page in System.Enum.GetValues(typeof(OptionsPage)))
				{
					view.Options.ShowPage(page);
					yield return HudTestRig.Frames();
					Object.Destroy(_rig.Screenshot($"options-{page.ToString().ToLowerInvariant()}-{tag}-{size}.png"));
					if (checkContrast)
					{
						Assert.That(Contrast.CheckVisibleText(view.Options, colors, textFloor, otherFloor), Is.GreaterThan(8), $"options {page} {tag}");
					}
				}

				// A question showing (delete a saved layout), for its colors.
				view.HudLayout.Choose(HudPresets.Minimal);
				view.HudEditing = true;
				view.HudLayer.EditBar.Save("capture");
				view.HudEditing = false;
				view.OptionsVisible = true;
				view.Options.ShowPage(OptionsPage.Hud);
				view.Options.DeleteChosenLayout();
				yield return HudTestRig.Frames();
				Object.Destroy(_rig.Screenshot($"options-hud-delete-{tag}-{size}.png"));
				if (checkContrast)
				{
					Assert.That(Contrast.CheckVisibleText(view.Options, colors, textFloor, otherFloor), Is.GreaterThan(8), "delete question " + tag);
				}

				view.Options.ConfirmDelete(true);
				view.OptionsVisible = false;
			}
		}
	}
}
