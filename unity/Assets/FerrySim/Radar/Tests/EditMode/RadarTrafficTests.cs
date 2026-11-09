using System;
using FerrySim.Core.Radar;
using FerrySim.Core.Traffic;
using NUnit.Framework;

namespace FerrySim.Radar.Tests
{
	// Failure modes: an AIS vessel without a length reaching the radar as a NaN target and turning echoes NaN; a non-finite
	// echo indexing outside the palette (on x64 a NaN cast to int is int.MinValue, so this threw every frame on Windows only).
	public class RadarTrafficTests
	{
		private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);

		[Test] public void AVesselWithoutAisSizeIsSampledAtItsDrawnHullAndEchoesStayFinite()
		{
			var vessel = new TrafficVessel(0, 367000001, "NO SIZE", 52, TrafficClass.TugTow, double.NaN, double.NaN, double.NaN, 43100, 43300, 2, 1, false, false);
			var reports = new TrafficReports(new[] { 0, 0 }, new[] { 0, 0 }, new[] { 43100.0, 43300.0 }, new[] { 300.0, 300.0 }, new[] { 0.0, 0.0 },
				new[] { 0f, 0f }, new[] { 0f, 0f }, new[] { 0f, 0f });
			var pack = new TrafficPack("2025-07-14", Epoch, -3600, 90000, new[] { vessel }, reports, string.Empty);
			var traffic = new TrafficRun(pack, Epoch.AddHours(12), TrafficLeaveOut.Off, 0);
			var targets = new RadarTarget[1];
			Assert.That(RadarController.SampleTraffic(traffic, 0, targets), Is.EqualTo(1));
			Assert.That(targets[0].Length, Is.EqualTo(vessel.Hull.Length));
			Assert.That(double.IsNaN(targets[0].Height) || double.IsInfinity(targets[0].Height), Is.False);
			var engine = Sweep(targets);
			Assert.That(NonFinite(engine), Is.Zero);
		}
		[Test] public void ANonFiniteEchoPaintsAsNoReturn()
		{
			var engine = Sweep(new[] { new RadarTarget(300, 0, double.NaN, double.NaN) });
			Assert.That(NonFinite(engine), Is.Positive, "precondition: the core passes a NaN target through to its echoes");
			var raster = new RadarRaster(engine, 128);
			try { Assert.DoesNotThrow(raster.RefreshAll); }
			finally { raster.Dispose(); }
		}
		private static RadarEngine Sweep(RadarTarget[] targets)
		{
			var engine = new RadarEngine(new RadarScene(Array.Empty<RadarFeature>())); var frame = new RadarFrame(0, 0, 0, 0, 9, 8);
			for (var i = 0; i <= 600; i++) engine.Advance(i / 60.0, frame, targets, targets.Length);
			return engine;
		}
		private static int NonFinite(RadarEngine engine)
		{
			var count = 0;
			for (var s = 0; s < engine.Configuration.Spokes; s++)
				for (var b = 0; b < engine.Scale.Bins; b++) { var echo = engine.Echo(s, b); if (float.IsNaN(echo) || float.IsInfinity(echo)) count++; }
			return count;
		}
	}
}
