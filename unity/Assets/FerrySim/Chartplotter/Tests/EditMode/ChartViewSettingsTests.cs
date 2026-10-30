using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure modes: a zoom past the limits, a ladder step that skips or sticks, orientation or centring state lost, and
	// alarm limits not applied. The view is constructed without a panel (UI Toolkit allows it); nothing is laid out here.
	public class ChartViewSettingsTests
	{
		[Test]
		public void Zoom_FollowsTheLadderWithinLimits()
		{
			var view = new ChartplotterView();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(1.0));
			view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(2.0));
			view.ZoomOut(); view.ZoomOut(); view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(8.0), "stops at 8");
			view.SpanNauticalMiles = 20;
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(8.0), "clamped");
			view.SpanNauticalMiles = 0.1;
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.25), "clamped");
			view.ZoomIn();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.25), "stays at 0.25");
			view.SpanNauticalMiles = 0.7;
			view.ZoomIn();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.5), "off the ladder, zooming in takes the next rung in");
			view.SpanNauticalMiles = 0.7;
			view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(1.0));
			view.Canvas.Dispose();
			view.Targets.Dispose();
		}

		[Test]
		public void Orientation_Centring_AndLimits()
		{
			var view = new ChartplotterView();
			view.ToggleOrientation();
			Assert.That(view.Orientation, Is.EqualTo(ChartOrientation.CourseUp));
			Assert.That(view.Centred, Is.True);
			view.Pan(10, 10);
			Assert.That(view.Centred, Is.True, "no view yet: a pan has nothing to hold");
			view.AlarmLimits = new CpaAlarmLimits(300, 120);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(300));
			view.VectorMinutes = 0;
			Assert.That(view.VectorMinutes, Is.EqualTo(6.0), "a non-positive vector time is ignored");
			view.SafetyContourMetres = 4.5;
			Assert.That(view.SafetyContourMetres, Is.EqualTo(4.5));
			view.SafetyContourMetres = -1;
			Assert.That(view.SafetyContourMetres, Is.EqualTo(4.5));
			view.VisibilityMetres = -5;
			Assert.That(view.VisibilityMetres, Is.EqualTo(0));
			view.CyclePalette();
			Assert.That(view.Palette, Is.EqualTo(ChartPaletteKind.Dusk));
			view.CyclePalette(); view.CyclePalette();
			Assert.That(view.Palette, Is.EqualTo(ChartPaletteKind.Day));
			Assert.That(view.List.Rows, Is.EqualTo(CpaList.DefaultRows));
			Assert.That(view.Canvas, Is.InstanceOf<VisualElement>());
			view.Canvas.Dispose();
			view.Targets.Dispose();
		}
	}
}
