using System;
using System.Runtime.CompilerServices;
using FerrySim.Core.Radar;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Radar
{
	public class RadarEdgeVisitTests
	{
		[Test] public void CompiledIndexDoesNotRetainInputFeatureObjectsAndTheirDuplicateEdges()
		{
			var scene = DetachedScene(out var input);
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			Assert.That(input.IsAlive, Is.False, "the compiled index must own compact geometry, not retain the input graph too");
			Assert.That(scene.FeatureCount, Is.EqualTo(1)); Assert.That(scene.EdgeCount, Is.EqualTo(1)); GC.KeepAlive(scene);
		}
		[MethodImpl(MethodImplOptions.NoInlining)] private static RadarScene DetachedScene(out WeakReference input)
		{
			var feature = new RadarFeature(new[] { new RadarSegment(-1000, -1000, 1000, 1000) }, 30);
			input = new WeakReference(feature); return new RadarScene(new[] { feature });
		}
		// A long diagonal occupies many cells. Deduplicating by feature rather than edge can lose its nearest face.
		[Test] public void LongEdgesMatchUndeduplicatedPictureAcrossCellsAndScales()
		{
			var scene = new RadarScene(new[]
			{
				new RadarFeature(new[] { new RadarSegment(-1000, -1000, 1000, 1000), new RadarSegment(100, -1000, 100, 1000) }, 30),
				new RadarFeature(new[] { new RadarSegment(500, -1000, 500, 1000) }, 90)
			});
			var expected = new RadarEngine(scene, new RadarConfiguration(128, deduplicateEdges: false));
			var actual = new RadarEngine(scene, new RadarConfiguration(128, deduplicateEdges: true));
			var frame = new RadarFrame(-500, 0, 0, .7, 9, 5);
			for (var scale = 0; scale < RadarScale.Count; scale++)
			{
				expected.SetRange(scale); actual.SetRange(scale);
				for (var spoke = 0; spoke < 128; spoke++)
				{
					expected.RenderSpoke(spoke, 3, frame); actual.RenderSpoke(spoke, 3, frame);
					for (var bin = 0; bin < actual.Scale.Bins; bin++) Assert.That(BitConverter.SingleToInt32Bits(actual.Echo(spoke, bin)), Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Echo(spoke, bin))));
				}
			}
			actual.RenderSpoke(0, 0, frame); expected.RenderSpoke(0, 0, frame);
			Assert.That(actual.LastIntersectionTests, Is.LessThanOrEqualTo(9), "three edges at most once per each of three rays");
			Assert.That(expected.LastIntersectionTests, Is.GreaterThan(actual.LastIntersectionTests * 5));
		}
	}
}
