using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: a night palette that brightens the screen (mean luminance), day text that does not reach WCAG AA
	// over what it is drawn on, a blank render, and a layout that breaks at 1440p (the 2x scale of the 720p design).
	public class PalettePlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator DayDuskAndNight_AreLegible_AndNightIsDark_At720pAnd1440p()
		{
			double dayMean = -1;
			foreach (var (width, height) in new[] { (1280, 720), (2560, 1440) })
			{
				_rig = new PlotterRig(width, height, new Rect(8, 8, 1264, 704));
				_rig.Own.Place(0, -700, 150, 20, 7);
				_rig.Targets.Add("TUG ALPHA", 111, -200, 300, -3, 0);
				_rig.Targets.Add("FERRY", 222, -1200, -500, 2, 2);
				_rig.Targets.Add("MOORED", 333, -1300, 900, 0, 0, 40, 10, false);
				_rig.View.SpanNauticalMiles = 1.0;
				_rig.View.SelectedTarget = 0;
				foreach (var palette in new[] { ChartPaletteKind.Day, ChartPaletteKind.Dusk, ChartPaletteKind.Night })
				{
					_rig.View.Palette = palette;
					for (var i = 0; i < 4; i++) yield return null;
					var name = "palette-" + palette.ToString().ToLowerInvariant() + "-" + height + "p.png";
					var image = _rig.Screenshot(name);
					Assert.That(PlotterRig.DistinctColours(image), Is.GreaterThan(32), name + " is not blank");
					var mean = PlotterRig.MeanLuminance(image);
					TestContext.WriteLine(name + ": mean luminance " + mean.ToString("F4"));
					if (palette == ChartPaletteKind.Day) dayMean = mean;
					if (palette == ChartPaletteKind.Night)
					{
						Assert.That(mean, Is.LessThan(0.04), name + ": the night palette does not brighten the wheelhouse");
						Assert.That(mean, Is.LessThan(dayMean * 0.8), name + ": night is darker than the (already dark) day chart");
					}

					if (palette == ChartPaletteKind.Day)
					{
						Assert.That(CheckVisibleText(_rig.View), Is.GreaterThan(20), "visible day labels checked");
					}

					Object.Destroy(image);
				}

				// The 1440p layout is the 720p one at exactly twice the size.
				var viewport = _rig.View.Viewport.worldBound;
				Assert.That(viewport.width, Is.GreaterThan(600), "viewport laid out at " + height + "p");
				Assert.That(_rig.View.Canvas.SoundingsShown, Is.GreaterThan(10), "soundings shown at 1 NM");
				Assert.That(_rig.View.Canvas.StopLabelsShown, Is.EqualTo(2));
				_rig.Dispose();
				_rig = null;
			}
		}

		/// <summary>Every visible label in the view, by day, against the solid backing behind it (panel, list or the chart's deep water).</summary>
		private static int CheckVisibleText(ChartplotterView view)
		{
			var day = ChartPalette.Day;
			var count = 0;
			foreach (var label in view.Query<Label>().ToList())
			{
				if (label.resolvedStyle.display == DisplayStyle.None || string.IsNullOrEmpty(label.text) || label.worldBound.width <= 0)
				{
					continue;
				}

				var foreground = label.resolvedStyle.color;
				var background = BackgroundOf(label, day);
				var ratio = PlotterRig.Contrast(foreground, background);
				count++;
				// Soundings and aid characters sit over water of any band: they are checked against the band they contrast least with (unsafe, the brightest).
				var minimum = label.parent is ChartLabelLayer ? 3.0 : 4.5;
				Assert.That(ratio, Is.GreaterThanOrEqualTo(minimum), "'" + label.text + "' " + foreground + " on " + background + ": " + ratio.ToString("F2") + ":1");
			}

			return count;
		}

		private static Color BackgroundOf(VisualElement element, ChartPalette day)
		{
			if (element.parent is ChartLabelLayer)
			{
				return day.Unsafe;
			}

			for (var e = element; e != null; e = e.parent)
			{
				var background = e.resolvedStyle.backgroundColor;
				if (background.a >= 0.99f)
				{
					return background;
				}
			}

			return day.Panel;
		}
	}
}
