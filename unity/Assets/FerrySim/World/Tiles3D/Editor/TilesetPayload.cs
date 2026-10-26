using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Core.Contracts.Geodesy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.World.Tiles3D.EditorTools
{
	/// <summary>
	/// Copies the pipeline's harbor 3D Tiles (data/processed/harbor/3dtiles, ferrydata.world3d) into a player's
	/// StreamingAssets/harbor3d, checking every file against the pipeline's index.json first, and writes
	/// harbor3d/payload.json, which the player checks before it loads anything. The scope is the whole harbor, or the slice's
	/// tiles and their neighbours, with the root tileset.json rewritten to name only those.
	/// </summary>
	public static class TilesetPayload
	{
		public const string FullScope = "full";
		public const string SliceScope = "slice-and-neighbours";

		/// <summary>The zip a user downloads stays under about this, or the probe carries the slice's tiles only.</summary>
		public const long ZipLimitBytes = 400_000_000;

		/// <summary>The slice's box (data-pipeline/ferrydata/slices.py, LOWER_EAST_RIVER): latitude and longitude, degrees.</summary>
		public const double SliceSouth = 40.695, SliceNorth = 40.750, SliceWest = -74.020, SliceEast = -73.960;

		public const double TileSizeMetres = 4000.0;

		public static string ChooseScope(long fullZipBytes, long limitBytes = ZipLimitBytes) => fullZipBytes > limitBytes ? SliceScope : FullScope;

		/// <summary>Tile ids of the harbor tiles under the slice's box and their eight neighbours, among <paramref name="active"/>.</summary>
		public static IReadOnlyList<string> SliceAndNeighbours(IEnumerable<string> active)
		{
			var corners = new[] { (SliceSouth, SliceWest), (SliceSouth, SliceEast), (SliceNorth, SliceWest), (SliceNorth, SliceEast) }
				.Select(c => LocalTangentPlane.World.ToLocal(new GeodeticPosition(c.Item1, c.Item2))).ToList();
			var i0 = (int)Math.Floor(corners.Min(c => c.North) / TileSizeMetres) - 1;
			var i1 = (int)Math.Floor(corners.Max(c => c.North) / TileSizeMetres) + 1;
			var j0 = (int)Math.Floor(corners.Min(c => c.East) / TileSizeMetres) - 1;
			var j1 = (int)Math.Floor(corners.Max(c => c.East) / TileSizeMetres) + 1;
			return active.Where(id =>
			{
				var parts = id.Split('_');
				return parts.Length == 3 && int.TryParse(parts[1], out var i) && int.TryParse(parts[2], out var j) && i >= i0 && i <= i1 && j >= j0 && j <= j1;
			}).OrderBy(id => id, StringComparer.Ordinal).ToList();
		}

		/// <summary>
		/// Copies the tiles in <paramref name="sourceFolder"/> into <paramref name="streamingAssets"/>/harbor3d for
		/// <paramref name="scope"/> and writes payload.json. Throws <see cref="InvalidDataException"/>, naming the file, when
		/// a source file is missing or differs from index.json: a player must not carry tiles the pipeline did not write.
		/// </summary>
		public static TilesetPayloadManifest Copy(string sourceFolder, string streamingAssets, string scope, string reason)
		{
			if (scope != FullScope && scope != SliceScope)
			{
				throw new ArgumentException($"Unknown scope '{scope}': {FullScope} or {SliceScope}.", nameof(scope));
			}

			var indexPath = Path.Combine(sourceFolder, "index.json");
			if (!File.Exists(indexPath))
			{
				throw new FileNotFoundException("No harbor 3D Tiles: run PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.world3d build", indexPath);
			}

			var index = JObject.Parse(File.ReadAllText(indexPath));
			var listed = index["files"].Select(f => new PayloadFile { path = (string)f["path"], bytes = (long)f["bytes"], sha256 = (string)f["sha256"] }).ToList();
			var active = index["tiles"].Select(t => (string)t["id"]).ToList();
			var tiles = scope == FullScope ? active.OrderBy(id => id, StringComparer.Ordinal).ToList() : SliceAndNeighbours(active).ToList();
			var keep = new HashSet<string>(tiles.Select(id => "tiles/" + id + "/"), StringComparer.Ordinal);
			var target = Path.Combine(streamingAssets, TilesetUrl.FolderName);
			if (Directory.Exists(target))
			{
				Directory.Delete(target, true);
			}

			Directory.CreateDirectory(target);
			var files = new List<PayloadFile>();
			foreach (var file in listed)
			{
				var source = Path.Combine(sourceFolder, file.path);
				var bytes = File.Exists(source) ? File.ReadAllBytes(source) : throw new InvalidDataException($"{file.path} is in index.json but missing from {sourceFolder}");
				if (bytes.LongLength != file.bytes || Sha256(bytes) != file.sha256)
				{
					throw new InvalidDataException($"{file.path} differs from index.json: rebuild the harbor 3D Tiles");
				}

				var inTile = file.path.StartsWith("tiles/", StringComparison.Ordinal);
				if (inTile && !keep.Any(prefix => file.path.StartsWith(prefix, StringComparison.Ordinal)))
				{
					continue;
				}

				if (file.path == TilesetUrl.FileName && scope != FullScope)
				{
					bytes = System.Text.Encoding.UTF8.GetBytes(SubsetRoot(System.Text.Encoding.UTF8.GetString(bytes), tiles));
				}

				Write(target, file.path, bytes);
				files.Add(new PayloadFile { path = file.path, bytes = bytes.LongLength, sha256 = Sha256(bytes) });
			}

			var indexBytes = File.ReadAllBytes(indexPath);
			Write(target, "index.json", indexBytes);
			files.Add(new PayloadFile { path = "index.json", bytes = indexBytes.LongLength, sha256 = Sha256(indexBytes) });
			var manifest = new TilesetPayloadManifest
			{
				scope = scope, reason = reason, tiles = tiles.ToArray(),
				files = files.OrderBy(f => f.path, StringComparer.Ordinal).ToArray(), source_index_sha256 = Sha256(indexBytes),
			};
			File.WriteAllText(Path.Combine(target, TilesetPayloadManifest.FileName), JsonUtility.ToJson(manifest, true) + "\n");
			return manifest;
		}

		/// <summary>The root tileset with only <paramref name="tiles"/>' children, and its region their union.</summary>
		public static string SubsetRoot(string rootJson, IReadOnlyCollection<string> tiles)
		{
			var root = JObject.Parse(rootJson);
			var children = (JArray)root["root"]["children"];
			var kept = new JArray(children.Where(child => tiles.Any(id => (string)child["content"]["uri"] == $"tiles/{id}/tileset.json")));
			if (kept.Count == 0)
			{
				throw new InvalidDataException("None of the chosen tiles is in the root tileset.");
			}

			var regions = kept.Select(child => child["boundingVolume"]["region"].Select(v => (double)v).ToArray()).ToList();
			root["root"]["children"] = kept;
			root["root"]["boundingVolume"]["region"] = new JArray(regions.Min(r => r[0]), regions.Min(r => r[1]), regions.Max(r => r[2]),
				regions.Max(r => r[3]), regions.Min(r => r[4]), regions.Max(r => r[5]));
			return root.ToString(Formatting.Indented) + "\n";
		}

		public static string Sha256(byte[] bytes)
		{
			using (var sha = SHA256.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
			}
		}

		private static void Write(string folder, string relative, byte[] bytes)
		{
			var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? folder);
			File.WriteAllBytes(path, bytes);
		}
	}
}
