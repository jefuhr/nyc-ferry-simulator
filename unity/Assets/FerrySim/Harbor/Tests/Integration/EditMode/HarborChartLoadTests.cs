using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests.Integration
{
	/// <summary>
	/// Failure modes of the shared chart load: a start that blocks on the seconds the pack takes; a second scene loading
	/// a second copy; a corrupt pack throwing on the worker thread instead of reporting the file; a failure or a success
	/// published before its fields are.
	/// </summary>
	public class HarborChartLoadTests
	{
		[SetUp]
		public void Reset() => HarborChartLoad.ResetForTests();

		private static HarborChartPack FakePack(string digest)
		{
			var layers = new List<ChartFeatureLayer>();
			for (var i = 0; i < 8; i++)
			{
				layers.Add(new ChartFeatureLayer(new List<ChartFeature>(), new Dictionary<long, int[]>()));
			}

			return new HarborChartPack(digest, "runtime", layers, HarborValue.Null);
		}

		private static void WaitUntilSettled(HarborChartLoad load, double seconds = 10)
		{
			var watch = Stopwatch.StartNew();
			while (load.State == HarborChartLoadState.Loading)
			{
				Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(seconds), "the load did not settle");
				Thread.Sleep(5);
			}
		}

		[Test]
		public void BeginReturnsAtOnceAndASecondBeginSharesTheOneLoad()
		{
			using (var gate = new ManualResetEventSlim())
			{
				var calls = 0;
				var pack = FakePack("one");
				var load = HarborChartLoad.Begin("fake", () => { Interlocked.Increment(ref calls); gate.Wait(); return new HarborChartLoad.Result { Pack = pack }; });
				Assert.That(load.State, Is.EqualTo(HarborChartLoadState.Loading), "the caller never waits");
				Assert.That(load.Pack, Is.Null);
				Assert.That(double.IsNaN(load.TotalMilliseconds), Is.True);
				var again = HarborChartLoad.Begin("fake", () => throw new InvalidOperationException("a second load must never start"));
				Assert.That(again, Is.SameAs(load));
				Assert.That(HarborChartLoad.StartedCount, Is.EqualTo(1));
				Assert.That(HarborChartLoad.Find("fake"), Is.SameAs(load));
				gate.Set();
				WaitUntilSettled(load);
				Assert.That(load.IsReady, Is.True, load.Error);
				Assert.That(calls, Is.EqualTo(1), "exactly once per process");
				Assert.That(load.Pack, Is.SameAs(pack));
				Assert.That(again.Pack, Is.SameAs(pack), "no second copy");
				Assert.That(load.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
			}
		}

		[Test]
		public void AFailingLoadReportsItsErrorInsteadOfThrowing()
		{
			var load = HarborChartLoad.Begin("broken", () => throw new HarborChartPackException("/packs/chart/DIGEST", "aggregate digest mismatch."));
			WaitUntilSettled(load);
			Assert.That(load.IsFailed, Is.True);
			Assert.That(load.Error, Does.Contain("DIGEST").And.Contain("digest mismatch"));
			Assert.That(load.Pack, Is.Null);
			Assert.That(load.RadarScene, Is.Null);
			Assert.That(load.Tessellation, Is.Null);
		}

		[Test]
		public void AnEmptyResultIsAFailure()
		{
			var load = HarborChartLoad.Begin("empty", () => new HarborChartLoad.Result());
			WaitUntilSettled(load);
			Assert.That(load.IsFailed, Is.True);
			Assert.That(load.Error, Does.Contain("no pack"));
		}

		[Test, Category("HarborRealData")]
		public void ACorruptPackOnDiskFailsNamingTheFile()
		{
			var harbor = Path.Combine(SimPaths.RepoRoot, "data", "processed", "harbor");
			if (!Directory.Exists(Path.Combine(harbor, "runtime")) || !Directory.Exists(Path.Combine(harbor, "chart")))
			{
				Assert.Ignore("The harbor data is not copied.");
			}

			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var folder = Path.Combine(Path.GetTempPath(), "ferrysim-corrupt-chart-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			try
			{
				foreach (var file in Directory.GetFiles(Path.Combine(harbor, "chart")))
				{
					var name = Path.GetFileName(file);
					if (name == "soundings.json")
					{
						File.WriteAllText(Path.Combine(folder, name), "[]");
					}
					else
					{
						File.Copy(file, Path.Combine(folder, name));
					}
				}

				var load = HarborChartLoad.Start(folder, runtime);
				WaitUntilSettled(load, 60);
				Assert.That(load.IsFailed, Is.True);
				Assert.That(load.Error, Does.Contain("DIGEST").Or.Contain("soundings.json"));
				Assert.That(load.Pack, Is.Null);
			}
			finally
			{
				Directory.Delete(folder, true);
			}
		}

		[Test, Category("HarborRealData")]
		public void TheRealPackLoadsOnceWithItsIndexAndTessellation()
		{
			var harbor = Path.Combine(SimPaths.RepoRoot, "data", "processed", "harbor");
			if (!Directory.Exists(Path.Combine(harbor, "runtime")) || !Directory.Exists(Path.Combine(harbor, "chart")))
			{
				Assert.Ignore("The harbor data is not copied.");
			}

			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var folder = Path.Combine(harbor, "chart");
			var first = HarborChartLoad.Start(folder, runtime);
			WaitUntilSettled(first, 120);
			Assert.That(first.IsReady, Is.True, first.Error);
			Assert.That(first.Pack.Digest, Is.EqualTo(File.ReadAllText(Path.Combine(folder, "DIGEST")).Trim()));
			Assert.That(first.Pack.RuntimeDigest, Is.EqualTo(runtime.Digest));
			Assert.That(first.RadarScene.FeatureCount, Is.GreaterThan(1000));
			Assert.That(first.Tessellation.VertexCount, Is.GreaterThan(1000));
			Assert.That(first.Tessellation.Stops.Count, Is.EqualTo(24));
			var before = GC.GetTotalMemory(true);
			var second = HarborChartLoad.Start(folder, runtime);
			var after = GC.GetTotalMemory(true);
			Assert.That(second, Is.SameAs(first), "a second scene shares the one load");
			Assert.That(second.Pack, Is.SameAs(first.Pack));
			Assert.That(HarborChartLoad.StartedCount, Is.EqualTo(1));
			Assert.That(after - before, Is.LessThan(1 << 20), "no second copy in memory");
			TestContext.WriteLine("chart load " + first.LoadMilliseconds.ToString("F0") + " ms, radar index " + first.IndexMilliseconds.ToString("F0") + " ms, tessellation " +
				first.TessellationMilliseconds.ToString("F0") + " ms, total " + first.TotalMilliseconds.ToString("F0") + " ms; managed " + first.ManagedBytesBefore + " -> " + first.ManagedBytesAfter + " bytes");
		}
	
		/// <summary>Review item 3: a second composition loads its own runtime pack; the first one, used by the shared load, must then be collectable.</summary>
		[Test, Category("HarborRealData")]
		public void ASecondCompositionDoesNotKeepTheFirstRuntimePackAlive()
		{
			var harbor = Path.Combine(SimPaths.RepoRoot, "data", "processed", "harbor");
			if (!Directory.Exists(Path.Combine(harbor, "runtime")) || !Directory.Exists(Path.Combine(harbor, "chart")))
			{
				Assert.Ignore("The harbor data is not copied.");
			}

			var folder = Path.Combine(harbor, "chart");
			var first = StartWithAFreshRuntime(harbor, folder, out var firstRuntime);
			WaitUntilSettled(first, 120);
			Assert.That(first.IsReady, Is.True, first.Error);
			// The second composition holds its own pack (as a scene's services do); the first is referenced by nothing but the cache's products.
			var secondPack = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var second = HarborChartLoad.Start(folder, secondPack);
			Assert.That(second, Is.SameAs(first), "the same key shares the one load");
			for (var i = 0; i < 5 && firstRuntime.IsAlive; i++)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
			}

			Assert.That(firstRuntime.IsAlive, Is.False, "the obsolete runtime pack is collectable: the cached tessellation holds no runtime pack");
			// By design the shared products reference no runtime pack at all (the fix for review item 3); the digest is the only link they carry, so it is the only secondary check.
			Assert.That(second.Tessellation.RuntimeDigest, Is.EqualTo(secondPack.Digest), "the shared products carry the live composition's pack digest");
			GC.KeepAlive(secondPack);
			GC.KeepAlive(second);
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static HarborChartLoad StartWithAFreshRuntime(string harbor, string folder, out WeakReference runtime)
		{
			var pack = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			runtime = new WeakReference(pack);
			return HarborChartLoad.Start(folder, pack);
		}
}
}
