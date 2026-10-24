using System;
using UnityEditor;
using UnityEngine;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Menu items and the batchmode entry point for rebuilding the world from the pipeline outputs.
	/// <code>
	/// Unity -batchmode -nographics -projectPath unity -executeMethod FerrySim.World.EditorTools.WorldBuildCommands.RebuildAllBatch -logFile ...
	/// </code>
	/// </summary>
	public static class WorldBuildCommands
	{
		[MenuItem("FerrySim/World/Rebuild Greybox From Pipeline Data")]
		public static void RebuildGreybox()
		{
			var report = GreyboxBuilder.Build(PipelineData.Load());
			Debug.Log(report);
		}

		[MenuItem("FerrySim/World/Rebuild LowerEastRiver Scene")]
		public static void RebuildScene()
		{
			SceneBuilder.Build(PipelineData.Load());
			Debug.Log("Wrote " + SceneBuilder.ScenePath);
		}

		[MenuItem("FerrySim/World/Rebuild All")]
		public static void RebuildAll()
		{
			var data = PipelineData.Load();
			var report = GreyboxBuilder.Build(data);
			Debug.Log(report);
			SceneBuilder.Build(data);
			Debug.Log("Wrote " + SceneBuilder.ScenePath);
		}

		/// <summary>Batchmode: rebuilds everything and exits with 0 on success, 1 on any failure.</summary>
		public static void RebuildAllBatch()
		{
			try
			{
				RebuildAll();
				EditorApplication.Exit(0);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
				EditorApplication.Exit(1);
			}
		}
	}
}
