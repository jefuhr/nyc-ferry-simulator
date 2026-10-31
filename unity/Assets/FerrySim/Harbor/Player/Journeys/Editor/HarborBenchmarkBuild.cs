using System;
using System.IO;
using FerrySim.Build;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Harbor.Player.Journeys.EditorTools
{
	/// <summary>Approved diagnostic build only. Shipping release.sh and PlayerBuild sources are untouched.</summary>
	public static class HarborBenchmarkBuild
	{
		public static void WithFrameTiming(Action build)
		{
			var original=PlayerSettings.enableFrameTimingStats;
			try { PlayerSettings.enableFrameTimingStats=true; build(); }
			finally { PlayerSettings.enableFrameTimingStats=original; }
		}
		public static void BuildFromCommandLine()
		{
			var args=Environment.GetCommandLineArgs();
			var request=PlayerBuild.Parse(args,PlayerSettings.bundleVersion,PlayerSettings.productName,new[] {HarborPayload.Scene});
			try { var result=UnityEditor.Build.Reporting.BuildResult.Unknown; WithFrameTiming(() => result=(Array.IndexOf(args,"-ferrysimProfile") >= 0 ? BuildProfile(request) : PlayerBuild.Build(request)).summary.result); EditorApplication.Exit(result==UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 2); }
			catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
		}

		private static UnityEditor.Build.Reporting.BuildReport BuildProfile(PlayerBuild.Request request)
		{
			Directory.CreateDirectory(request.OutputFolder);
			foreach (var scene in EditorBuildSettings.scenes)
				if (scene.enabled && scene.path != HarborPayload.Scene)
					throw new InvalidOperationException("The diagnostic player requires Harbor-only enabled build settings.");
			var originalArchitecture = UnityEditor.OSXStandalone.UserBuildSettings.architecture;
			try
			{
				if (request.Target == BuildTarget.StandaloneOSX)
					UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.x64ARM64;
				var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
					scenes = request.Scenes, locationPathName = request.PlayerPath, target = request.Target,
					targetGroup = BuildTargetGroup.Standalone, options = BuildOptions.StrictMode | BuildOptions.Development });
				File.WriteAllText(Path.Combine(request.OutputFolder, "profile-build.txt"),
					"result=" + report.summary.result + "\nerrors=" + report.summary.totalErrors +
					"\nwarnings=" + report.summary.totalWarnings + "\ndevelopment=true\nframe_timing=true\n");
				return report;
			}
			finally { UnityEditor.OSXStandalone.UserBuildSettings.architecture = originalArchitecture; }
		}
	}
}
