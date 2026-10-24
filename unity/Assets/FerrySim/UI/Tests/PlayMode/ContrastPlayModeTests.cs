using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Day text contrast as rendered (review finding 6), complementing the palette pairs in PaletteTests.
	/// Guards against: HUD captions washed out by bright sky behind a translucent backing (measured 3.16:1
	/// in the HUD screenshot), and any visible day label, button or badge drawn on a background it does not
	/// reach WCAG AA (4.5:1) against. Writes artifacts/unity/ui-hud-day-bright.png.
	/// </summary>
	public class ContrastPlayModeTests
	{
		private const double AA = 4.5;

		private UITestRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator Hud_KeepsItsTextContrast_OverABrightBackdrop()
		{
			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			var view = _rig.View;
			view.PanelVisible = false;
			view.HudVisible = true;
			_rig.Settings.colorClearValue = Color.white;
			yield return UITestRig.Frames();

			var image = _rig.Screenshot("ui-hud-day-bright.png");
			var hud = view.Hud.worldBound;
			// Bare backing: inside the border and the rounded corner, in the padding clear of any text.
			var backing = image.GetPixel(Mathf.RoundToInt(hud.xMin + 5f), image.height - 1 - Mathf.RoundToInt(hud.yMin + 4f));
			Object.Destroy(image);
			var day = InstrumentPalette.Day;
			foreach (var (what, color) in new[] { ("captions and units", day.TextDim), ("values", day.Text), ("SHIFT", day.Shifting) })
			{
				var ratio = Contrast.Ratio(color, backing);
				Assert.That(ratio, Is.GreaterThanOrEqualTo(AA), $"HUD {what} {color} on the rendered backing {backing} over white: {ratio:F2}:1");
			}
		}

		[UnityTest]
		public IEnumerator EveryVisibleDayText_MeetsWcagAA_OverWhatIsBehindIt()
		{
			_rig = new UITestRig(1280, 720, devToolbar: true);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			_rig.View.HudVisible = true;
			yield return UITestRig.Frames();
			Assert.That(CheckVisibleText(_rig.View), Is.GreaterThan(60), "panel, HUD and toolbar");

			Assert.That(_rig.View.Debrief.TryLoad(UIRepoPaths.SampleCrashStopCsv), Is.True, _rig.View.Debrief.ErrorText);
			_rig.View.DebriefVisible = true;
			yield return UITestRig.Frames();
			Assert.That(CheckVisibleText(_rig.View.Debrief), Is.GreaterThan(40), "debrief");
		}

		/// <summary>
		/// Checks every visible text element under <paramref name="root"/> against the backgrounds of its
		/// ancestors blended over the 3D view, taking the worse of a black and a white view where the
		/// backgrounds let it through. Returns how many were checked.
		/// </summary>
		private static int CheckVisibleText(VisualElement root)
		{
			var checkedCount = 0;
			root.Query<TextElement>().ForEach(text =>
			{
				if (string.IsNullOrEmpty(text.text) || text.GetFirstAncestorOfType<TextField>() != null || !IsVisible(text))
				{
					return;
				}

				checkedCount++;
				var foreground = Contrast.Luminance(text.resolvedStyle.color);
				var worst = Math.Min(
					Contrast.Ratio(foreground, BackgroundLuminance(text, 0.0)),
					Contrast.Ratio(foreground, BackgroundLuminance(text, 1.0)));
				Assert.That(worst, Is.GreaterThanOrEqualTo(AA),
					$"'{text.text}' ({text.name ?? text.GetType().Name}) color {text.resolvedStyle.color}: {worst:F2}:1");
			});
			return checkedCount;
		}

		private static bool IsVisible(VisualElement element)
		{
			if (element.resolvedStyle.visibility != Visibility.Visible || element.worldBound.width < 1f)
			{
				return false;
			}

			for (var e = element; e != null; e = e.parent)
			{
				if (e.resolvedStyle.display == DisplayStyle.None || e.resolvedStyle.opacity <= 0f)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Luminance behind <paramref name="element"/>: its own and its ancestors' backgrounds, outermost first, over the view.</summary>
		private static double BackgroundLuminance(VisualElement element, double viewLuminance)
		{
			var chain = new System.Collections.Generic.List<VisualElement>();
			for (var e = element; e != null; e = e.parent)
			{
				chain.Add(e);
			}

			var luminance = viewLuminance;
			for (var i = chain.Count - 1; i >= 0; i--)
			{
				var background = chain[i].resolvedStyle.backgroundColor;
				if (background.a > 0f)
				{
					luminance = background.a * Contrast.Luminance(background) + (1.0 - background.a) * luminance;
				}
			}

			return luminance;
		}
	}

	/// <summary>WCAG 2.x relative luminance and contrast of sRGB colors.</summary>
	internal static class Contrast
	{
		public static double Ratio(Color a, Color b) => Ratio(Luminance(a), Luminance(b));

		public static double Ratio(double la, double lb) => (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

		public static double Luminance(Color color) =>
			0.2126 * Linear(color.r) + 0.7152 * Linear(color.g) + 0.0722 * Linear(color.b);

		private static double Linear(float channel) =>
			channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
	}
}
