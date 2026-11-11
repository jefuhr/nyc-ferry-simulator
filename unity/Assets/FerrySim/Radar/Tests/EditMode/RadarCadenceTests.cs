using System;
using FerrySim.Core.Radar;
using NUnit.Framework;

namespace FerrySim.Radar.Tests
{
	// Failure modes: only the last engine step reaches the texture, accumulated data uploads early, resets retain echoes.
	public class RadarCadenceTests
	{
		[Test] public void TwentyHertzUploadPreservesEverySpokeOfTheSweep()
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>()));
			using var reference = new RadarRaster(engine, 128);
			using var throttled = new RadarRaster(engine, 128);
			var initial = throttled.Uploads;
			var frame = new RadarFrame(0, 0, .2, 0, 9, 8);
			for (var i = 0; i <= 300; i++)
			{
				engine.Advance(i / 120.0, frame);
				reference.Refresh();
				throttled.Accumulate();
				if (i % 6 == 0)
				{
					throttled.UploadPending();
					CollectionAssert.AreEqual(reference.Texture.GetPixels32(), throttled.Texture.GetPixels32(), "at step " + i);
				}
			}
			Assert.That(throttled.Uploads - initial, Is.InRange(49, 51));
			var before = throttled.Uploads;
			throttled.UploadPending();
			Assert.That(throttled.Uploads, Is.EqualTo(before), "no dirty pixels means no upload");
			engine.Reset(); throttled.Accumulate(); throttled.UploadPending(); reference.RefreshAll();
			CollectionAssert.AreEqual(reference.Texture.GetPixels32(), throttled.Texture.GetPixels32());
		}
	}
}
