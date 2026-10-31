using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// A centre discontinuity (v0.3.1 review, finding 3): a far pan, the centre key after one, or a new run elsewhere moves
	/// the view off the committed chart's coverage. Failure modes: the old chart translated off its coverage (empty map
	/// around own ship) while a spread replacement generates, a pending drift generation finished first and committed a
	/// chart for the wrong place, and the obsolete request committing late over the replacement.
	/// </summary>
	public class CentreJumpPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator ADistantPanAndTheCentreKey_RegenerateAtOnce_WithAndWithoutAPendingGeneration()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.View.SpanNauticalMiles = 0.25;
			for (var i = 0; i < 3; i++) yield return null;
			var canvas = _rig.View.Canvas;
			AssertCovered("at the start");
			// One drawn item per slice throughout: a regeneration taken down the spread path is certain to be seen pending.
			canvas.DiagnosticItemsPerSlice = 1;
			var requests = canvas.RequestCount;
			var renders = canvas.RenderCount;

			// A far pan with nothing pending: about 1.1 km, beyond the chart's square (0.85 km a side at 0.25 NM), still over the synthetic harbor.
			_rig.View.Pan(0, 3000);
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "the pan regenerated");
			Assert.That(canvas.Generating, Is.False, "at once: the old chart does not cover the panned view");
			AssertCovered("after a far pan");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1));
			AssertCovered("a frame after a far pan");

			// The centre key with nothing pending: own ship is kilometres from the panned chart.
			_rig.View.Centre();
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 2), "the recentre regenerated");
			Assert.That(canvas.Generating, Is.False, "at once");
			AssertCovered("after the centre key");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 2));

			// A pending drift generation, then a far pan: the obsolete generation is dropped and the replacement is immediate.
			requests = canvas.RequestCount;
			renders = canvas.RenderCount;
			var frame = 0;
			var north = -400.0;
			yield return DriftUntilPending(frame, north, 100, (f, n) => { frame = f; north = n; });
			Assert.That(canvas.Generating, Is.True, "a drift generation is pending");
			AssertCovered("while the drift generation is pending");
			var pendingRequests = canvas.RequestCount;
			_rig.View.Pan(0, 3000);
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(pendingRequests + 1), "the far pan requested its own regeneration");
			Assert.That(canvas.Generating, Is.False, "at once, dropping the pending drift generation");
			AssertCovered("after a far pan during a drift generation");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "one upload: the dropped drift generation never committed");
			for (var i = 0; i < 10; i++)
			{
				yield return null;
				Assert.That(canvas.Generating, Is.False);
				Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "no late commit of the dropped request");
				AssertCovered("holding after the pan");
			}

			// A pending drift generation while panned (small pans), then the centre key.
			renders = canvas.RenderCount;
			requests = canvas.RequestCount;
			var pans = 0;
			while (canvas.RequestCount == requests && pans < 400)
			{
				pans++;
				_rig.View.Pan(0, 60);
				_rig.Display.Refresh();
				if (canvas.RequestCount != requests) break;
				yield return null;
			}

			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "panning in small steps drifts the chart");
			Assert.That(canvas.Generating, Is.True, "a drift generation is pending while panned");
			_rig.View.Centre();
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 2), "the centre key requested its own regeneration");
			Assert.That(canvas.Generating, Is.False, "at once");
			AssertCovered("after the centre key during a drift generation");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "one upload for the recentre");
			for (var i = 0; i < 10; i++)
			{
				yield return null;
				Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "no late commit of the dropped request");
				AssertCovered("holding after the centre key");
			}
		}

		[UnityTest]
		public IEnumerator ANewRunElsewhere_RegeneratesAtOnce_WithAndWithoutAPendingGeneration()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.View.SpanNauticalMiles = 0.25;
			for (var i = 0; i < 3; i++) yield return null;
			var canvas = _rig.View.Canvas;
			canvas.DiagnosticItemsPerSlice = 1;
			var requests = canvas.RequestCount;
			var renders = canvas.RenderCount;

			// A new run 1.8 km away (still over the synthetic harbor) with nothing pending.
			_rig.Own.RunId++;
			_rig.Own.Place(0, 1000, 1200, 0, 6);
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "the run jump regenerated");
			Assert.That(canvas.Generating, Is.False, "at once");
			AssertCovered("after a new run elsewhere");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1));

			// A pending drift generation, then a new run elsewhere.
			renders = canvas.RenderCount;
			var frame = 0;
			var north = 1000.0;
			yield return DriftUntilPending(frame, north, 1200, (f, n) => { frame = f; north = n; });
			Assert.That(canvas.Generating, Is.True, "a drift generation is pending");
			var pendingRequests = canvas.RequestCount;
			_rig.Own.RunId++;
			_rig.Own.Place(0, -400, 100, 0, 6);
			_rig.Display.Refresh();
			Assert.That(canvas.RequestCount, Is.EqualTo(pendingRequests + 1), "the run jump requested its own regeneration");
			Assert.That(canvas.Generating, Is.False, "at once, dropping the pending drift generation");
			AssertCovered("after a new run elsewhere during a drift generation");
			yield return null;
			Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "one upload: the dropped drift generation never committed");
			for (var i = 0; i < 10; i++)
			{
				yield return null;
				Assert.That(canvas.RenderCount, Is.EqualTo(renders + 1), "no late commit of the dropped request");
				AssertCovered("holding after the run jump");
			}
		}

		/// <summary>Own ship north, 20 m a frame, until a drift regeneration is requested (and, at one item a slice, pending).</summary>
		private IEnumerator DriftUntilPending(int frame, double north, double east, Action<int, double> progress)
		{
			var canvas = _rig.View.Canvas;
			var requests = canvas.RequestCount;
			while (canvas.RequestCount == requests && frame < 400)
			{
				frame++;
				north += 20.0;
				_rig.Own.Place(frame, north, east, 0, 6);
				_rig.Display.Refresh();
				progress(frame, north);
				if (canvas.RequestCount != requests) break;
				yield return null;
			}

			Assert.That(canvas.RequestCount, Is.EqualTo(requests + 1), "a drift regeneration was requested");
		}

		/// <summary>The committed chart's square contains the viewport's circumscribed circle at the current centre and scale.</summary>
		private void AssertCovered(string when)
		{
			var view = _rig.View;
			var rendered = view.Canvas.RenderedView;
			var current = view.CurrentView;
			var side = rendered.ScreenCentreX * 2.0;
			var diagonal = 2.0 * Math.Sqrt(current.ScreenCentreX * current.ScreenCentreX + current.ScreenCentreY * current.ScreenCentreY);
			var margin = (side - diagonal) * 0.5 * current.MetresPerPixel;
			Assert.That(current.MetresPerPixel, Is.EqualTo(rendered.MetresPerPixel), when + ": the chart on screen is at the view's scale");
			Assert.That(Math.Abs(current.CentreNorth - rendered.CentreNorth), Is.LessThanOrEqualTo(margin), when + ": the chart on screen covers the viewport (north offset " + (current.CentreNorth - rendered.CentreNorth).ToString("F0") + " m, margin " + margin.ToString("F0") + " m)");
			Assert.That(Math.Abs(current.CentreEast - rendered.CentreEast), Is.LessThanOrEqualTo(margin), when + ": the chart on screen covers the viewport (east offset " + (current.CentreEast - rendered.CentreEast).ToString("F0") + " m, margin " + margin.ToString("F0") + " m)");
		}
	}
}
