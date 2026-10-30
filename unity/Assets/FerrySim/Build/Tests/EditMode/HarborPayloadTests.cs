using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace FerrySim.Build.Tests
{
	/// <summary>Missing roots, intermediates, dot files, escaped content, stale hashes, overlap and wrong scenes must fail closed.</summary>
	public class HarborPayloadTests
	{
		private string _root;
		[SetUp] public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "ferrysim-harbor-payload-" + Guid.NewGuid().ToString("N"));
		[TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

		[Test] public void MissingRootIsRefused() => Assert.Throws<InvalidOperationException>(() => HarborPayload.EnumerateRoots(_root, new[] { "missing" }));
		[TestCase("_sources/raw.bin"), TestCase(".DS_Store"), TestCase("nested/.hidden/file"), TestCase("working.tif"), TestCase("build.log")]
		public void ExcludedContentsAreRefused(string name)
		{
			Write("traffic/2025-07-16/" + name, "x");
			Assert.Throws<InvalidOperationException>(() => HarborPayload.EnumerateRoots(_root, new[] { "traffic/2025-07-16" }));
		}
		[Test] public void TrafficSourcesOutsideExplicitDateAreNotPackaged()
		{
			Write("traffic/_sources/raw.bin", "x");
			Write("traffic/2025-07-16/reports.bin", "x");
			Assert.That(HarborPayload.EnumerateRoots(_root, new[] { "traffic/2025-07-16" }), Is.EqualTo(new[] { "traffic/2025-07-16/reports.bin" }));
		}
		[Test] public void DuplicateFilesAcrossRootsAreRefused()
		{
			Write("runtime/data.json", "{}");
			Assert.Throws<InvalidOperationException>(() => HarborPayload.EnumerateRoots(_root, new[] { "runtime", "runtime/data.json" }));
		}
		[TestCase("../outside.glb"), TestCase("%2e%2e/outside.glb"), TestCase("https://example.com/a.glb"), TestCase("/a.glb")]
		public void EscapingTileUriIsRefused(string uri)
		{
			Tiles("{\"asset\":{\"version\":\"1.1\"},\"root\":{\"content\":{\"uri\":\"" + uri + "\"}}}");
			Assert.Throws<InvalidOperationException>(() => HarborPayload.ValidateTileset(_root, "tiles"));
		}
		[Test] public void LegacyTileUrlCannotEscapeItsRoot()
		{
			Tiles("{\"asset\":{\"version\":\"1.0\"},\"root\":{\"content\":{\"url\":\"../outside.glb\"}}}");
			Assert.Throws<InvalidOperationException>(() => HarborPayload.ValidateTileset(_root, "tiles"));
		}
		[Test] public void UnknownTilesetSchemaIsRefused()
		{
			Tiles("{\"asset\":{\"version\":\"1.1\"},\"root\":{\"content\":{\"uri\":\"data.glb\"}}}");
			var path = Path.Combine(_root, "tiles/index.json");
			File.WriteAllText(path, File.ReadAllText(path).Replace("harbor-3dtiles-v1", "unsupported/99"));
			Assert.Throws<InvalidOperationException>(() => HarborPayload.ValidateTileset(_root, "tiles"));
		}
		[Test] public void StaleIndexHashIsRefused()
		{
			Tiles("{\"asset\":{\"version\":\"1.1\"},\"root\":{\"content\":{\"uri\":\"data.glb\"}}}");
			Write("tiles/data.glb", "changed");
			Assert.Throws<InvalidOperationException>(() => HarborPayload.ValidateTileset(_root, "tiles"));
		}
		[Test] public void HarborIsTheOnlyEnabledScene()
		{
			Assert.That(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path), Is.EqualTo(new[] { HarborPayload.Scene }));
			Assert.DoesNotThrow(() => HarborPayload.ValidateScenes(new[] { HarborPayload.Scene }));
			Assert.Throws<InvalidOperationException>(() => HarborPayload.ValidateScenes(new[] { HarborPayload.Scene, HarborPayload.SliceScene }));
		}
		[TestCase(HarborPayload.Scene, false), TestCase(HarborPayload.SliceScene, true), TestCase("Assets/Other.unity", false)]
		public void SliceSmokeRunsOnlyForSliceFirstPlayer(string scene, bool expected) =>
			Assert.That(FerrySim.Slice.PlayerSmokeRun.IsSlicePlayer(scene), Is.EqualTo(expected));

		[Test, Category("HarborRealData")] public void RealHarborInventoryVerifiesBeforeAnyPlayerBuild()
		{
			if (!Directory.Exists(Path.Combine(PlayerPayload.RepoRoot, HarborPayload.Harbor))) Assert.Ignore("Copied harbor outputs unavailable");
			Assert.DoesNotThrow(() => HarborPayload.ValidateRepository(PlayerPayload.RepoRoot));
		}
		[Test] public void ChartRootIsConfigured()
		{
			var roots = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(PlayerPayload.RepoRoot, HarborPayload.Configuration)))["roots"].Values<string>().ToArray();
			Assert.That(roots, Does.Contain(HarborPayload.Chart));
			Assert.That(roots.Distinct().Count(), Is.EqualTo(roots.Length));
		}
		/// <summary>Review item 1: the chart records the skyline index, every consumed attributes sidecar and the runtime manifest it was built from; a release with any of them changed must fail before a build, naming the file.</summary>
		[Test, Category("HarborRealData")] public void ChartBuiltAgainstAnotherSkylineOrSidecarIsRefusedBeforeAnyBuild()
		{
			var harbor = Path.Combine(PlayerPayload.RepoRoot, HarborPayload.Harbor);
			if (!Directory.Exists(Path.Combine(harbor, "runtime")) || !Directory.Exists(Path.Combine(harbor, "chart"))) Assert.Ignore("Copied harbor outputs unavailable");
			var runtime = FerrySim.Core.Serialization.HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			CopyChartInputs(harbor);
			Assert.DoesNotThrow(() => HarborPayload.ValidateChart(_root, runtime), "the unchanged real inputs pass");
			// An internally valid but different skyline index with the old chart.
			var index = Path.Combine(_root, HarborPayload.Harbor, "3dtiles-buildings/index.json");
			var original = File.ReadAllText(index);
			File.WriteAllText(index, original.Replace("\"schema\"", "\"review_note\": \"rebuilt skyline\", \"schema\""));
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains("3dtiles-buildings/index.json"), "a chart built against another skyline");
			File.WriteAllText(index, original);
			Assert.DoesNotThrow(() => HarborPayload.ValidateChart(_root, runtime));
			// A changed consumed sidecar.
			var sidecar = Directory.GetFiles(Path.Combine(_root, HarborPayload.Harbor, "3dtiles-buildings"), "attributes.json.gz", SearchOption.AllDirectories)[0];
			var bytes = File.ReadAllBytes(sidecar);
			File.WriteAllBytes(sidecar, bytes.Concat(new byte[] { 0 }).ToArray());
			var relative = sidecar.Substring(Path.Combine(_root, HarborPayload.Harbor).Length + 1).Replace('\\', '/');
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains(relative), "a changed sidecar");
			File.WriteAllBytes(sidecar, bytes);
			// A changed runtime manifest file on disk.
			var manifest = Path.Combine(_root, HarborPayload.Harbor, "runtime/manifest.json");
			var manifestText = File.ReadAllText(manifest);
			File.WriteAllText(manifest, manifestText + "\n");
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains("runtime/manifest.json"), "a changed runtime manifest");
		}

		/// <summary>The chart folder, the runtime folder and only the skyline files the chart manifest records, into the fixture root.</summary>
		private void CopyChartInputs(string harbor)
		{
			foreach (var folder in new[] { "chart", "runtime" })
			{
				Directory.CreateDirectory(Path.Combine(_root, HarborPayload.Harbor, folder));
				foreach (var file in Directory.GetFiles(Path.Combine(harbor, folder))) File.Copy(file, Path.Combine(_root, HarborPayload.Harbor, folder, Path.GetFileName(file)));
			}
			var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(harbor, "chart/manifest.json")));
			foreach (var source in manifest["sources"])
			{
				var path = (string)source["file"];
				if (!path.StartsWith("3dtiles-buildings/", StringComparison.Ordinal)) continue;
				var target = Path.Combine(_root, HarborPayload.Harbor, path);
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.Copy(Path.Combine(harbor, path), target);
			}
		}

		[Test, Category("HarborRealData")] public void MissingOrStaleChartPackIsRefusedBeforeAnyBuild()
		{
			var harbor = Path.Combine(PlayerPayload.RepoRoot, HarborPayload.Harbor);
			if (!Directory.Exists(Path.Combine(harbor, "runtime")) || !Directory.Exists(Path.Combine(harbor, "chart"))) Assert.Ignore("Copied harbor outputs unavailable");
			var runtime = FerrySim.Core.Serialization.HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains("Missing chart pack"));
			Directory.CreateDirectory(Path.Combine(_root, HarborPayload.Chart));
			foreach (var file in Directory.GetFiles(Path.Combine(harbor, "chart")))
			{
				if (Path.GetFileName(file) == "radar_obstructions.json") Write(HarborPayload.Chart + "/radar_obstructions.json", "{}");
				else File.Copy(file, Path.Combine(_root, HarborPayload.Chart, Path.GetFileName(file)));
			}
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains("Stale or invalid chart pack").And.Message.Contains("DIGEST").And.Message.Contains("digest mismatch"));
			File.Delete(Path.Combine(_root, HarborPayload.Chart, "DIGEST"));
			Assert.That(() => HarborPayload.ValidateChart(_root, runtime), Throws.InvalidOperationException.With.Message.Contains("Missing chart DIGEST"));
		}

		private void Tiles(string json)
		{
			Write("tiles/tileset.json", json);
			Write("tiles/data.glb", "x");
			Write("tiles/index.json", "{\"schema\":\"harbor-3dtiles-v1\",\"tileset\":\"tileset.json\",\"files\":[{\"path\":\"tileset.json\",\"bytes\":" + System.Text.Encoding.UTF8.GetByteCount(json) + ",\"sha256\":\"" + HarborPayload.HashFile(Path.Combine(_root, "tiles/tileset.json")) + "\"},{\"path\":\"data.glb\",\"bytes\":1,\"sha256\":\"" + HarborPayload.HashFile(Path.Combine(_root, "tiles/data.glb")) + "\"}]}");
		}
		private void Write(string path, string value)
		{
			var full = Path.Combine(_root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, value);
		}
	}
}
