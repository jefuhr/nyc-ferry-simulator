using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace FerrySim.World.HarborTiles
{
	/// <summary>Checks the offline render inventory once, before enabling Cesium. No IO occurs in update loops.</summary>
	public static class HarborTileInventory
	{
		public static void Verify(string tilesetPath)
		{
			RequireFile(tilesetPath);
			var root = Path.GetDirectoryName(Path.GetFullPath(tilesetPath));
			var indexPath = Path.Combine(root, "index.json");
			RequireFile(indexPath);
			var index = JObject.Parse(File.ReadAllText(indexPath));
			var schema = (string)index["schema"];
			if (schema != "harbor-3dtiles-v1" && schema != "harbor-buildings-3dtiles-v1")
				throw new InvalidDataException(indexPath + ": unsupported harbor tile schema " + schema);
			if ((string)index["tileset"] != "tileset.json" || !(index["files"] is JArray files) || files.Count == 0)
				throw new InvalidDataException(indexPath + ": missing tileset or file inventory");
			var paths = new HashSet<string>(StringComparer.Ordinal);
			var manifests = new List<string>();
			using var sha = SHA256.Create();
			foreach (var file in files)
			{
				var path = LocalPath(root, root, (string)file["path"]);
				if (!paths.Add(path)) throw new InvalidDataException(indexPath + ": duplicate " + path);
				RequireFile(path);
				using var stream = File.OpenRead(path);
				if (stream.Length != (long?)file["bytes"]) throw new InvalidDataException(path + ": byte count differs from index.json");
				var digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
				if (digest != (string)file["sha256"]) throw new InvalidDataException(path + ": SHA-256 differs from index.json");
				if (path.EndsWith(".json", StringComparison.Ordinal)) manifests.Add(path);
			}
			if (!paths.Contains(Path.GetFullPath(tilesetPath))) throw new InvalidDataException(indexPath + ": tileset.json is not inventoried");
			foreach (var manifest in manifests)
			{
				var document = JObject.Parse(File.ReadAllText(manifest));
				foreach (var token in document.Descendants())
				{
					if (!(token is JProperty property) || (property.Name != "uri" && property.Name != "url")) continue;
					var path = LocalPath(root, Path.GetDirectoryName(manifest), (string)property.Value);
					if (!paths.Contains(path)) throw new InvalidDataException(manifest + ": content is absent from index.json: " + path);
				}
			}
		}

		private static string LocalPath(string root, string parent, string relative)
		{
			if (string.IsNullOrEmpty(relative) || relative.Contains(":") || relative.Contains("\\") || relative.Contains("%") || Path.IsPathRooted(relative))
				throw new InvalidDataException(parent + ": non-local tile path " + relative);
			var path = Path.GetFullPath(Path.Combine(parent, relative));
			if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				throw new InvalidDataException(parent + ": tile path escapes its inventory: " + relative);
			return path;
		}

		private static void RequireFile(string path)
		{
			if (!File.Exists(path)) throw new FileNotFoundException("Missing harbor render data: " + path, path);
		}
	}
}
