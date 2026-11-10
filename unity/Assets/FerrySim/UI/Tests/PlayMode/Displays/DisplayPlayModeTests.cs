using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FerrySim.Core.Instruments;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.PlayMode.Displays
{
	/// <summary>
	/// The generic engine monitor and alarm panel as rendered into a texture, the way they reach a console screen.
	/// Failure modes guarded: a display that does not fill its texture or lands at the wrong scale at 2×; text too
	/// faint to read by day (WCAG AA, 4.5:1) or by night (3:1 for readings, 2:1 for captions, the palette's night
	/// rules); any blue, green or white light at night; and a lamp or selected key drawn in the wrong colour. Writes
	/// artifacts/display-skins/&lt;case&gt;-&lt;day|night&gt;-&lt;1x|2x&gt;.png and contrast.txt.
	/// </summary>
	public class DisplayPlayModeTests
	{
		private static readonly string[] Cases = { "engine-port-main", "engine-starboard-alarms", "alarm-panel", "alarm-panel-lamp-test" };

		private DisplayRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		private static DisplaySkinLibrary Library() => new DisplaySkinLibrary(DisplaySkinAlarms.From(FakeInstrumentReadout.TClass()), privateFolder: null);

		/// <summary>Builds a case's view and the readout it shows; the time is in the lit half of every flash.</summary>
		private static (SkinnedDisplayView view, FakeInstrumentReadout readout) Build(string name)
		{
			var library = Library();
			switch (name)
			{
				case "engine-port-main":
					return (new EngineMonitorView(DisplaySide.Port, library.Load("engine", "generic-a")), FakeInstrumentReadout.Underway());
				case "engine-starboard-alarms":
				{
					var view = new EngineMonitorView(DisplaySide.Starboard, library.Load("engine", "generic-a"));
					view.ShowPage("alarms");
					return (view, FakeInstrumentReadout.Underway());
				}
				case "alarm-panel":
				{
					var readout = FakeInstrumentReadout.Underway();
					readout.SetPhase("steering.failure", AlarmPhase.Unacknowledged);
					readout.SetPhase("navigation.shallow-water", AlarmPhase.Acknowledged);
					readout.SetPhase("hull.contact", AlarmPhase.RingBack);
					return (new AlarmPanelView(library.Load("alarm-panel", "generic-a")), readout);
				}
				case "alarm-panel-lamp-test":
				{
					var readout = FakeInstrumentReadout.Underway();
					readout.LampTest = true;
					return (new AlarmPanelView(library.Load("alarm-panel", "generic-a")), readout);
				}
				default:
					throw new ArgumentException(name);
			}
		}

		[UnityTest]
		public IEnumerator EachDisplay_RendersToItsTexture_AtReferenceSizeAndAt2x()
		{
			foreach (var name in Cases)
			{
				foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
				{
					foreach (var scale in new[] { 1, 2 })
					{
						var (view, readout) = Build(name);
						view.Palette = palette;
						_rig = new DisplayRig(view, scale);
						view.Refresh(readout, 0.1);
						yield return DisplayRig.Frames();
						view.Refresh(readout, 0.2);
						yield return DisplayRig.Frames();

						var what = $"{name} {palette} {scale}x";
						var reference = view.Skin.Reference;
						Assert.That(view.worldBound.width, Is.EqualTo(reference.x).Within(0.5f), what + ": panel width in reference pixels");
						Assert.That(view.worldBound.height, Is.EqualTo(reference.y).Within(0.5f), what);
						Assert.That(view.worldBound.x, Is.EqualTo(0f).Within(0.5f), what);
						var file = $"{name}-{palette.ToString().ToLowerInvariant()}-{scale}x.png";
						var image = _rig.Screenshot(file);
						Assert.That(image.width, Is.EqualTo(Mathf.RoundToInt(reference.x * scale)));

						var colours = InstrumentPalette.For(palette);
						// Both generic skins have a title frame across the top and nothing in the bottom corners.
						foreach (var corner in new[] { new Vector2(1, reference.y - 2), new Vector2(reference.x - 2, reference.y - 2) })
						{
							AssertColour(_rig.PixelAt(image, corner), colours.Console, what + " corner " + corner + " (the display fills its texture)");
						}

						foreach (var corner in new[] { new Vector2(1, 1), new Vector2(reference.x - 2, 1) })
						{
							AssertColour(_rig.PixelAt(image, corner), colours.Bezel, what + " title frame border at " + corner);
						}

						CheckLandmark(name, view, image, colours, what);
						if (palette == PaletteKind.Night)
						{
							AssertNoBlueGreenOrWhite(image, what);
						}

						_rig.Dispose();
						_rig = null;
					}
				}
			}
		}

		/// <summary>One element per case whose fill colour is known, sampled where the rect says it is: proves the scale.</summary>
		private void CheckLandmark(string name, SkinnedDisplayView view, Texture2D image, InstrumentPalette colours, string what)
		{
			if (name.StartsWith("engine", StringComparison.Ordinal))
			{
				var page = view.CurrentPage;
				var key = view.Elements.OfType<SoftkeyView>().Single(k => k.Spec.Target == page);
				var rect = key.Spec.Rect;
				AssertColour(_rig.PixelAt(image, new Vector2(rect.x + 6, rect.y + rect.height * 0.5f)), colours.Engaged, what + ": the selected key " + page);
				return;
			}

			var list = view.Elements.OfType<AlarmListView>().Single();
			var lamp = list.Rows.First(row => !row.IsHeader && (row.Text == "FAILURE" || name.EndsWith("lamp-test", StringComparison.Ordinal)));
			var bound = lamp.Lamp.worldBound;
			Assert.That(lamp.Lit, Is.True, what + ": lamp lit at 0.2 s");
			AssertColour(_rig.PixelAt(image, bound.center), colours.Alarm, what + ": lit lamp " + lamp.Text + " at " + bound);
		}

		[UnityTest]
		public IEnumerator EveryVisibleText_IsLegible_ByDayAndByNight()
		{
			var report = new StringBuilder();
			foreach (var name in Cases)
			{
				foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
				{
					var (view, readout) = Build(name);
					view.Palette = palette;
					_rig = new DisplayRig(view, 1);
					view.Refresh(readout, 0.1);
					yield return DisplayRig.Frames();
					view.Refresh(readout, 0.2);
					yield return DisplayRig.Frames();

					var (count, worstPrimary, worstOther) = CheckVisibleText(view, palette, name);
					report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1}: {2} texts, worst reading {3:F2}:1, worst caption {4:F2}:1",
						name, palette, count, worstPrimary, worstOther));
					Assert.That(count, Is.GreaterThanOrEqualTo(name.StartsWith("engine-port", StringComparison.Ordinal) ? 30 : 4), name + " " + palette);
					_rig.Dispose();
					_rig = null;
				}
			}

			Directory.CreateDirectory(DisplayRig.Folder);
			File.WriteAllText(Path.Combine(DisplayRig.Folder, "contrast.txt"), report.ToString());
			TestContext.WriteLine(report.ToString());
		}

		/// <summary>
		/// Every visible text against the backgrounds of its ancestors (the display paints the console colour, so
		/// nothing shows through). By day each must reach 4.5:1; by night readings (class fs-display-primary) 3:1
		/// and the rest 2:1, the palette's night rules.
		/// </summary>
		private static (int count, double worstPrimary, double worstOther) CheckVisibleText(VisualElement root, PaletteKind palette, string name)
		{
			var count = 0;
			var worstPrimary = double.PositiveInfinity;
			var worstOther = double.PositiveInfinity;
			root.Query<TextElement>().ForEach(text =>
			{
				if (string.IsNullOrEmpty(text.text) || !IsVisible(text))
				{
					return;
				}

				count++;
				var ratio = Contrast.Ratio(Contrast.Luminance(text.resolvedStyle.color), BackgroundLuminance(text));
				var primary = text.ClassListContains(SkinnedDisplayView.PrimaryTextClass);
				var minimum = palette == PaletteKind.Day ? 4.5 : primary ? 3.0 : 2.0;
				if (primary)
				{
					worstPrimary = Math.Min(worstPrimary, ratio);
				}
				else
				{
					worstOther = Math.Min(worstOther, ratio);
				}

				Assert.That(ratio, Is.GreaterThanOrEqualTo(minimum), $"{name} {palette}: '{text.text}' colour {text.resolvedStyle.color}: {ratio:F2}:1, need {minimum}:1");
			});
			return (count, worstPrimary, worstOther);
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

		private static double BackgroundLuminance(VisualElement element)
		{
			var chain = new List<VisualElement>();
			for (var e = element; e != null; e = e.parent)
			{
				chain.Add(e);
			}

			var luminance = 1.0;
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

		private static void AssertColour(Color actual, Color expected, string what)
		{
			const float tolerance = 4f / 255f;
			Assert.That(Mathf.Abs(actual.r - expected.r) <= tolerance && Mathf.Abs(actual.g - expected.g) <= tolerance && Mathf.Abs(actual.b - expected.b) <= tolerance,
				Is.True, $"{what}: rendered {actual}, expected {expected}");
		}

		/// <summary>The palette's night rule, pixel by pixel, with a little room for rounding at anti-aliased edges.</summary>
		private static void AssertNoBlueGreenOrWhite(Texture2D image, string what)
		{
			var bad = 0;
			var example = default(Color32);
			foreach (var p in image.GetPixels32())
			{
				var violates = p.g > p.r + 3 || p.b > p.g + 3 || (p.r > 25 && p.g > 0.65f * p.r + 3);
				if (violates)
				{
					if (bad == 0)
					{
						example = p;
					}

					bad++;
				}
			}

			Assert.That(bad, Is.EqualTo(0), $"{what}: {bad} pixels outside red to amber, for example {example}");
		}
	}
}
