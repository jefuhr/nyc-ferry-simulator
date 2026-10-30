using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure modes: a night colour with blue or green light, a night palette brighter than day, depth bands whose
	// brightness order does not put the unsafe band brightest, and captions that do not reach WCAG AA on the panel by day.
	public class ChartPaletteTests
	{
		private static (string, Color)[] Colours(ChartPalette p) => new[]
		{
			("NoData", p.NoData), ("Panel", p.Panel), ("Bezel", p.Bezel), ("Text", p.Text), ("TextDim", p.TextDim), ("Sounding", p.Sounding), ("Land", p.Land), ("Drying", p.Drying),
			("Unsafe", p.Unsafe), ("Caution", p.Caution), ("Deep", p.Deep), ("Contour", p.Contour), ("SafetyContour", p.SafetyContour), ("Coastline", p.Coastline),
			("Channel", p.Channel), ("Hazard", p.Hazard), ("Bridge", p.Bridge), ("Barge", p.Barge), ("Landing", p.Landing), ("AidPort", p.AidPort),
			("AidStarboard", p.AidStarboard), ("AidSpecial", p.AidSpecial), ("AidOther", p.AidOther), ("LightFlare", p.LightFlare), ("OwnShip", p.OwnShip),
			("HeadingLine", p.HeadingLine), ("CourseVector", p.CourseVector), ("Track", p.Track), ("Target", p.Target), ("TargetAlarm", p.TargetAlarm),
			("TargetAlarmText", p.TargetAlarmText), ("Selection", p.Selection), ("Visibility", p.Visibility),
		};

		[Test]
		public void Night_IsRedToAmberOnly()
		{
			foreach (var (name, color) in Colours(ChartPalette.Night))
			{
				Assert.That(color.r, Is.GreaterThanOrEqualTo(color.g), name + " red >= green");
				Assert.That(color.g, Is.GreaterThanOrEqualTo(color.b), name + " green >= blue");
				Assert.That(color.g, Is.LessThanOrEqualTo(color.r * 0.65f + 1e-6f), name + " green at most 0.65 of red");
			}
		}

		[Test]
		public void Night_IsDarkerThanDay_AndDuskBetween()
		{
			foreach (var (name, _) in Colours(ChartPalette.Day))
			{
				if (name == "TargetAlarm" || name == "TargetAlarmText")
				{
					// The alarm fill is the instruments' own: their night alarm is deliberately the one bright thing on the panel.
					continue;
				}

				// Compare the same role across palettes.
				var day = Find(ChartPalette.Day, name);
				var dusk = Find(ChartPalette.Dusk, name);
				var night = Find(ChartPalette.Night, name);
				Assert.That(PlotterRig.Luminance(night), Is.LessThanOrEqualTo(PlotterRig.Luminance(day) + 1e-9), name + ": night no brighter than day");
				Assert.That(PlotterRig.Luminance(dusk), Is.LessThanOrEqualTo(PlotterRig.Luminance(day) + 1e-9), name + ": dusk no brighter than day");
			}

			Assert.That(PlotterRig.Luminance(ChartPalette.Night.Unsafe), Is.LessThan(0.05), "the brightest night water stays dim");
		}

		private static Color Find(ChartPalette palette, string name)
		{
			foreach (var (candidate, color) in Colours(palette))
			{
				if (candidate == name) return color;
			}

			throw new System.ArgumentException(name);
		}

		[Test]
		public void DepthBands_ShoalBrighter_InEveryPalette()
		{
			foreach (var palette in new[] { ChartPalette.Day, ChartPalette.Dusk, ChartPalette.Night })
			{
				Assert.That(PlotterRig.Luminance(palette.Unsafe), Is.GreaterThan(PlotterRig.Luminance(palette.Caution)), palette.Kind + ": unsafe brighter than caution");
				Assert.That(PlotterRig.Luminance(palette.Caution), Is.GreaterThan(PlotterRig.Luminance(palette.Deep)), palette.Kind + ": caution brighter than deep");
				Assert.That(PlotterRig.Luminance(palette.SafetyContour), Is.GreaterThan(PlotterRig.Luminance(palette.Contour)), palette.Kind + ": the safety contour stands out");
			}
		}

		[Test]
		public void Day_TextMeetsWcagAA_OnThePanelAndTheWater()
		{
			var day = ChartPalette.Day;
			foreach (var (name, color) in new[] { ("Text", day.Text), ("TextDim", day.TextDim), ("Landing", day.Landing), ("Sounding", day.Sounding) })
			{
				Assert.That(PlotterRig.Contrast(color, day.Panel), Is.GreaterThanOrEqualTo(4.5), name + " on the panel");
				Assert.That(PlotterRig.Contrast(color, day.Deep), Is.GreaterThanOrEqualTo(4.5), name + " over deep water");
			}

			foreach (var (name, color) in new[] { ("Text", day.Text), ("Landing", day.Landing), ("Sounding", day.Sounding) })
			{
				Assert.That(PlotterRig.Contrast(color, day.Unsafe), Is.GreaterThanOrEqualTo(3.0), name + " over the unsafe band, the brightest water");
			}

			Assert.That(PlotterRig.Contrast(day.TargetAlarmText, day.TargetAlarm), Is.GreaterThanOrEqualTo(4.5), "alarm rows");
			Assert.That(PlotterRig.Contrast(day.Target, day.Deep), Is.GreaterThanOrEqualTo(3.0), "targets over deep water");
			Assert.That(PlotterRig.Contrast(day.OwnShip, day.Unsafe), Is.GreaterThanOrEqualTo(3.0), "own ship over the unsafe band");
		}

		[Test]
		public void Night_TextStillReadable()
		{
			var night = ChartPalette.Night;
			Assert.That(PlotterRig.Contrast(night.Text, night.Panel), Is.GreaterThanOrEqualTo(3.0));
			Assert.That(PlotterRig.Contrast(night.Text, night.Deep), Is.GreaterThanOrEqualTo(3.0));
			Assert.That(PlotterRig.Contrast(night.TargetAlarmText, night.TargetAlarm), Is.GreaterThanOrEqualTo(3.0));
		}
	}
}
