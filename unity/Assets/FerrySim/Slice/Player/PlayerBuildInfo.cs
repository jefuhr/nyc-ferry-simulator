using System;
using System.IO;
using UnityEngine;

namespace FerrySim.Slice
{
	/// <summary>
	/// What a built player is: its version, the commit it was built from, and the repository files its build copied
	/// into StreamingAssets, with their SHA-256. Every player build writes <see cref="FileName"/> there
	/// (FerrySim.Build.PlayerPayload); a player logs it at startup and the smoke run reports it. The editor has none.
	/// Field names are the file's.
	/// </summary>
	[Serializable]
	public sealed class PlayerBuildInfo
	{
		public const string FileName = "build-info.json";

		public string product;
		public string version;
		public string commit;
		public bool dirty;
		public string target;
		public string built_utc;
		public string unity;
		public PackagedFile[] payload = Array.Empty<PackagedFile>();

		/// <summary>A repository file copied into StreamingAssets at the same relative path.</summary>
		[Serializable]
		public sealed class PackagedFile
		{
			public string path;
			public string sha256;
			public long bytes;
		}

		/// <summary>The version and the short commit, marked -dirty when the build had uncommitted changes: <c>0.1.0 (04db90b)</c>.</summary>
		public string Describe()
		{
			var shortCommit = string.IsNullOrEmpty(commit) ? "unknown" : commit.Length > 7 ? commit.Substring(0, 7) : commit;
			return $"{version} ({shortCommit}{(dirty ? "-dirty" : string.Empty)})";
		}

		/// <summary>Reads <see cref="FileName"/> in <paramref name="folder"/>. False, with the reason, when it is missing, malformed or has no version.</summary>
		public static bool TryLoad(string folder, out PlayerBuildInfo info, out string problem)
		{
			info = null;
			var path = Path.Combine(folder ?? string.Empty, FileName);
			if (!File.Exists(path))
			{
				problem = $"no {FileName} at {path}";
				return false;
			}

			PlayerBuildInfo read;
			try
			{
				read = JsonUtility.FromJson<PlayerBuildInfo>(File.ReadAllText(path));
			}
			catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
			{
				problem = $"{FileName} at {path} did not load: {error.Message}";
				return false;
			}

			if (read == null || string.IsNullOrEmpty(read.version))
			{
				problem = $"{FileName} at {path} has no version";
				return false;
			}

			read.payload = read.payload ?? Array.Empty<PackagedFile>();
			info = read;
			problem = null;
			return true;
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void LogStartup()
		{
			if (Application.isEditor)
			{
				return;
			}

			if (TryLoad(Application.streamingAssetsPath, out var info, out var problem))
			{
				Debug.Log($"[FerrySim] {info.product} {info.Describe()}, built {info.built_utc} for {info.target} with Unity {info.unity}.");
			}
			else
			{
				Debug.LogWarning($"[FerrySim] This player has no build record: {problem}.");
			}
		}
	}
}
