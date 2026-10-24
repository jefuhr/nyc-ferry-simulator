using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FerrySim.Build
{
	/// <summary>
	/// Builds a release player from the command line (scripts/release.sh):
	/// <c>-executeMethod FerrySim.Build.PlayerBuild.BuildFromCommandLine -ferrysimTarget mac|windows -ferrysimOutput &lt;absolute folder&gt; [-ferrysimVersion &lt;x.y.z&gt;]</c>.
	/// The Mac player is a universal (Intel and Apple silicon) .app at the top of the folder; the Windows player is an x64
	/// .exe in a folder named after the product. The scenes are the build settings' enabled ones. The build fails on any
	/// error (strict mode), writes <see cref="ReportFileName"/> beside the player and exits Unity with 0 when it succeeded,
	/// 1 when it failed and 2 when the command line was unusable. <see cref="PlayerPayloadBuildStep"/> packages the data.
	/// </summary>
	public static class PlayerBuild
	{
		public const string TargetSwitch = "-ferrysimTarget";
		public const string OutputSwitch = "-ferrysimOutput";
		public const string VersionSwitch = "-ferrysimVersion";
		public const string ReportFileName = "build-report.json";

		/// <summary>
		/// Reads the command line (the program first). Throws <see cref="ArgumentException"/>, naming the switch, for a
		/// missing or unknown target, a missing or relative output folder, a version other than <paramref name="bundleVersion"/>,
		/// or no scenes.
		/// </summary>
		public static Request Parse(IReadOnlyList<string> args, string bundleVersion, string productName, IReadOnlyList<string> scenes)
		{
			var targetName = ValueOf(args, TargetSwitch) ?? throw new ArgumentException($"{TargetSwitch} is required: mac or windows.");
			BuildTarget target;
			switch (targetName)
			{
				case "mac":
					target = BuildTarget.StandaloneOSX;
					break;
				case "windows":
					target = BuildTarget.StandaloneWindows64;
					break;
				default:
					throw new ArgumentException($"{TargetSwitch} must be mac or windows (got '{targetName}').");
			}

			var output = ValueOf(args, OutputSwitch) ?? throw new ArgumentException($"{OutputSwitch} is required: the folder the player goes in.");
			if (!Path.IsPathRooted(output))
			{
				throw new ArgumentException($"{OutputSwitch} must be an absolute path, since Unity's working directory is not the caller's (got '{output}').");
			}

			var version = ValueOf(args, VersionSwitch);
			if (version != null && version != bundleVersion)
			{
				throw new ArgumentException($"{VersionSwitch} {version} is not the project's version, {bundleVersion} (Player Settings, bundleVersion).");
			}

			if (scenes == null || scenes.Count == 0)
			{
				throw new ArgumentException("The build settings have no enabled scene; a player needs one.");
			}

			return new Request(target, output, PlayerPath(target, output, productName), scenes.ToArray());
		}

		/// <summary>
		/// The exit code of a command-line build: 0 when <paramref name="build"/> succeeded, 1 when it failed or threw,
		/// 2 when the command line was unusable. Every outcome but success is logged as an error.
		/// </summary>
		public static int Run(IReadOnlyList<string> args, string bundleVersion, string productName, IReadOnlyList<string> scenes, Func<Request, BuildResult> build)
		{
			Request request;
			try
			{
				request = Parse(args, bundleVersion, productName, scenes);
			}
			catch (ArgumentException error)
			{
				Debug.LogError($"[FerrySim] No player built: {error.Message}");
				return 2;
			}

			try
			{
				var result = build(request);
				if (result == BuildResult.Succeeded)
				{
					return 0;
				}

				Debug.LogError($"[FerrySim] The {request.Target} build ended {result}; see {Path.Combine(request.OutputFolder, ReportFileName)}.");
				return 1;
			}
			catch (Exception error)
			{
				// The command line's only answer is its exit code, so nothing may escape to Unity's own handling.
				Debug.LogError($"[FerrySim] The {request.Target} build threw {error.GetType().Name}: {error.Message}");
				return 1;
			}
		}

		public static string PlayerPath(BuildTarget target, string outputFolder, string productName) =>
			target == BuildTarget.StandaloneOSX
				? Path.Combine(outputFolder, productName + ".app")
				: Path.Combine(outputFolder, productName, productName + ".exe");

		public static void BuildFromCommandLine()
		{
			var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToList();
			EditorApplication.Exit(Run(Environment.GetCommandLineArgs(), PlayerSettings.bundleVersion, PlayerSettings.productName, scenes, request => Build(request).summary.result));
		}

		public static BuildReport Build(Request request)
		{
			if (request.Target == BuildTarget.StandaloneOSX)
			{
				UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.x64ARM64;
			}

			Directory.CreateDirectory(request.OutputFolder);
			var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
			{
				scenes = request.Scenes,
				locationPathName = request.PlayerPath,
				target = request.Target,
				targetGroup = BuildTargetGroup.Standalone,
				options = BuildOptions.StrictMode,
			});
			WriteReport(report, Path.Combine(request.OutputFolder, ReportFileName));
			var summary = report.summary;
			Debug.Log($"[FerrySim] {summary.platform} player {summary.result}: {summary.totalSize / 1e6:0.0} MB, {summary.totalErrors} error(s), " +
				$"{summary.totalWarnings} warning(s), {summary.totalTime.TotalSeconds:0} s, at {request.PlayerPath}");
			return report;
		}

		private static void WriteReport(BuildReport report, string path)
		{
			var summary = report.summary;
			var file = new ReportFile
			{
				result = summary.result.ToString(),
				platform = summary.platform.ToString(),
				player = summary.outputPath,
				total_size_bytes = (long)summary.totalSize,
				total_time_s = summary.totalTime.TotalSeconds,
				total_errors = summary.totalErrors,
				total_warnings = summary.totalWarnings,
				errors = report.steps.SelectMany(step => step.messages)
					.Where(message => message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert)
					.Select(message => message.content)
					.Take(50)
					.ToArray(),
			};
			File.WriteAllText(path, JsonUtility.ToJson(file, true) + "\n");
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

		/// <summary>A parsed command line.</summary>
		public sealed class Request
		{
			public Request(BuildTarget target, string outputFolder, string playerPath, string[] scenes)
			{
				Target = target;
				OutputFolder = outputFolder;
				PlayerPath = playerPath;
				Scenes = scenes;
			}

			public BuildTarget Target { get; }

			public string OutputFolder { get; }

			public string PlayerPath { get; }

			public string[] Scenes { get; }
		}

		// Field names are build-report.json's.
		[Serializable]
		private sealed class ReportFile
		{
			public string result;
			public string platform;
			public string player;
			public long total_size_bytes;
			public double total_time_s;
			public int total_errors;
			public int total_warnings;
			public string[] errors;
		}
	}
}
