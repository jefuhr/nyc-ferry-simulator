using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// The built harbor packs (gitignored, under data/processed/harbor/env) through the core: every day loads, with one
	/// depth grid per active tile and the station blend, each tile's grid decides inside its square, and the bed is
	/// continuous across every shared edge. Explicit: they need the pipeline's output, so they run by name and write their
	/// numbers, the load time and memory included, to artifacts/harbor-environment.
	/// </summary>
	[TestFixture]
	[Explicit("needs the built packs: PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region harbor --all")]
	public class HarborPackTests
	{
		private const double TileSize = 4000.0;

		private static string Environment => Path.Combine(RepoPaths.Root, "data", "processed", "harbor", "env");

		private static string Output
		{
			get
			{
				var directory = Path.Combine(RepoPaths.Root, "artifacts", "harbor-environment");
				Directory.CreateDirectory(directory);
				return directory;
			}
		}

		[Test]
		public void EveryBuiltDay_Loads_WithItsTileGridsAndTheStationBlend()
		{
			var days = new List<string>(Directory.GetDirectories(Path.Combine(Environment, "days")));
			days.Sort(StringComparer.Ordinal);
			var report = new StringBuilder("| Day | Day digest | Depth digest | Grids | Current stations | Gauges | Wind | Load s | Managed memory after load MB |\n|---|---|---|---:|---:|---:|---|---:|---:|\n");
			string depthDigest = null;
			var loads = new List<double>();
			foreach (var day in days)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
				var before = GC.GetTotalMemory(true);
				var watch = Stopwatch.StartNew();
				var pack = EnvironmentPackJson.Load(day);
				watch.Stop();
				var held = GC.GetTotalMemory(true) - before;
				loads.Add(watch.Elapsed.TotalSeconds);
				Assert.That(pack.Region, Is.EqualTo("harbor"));
				Assert.That(pack.CurrentField, Is.Null, "the harbor has no current field yet");
				Assert.That(new DataEnvironment(pack, pack.EpochUtc).CurrentField, Is.TypeOf<StationBlendCurrentField>());
				Assert.That(pack.Depth.Grids.Count, Is.EqualTo(29));
				depthDigest = depthDigest ?? pack.Depth.Digest;
				Assert.That(pack.Depth.Digest, Is.EqualTo(depthDigest), "one depth set for every day");
				report.Append($"| {pack.Date} | `{pack.Digest}` | `{pack.Depth.Digest}` | {pack.Depth.Grids.Count} | {pack.CurrentStations.Count} | " +
					$"{pack.WaterLevelStations.Count} | {(pack.WindStations.Count > 0 ? pack.WindStations[0].Id : "still air")} | {watch.Elapsed.TotalSeconds:F2} | {held / 1e6:F1} |\n");
				GC.KeepAlive(pack);
			}

			Assert.That(days.Count, Is.EqualTo(28));
			loads.Sort();
			report.Append($"\nLoad time per day, median {loads[loads.Count / 2]:F2} s, largest {loads[loads.Count - 1]:F2} s (each load reads and verifies the depth set again).\n");
			File.WriteAllText(Path.Combine(Output, "core-load.md"), report.ToString());
			TestContext.Out.WriteLine(report.ToString());
		}

		[Test]
		public void EachTilesGrid_DecidesInsideItsSquare_AndTheBedIsContinuousAcrossEverySharedEdge()
		{
			var depth = EnvironmentPackJson.LoadDepthSet(Path.Combine(Environment, "depth"));
			var tiles = new Dictionary<(int, int), int>();
			for (var g = 0; g < depth.Grids.Count; g++)
			{
				var grid = depth.Grids[g];
				var i = (int)Math.Round(grid.OriginNorth / TileSize);
				var j = (int)Math.Round(grid.OriginEast / TileSize);
				Assert.That(grid.Id, Is.EqualTo($"tile_{i}_{j}"));
				Assert.That((grid.EndNorth, grid.EndEast), Is.EqualTo(((i + 1) * TileSize, (j + 1) * TileSize)), "the square with both edges");
				Assert.That(depth.GridAt((i + 0.5) * TileSize, (j + 0.5) * TileSize), Is.EqualTo(g));
				tiles[(i, j)] = g;
			}

			// Across each shared edge every metre along it: the bed 1 mm either side, against what the bed's slope on each side
			// (over the 0.5 m before and after, inside the cells next to the seam, where the bilinear bed is linear across
			// it) gives over that millimetre. A step at the seam would remain in the difference.
			var report = new StringBuilder("| Seam | Points | Bed difference over 2 mm, largest m | Less the slopes either side, largest m |\n|---|---:|---:|---:|\n");
			var seams = 0;
			foreach (var tile in tiles.Keys)
			{
				foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
				{
					if (!tiles.ContainsKey((tile.Item1 + di, tile.Item2 + dj)))
					{
						continue;
					}

					seams++;
					double largest = 0.0, excess = 0.0;
					var edge = di == 1 ? (tile.Item1 + 1) * TileSize : (tile.Item2 + 1) * TileSize;
					for (var x = 0.5; x < TileSize; x += 1.0)
					{
						var along = (di == 1 ? tile.Item2 : tile.Item1) * TileSize + x;
						double Bed(double offset) => Sample(depth, di == 1, edge + offset, along);
						var difference = Bed(1e-3) - Bed(-1e-3);
						var slopes = (Bed(-1e-3) - Bed(-0.501)) / 0.5 + (Bed(0.501) - Bed(1e-3)) / 0.5;
						largest = Math.Max(largest, Math.Abs(difference));
						excess = Math.Max(excess, Math.Abs(difference - 1e-3 * slopes));
					}

					Assert.That(excess, Is.LessThan(1e-9), $"tile_{tile.Item1}_{tile.Item2} {(di == 1 ? "north" : "east")} edge");
					report.Append($"| tile_{tile.Item1}_{tile.Item2} / tile_{tile.Item1 + di}_{tile.Item2 + dj} | 4,000 | {largest:E2} | {excess:E2} |\n");
				}
			}

			Assert.That(seams, Is.GreaterThan(30));
			File.WriteAllText(Path.Combine(Output, "core-seams.md"), report.ToString());
			TestContext.Out.WriteLine(report.ToString());
		}

		private static double Sample(DepthSet depth, bool northEdge, double across, double along)
		{
			var kind = northEdge ? depth.Sample(across, along, out var bed) : depth.Sample(along, across, out bed);
			Assert.That(kind, Is.EqualTo(BedSample.Known));
			return bed;
		}
	}
}
