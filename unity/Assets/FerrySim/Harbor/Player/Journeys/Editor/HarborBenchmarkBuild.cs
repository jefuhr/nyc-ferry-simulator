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
			try { var result=UnityEditor.Build.Reporting.BuildResult.Unknown; WithFrameTiming(() => result=PlayerBuild.Build(request).summary.result); EditorApplication.Exit(result==UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 2); }
			catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
		}
	}
}
