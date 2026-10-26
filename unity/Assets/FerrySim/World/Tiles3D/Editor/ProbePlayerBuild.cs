using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FerrySim.World.Tiles3D.EditorTools
{
	/// <summary>
	/// Builds the Tiles3D probe as a development player, with the probe scene alone, without touching the build settings:
	/// <c>-executeMethod FerrySim.World.Tiles3D.EditorTools.ProbePlayerBuild.BuildWindowsFromCommandLine</c> (an x64 player in a
	/// folder named <see cref="WindowsFolderName"/>, zipped) or <c>...BuildMacFromCommandLine</c> (an Apple silicon .app).
	/// Options: <c>-probeOutput &lt;absolute folder&gt;</c> (default artifacts/tiles3d-probe/&lt;windows|mac&gt;),
	/// <c>-probeTiles &lt;folder&gt;</c> (default data/processed/harbor/3dtiles) and <c>-probeScope auto|full|slice</c>.
	/// The build copies the harbor's tiles into the player's StreamingAssets/harbor3d (<see cref="TilesetPayload"/>): the
	/// whole harbor, unless the Windows zip would exceed <see cref="TilesetPayload.ZipLimitBytes"/>, when it carries the
	/// slice's tiles and their neighbours instead. It writes <see cref="ReportFileName"/> beside the player, saying which,
	/// and exits Unity with 0 when the player was built, 1 when not.
	/// </summary>
	public static class ProbePlayerBuild
	{
		public const string ScenePath = ProbeSceneBuilder.ScenePath;
		public const string ExecutableName = "Tiles3DProbe";

		/// <summary>The Windows player's folder, and the zip's top folder: with spaces, so the probe tests escaping in its URL.</summary>
		public const string WindowsFolderName = "FerrySim Tiles3D Probe";

		public const string ZipFileName = "FerrySim-Tiles3D-Probe-windows-x64.zip";
		public const string ReportFileName = "probe-build.json";
		public const string OutputSwitch = "-probeOutput";
		public const string TilesSwitch = "-probeTiles";
		public const string ScopeSwitch = "-probeScope";

		private static string Repository => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static void BuildWindowsFromCommandLine() => FromCommandLine(BuildTarget.StandaloneWindows64);

		public static void BuildMacFromCommandLine() => FromCommandLine(BuildTarget.StandaloneOSX);

		private static void FromCommandLine(BuildTarget target)
		{
			var exit = 1;
			try
			{
				var args = Environment.GetCommandLineArgs();
				var output = ValueOf(args, OutputSwitch) ?? Path.Combine(Repository, "artifacts", "tiles3d-probe", target == BuildTarget.StandaloneOSX ? "mac" : "windows");
				if (!Path.IsPathRooted(output))
				{
					throw new ArgumentException($"{OutputSwitch} must be absolute, since Unity's working directory is not the caller's (got '{output}').");
				}

				var tiles = ValueOf(args, TilesSwitch) ?? Path.Combine(Repository, "data", "processed", "harbor", "3dtiles");
				var scope = ValueOf(args, ScopeSwitch) ?? "auto";
				exit = Build(target, output, Path.GetFullPath(tiles), scope).result == BuildResult.Succeeded.ToString() ? 0 : 1;
			}
			catch (Exception error)
			{
				Debug.LogError($"[FerrySim] Tiles3D probe build failed: {error}");
			}

			EditorApplication.Exit(exit);
		}

		public static string PlayerPath(BuildTarget target, string output) => target == BuildTarget.StandaloneOSX
			? Path.Combine(output, ExecutableName + ".app")
			: Path.Combine(output, WindowsFolderName, ExecutableName + ".exe");

		/// <summary>Where a standalone player keeps its StreamingAssets.</summary>
		public static string StreamingAssets(BuildTarget target, string playerPath) => target == BuildTarget.StandaloneOSX
			? Path.Combine(playerPath, "Contents", "Resources", "Data", "StreamingAssets")
			: Path.Combine(Path.GetDirectoryName(playerPath) ?? string.Empty, Path.GetFileNameWithoutExtension(playerPath) + "_Data", "StreamingAssets");

		public static BuildRecord Build(BuildTarget target, string output, string tilesFolder, string scope)
		{
			if (scope != "auto" && scope != "full" && scope != "slice")
			{
				throw new ArgumentException($"{ScopeSwitch} must be auto, full or slice (got '{scope}').");
			}

			var watch = Stopwatch.StartNew();
			var playerPath = PlayerPath(target, output);
			var playerFolder = target == BuildTarget.StandaloneOSX ? playerPath : Path.GetDirectoryName(playerPath);
			if (Directory.Exists(playerFolder))
			{
				Directory.Delete(playerFolder, true);
			}

			Directory.CreateDirectory(output);
			var previousArchitecture = UnityEditor.OSXStandalone.UserBuildSettings.architecture;
			BuildReport report;
			try
			{
				UnityEditor.OSXStandalone.UserBuildSettings.architecture = OSArchitecture.ARM64;
				report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
				{
					scenes = new[] { ScenePath },
					locationPathName = playerPath,
					target = target,
					targetGroup = BuildTargetGroup.Standalone,
					options = BuildOptions.Development | BuildOptions.StrictMode,
				});
			}
			finally
			{
				UnityEditor.OSXStandalone.UserBuildSettings.architecture = previousArchitecture;
			}

			var record = new BuildRecord
			{
				result = report.summary.result.ToString(), target = target.ToString(), player = playerPath, unity = Application.unityVersion,
				player_bytes = (long)report.summary.totalSize, errors = report.summary.totalErrors, warnings = report.summary.totalWarnings,
			};
			if (report.summary.result == BuildResult.Succeeded)
			{
				var streaming = StreamingAssets(target, playerPath);
				var chosen = scope == "slice" ? TilesetPayload.SliceScope : TilesetPayload.FullScope;
				var reason = scope == "auto"
					? $"automatic: the whole harbor unless the zip would exceed {TilesetPayload.ZipLimitBytes / 1e6:0} MB"
					: $"{ScopeSwitch} {scope}";
				var payload = TilesetPayload.Copy(tilesFolder, streaming, chosen, reason);
				if (target != BuildTarget.StandaloneOSX)
				{
					var zip = Path.Combine(output, ZipFileName);
					var bytes = Zip(playerFolder, zip);
					if (scope == "auto" && TilesetPayload.ChooseScope(bytes) == TilesetPayload.SliceScope)
					{
						reason = $"automatic: the whole harbor's zip was {bytes / 1e6:0} MB, over {TilesetPayload.ZipLimitBytes / 1e6:0} MB";
						record.full_zip_bytes = bytes;
						payload = TilesetPayload.Copy(tilesFolder, streaming, TilesetPayload.SliceScope, reason);
						bytes = Zip(playerFolder, zip);
					}

					record.zip = zip;
					record.zip_bytes = bytes;
					record.zip_sha256 = TilesetPayload.Sha256(File.ReadAllBytes(zip));
				}
				else
				{
					// The tiles go in after Unity has sealed the bundle, so seal it again, ad hoc, as scripts/release.sh does.
					record.codesign = Run("codesign", $"--force --deep --sign - \"{playerPath}\"");
				}

				record.scope = payload.scope;
				record.reason = payload.reason;
				record.tiles = payload.tiles;
				record.payload_files = payload.files.Length;
				record.payload_bytes = payload.TotalBytes;
			}
			else
			{
				record.messages = report.steps.SelectMany(step => step.messages)
					.Where(m => m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert).Select(m => m.content).Take(30).ToArray();
			}

			record.seconds = watch.Elapsed.TotalSeconds;
			File.WriteAllText(Path.Combine(output, ReportFileName), JsonUtility.ToJson(record, true) + "\n");
			Debug.Log($"[FerrySim] Tiles3D probe {target} build {record.result}: {playerPath}; {record.scope} ({record.tiles?.Length ?? 0} tiles, "
				+ $"{record.payload_bytes / 1e6:0.0} MB of tiles){(record.zip != null ? $"; zip {record.zip_bytes / 1e6:0.0} MB, SHA-256 {record.zip_sha256}" : string.Empty)}");
			return record;
		}

		/// <summary>Zips the player folder (its name as the top folder), leaving out the Burst debug folders Unity marks DoNotShip.</summary>
		public static long Zip(string folder, string zipPath)
		{
			if (File.Exists(zipPath))
			{
				File.Delete(zipPath);
			}

			var root = Path.GetDirectoryName(folder) ?? folder;
			using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
			{
				foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
				{
					var entry = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
					if (entry.Split('/').Any(part => part.EndsWith("_DoNotShip", StringComparison.Ordinal)))
					{
						continue;
					}

					archive.CreateEntryFromFile(file, entry, System.IO.Compression.CompressionLevel.Optimal);
				}
			}

			return new FileInfo(zipPath).Length;
		}

		/// <summary>Runs a tool and returns its exit code and output, for the build record.</summary>
		private static string Run(string program, string arguments)
		{
			try
			{
				using (var process = Process.Start(new ProcessStartInfo(program, arguments)
					{ UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
				{
					var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
					process.WaitForExit();
					return $"exit {process.ExitCode}: {output.Trim()}";
				}
			}
			catch (Exception error)
			{
				return $"{program} did not run: {error.Message}";
			}
		}

		// Index 0 is the program.
		private static string ValueOf(IReadOnlyList<string> args, string name)
		{
			for (var i = 1; i < args.Count - 1; i++)
			{
				if (string.Equals(args[i], name, StringComparison.Ordinal) && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
				{
					return args[i + 1];
				}
			}

			return null;
		}

		/// <summary>probe-build.json. Field names are the file's.</summary>
		[Serializable]
		public sealed class BuildRecord
		{
			public string result;
			public string target;
			public string player;
			public string unity;
			public long player_bytes;
			public int errors;
			public int warnings;
			public string scope;
			public string reason;
			public string[] tiles;
			public int payload_files;
			public long payload_bytes;
			public string zip;
			public long zip_bytes;
			public string zip_sha256;

			/// <summary>When the automatic scope fell back to the slice: the whole harbor's zip size that was over the limit.</summary>
			public long full_zip_bytes;

			/// <summary>The Mac app's ad hoc signing after the tiles went in.</summary>
			public string codesign;
			public double seconds;
			public string[] messages;
		}
	}
}
