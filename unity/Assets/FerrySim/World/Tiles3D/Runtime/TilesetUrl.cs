using System;
using System.IO;
using System.Linq;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// The file:// URL Cesium for Unity loads a local tileset from. Every path segment is escaped, so spaces, '#', '%' and
	/// non-ASCII names survive, and a Windows drive letter keeps its place: <c>/Users/a b/x.json</c> is
	/// <c>file:///Users/a%20b/x.json</c> and <c>C:\a b\x.json</c> is <c>file:///C:/a%20b/x.json</c>, as
	/// <see cref="Uri.AbsoluteUri"/> writes them. It is built by hand rather than with <see cref="Uri"/> so that the
	/// Windows form can be tested on any machine.
	/// </summary>
	public static class TilesetUrl
	{
		/// <summary>The folder under StreamingAssets the probe build copies the harbor's tiles into.</summary>
		public const string FolderName = "harbor3d";

		public const string FileName = "tileset.json";

		public static string FromAbsolutePath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				throw new ArgumentException("A tileset needs a path.", nameof(path));
			}

			var forward = path.Replace('\\', '/');
			var drive = string.Empty;
			if (forward.Length >= 3 && char.IsLetter(forward[0]) && forward[1] == ':' && forward[2] == '/')
			{
				drive = "/" + forward.Substring(0, 2);
				forward = forward.Substring(2);
			}
			else if (!forward.StartsWith("/", StringComparison.Ordinal))
			{
				throw new ArgumentException($"A file URL needs an absolute path (got '{path}').", nameof(path));
			}

			return "file://" + drive + string.Join("/", forward.Split('/').Select(Uri.EscapeDataString));
		}

		/// <summary>The tileset's path inside a player's StreamingAssets folder.</summary>
		public static string PathIn(string streamingAssets) => Path.Combine(streamingAssets, FolderName, FileName);
	}
}
