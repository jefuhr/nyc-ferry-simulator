using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.World;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.HarborWater.Tests
{
	/// <summary>Failure modes: a uniform tide across the harbor, extrapolated datum, ECEF float loss, mesh seams and update allocations.</summary>
	public class HarborWaterTests
	{
		internal static HarborWaterService Synthetic()
		{
			var grid = new HarborGeoidLattice(0, 0, 250, 3, 3, new double[] { -32, -31.9, -31.8, -31.9, -31.8, -31.7, -31.8, -31.7, -31.6 }, new byte[] { 1, 1, 1, 1 });
			var times = new double[]
			{
				-3600,
				0,
				3600,
				90000
			};
			var origin = LocalTangentPlane.World.ToGeodetic(0, 0);
			var remote = LocalTangentPlane.World.ToGeodetic(500, 500);
			var gauges = new[]
			{
				new WaterLevelStation("a", "a", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, "synthetic", "", new ScalarSeries(times, new double[] { -1, 0, 1, 0 }), new bool[4]),
				new WaterLevelStation("b", "b", remote.LatitudeDegrees, remote.LongitudeDegrees, 500, 500, "synthetic", "", new ScalarSeries(times, new double[] { 1, 2, 3, 2 }), new bool[4]),
			};
			var depth = new DepthSet("harbor", "NAVD88", 20, new[] { new DepthGrid("synthetic", 0, 0, 500, 2, 2, new float[] { -10, -10, -10, -10 }) }, Array.Empty<DepthSource>(), "");
			var currents = new[]
			{
				new CurrentStation("c", "c", "harmonic-prediction", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, null, "synthetic", "", new VectorSeries(times, new double[4], new double[4]))
			};
			var epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);
			var pack = new EnvironmentPack("harbor", "2025-07-14", epoch, -3600, 90000, depth, currents, gauges, Array.Empty<WindStation>(), Array.Empty<string>(), "");
			return new HarborWaterService(new HarborDatum(grid), RunEnvironment.ForPack(pack, epoch));
		}

		[Test]
		public void Surface_IsLocalDatumPlusLocalTide_AndNeverSuppliesDepth()
		{
			var service = Synthetic();
			Assert.That(service.EllipsoidalHeight(0, 0, 0), Is.EqualTo(-32));
			Assert.That(service.EllipsoidalHeight(500, 500, 0), Is.EqualTo(-29.6).Within(1e-12));
			Assert.That(service, Is.Not.InstanceOf<IEnvironment>());
			Assert.Throws<InvalidOperationException>(() => service.EllipsoidalHeight(-1, 0, 0));
		}

		[Test]
		public void CellCoverage_RefusesMaskedAndInvalidCells_WithoutDroppingSharedEdges()
		{
			var grid = new HarborGeoidLattice(0, 0, 250, 3, 3, new double[9], new byte[] { 1, 0, 1, 1 });
			var service = new HarborWaterService(new HarborDatum(grid), Synthetic().Environment);
			Assert.That(service.CellCovered(0, 150, 100), Is.True, "touching a masked cell boundary has no positive-area overlap");
			Assert.That(service.CellCovered(0, 200, 100), Is.False);
			Assert.That(service.CellCovered(-1, 0, 100), Is.False);
			Assert.That(service.CellCovered(0, 0, double.NaN), Is.False);
			Assert.That(service.CellCovered(double.PositiveInfinity, 0, 100), Is.False);
		}

		[Test]
		public void BakedNodes_MatchExactSurface_AndHaveNoUpdateAllocation()
		{
			var service = Synthetic();
			var frame = WaterFrame.AtNorthEast(250, 250);
			var weights = new double[service.GaugeCount * 2];
			var nodes = new[]
			{
				service.BakeNode(0, 0, frame, weights, 0),
				service.BakeNode(183, 291, frame, weights, service.GaugeCount)
			};
			var levels = new double[service.GaugeCount];
			var vertices = new Vector3[2];
			service.SampleGaugeLevels(600, levels);
			service.UpdateNodes(nodes, weights, levels, vertices);
			var expected = WaterFrame.Ecef(183, 291, service.EllipsoidalHeight(183, 291, 600));
			Assert.That(math.distance(frame.ToEcef(vertices[1]), expected), Is.LessThan(0.0001));
			CalibrateAllocationRecorder();
			for (var i = 0; i < 10; i++)
			{
				service.SampleGaugeLevels(i, levels);
				service.UpdateNodes(nodes, weights, levels, vertices);
			}

			var recorder = BeginAllocations();
			for (var i = 0; i < 1000; i++)
			{
				service.SampleGaugeLevels(i, levels);
				service.UpdateNodes(nodes, weights, levels, vertices);
			}

			var allocations = EndAllocations(recorder);
			Assert.That(allocations, Is.Zero, "calibrated main-thread GC.Alloc samples; Mono byte counter is not used");
		}

		internal static Recorder BeginAllocations()
		{
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			recorder.enabled = true;
			return recorder;
		}

		internal static int EndAllocations(Recorder recorder)
		{
			recorder.enabled = false;
			recorder.CollectFromAllThreads();
			return recorder.sampleBlockCount;
		}

		internal static void CalibrateAllocationRecorder()
		{
			var chars = new char[8];
			var recorder = BeginAllocations();
			var value = new string (chars);
			var count = EndAllocations(recorder);
			GC.KeepAlive(value);
			Assert.That(count, Is.EqualTo(1), "positive control: one deliberate string allocation");
		}
	}
}
