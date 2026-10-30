using System.Collections;
using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: the static canvas (moved and turned by its transform) disagreeing with the moving layer's projection,
	// in north-up, course-up or after a pan; a label drawn away from its feature; the rendered canvas not covering the viewport.
	public class ProjectionPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		private static IEnumerator Frames(int count)
		{
			for (var i = 0; i < count; i++) yield return null;
		}

		[UnityTest]
		public IEnumerator KnownFeatures_LandWhereTheViewProjectsThem_NorthUpAndCourseUp()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.View.SpanNauticalMiles = 2.0;
			yield return Frames(3);
			yield return CheckLabel("north-up");
			_rig.Own.Place(1, -400, 100, 90, 6);
			_rig.View.Orientation = ChartOrientation.CourseUp;
			yield return Frames(3);
			yield return CheckLabel("course-up, heading east");
			_rig.Own.Place(2, -400, 100, 225, 6);
			yield return Frames(3);
			yield return CheckLabel("course-up, heading south-west");
			_rig.View.Orientation = ChartOrientation.NorthUp;
			_rig.View.Pan(120, -80);
			yield return Frames(3);
			Assert.That(_rig.View.Centred, Is.False);
			yield return CheckLabel("north-up, panned");
			_rig.View.Centre();
			yield return Frames(2);
			Assert.That(_rig.View.Centred, Is.True);
		}

		private IEnumerator CheckLabel(string what)
		{
			yield return null;
			var view = _rig.View;
			var viewport = view.Viewport.worldBound;
			var current = view.CurrentView;
			var rendered = view.Canvas.RenderedView;
			// The chart turns by -upBearing (clockwise-positive on screen); a label's anchor offset from its feature turns with it.
			var angle = -(float)current.UpBearing;
			var cos = Mathf.Cos(angle);
			var sin = Mathf.Sin(angle);

			// Pier A's label is anchored 8 px right of its mark plus half its estimated width, 9 px above, in canvas pixels.
			var stops = view.Canvas.StopLabelPool;
			var stop = stops.Find(SyntheticHarbor.PierA);
			Assert.That(stop, Is.GreaterThanOrEqualTo(0), what + ": Pier A is labelled");
			current.ToScreen(SyntheticHarbor.PierAPosition.North, SyntheticHarbor.PierAPosition.East, out var expectedX, out var expectedY);
			rendered.ToScreen(SyntheticHarbor.PierAPosition.North, SyntheticHarbor.PierAPosition.East, out var canvasX, out var canvasY);
			var offsetX = stops.AnchorX(stop) - (float)canvasX;
			var offsetY = stops.AnchorY(stop) - (float)canvasY;
			var centre = stops[stop].worldBound.center - viewport.position;
			Assert.That(centre.x, Is.EqualTo(expectedX + offsetX * cos - offsetY * sin).Within(2.0), what + ": label x");
			Assert.That(centre.y, Is.EqualTo(expectedY + offsetX * sin + offsetY * cos).Within(2.0), what + ": label y");
			Assert.That(Mathf.Abs(stops[stop].worldBound.width - stops[stop].layout.width), Is.LessThan(0.5f), what + ": the label itself is upright");

			// A sounding label sits centred on its sounding.
			var soundings = view.Canvas.SoundingLabelPool;
			if (soundings.Count > 0)
			{
				var text = soundings[0].text;
				var i = -1;
				for (var k = 0; k < view.Chart.Soundings.Count && i < 0; k++)
				{
					if (view.Chart.Soundings.TextFeet[k] == text && Mathf.Abs(soundings.AnchorX(0) - CanvasX(rendered, k)) < 0.01f && Mathf.Abs(soundings.AnchorY(0) - CanvasY(rendered, k)) < 0.01f) i = k;
				}

				Assert.That(i, Is.GreaterThanOrEqualTo(0), what + ": the first sounding label belongs to a sounding");
				current.ToScreen(view.Chart.Soundings.North[i], view.Chart.Soundings.East[i], out var sx, out var sy);
				var soundingCentre = soundings[0].worldBound.center - viewport.position;
				Assert.That(soundingCentre.x, Is.EqualTo(sx).Within(2.0), what + ": sounding x");
				Assert.That(soundingCentre.y, Is.EqualTo(sy).Within(2.0), what + ": sounding y");
			}

			// The rendered canvas covers the whole viewport whatever the rotation.
			var canvasBound = view.Canvas.worldBound;
			Assert.That(canvasBound.width, Is.GreaterThanOrEqualTo(viewport.width), what + ": canvas wide enough");
			Assert.That(view.Canvas.RequestCount, Is.GreaterThan(0));
		}

		private float CanvasX(ChartView rendered, int sounding)
		{
			rendered.ToScreen(_rig.View.Chart.Soundings.North[sounding], _rig.View.Chart.Soundings.East[sounding], out var x, out _);
			return (float)x;
		}

		private float CanvasY(ChartView rendered, int sounding)
		{
			rendered.ToScreen(_rig.View.Chart.Soundings.North[sounding], _rig.View.Chart.Soundings.East[sounding], out _, out var y);
			return (float)y;
		}

		[UnityTest]
		public IEnumerator OwnShipAndTargets_ProjectWithTheCurrentView()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 45, 8);
			_rig.Targets.Add("TUG", 123, -900, 600, 2, -2);
			_rig.View.SpanNauticalMiles = 1.0;
			yield return Frames(3);
			var view = _rig.View.CurrentView;
			Assert.That(view.CentreNorth, Is.EqualTo(-400));
			Assert.That(view.CentreEast, Is.EqualTo(100));
			Assert.That(view.MetresPerPixel, Is.EqualTo(ChartView.MetresPerPixelFor(1.0, _rig.View.Viewport.contentRect.width)).Within(1e-9));
			Assert.That(_rig.View.Targets.TargetsDrawn, Is.EqualTo(1));
			Assert.That(_rig.View.Ranking.Count, Is.EqualTo(1));
			Assert.That(_rig.View.List.RowName(0), Is.EqualTo("TUG"));
			Assert.That(_rig.View.HeadingText, Is.EqualTo("HDG 045"));
			Assert.That(_rig.View.CourseText, Is.EqualTo("COG 045"));
			Assert.That(_rig.View.SpeedText, Is.EqualTo("SOG 8.0 kn"));
			Assert.That(_rig.View.NoPositionShown, Is.False);
			_rig.Own.HasTelemetry = false;
			yield return Frames(2);
			Assert.That(_rig.View.NoPositionShown, Is.True, "no telemetry: NO POSITION over the last chart");
			Assert.That(_rig.View.HeadingText, Is.EqualTo("HDG ---"));
		}
	}
}
