using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FerrySim.World.Tiles3D.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The probe build's copy step (<see cref="TilesetPayload"/>), on a small stand-in for data/processed/harbor/3dtiles.
	/// Failure modes guarded: a player that carries tiles other than the pipeline wrote (a changed or missing file); a slice
	/// subset whose root still names tiles it left behind, or whose region does not shrink to them; files dropped or added
	/// on the way; payload.json disagreeing with the copies; the 400 MB rule chosen the wrong way round.
	/// </summary>
	public sealed class TilesetPayloadTests
	{
		private static readonly string[] Tiles = { "tile_-2_-2", "tile_0_0", "tile_3_3" };
		private string _root;
		private string _source;
		private string _streaming;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "tiles3d-payload-" + Guid.NewGuid().ToString("N"));
			_source = Path.Combine(_root, "3dtiles");
			_streaming = Path.Combine(_root, "Player_Data", "StreamingAssets");
			var children = new JArray();
			var files = new JArray();
			for (var k = 0; k < Tiles.Length; k++)
			{
				var tile = Tiles[k];
				Write($"tiles/{tile}/tileset.json", "{\"asset\":{\"version\":\"1.1\"},\"root\":{}}\n", files);
				Write($"tiles/{tile}/l2.glb", "glb " + tile, files);
				Write($"tiles/{tile}/c0_0_l0.glb", "glb l0 " + tile, files);
				children.Add(new JObject
				{
					["boundingVolume"] = new JObject { ["region"] = new JArray(-1.2918 + 0.001 * k, 0.7101 + 0.001 * k, -1.2908 + 0.001 * k, 0.7111 + 0.001 * k, -90.0 - k, 40.0 + k) },
					["geometricError"] = 10000.0,
					["content"] = new JObject { ["uri"] = $"tiles/{tile}/tileset.json" },
				});
			}

			var root = new JObject
			{
				["asset"] = new JObject { ["version"] = "1.1" },
				["geometricError"] = 100000.0,
				["root"] = new JObject
				{
					["boundingVolume"] = new JObject { ["region"] = new JArray(-1.2918, 0.7101, -1.2888, 0.7131, -92.0, 42.0) },
					["geometricError"] = 100000.0, ["refine"] = "REPLACE", ["children"] = children,
				},
			};
			Write("tileset.json", root.ToString(), files);
			var index = new JObject
			{
				["schema"] = "harbor-3dtiles-v1",
				["tiles"] = new JArray(Tiles.Select(t => new JObject { ["id"] = t })),
				["files"] = new JArray(files.OrderBy(f => (string)f["path"], StringComparer.Ordinal)),
			};
			File.WriteAllText(Path.Combine(_source, "index.json"), index.ToString());
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, true);
			}
		}

		private void Write(string relative, string text, JArray files)
		{
			var path = Path.Combine(_source, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var bytes = Encoding.UTF8.GetBytes(text);
			File.WriteAllBytes(path, bytes);
			files.Add(new JObject { ["path"] = relative, ["bytes"] = bytes.LongLength, ["sha256"] = Hash(bytes) });
		}

		private static string Hash(byte[] bytes)
		{
			using (var sha = SHA256.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
			}
		}

		private string Copied(string relative) => Path.Combine(_streaming, TilesetUrl.FolderName, relative);

		[Test]
		public void TheWholeHarborIsCopiedFileForFileWithAMatchingPayload()
		{
			var manifest = TilesetPayload.Copy(_source, _streaming, TilesetPayload.FullScope, "test");

			Assert.That(manifest.scope, Is.EqualTo("full"));
			Assert.That(manifest.tiles, Is.EqualTo(Tiles.OrderBy(t => t, StringComparer.Ordinal).ToArray()));
			var index = JObject.Parse(File.ReadAllText(Path.Combine(_source, "index.json")));
			foreach (var file in index["files"])
			{
				var path = (string)file["path"];
				Assert.That(File.ReadAllBytes(Copied(path)), Is.EqualTo(File.ReadAllBytes(Path.Combine(_source, path))), path);
			}

			Assert.That(manifest.files.Select(f => f.path), Is.EqualTo(index["files"].Select(f => (string)f["path"]).Append("index.json").OrderBy(p => p, StringComparer.Ordinal)));
			var written = JsonUtility.FromJson<TilesetPayloadManifest>(File.ReadAllText(Copied(TilesetPayloadManifest.FileName)));
			Assert.That(written.Check(Path.Combine(_streaming, TilesetUrl.FolderName), out var missing, out var mismatched), Is.Empty);
			Assert.That(missing + mismatched, Is.Zero);
		}

		[Test]
		public void TheSliceSubsetKeepsTheSlicesTilesAndNeighboursAndRewritesTheRoot()
		{
			var manifest = TilesetPayload.Copy(_source, _streaming, TilesetPayload.SliceScope, "test");

			// The slice lies over tiles -1 to 1 both ways, so its neighbours reach -2 to 2: tile_3_3 is left behind.
			Assert.That(manifest.tiles, Is.EqualTo(new[] { "tile_-2_-2", "tile_0_0" }));
			Assert.That(File.Exists(Copied("tiles/tile_3_3/l2.glb")), Is.False);
			Assert.That(File.Exists(Copied("tiles/tile_0_0/c0_0_l0.glb")), Is.True);
			var root = JObject.Parse(File.ReadAllText(Copied("tileset.json")));
			var uris = root["root"]["children"].Select(c => (string)c["content"]["uri"]).ToArray();
			Assert.That(uris, Is.EqualTo(new[] { "tiles/tile_-2_-2/tileset.json", "tiles/tile_0_0/tileset.json" }));
			var region = root["root"]["boundingVolume"]["region"].Select(v => (double)v).ToArray();
			Assert.That(region[2], Is.EqualTo(-1.2908 + 0.001).Within(1e-12));
			Assert.That(region[5], Is.EqualTo(41.0).Within(1e-12));
			Assert.That(manifest.Check(Path.Combine(_streaming, TilesetUrl.FolderName), out _, out _), Is.Empty);
			Assert.That(manifest.files.Any(f => f.path.StartsWith("tiles/tile_3_3/", StringComparison.Ordinal)), Is.False);
		}

		[Test]
		public void AFileThatDiffersFromTheIndexStopsTheCopy()
		{
			File.WriteAllText(Path.Combine(_source, "tiles", "tile_0_0", "l2.glb"), "changed");

			var error = Assert.Throws<InvalidDataException>(() => TilesetPayload.Copy(_source, _streaming, TilesetPayload.FullScope, "test"));
			Assert.That(error.Message, Does.Contain("tiles/tile_0_0/l2.glb"));
		}

		[Test]
		public void AMissingFileStopsTheCopy()
		{
			File.Delete(Path.Combine(_source, "tiles", "tile_-2_-2", "c0_0_l0.glb"));

			var error = Assert.Throws<InvalidDataException>(() => TilesetPayload.Copy(_source, _streaming, TilesetPayload.FullScope, "test"));
			Assert.That(error.Message, Does.Contain("tiles/tile_-2_-2/c0_0_l0.glb"));
		}

		[Test]
		public void APlayerCopyThatChangesAfterwardsFailsThePayloadCheck()
		{
			var manifest = TilesetPayload.Copy(_source, _streaming, TilesetPayload.FullScope, "test");
			File.WriteAllText(Copied("tiles/tile_0_0/l2.glb"), "damaged");
			File.Delete(Copied("tiles/tile_3_3/l2.glb"));

			var problems = manifest.Check(Path.Combine(_streaming, TilesetUrl.FolderName), out var missing, out var mismatched);
			Assert.That(missing, Is.EqualTo(1));
			Assert.That(mismatched, Is.EqualTo(1));
			Assert.That(problems, Has.Count.EqualTo(2));
		}

		[Test]
		public void TheZipLimitChoosesTheWholeHarborUnlessItIsOver400MB()
		{
			Assert.That(TilesetPayload.ChooseScope(399_000_000), Is.EqualTo(TilesetPayload.FullScope));
			Assert.That(TilesetPayload.ChooseScope(400_000_000), Is.EqualTo(TilesetPayload.FullScope));
			Assert.That(TilesetPayload.ChooseScope(401_000_000), Is.EqualTo(TilesetPayload.SliceScope));
		}

		[Test]
		public void TheSlicesNeighbourhoodIsTheTwentyFiveTilesAroundIt()
		{
			var all = Enumerable.Range(-2, 6).SelectMany(i => Enumerable.Range(-2, 6).Select(j => $"tile_{i}_{j}")).ToList();

			var chosen = TilesetPayload.SliceAndNeighbours(all);

			Assert.That(chosen, Has.Count.EqualTo(25));
			Assert.That(chosen, Does.Contain("tile_-2_-2").And.Contain("tile_2_2").And.Not.Contain("tile_3_0").And.Not.Contain("tile_0_3"));
		}

		[Test]
		public void TheZipHoldsThePlayerFolderUnderItsOwnNameWithoutDoNotShipFolders()
		{
			var player = Path.Combine(_root, "zip", ProbePlayerBuild.WindowsFolderName);
			Directory.CreateDirectory(Path.Combine(player, "Tiles3DProbe_Data", "StreamingAssets"));
			Directory.CreateDirectory(Path.Combine(player, "Tiles3DProbe_BurstDebugInformation_DoNotShip"));
			File.WriteAllText(Path.Combine(player, "Tiles3DProbe.exe"), "exe");
			File.WriteAllText(Path.Combine(player, "Tiles3DProbe_Data", "StreamingAssets", "a.txt"), "a");
			File.WriteAllText(Path.Combine(player, "Tiles3DProbe_BurstDebugInformation_DoNotShip", "x.pdb"), "x");
			var zip = Path.Combine(_root, "probe.zip");

			ProbePlayerBuild.Zip(player, zip);

			using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
			{
				Assert.That(archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(new[]
				{
					"FerrySim Tiles3D Probe/Tiles3DProbe.exe",
					"FerrySim Tiles3D Probe/Tiles3DProbe_Data/StreamingAssets/a.txt",
				}));
			}
		}
	}
}
