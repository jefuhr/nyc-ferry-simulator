using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Chart
{
	// Failure modes: semantic drift, lost holes/components/evidence, rounded doubles, grid drift,
	// different candidate ordering/truncation, and allocation metrics accidentally including forced GC.
	public class HarborChartStreamingTests
	{
		private static string Harbor => Path.Combine(RepoPaths.Root, "data/processed/harbor");

		[Test, Explicit("Requires the copied real chart/runtime pack; run by exact test filter.")]
		public void BaselineLoadMeasurements() => Measure(true);

		[Test, Explicit("Requires the copied real chart/runtime pack; run by exact test filter.")]
		public void StreamingLoadMeasurements() => Measure(false);

		private static void Measure(bool legacy)
		{
			var runtime = HarborRuntimePackJson.Load(Path.Combine(Harbor, "runtime"));
			var selected = Environment.GetEnvironmentVariable("FERRYSIM_CHART_MEASUREMENT_RUN");
			if (selected != null) MeasureOnce(legacy, int.Parse(selected), runtime);
			else for (var run = 1; run <= 3; run++) MeasureOnce(legacy, run, runtime);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void MeasureOnce(bool legacy, int run, HarborRuntimePack runtime)
		{
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			var retainedBefore = GC.GetTotalMemory(true);
			var heapBefore = GC.GetGCMemoryInfo();
			var watch = new Stopwatch();
			var allocatedBefore = GC.GetTotalAllocatedBytes(true);
			var gen2Before = GC.CollectionCount(2);
			watch.Start();
			var chart = legacy ? LegacyHarborChartPackJson.Load(Path.Combine(Harbor, "chart"), runtime) : HarborChartPackJson.Load(Path.Combine(Harbor, "chart"), runtime);
			watch.Stop();
			var gen2 = GC.CollectionCount(2) - gen2Before;
			var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
			var retained = GC.GetTotalMemory(true) - retainedBefore;
			var heapAfter = GC.GetGCMemoryInfo();
			var heapUsed = heapAfter.HeapSizeBytes - heapAfter.FragmentedBytes - heapBefore.HeapSizeBytes + heapBefore.FragmentedBytes;
			TestContext.Progress.WriteLine($"reader={(legacy ? "baseline" : "streaming")} run={run} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} retained_bytes={retained} gen2={gen2} heap_used_delta_bytes={heapUsed} digest={chart.Digest}");
			GC.KeepAlive(chart); GC.KeepAlive(runtime);
		}

		[Test, Explicit("Requires the copied real chart/runtime pack; run by exact test filter.")]
		public void RealPackMatchesFrozenReaderBitExactly()
		{
			var runtime = HarborRuntimePackJson.Load(Path.Combine(Harbor, "runtime"));
			var old = LegacyHarborChartPackJson.Load(Path.Combine(Harbor, "chart"), runtime);
			var current = HarborChartPackJson.Load(Path.Combine(Harbor, "chart"), runtime);
			Assert.That(current.Digest, Is.EqualTo(old.Digest));
			Assert.That(current.RuntimeDigest, Is.EqualTo(old.RuntimeDigest));
			Value(current.Manifest, old.Manifest);
			var gridField = typeof(ChartFeatureLayer).GetField("_grid", BindingFlags.NonPublic | BindingFlags.Instance);
			for (var l = 0; l < 8; l++)
			{
				var a = current[(ChartLayer)l]; var b = old[(ChartLayer)l];
				Assert.That(a.Features.Count, Is.EqualTo(b.Features.Count));
				for (var i = 0; i < a.Features.Count; i++)
				{
					var f = a.Features[i]; var g = b.Features[i];
					Assert.That(f.Id, Is.EqualTo(g.Id)); Assert.That(f.SourceId, Is.EqualTo(g.SourceId)); Assert.That(f.FeatureClass, Is.EqualTo(g.FeatureClass));
					Bits(f.MinimumNorth, g.MinimumNorth); Bits(f.MinimumEast, g.MinimumEast); Bits(f.MaximumNorth, g.MaximumNorth); Bits(f.MaximumEast, g.MaximumEast);
					Value(f.Record, g.Record); CompareGeometry(f.Geometry, g.Geometry);
				}
				var gridA = (Dictionary<long, int[]>)gridField.GetValue(a); var gridB = (Dictionary<long, int[]>)gridField.GetValue(b);
				Assert.That(gridA.Count, Is.EqualTo(gridB.Count));
				foreach (var cell in gridB) { Assert.That(gridA.ContainsKey(cell.Key), Is.True); Assert.That(gridA[cell.Key], Is.EqualTo(cell.Value)); }
				foreach (var capacity in new[] { 0, 7, a.Features.Count })
				{
					var qa = a.CreateQueryBuffer(capacity); var qb = b.CreateQueryBuffer(capacity);
					for (var i = 0; i < 64; i++)
					{
						var feature = a.Features[i * a.Features.Count / 64];
						var n = (feature.MinimumNorth + feature.MaximumNorth) * .5; var e = (feature.MinimumEast + feature.MaximumEast) * .5;
						var r = i % 4 == 0 ? 0 : i % 4 == 1 ? 250 : i % 4 == 2 ? 1000 : 100000;
						a.QueryCircle(n, e, r, qa); b.QueryCircle(n, e, r, qb); Query(qa, qb);
						a.QueryBox(n - r, e - r, n + r, e + r, qa); b.QueryBox(n - r, e - r, n + r, e + r, qb); Query(qa, qb);
					}
				}
				TestContext.Progress.WriteLine($"equivalent layer={(ChartLayer)l} features={a.Features.Count} cells={gridA.Count} query_pairs=384");
			}
		}

		private static void Query(ChartQueryBuffer a, ChartQueryBuffer b)
		{
			Assert.That(a.Count, Is.EqualTo(b.Count)); Assert.That(a.TotalMatches, Is.EqualTo(b.TotalMatches)); Assert.That(a.Truncated, Is.EqualTo(b.Truncated));
			for (var i = 0; i < a.Count; i++) Assert.That(a[i], Is.EqualTo(b[i]));
		}
		private static void Bits(double a, double b) => Assert.That(BitConverter.DoubleToInt64Bits(a), Is.EqualTo(BitConverter.DoubleToInt64Bits(b)));
		private static void Points(IReadOnlyList<HorizontalVector> a, IReadOnlyList<HorizontalVector> b)
		{
			Assert.That(a.Count, Is.EqualTo(b.Count));
			for (var i = 0; i < a.Count; i++) { Bits(a[i].North, b[i].North); Bits(a[i].East, b[i].East); }
		}
		internal static void CompareGeometry(HarborGeometry a, HarborGeometry b)
		{
			if (b == null) { Assert.That(a, Is.Null); return; }
			Assert.That(a.Type, Is.EqualTo(b.Type)); Assert.That(a.Polygons.Count, Is.EqualTo(b.Polygons.Count)); Assert.That(a.Lines.Count, Is.EqualTo(b.Lines.Count));
			for (var i = 0; i < a.Polygons.Count; i++)
			{
				Assert.That(a.Polygons[i].Rings.Count, Is.EqualTo(b.Polygons[i].Rings.Count));
				for (var j = 0; j < a.Polygons[i].Rings.Count; j++) Points(a.Polygons[i].Rings[j], b.Polygons[i].Rings[j]);
			}
			for (var i = 0; i < a.Lines.Count; i++) Points(a.Lines[i], b.Lines[i]);
			Points(a.Points, b.Points);
		}
		private static void Value(HarborValue a, HarborValue b)
		{
			Assert.That(a.Text, Is.EqualTo(b.Text)); Assert.That(a.Boolean, Is.EqualTo(b.Boolean)); Assert.That(a.IsNull, Is.EqualTo(b.IsNull));
			Assert.That(a.Number.HasValue, Is.EqualTo(b.Number.HasValue)); if (b.Number.HasValue) Bits(a.Number.Value, b.Number.Value);
			Assert.That(a.Items == null, Is.EqualTo(b.Items == null)); Assert.That(a.Fields == null, Is.EqualTo(b.Fields == null));
			if (b.Items != null) { Assert.That(a.Items.Count, Is.EqualTo(b.Items.Count)); for (var i = 0; i < a.Items.Count; i++) Value(a.Items[i], b.Items[i]); }
			if (b.Fields != null) { Assert.That(a.Fields.Count, Is.EqualTo(b.Fields.Count)); foreach (var field in b.Fields) { Assert.That(a.Fields.ContainsKey(field.Key), Is.True); Value(a.Fields[field.Key], field.Value); } }
		}
	}
}
