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
