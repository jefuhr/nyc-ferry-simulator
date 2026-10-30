using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: the static chart regenerated every frame while own ship moves inside the margin, not regenerated on a
	// zoom, palette, unit or safety-contour change, and not regenerated once the ship has drifted out of the rendered area.
	public class StaticRenderPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator TheChartRegeneratesOnlyOnAViewChange()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.View.SpanNauticalMiles = 1.0;
			for (var i = 0; i < 3; i++) yield return null;
			var canvas = _rig.View.Canvas;
			var requests = canvas.RequestCount;
			var renders = canvas.RenderCount;
			Assert.That(requests, Is.GreaterThanOrEqualTo(1));
			Assert.That(renders, Is.GreaterThanOrEqualTo(1), "the meshes were generated");
			var paints = _rig.View.Targets.PaintCount;
			// Under way at 6 kn for 60 frames: well inside the rendered margin.
			for (var i = 1; i <= 60; i++)
			{
				_rig.Own.Place(i * 0.1, -400 + i * 0.3, 100, 0, 6);
				yield return null;
			}

			Assert.That(canvas.RequestCount, Is.EqualTo(requests), "no regeneration while the view holds");
			Assert.That(canvas.RenderCount, Is.EqualTo(renders));
			Assert.That(_rig.View.Targets.PaintCount + _rig.View.Targets.SkippedPaints, Is.GreaterThanOrEqualTo(paints + 60), "the moving layer is considered every frame");
			Assert.That(_rig.View.Targets.PaintCount, Is.GreaterThan(paints), "and repaints as own ship creeps across the screen (18 m in 60 frames at 0.84 m/px)");
			_rig.View.ZoomIn();
			yield return null;
			yield return null;
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "a zoom regenerates once");
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1));
			_rig.View.Palette = ChartPaletteKind.Night;
			yield return null;
			yield return null;
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 2), "a palette change regenerates once");
			_rig.View.DistanceUnit = UI.DistanceUnit.Metres;
			yield return null;
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 3), "a unit change regenerates once (soundings)");
			_rig.View.SafetyContourMetres = 6.0;
			yield return null;
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 4), "a safety contour change regenerates once");
			Assert.That(_rig.View.SafetyContourDrawnMetres, Is.EqualTo(10.0), "the next deeper charted contour");
			Assert.That(_rig.View.DepthText, Does.Contain("SAFETY 10 m").And.Contain("SET 6.0 m"));
			var before = canvas.RequestCount;
			// A long run north: the view leaves the rendered square and regenerates, but only a few times, not per frame.
			for (var i = 1; i <= 120; i++)
			{
				_rig.Own.Place(100 + i, -400 + i * 20.0, 100, 0, 6);
				yield return null;
			}

			var drift = canvas.RequestCount - before;
			Assert.That(drift, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(12), "2.4 km of travel at 0.5 nm regenerates every few hundred metres, not every frame: " + drift);
		}
	}
}
