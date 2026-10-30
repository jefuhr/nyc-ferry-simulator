using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Radar;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Radar
{
	// Failure modes: wrong N/E signs, bin overflow, hidden low roofs, tide treated as antenna height,
	// horizon ignored, stale sweep on rewind/range changes, nonrepeatable noise and hot-path allocations.
	public sealed class RadarTests
	{
		private static RadarFeature Wall(double range, double? roof, bool land = false) =>
			new RadarFeature(new[] { new RadarSegment(range, -30, range, 30) }, roof, land);
		private static RadarEngine Engine(params RadarFeature[] features) => new RadarEngine(new RadarScene(features), new RadarConfiguration(128));
		private static RadarFrame Frame(double tide = 0) => new RadarFrame(0, 0, 0, tide, 10, 0);
		[Test] public void TallRoofShadowsLowerButNotHigherRoof()
		{
			var engine = Engine(Wall(100, 30), Wall(200, 20), Wall(300, 90));
			engine.RenderSpoke(0, 0, Frame());
			Assert.That(engine.Echo(0, engine.Scale.Bin(100)), Is.GreaterThan(.5));
			Assert.That(engine.Echo(0, engine.Scale.Bin(200)), Is.LessThan(.1));
			Assert.That(engine.Echo(0, engine.Scale.Bin(300)), Is.GreaterThan(.5));
		}
		[Test] public void TideRaisesAntennaWithVesselAndChangesShadow()
		{
			var engine = Engine(Wall(100, 20), Wall(200, 29));
			engine.RenderSpoke(0, 0, Frame()); var low = engine.Echo(0, engine.Scale.Bin(200));
			engine.RenderSpoke(0, 0, Frame(3)); var high = engine.Echo(0, engine.Scale.Bin(200));
			Assert.That(low, Is.LessThan(.1)); Assert.That(high, Is.GreaterThan(.5));
			Assert.That(RadarMath.HorizonMetres(10, 20 - 3), Is.LessThan(RadarMath.HorizonMetres(10, 20)));
		}
		[Test] public void HorizonCutsOffLowTarget()
		{
			var engine = Engine(Wall(10000, 1)); engine.SetRange(6);
			engine.RenderSpoke(0, 0, new RadarFrame(0, 0, 0, 0, 1, 0));
			Assert.That(engine.Echo(0, engine.Scale.Bin(10000)), Is.LessThan(.1));
			Assert.That(RadarMath.HorizonMetres(1, 1), Is.EqualTo(2.21 * 2 * 1852).Within(1e-9));
		}
		[Test] public void LandReturnsAtFirstEdgeOnlyWithoutInventedHeight()
		{
			var engine = Engine(Wall(100, null, true), Wall(200, 20)); engine.RenderSpoke(0, 0, Frame());
			Assert.That(engine.Echo(0, engine.Scale.Bin(100)), Is.GreaterThan(.5));
			Assert.That(engine.Echo(0, engine.Scale.Bin(200)), Is.GreaterThan(.5));
		}
		[Test] public void EveryScaleMapsClosedOuterEdgeAndRejectsOutside()
		{
			for (var i = 0; i < RadarScale.Count; i++)
			{
				var scale = RadarScale.At(i); Assert.That(scale.Bin(0), Is.Zero);
				Assert.That(scale.Bin(scale.RangeMetres), Is.EqualTo(scale.Bins - 1));
				Assert.That(scale.Bin(scale.RangeMetres + .001), Is.EqualTo(-1));
				for (var b = 0; b < scale.Bins; b++) Assert.That(scale.Bin(scale.Centre(b)), Is.EqualTo(b));
			}
		}
		[Test] public void BearingRangeAndHeadUpUseClockwiseNorth()
		{
			Assert.That(RadarMath.Bearing(0, 100), Is.EqualTo(Math.PI / 2));
			Assert.That(RadarMath.RelativeBearing(Math.PI / 2, Math.PI), Is.EqualTo(Math.PI * 1.5));
			Assert.That(RadarMath.TrueBearing(Math.PI * 1.5, Math.PI), Is.EqualTo(Math.PI / 2));
			Assert.That(RadarMath.Range(300, 400), Is.EqualTo(500));
		}
		[Test] public void SweepEqualsSpokeReferenceAndRewindClears()
		{
			var incremental = Engine(Wall(100, 20)); var reference = Engine(Wall(100, 20));
			incremental.Advance(0, Frame());
			for (var i = 1; i <= 10; i++) { incremental.Advance(i * .25, Frame()); while (incremental.PendingSpokes > 0) incremental.Advance(i * .25, Frame()); }
			for (var s = 0; s < 128; s++) reference.RenderSpoke(s, 0, Frame());
			for (var s = 0; s < 128; s++) for (var b = 0; b < reference.Scale.Bins; b++)
				Assert.That(incremental.Echo(s, b), Is.EqualTo(reference.Echo(s, b)));
			incremental.Advance(0, Frame()); Assert.That(incremental.Echo(0, 0), Is.Zero);
		}
		[Test] public void TargetSmearClutterAndInertRainAreDeterministic()
		{
			var engine = Engine(); engine.Sea = 0;
			var targets = new[] { new RadarTarget(200, 0, 30, 8) };
			engine.RenderSpoke(0, 0, Frame(), targets, 1);
			Assert.That(engine.Echo(0, engine.Scale.Bin(200)), Is.GreaterThan(.8));
			Assert.That(engine.Echo(0, engine.Scale.Bin(210)), Is.GreaterThan(.3));
			engine.RenderSpoke(1, 0, Frame(), targets, 1);
			Assert.That(engine.Echo(1, engine.Scale.Bin(200)), Is.GreaterThan(.3));
			var wind = new RadarFrame(0, 0, 0, 0, 10, 15);
			engine.RenderSpoke(0, 2, wind); var rough = engine.Echo(0, 10);
			engine.Rain = 1; engine.RenderSpoke(0, 2, wind); Assert.That(engine.Echo(0, 10), Is.EqualTo(rough));
			engine.Sea = 1; engine.RenderSpoke(0, 2, wind); Assert.That(engine.Echo(0, 10), Is.LessThan(rough));
		}
		[Test] public void TargetHeightChangesEchoStrengthAndLandObeysHorizon()
		{
			var engine = Engine(); engine.Sea = 0;
			engine.RenderSpoke(0, 0, Frame(), new[] { new RadarTarget(200, 0, 20, 3) }, 1);
			var low = engine.Echo(0, engine.Scale.Bin(200));
			engine.RenderSpoke(0, 0, Frame(), new[] { new RadarTarget(200, 0, 20, 12) }, 1);
			Assert.That(engine.Echo(0, engine.Scale.Bin(200)), Is.GreaterThan(low));
			var land = Engine(Wall(9000, null, true)); land.SetRange(6); land.Sea = 0;
			land.RenderSpoke(0, 0, new RadarFrame(0, 0, 0, 0, 1, 0));
			Assert.That(land.Echo(0, land.Scale.Bin(9000)), Is.LessThan(.1));
		}

		[Test] public void LongJumpIsBoundedAndRangeClearsOldPicture()
		{
			var engine = Engine(); engine.Advance(0, Frame()); engine.Advance(10000, Frame());
			Assert.That(engine.LastSpokeCount, Is.EqualTo(4)); Assert.That(engine.PendingSpokes, Is.EqualTo(124));
			var rendered = engine.LastSpokeCount; while (engine.PendingSpokes > 0) { engine.Advance(10000, Frame()); Assert.That(engine.LastSpokeCount, Is.LessThanOrEqualTo(4)); rendered += engine.LastSpokeCount; } Assert.That(rendered, Is.EqualTo(128));
			engine.SetRange(0); Assert.That(engine.Echo(0, 10), Is.Zero);
			engine.Advance(10000, Frame()); Assert.That(engine.LastSpokeCount, Is.Zero);
		}

		[Test] public void LocalSubtractionPreservesNarrowFacesAcrossGridBoundaries()
		{
			var a = Engine(new RadarFeature(new[] { new RadarSegment(100.25, -.25, 100.25, .25) }, 20));
			const double shift = 1000000000;
			var b = Engine(new RadarFeature(new[] { new RadarSegment(shift + 100.25, shift - .25, shift + 100.25, shift + .25) }, 20));
			a.RenderSpoke(0, 0, Frame()); b.RenderSpoke(0, 0, new RadarFrame(shift, shift, 0, 0, 10, 0));
			for (var bin = 0; bin < a.Scale.Bins; bin++) Assert.That(b.Echo(0, bin), Is.EqualTo(a.Echo(0, bin)));
		}

		[Test] public void NoiseIsRepeatableAndSpokesAllocateNothing()
		{
			var engine = Engine(Wall(100, 20)); var frame = Frame();
			engine.RenderSpoke(7, 4, frame); var value = engine.Echo(7, 5);
			engine.RenderSpoke(7, 4, frame); Assert.That(engine.Echo(7, 5), Is.EqualTo(value));
			for (var i = 0; i < 256; i++) engine.RenderSpoke(i % 128, 4, frame);
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 1000; i++) engine.RenderSpoke(i % 128, 4, frame);
			var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.That(bytes, Is.Zero);
		}
	}
}
