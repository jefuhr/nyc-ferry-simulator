using System;
using FerrySim.Core.Radar;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.Radar.Tests
{
	// Failure modes: mirrored PPI, full-screen recomputation, palette affecting echoes, unsupported allocation counters.
	public class RadarDisplayTests
	{
		[Test] public void RasterSizeIsExplicitAndMeasuredDefaultIs384()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>()));
			using (var normal = new RadarView(engine)) Assert.That(normal.Raster.Size, Is.EqualTo(384));
			using (var larger = new RadarView(engine, 512)) Assert.That(larger.Raster.Size, Is.EqualTo(512));
		}
		[Test] public void NorthAndEastPixelsFollowKnownBearingsAndRanges()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>()));
			var raster = new RadarRaster(engine, 128);
			try
			{
				Assert.That(raster.SpokeAt(64, 110), Is.LessThan(10));
				Assert.That(raster.SpokeAt(110, 64), Is.InRange(engine.Configuration.Spokes / 4 - 6, engine.Configuration.Spokes / 4 + 6));
				Assert.That(raster.BinAt(64, 110), Is.InRange(365, 385));
			}
			finally { raster.Dispose(); }
		}
		[TestCase(128), TestCase(384), TestCase(512)] public void IncrementalPixelsMatchReferenceAndPaletteLeavesAmplitudesUntouched(int size)
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>()), new RadarConfiguration(128));
			var raster = new RadarRaster(engine, size); var frame = new RadarFrame(0, 0, 0, 0, 9, 8);
			try
			{
				engine.Advance(0, frame); raster.Refresh();
				for (var i = 1; i <= 10; i++)
				{
					do { engine.Advance(i * .25, frame); raster.Refresh(); } while (engine.PendingSpokes > 0);
				}
				var before = raster.Texture.GetPixels32(); var echo = engine.Echo(20, 30);
				raster.RefreshAll(); CollectionAssert.AreEqual(before, raster.Texture.GetPixels32());
				raster.SetPalette(RadarPalette.Night); Assert.That(engine.Echo(20, 30), Is.EqualTo(echo));
			}
			finally { raster.Dispose(); }
		}
		[Test] public void CalibratedMovingSweepAndRasterAllocateNothing()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>())); var raster = new RadarRaster(engine, 128);
			var frame = new RadarFrame(0, 0, .4, 1, 9, 8); var t = 0.0;
			try
			{
				Action action = () => { for (var i = 0; i < 100; i++) { engine.Advance(t += 1.0 / 60, frame); raster.Refresh(); } };
				action(); Assert.That(MeasureAllocations(action), Is.Zero);
			}
			finally { raster.Dispose(); }
		}
		public static int MeasureAllocations(Action action)
		{
			var chars = new char[16]; var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true; var control = new string(chars); recorder.enabled = false;
				var calibration = recorder.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1), "deliberate one-sample positive control");
				recorder.enabled = true; action(); recorder.enabled = false; var count = recorder.sampleBlockCount;
				TestContext.WriteLine("GC.Alloc positive control=" + calibration + "; measured samples=" + count); return count;
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}
	}
}
