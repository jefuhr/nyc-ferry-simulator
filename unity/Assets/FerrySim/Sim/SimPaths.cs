using System.IO;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Resolves repository-relative paths (vessel files, processed slice data, artifacts). In the editor
	/// <c>Application.dataPath</c> is <c>&lt;repo&gt;/unity/Assets</c>, so the repository root is two levels up.
	/// A player looks in StreamingAssets first.
	/// </summary>
	public static class SimPaths
	{
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		/// <summary>Default folder for recorded runs: <c>artifacts/unity/runs</c> in the editor, persistent data in a player.</summary>
		public static string DefaultRecordingDirectory =>
			Application.isEditor
				? Path.Combine(RepoRoot, "artifacts", "unity", "runs")
				: Path.Combine(Application.persistentDataPath, "runs");

		/// <summary>Recording overrides are relative to the Unity project in the editor, persistent data in a player.</summary>
		public static string ResolveRecordingDirectory(string path)
		{
			var basePath = Application.isEditor ? Path.GetDirectoryName(Application.dataPath) : Application.persistentDataPath;
			var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
			var assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(resolved, assets, System.StringComparison.OrdinalIgnoreCase)
				|| resolved.StartsWith(assets + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase))
			{
				throw new System.InvalidOperationException($"Recordings cannot be written inside Unity Assets: {resolved}. Choose a directory outside Assets, such as {DefaultRecordingDirectory}.");
			}
			return resolved;
		}

		/// <summary>
		/// An absolute path as given; otherwise the first existing of StreamingAssets/&lt;path&gt; and
		/// &lt;repo&gt;/&lt;path&gt;, falling back to the repository location so error messages name it.
		/// </summary>
		public static string Resolve(string path)
		{
			if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
			{
				return path;
			}

			var streaming = Path.Combine(Application.streamingAssetsPath, path);
			if (File.Exists(streaming))
			{
				return streaming;
			}

			return Path.Combine(RepoRoot, path);
		}
	}
}
