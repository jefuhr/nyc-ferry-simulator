using System;
using System.IO;
using FerrySim.World.Tiles3D;

namespace FerrySim.World.HarborTiles
{
	public static class HarborTilePaths
	{
		public static string Resolve(string relative, bool editor, string repository, string streamingAssets)
		{
			if (string.IsNullOrEmpty(relative) || !relative.StartsWith("data/processed/harbor/", StringComparison.Ordinal) ||
				relative.Contains("..") || relative.Contains(":") || relative.Contains("\\"))
				throw new ArgumentException("Expected a relative data/processed/harbor path: " + relative, nameof(relative));
			return Path.GetFullPath(Path.Combine(editor ? repository : streamingAssets, relative));
		}

		public static string Url(string absolutePath) => TilesetUrl.FromAbsolutePath(absolutePath);
	}
}
