using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: the static chart regenerated every frame while own ship moves inside the margin, not regenerated on a
	// zoom, palette, unit or safety-contour change, and not regenerated once the ship has drifted out of the rendered area.
	// For the spread drift regeneration (v0.3.1): the chart on screen replaced before the new one is complete, a drift
	// regeneration never completing, a view change during one not taking effect at once, and the generation's slices
	// running far over their budget.
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

		[UnityTest]
		public IEnumerator ADriftRegenerationIsSpreadOverFrames_WhileTheOldChartStays_AndAViewChangeStillRegeneratesAtOnce()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.View.SpanNauticalMiles = 0.5;
			for (var i = 0; i < 3; i++) yield return null;
			var canvas = _rig.View.Canvas;
			Assert.That(canvas.Generating, Is.False, "a view change regenerates at once");
			Assert.That(canvas.LastGenerationFrames, Is.EqualTo(1));
			var rendered = canvas.RenderedView;
			var requests = canvas.RequestCount;
			var renders = canvas.RenderCount;
			// One drawn item per slice: the synthetic harbor would otherwise complete in the request's frame, and the test
			// must see the generation pending (the front chart held, nothing uploaded) and then superseded.
			canvas.DiagnosticItemsPerSlice = 1;
			// North until the drift regeneration begins (the margin's 80%), 20 m a frame.
			var frame = 0;
			var north = -400.0;
			while (canvas.RequestCount == requests && frame < 200)
			{
				frame++;
				north += 20.0;
				_rig.Own.Place(frame, north, 100, 0, 6);
				_rig.Display.Refresh();
				if (canvas.RequestCount != requests) break;
				yield return null;
			}

			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "the drift regeneration was requested");
			Assert.That(canvas.Generating, Is.True, "and is pending after the request's slice");
			Assert.That(canvas.RenderedView.CentreNorth, Is.EqualTo(rendered.CentreNorth), "the chart on screen is still the old one");
			Assert.That(canvas.RenderCount, Is.EqualTo(renders), "nothing uploaded by the request");
			var refreshAtRequest = _rig.View.LastRefreshMilliseconds;
			TestContext.WriteLine("drift regeneration requested after " + frame + " frames (" + (north + 400.0) + " m); that refresh took " + refreshAtRequest.ToString("F2") + " ms; generating " + canvas.Generating);
			var generatingFrames = 1; // The request's frame is the first slice.
			var sliceMax = refreshAtRequest;
			while (canvas.Generating && generatingFrames < 120)
			{
				Assert.That(canvas.RenderedView.CentreNorth, Is.EqualTo(rendered.CentreNorth), "the chart on screen is the old one until the commit");
				Assert.That(canvas.RenderCount, Is.EqualTo(renders), "nothing uploaded before the commit");
				yield return null;
				generatingFrames++;
				sliceMax = System.Math.Max(sliceMax, _rig.View.LastRefreshMilliseconds);
			}

			Assert.That(canvas.Generating, Is.False, "the drift regeneration completed");
			Assert.That(canvas.LastGenerationFrames, Is.EqualTo(generatingFrames), "every frame of it was a slice");
			Assert.That(canvas.RenderedView.CentreNorth, Is.GreaterThan(rendered.CentreNorth), "the committed chart is centred where the ship was at the request");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "uploaded once at the commit's repaint");
			TestContext.WriteLine("drift regeneration: " + generatingFrames + " frames, drawing " + canvas.LastGenerationMilliseconds.ToString("F2") + " ms in all, worst refresh slice " + sliceMax.ToString("F2") + " ms, upload " + canvas.LastUploadMilliseconds.ToString("F2") + " ms, " + canvas.LastVertices + " vertices in " + canvas.LastChunks + " chunks, staged " + canvas.StagedBytes + " bytes");
			// At one item a slice the generation must have crossed frames; a route over the real chart is profiled by RouteRefreshProfileTests.
			Assert.That(generatingFrames, Is.GreaterThanOrEqualTo(2), "the generation was spread over frames");

			// A zoom while a drift regeneration is pending: the view change regenerates at once and the pending work is dropped.
			requests = canvas.RequestCount;
			renders = canvas.RenderCount;
			while (canvas.RequestCount == requests && frame < 400)
			{
				frame++;
				north += 20.0;
				_rig.Own.Place(frame, north, 100, 0, 6);
				_rig.Display.Refresh();
				if (canvas.RequestCount != requests) break;
				yield return null;
			}

			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1));
			Assert.That(canvas.Generating, Is.True, "a drift generation is pending immediately before the zoom");
			Assert.That(canvas.RenderCount, Is.EqualTo(renders), "and has uploaded nothing");
			_rig.View.ZoomOut();
			_rig.Display.Refresh();
			Assert.That(canvas.Generating, Is.False, "the zoom regenerated at once");
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 2));
			var zoomed = canvas.RenderedView;
			Assert.That(zoomed.MetresPerPixel, Is.GreaterThan(rendered.MetresPerPixel), "at the new span");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "one upload for the zoom; the dropped drift generation uploaded nothing");
			Assert.That(_rig.View.SpanNauticalMiles, Is.EqualTo(1.0));
			// The superseded request never commits later either: more frames, holding, change nothing.
			for (var i = 0; i < 10; i++)
			{
				yield return null;
				Assert.That(canvas.Generating, Is.False);
				Assert.That(canvas.RequestCount, Is.EqualTo(requests + 2));
				Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "no late commit of the superseded drift request");
				Assert.That(canvas.RenderedView.MetresPerPixel, Is.EqualTo(zoomed.MetresPerPixel), "the chart on screen stays the zoom's");
			}
		}
	}
}
