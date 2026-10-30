using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Radar;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Radar
{
	// Failure modes: dedup drops a nearer face, compact storage shifts a range bin,
	// retained duplicate geometry hides in memory measurements, and a PNG masks changed amplitudes.
	public class RadarPerfTests
	{
		[Test, Explicit("Copied resident packs and frozen radar capture reference; select exact method.")]
		public void RealCaptureIdentityAndResidentMemory()
		{
			var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
			while (repo != null && !File.Exists(Path.Combine(repo.FullName, "README.md"))) repo = repo.Parent;
			Assert.That(repo, Is.Not.Null);
			var root = repo.FullName; var harbor = Path.Combine(root, "data/processed/harbor");
			var folder = Path.Combine(root, "artifacts/radar/perf"); var stage = Environment.GetEnvironmentVariable("RADAR_PERF_STAGE") ?? "final";
			var output = Path.Combine(folder, stage); Directory.CreateDirectory(output);
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime")); var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			var traffic = new TrafficRun(TrafficPackJson.Load(Path.Combine(harbor, "traffic/2025-07-16")), DateTimeOffset.Parse("2025-07-16T16:18:00Z", CultureInfo.InvariantCulture), TrafficLeaveOut.Off, 0);
			var before = GC.GetTotalMemory(true); var clock = Stopwatch.StartNew(); var scene = Build(chart, runtime); var buildMs = clock.Elapsed.TotalMilliseconds;
			var after = GC.GetTotalMemory(true); var engine = new RadarEngine(scene); var withEngine = GC.GetTotalMemory(true);
			File.WriteAllText(Path.Combine(output, "dotnet-memory.txt"), $"Index managed delta={after - before} bytes; engine delta={withEngine - after} bytes; cells={scene.SpatialCellCount}; features={scene.FeatureCount}; edges={scene.EdgeCount}; cell_references={scene.CellReferenceCount}; before={before}; after={after}; build_ms={buildMs.ToString("R", CultureInfo.InvariantCulture)}\nGC.GetTotalMemory(true), shared chart/runtime/traffic loaded before baseline; retained index, not peak construction memory.\n");
			var targets = new RadarTarget[traffic.Pack.Vessels.Count]; var count = 0;
			for (var i = 0; i < traffic.Pack.Vessels.Count; i++)
			{
				if (i == traffic.RemovedVessel || !traffic.Replay.TrySample(i, traffic.PackTime(0), out var sample)) continue;
				var length = traffic.Pack.Vessels[i].Length;
				targets[count++] = new RadarTarget(sample.North, sample.East, length, Math.Max(3, Math.Min(35, length * .15)));
			}
			var result = new StringBuilder("capture,amplitudes,different_bits,sha256\n");
			foreach (var line in File.ReadAllLines(Path.Combine(folder, "baseline/captures.csv")))
			{
				if (line.StartsWith("capture,", StringComparison.Ordinal)) continue;
				var c = line.Split(','); double Number(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
				var scale = 0; while (RadarScale.At(scale).RangeMetres / 1852 != Number(3)) scale++;
				engine.SetRange(scale); engine.Reset(); var frame = new RadarFrame(Number(1), Number(2), .5, Number(6), 9, Number(7));
				engine.Advance(0, frame, targets, count); for (var i = 1; i <= 150; i++) engine.Advance(i / 60.0, frame, targets, count);
				var path = Path.Combine(output, c[0] + ".polar");
				using (var writer = new BinaryWriter(File.Create(path))) for (var s = 0; s < engine.Configuration.Spokes; s++) for (var b = 0; b < engine.Scale.Bins; b++) writer.Write(engine.Echo(s, b));
				var bytes = File.ReadAllBytes(path); var different = 0;
				if (stage != "baseline")
				{
					var baseline = File.ReadAllBytes(Path.Combine(folder, "baseline", c[0] + ".polar")); Assert.That(bytes.Length, Is.EqualTo(baseline.Length));
					for (var i = 0; i < bytes.Length; i += 4) if (BitConverter.ToInt32(bytes, i) != BitConverter.ToInt32(baseline, i)) different++;
				}
				result.AppendLine(c[0] + "," + bytes.Length / 4 + "," + different + "," + Convert.ToHexString(SHA256.HashData(bytes)));
				Assert.That(different, Is.Zero, c[0] + " must remain bit-identical");
			}
			File.WriteAllText(Path.Combine(output, "polar-identity.csv"), result.ToString());
			GC.KeepAlive(scene); GC.KeepAlive(chart); GC.KeepAlive(runtime); GC.KeepAlive(traffic); GC.KeepAlive(engine);
		}
		[MethodImpl(MethodImplOptions.NoInlining)] private static RadarScene Build(HarborChartPack chart, HarborRuntimePack runtime) => RadarScene.FromPacks(chart, runtime);
	}
}
