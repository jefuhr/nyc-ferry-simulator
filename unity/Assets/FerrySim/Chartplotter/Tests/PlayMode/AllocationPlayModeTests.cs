using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: the per-frame refresh (own ship, track, targets, ranking, list, transforms) allocating while the
	// view holds; the moving layer's paint allocating per frame. Measured with the calibrated GC.Alloc recorder: a
	// deliberate string allocation must produce exactly one sample before each measurement (GetAllocatedBytesForCurrentThread
	// reads false zeroes in this editor's Mono).
	public class AllocationPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		private static Recorder Calibrated()
		{
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			var chars = new char[16];
			recorder.enabled = true;
			var control = new string(chars);
			recorder.enabled = false;
			GC.KeepAlive(control);
			Assert.That(recorder.sampleBlockCount, Is.EqualTo(1), "GC.Alloc sees the deliberate string allocation");
			return recorder;
		}

		[UnityTest]
		public IEnumerator MovingShipAndTargets_RefreshAllocatesNothing_WhileTheViewHolds()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			for (var t = 0; t < 40; t++)
			{
				_rig.Targets.Add("T" + t, 100000 + t, -1500 + t * 40, -800 + t * 30, 2 * Math.Sin(t), 2 * Math.Cos(t), 20 + t % 5, 6, t % 7 != 0);
			}

			_rig.View.SpanNauticalMiles = 2.0;
			_rig.View.SelectedTarget = 3;
			for (var i = 0; i < 60; i++)
			{
				_rig.Own.Place(i * 0.1, -1000 + i * 0.3, 0, 30 + i * 0.1, 6);
				yield return null;
			}

			var requests = _rig.View.Canvas.RequestCount;
			var recorder = Calibrated();
			var total = 0;
			var frames = 0;
			for (var i = 60; i < 300; i++)
			{
				_rig.Own.Place(i * 0.1, -1000 + i * 0.3, 0, 30 + i * 0.1, 6);
				recorder.enabled = true;
				_rig.Display.Refresh();
				recorder.enabled = false;
				total += recorder.sampleBlockCount;
				frames++;
				yield return null;
			}

			recorder.CollectFromAllThreads();
			TestContext.WriteLine("refresh: " + total + " GC.Alloc samples over " + frames + " frames, " + _rig.View.Ranking.Considered + " targets considered, " + _rig.View.Targets.TargetsDrawn + " drawn");
			Assert.That(_rig.View.Canvas.RequestCount, Is.EqualTo(requests), "the static chart held");
			Assert.That(total, Is.Zero, "refresh allocated");
		}

		[UnityTest]
		public IEnumerator WholeFrames_WithTheDisplay_AllocateNoMoreThanWithout()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			for (var t = 0; t < 40; t++)
			{
				_rig.Targets.Add("T" + t, 100000 + t, -1500 + t * 40, -800 + t * 30, 2 * Math.Sin(t), 2 * Math.Cos(t));
			}

			_rig.View.SpanNauticalMiles = 2.0;
			for (var i = 0; i < 60; i++)
			{
				_rig.Own.Place(i * 0.1, -1000 + i * 0.3, 0, 30 + i * 0.1, 6);
				yield return null;
			}

			var with = 0;
			var recorder = Calibrated();
			for (var i = 60; i < 180; i++)
			{
				_rig.Own.Place(i * 0.1, -1000 + i * 0.3, 0, 30 + i * 0.1, 6);
				recorder.enabled = true;
				yield return null;
				recorder.enabled = false;
				with += recorder.sampleBlockCount;
			}

			recorder.CollectFromAllThreads();
			_rig.Display.enabled = false;
			for (var i = 0; i < 20; i++) yield return null;
			var without = 0;
			recorder = Calibrated();
			for (var i = 0; i < 120; i++)
			{
				recorder.enabled = true;
				yield return null;
				recorder.enabled = false;
				without += recorder.sampleBlockCount;
			}

			recorder.CollectFromAllThreads();
			TestContext.WriteLine("whole frames: " + with + " samples over 120 frames with the display (refresh and UI Toolkit paint), " + without + " without it (the test runner and the panel alone)");
			// The frame loop itself (test runner, panel update) allocates a little; the display must add nothing to it beyond noise.
			Assert.That(with, Is.LessThanOrEqualTo(without + 12), "the display's frames allocate: with " + with + ", without " + without);
		}
	}
}
