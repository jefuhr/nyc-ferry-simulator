using System;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>Guards an off-centre shoal missed by centre/outline-only sampling, wrong tide time, and unknown bed treated as safe.</summary>
	public class ScenarioStartDepthTests
	{
		private static readonly HorizontalVector[] Hull = {
			new HorizontalVector(0, 0), new HorizontalVector(8, 0), new HorizontalVector(8, 8), new HorizontalVector(0, 8) };

		private static DataEnvironment Environment(float shoal, bool outside = false)
		{
			var values = Enumerable.Repeat(-10f, 121).ToArray();
			values[(outside ? 10 : 3) * 11 + 5] = shoal;
			// Grid nodes deliberately miss the world quarter-metre lattice.
			var grid = new DepthGrid("test", -.87, -.87, 1, 11, 11, values);
			var depth = new DepthSet("test", "NAVD88", 20, new[] { grid }, Array.Empty<DepthSource>(), string.Empty);
			var times = new[] { 0.0, 600.0 };
			var current = new CurrentStation("current", "Synthetic", "synthetic", 40.7, -74.015, 0, 0, null, "synthetic", "",
				new VectorSeries(times, new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }));
			var level = new WaterLevelStation("level", "Synthetic", 40.7, -74.015, 0, 0, "synthetic", "",
				new ScalarSeries(times, new[] { .5, 1.5 }), new[] { false, false });
			var epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);
			return new DataEnvironment(new EnvironmentPack("test", "2025-07-14", epoch, 0, 600, depth,
				new[] { current }, new[] { level }, Array.Empty<WindStation>(), Array.Empty<string>(), ""), epoch);
		}

		[Test]
		public void ShallowInteriorGridNode_AndChosenTideTime_SetTheMinimum()
		{
			var environment = Environment(-.4f);
			Assert.That(SliceScenarioCatalog.MinimumStartDepth(environment, Hull, 0), Is.EqualTo(.9).Within(1e-6));
			Assert.That(SliceScenarioCatalog.MinimumStartDepth(environment, Hull, 600), Is.EqualTo(1.9).Within(1e-6));
		}

		[Test]
		public void UnknownBedUnderTheHull_ReturnsNoNumericalMinimum() =>
			Assert.That(double.IsNaN(SliceScenarioCatalog.MinimumStartDepth(Environment(float.NaN), Hull, 0)), Is.True);

		[Test]
		public void ShoalOutsideTheHull_DoesNotSetItsMinimum() =>
			Assert.That(SliceScenarioCatalog.MinimumStartDepth(Environment(-.4f, true), Hull, 0), Is.EqualTo(10.5).Within(1e-6));
	}
}
