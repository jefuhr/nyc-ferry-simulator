using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Day palette contrast (WCAG 2.x relative luminance) and night palette discipline. Guards against:
	/// day text below AA contrast on any background it is drawn on, including the HUD backing over a
	/// bright or dark 3D view, badge text unreadable on its own fill, a night palette that leaks blue,
	/// green or white light (which spoils night vision), and night colors bright enough to dazzle.
	/// </summary>
	public class PaletteTests
	{
		private static InstrumentPalette Day => InstrumentPalette.Day;
		private static InstrumentPalette Night => InstrumentPalette.Night;

		[Test]
		public void Day_TextMeetsAccessibleContrast()
		{
			AssertContrast(Day.Text, Day.Face, 7.0, "primary numerals on instrument faces (AAA)");
			AssertContrast(Day.Text, Day.Console, 7.0, "primary numerals on the console (AAA)");
			AssertContrast(Day.TextDim, Day.Face, 4.5, "captions and units (AA)");
			AssertContrast(Day.TextDim, Day.Console, 4.5, "captions on the console (AA)");
			AssertContrast(Day.Scale, Day.Face, 4.5, "dial numerals (AA)");
		}

		/// <summary>
		/// Every day text color on every background it is drawn on (review finding 6). The HUD and the
		/// toolbar sit on <see cref="InstrumentPalette.HudBacking"/> over the 3D view, so their backgrounds are
		/// that backing blended (in linear light, as the renderer blends) over any scene from black to white:
		/// a translucent backing over bright sky is what measured 3.16:1 in the HUD screenshot.
		/// </summary>
		[Test]
		public void Day_EveryTextAndBackgroundPairMeetsWcagAA()
		{
			var pairs = new (string, Color, Color)[]
			{
				("vessel name and clock on the console", Day.Text, Day.Console),
				("zone, elapsed time and debrief captions on the console", Day.TextDim, Day.Console),
				("debrief axis numerals on the console", Day.Scale, Day.Console),
				("NO DATA badge on the console", Day.Caution, Day.Console),
				("numerals on instrument faces", Day.Text, Day.Face),
				("captions and units on instrument faces", Day.TextDim, Day.Face),
				("dial numerals on instrument faces", Day.Scale, Day.Face),
				("COG caption on the face", Day.SeriesB, Day.Face),
				("rudder scale P", Day.Port, Day.Face),
				("rudder scale S", Day.Starboard, Day.Face),
				("AHEAD/ASTERN badge", Day.EngagedText, Day.Engaged),
				("SHIFTING badge", Day.ShiftingText, Day.Shifting),
				("CONTACT fill", Day.CautionText, Day.Caution),
				("depth alarm fill", Day.AlarmText, Day.Alarm),
			};
			foreach (var (what, text, background) in pairs)
			{
				AssertContrast(text, background, 4.5, what);
			}

			foreach (var (what, text) in new[] { ("HUD values", Day.Text), ("HUD captions, units and toolbar status", Day.TextDim), ("HUD SHIFT", Day.Shifting) })
			{
				AssertContrastOverAnyScene(text, Day.HudBacking, 4.5, what);
			}
		}

		/// <summary>
		/// The colors the scenario picker and the readouts of the water and the air draw with, named for where they
		/// are used. They reuse existing roles, so this guards the pairings rather than new colors.
		/// </summary>
		[Test]
		public void Day_PickerAndEnvironmentPairsMeetWcagAA()
		{
			var pairs = new (string, Color, Color)[]
			{
				("tide, set, drift and wind values in the top strip", Day.Text, Day.Console),
				("their captions, units, trend and NAVD88", Day.TextDim, Day.Console),
				("coverage warning while live (filled)", Day.CautionText, Day.Caution),
				("coverage warning once earlier (outlined, on its own face)", Day.Text, Day.Face),
				("picker refusal under Start, on the picker", Day.Caution, Day.Console),
				("picker options, days and step buttons", Day.Text, Day.Face),
				("the selected option, day and Start", Day.EngagedText, Day.Engaged),
				("picker captions, weekdays, legend and zone", Day.TextDim, Day.Console),
			};
			foreach (var (what, text, background) in pairs)
			{
				AssertContrast(text, background, 4.5, what);
			}

			AssertContrast(Day.Text, Day.Console, 3.0, "a focused field's ring (WCAG 1.4.11)");
			AssertContrastOverAnyScene(Day.Text, Day.HudBacking, 4.5, "HUD water and air values and clock");
			AssertContrastOverAnyScene(Day.TextDim, Day.HudBacking, 4.5, "HUD water and air captions and units");
		}

		[Test]
		public void Night_PickerAndEnvironmentStayLegible()
		{
			AssertContrast(Night.CautionText, Night.Caution, 3.0, "night coverage warning while live");
			AssertContrast(Night.Text, Night.Face, 3.0, "night coverage warning once earlier");
			AssertContrast(Night.Caution, Night.Console, 2.0, "night picker refusal");
			AssertContrast(Night.EngagedText, Night.Engaged, 3.0, "night selected option and Start");
			AssertContrast(Night.Text, Night.Console, 3.0, "night readout values and a focused field's ring");
			AssertContrast(Night.TextDim, Night.Console, 2.0, "night captions");
		}

		[Test]
		public void Night_HudTextStaysLegibleOverAnyScene()
		{
			AssertContrastOverAnyScene(Night.Text, Night.HudBacking, 3.0, "night HUD values");
			AssertContrastOverAnyScene(Night.TextDim, Night.HudBacking, 2.0, "night HUD captions");
			AssertContrastOverAnyScene(Night.Shifting, Night.HudBacking, 2.0, "night HUD SHIFT");
		}

		[Test]
		public void Day_BadgeTextIsReadableOnItsFill()
		{
			AssertContrast(Day.EngagedText, Day.Engaged, 4.5, "AHEAD/ASTERN badge");
			AssertContrast(Day.ShiftingText, Day.Shifting, 4.5, "SHIFTING badge");
			AssertContrast(Day.CautionText, Day.Caution, 4.5, "CONTACT badge");
			AssertContrast(Day.AlarmText, Day.Alarm, 4.5, "alarm badge");
		}

		[Test]
		public void Day_GraphicsMeetNonTextContrast()
		{
			foreach (var (name, color) in new[]
			{
				("needle", Day.Needle), ("port", Day.Port), ("starboard", Day.Starboard), ("redline", Day.Redline),
				("series A", Day.SeriesA), ("series B", Day.SeriesB), ("cursor", Day.Cursor),
				("engaged badge", Day.Engaged), ("shifting badge", Day.Shifting), ("caution badge", Day.Caution), ("alarm badge", Day.Alarm),
			})
			{
				AssertContrast(color, Day.Face, 3.0, name + " against instrument faces (WCAG 1.4.11)");
			}
		}

		[Test]
		public void Night_HasNoBlueGreenOrWhiteLight()
		{
			foreach (var (name, color) in Roles(Night))
			{
				Assert.That(color.r + 1e-4f, Is.GreaterThanOrEqualTo(color.g), name + ": green must not exceed red");
				Assert.That(color.g + 1e-4f, Is.GreaterThanOrEqualTo(color.b), name + ": blue must not exceed green");
				if (color.r > 0.05f)
				{
					Assert.That(color.g / color.r, Is.LessThanOrEqualTo(0.65f), name + ": stays red to amber, not yellow or white");
				}
			}
		}

		[Test]
		public void Night_IsDimButStillLegibleInTheDark()
		{
			foreach (var (name, color) in Roles(Night))
			{
				var cap = name == nameof(InstrumentPalette.Alarm) ? 0.3 : 0.25;
				Assert.That(RelativeLuminance(color), Is.LessThanOrEqualTo(cap), name + " is too bright for night vision");
			}

			AssertContrast(Night.Text, Night.Face, 3.0, "night numerals");
			AssertContrast(Night.TextDim, Night.Face, 2.0, "night captions");
			AssertContrast(Night.EngagedText, Night.Engaged, 3.0, "night AHEAD/ASTERN badge");
			AssertContrast(Night.ShiftingText, Night.Shifting, 3.0, "night SHIFTING badge");
			AssertContrast(Night.CautionText, Night.Caution, 3.0, "night CONTACT badge");
			AssertContrast(Night.AlarmText, Night.Alarm, 3.0, "night alarm badge");
		}

		[Test]
		public void BothPalettes_DefineEveryRole()
		{
			foreach (var palette in new[] { Day, Night })
			{
				foreach (var (name, color) in Roles(palette))
				{
					Assert.That(color.a, Is.GreaterThan(0f), palette.Kind + "." + name + " is unset");
				}
			}

			Assert.That(InstrumentPalette.For(PaletteKind.Day), Is.SameAs(Day));
			Assert.That(InstrumentPalette.For(PaletteKind.Night), Is.SameAs(Night));
		}

		private static (string, Color)[] Roles(InstrumentPalette palette) =>
			typeof(InstrumentPalette)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(property => property.PropertyType == typeof(Color))
				.Select(property => (property.Name, (Color)property.GetValue(palette)))
				.ToArray();

		private static void AssertContrast(Color foreground, Color background, double minimum, string what)
		{
			var ratio = ContrastRatio(foreground, background);
			Assert.That(ratio, Is.GreaterThanOrEqualTo(minimum), $"{what}: contrast {ratio:F2}:1, need {minimum}:1");
		}

		/// <summary>
		/// Contrast of <paramref name="text"/> on <paramref name="backing"/> composited over scenes from black to
		/// white. Blending is linear in light, so the composite's luminance is a weighted sum of the two.
		/// </summary>
		private static void AssertContrastOverAnyScene(Color text, Color backing, double minimum, string what)
		{
			var worst = double.PositiveInfinity;
			var worstScene = 0.0;
			for (var i = 0; i <= 100; i++)
			{
				var scene = i / 100.0;
				var background = backing.a * RelativeLuminance(backing) + (1.0 - backing.a) * scene;
				var ratio = ContrastRatio(RelativeLuminance(text), background);
				if (ratio < worst)
				{
					worst = ratio;
					worstScene = scene;
				}
			}

			Assert.That(worst, Is.GreaterThanOrEqualTo(minimum),
				$"{what}: contrast {worst:F2}:1 over a scene of luminance {worstScene:F2}, need {minimum}:1 over any scene");
		}

		/// <summary>WCAG 2.x contrast ratio of two opaque sRGB colors.</summary>
		private static double ContrastRatio(Color a, Color b) => ContrastRatio(RelativeLuminance(a), RelativeLuminance(b));

		private static double ContrastRatio(double la, double lb) => (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

		private static double RelativeLuminance(Color color) =>
			0.2126 * Linear(color.r) + 0.7152 * Linear(color.g) + 0.0722 * Linear(color.b);

		private static double Linear(float channel) =>
			channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
	}
}
