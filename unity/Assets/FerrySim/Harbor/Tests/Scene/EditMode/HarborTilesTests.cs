using System;
using System.IO;
using System.Security.Cryptography;
using FerrySim.World.HarborTiles;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests
{
	public class HarborTilesTests
	{
		private string _folder;

		[SetUp]
		public void SetUp()
		{
			_folder = Path.Combine(Path.GetTempPath(), "harbor-tiles-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
		}
		[TearDown] public void TearDown() => Directory.Delete(_folder, true);

		[Test]
		public void CompleteLocalInventoryVerifies_ThenAChangedOrMissingTileFailsWithItsPath()
		{
			WriteFixture("tile.glb");
			Assert.DoesNotThrow(() => HarborTileInventory.Verify(Path.Combine(_folder, "tileset.json")));
			File.WriteAllText(Path.Combine(_folder, "tile.glb"), "evil");
			Assert.That(Assert.Throws<InvalidDataException>(() => HarborTileInventory.Verify(Path.Combine(_folder, "tileset.json"))).Message, Does.Contain("tile.glb"));
			File.Delete(Path.Combine(_folder, "tile.glb"));
			Assert.That(Assert.Throws<FileNotFoundException>(() => HarborTileInventory.Verify(Path.Combine(_folder, "tileset.json"))).FileName, Does.EndWith("tile.glb"));
		}

		[TestCase("https://example.invalid/tile.glb")]
		[TestCase("../outside.glb")]
		[TestCase("unlisted.glb")]
		public void ManifestCannotMakeCesiumReadRemoteEscapedOrUnlistedContent(string uri)
		{
			WriteFixture(uri);
			Assert.Throws<InvalidDataException>(() => HarborTileInventory.Verify(Path.Combine(_folder, "tileset.json")));
		}

		[Test]
		public void PlayerAndEditorResolveOnlyTheirOwnRootAndEscapeSpaces()
		{
			const string relative = "data/processed/harbor/3dtiles/tileset.json";
			Assert.That(HarborTilePaths.Resolve(relative, true, "/repo space", "/player"), Is.EqualTo("/repo space/" + relative));
			Assert.That(HarborTilePaths.Resolve(relative, false, "/repo space", "/player"), Is.EqualTo("/player/" + relative));
			Assert.That(HarborTilePaths.Url("/repo space/" + relative), Does.StartWith("file:///repo%20space/"));
			Assert.Throws<ArgumentException>(() => HarborTilePaths.Resolve("data/processed/lower-east-river/env", false, "/repo", "/player"));
		}

		private void WriteFixture(string uri)
		{
			File.WriteAllText(Path.Combine(_folder, "tile.glb"), "test");
			File.WriteAllText(Path.Combine(_folder, "tileset.json"), "{\"asset\":{\"version\":\"1.1\"},\"root\":{\"content\":{\"uri\":\"" + uri + "\"}}}");
			File.WriteAllText(Path.Combine(_folder, "index.json"), "{\"schema\":\"harbor-3dtiles-v1\",\"tileset\":\"tileset.json\",\"files\":[" + Entry("tile.glb") + "," + Entry("tileset.json") + "]}");
		}

		private string Entry(string name)
		{
			var bytes = File.ReadAllBytes(Path.Combine(_folder, name));
			using var sha = SHA256.Create();
			var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
			return "{\"path\":\"" + name + "\",\"bytes\":" + bytes.Length + ",\"sha256\":\"" + hash + "\"}";
		}
	}
}
