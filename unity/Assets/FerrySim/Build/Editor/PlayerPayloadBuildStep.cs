using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FerrySim.Slice;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FerrySim.Build
{
	/// <summary>
	/// Packages <see cref="PlayerPayload"/> into every standalone player build, from scripts/release.sh or the editor's
	/// Build menu alike. The commit is read before the build starts; each scene is read as the build processes it, and
	/// any enabled build-settings scene it did not process is opened and read afterwards; once the player is written, the
	/// files they name are copied into its StreamingAssets with build-info.json. A field naming a file a player could not
	/// use fails the build, rather than shipping a player that stops at startup.
	/// </summary>
	internal sealed class PlayerPayloadBuildStep : IPreprocessBuildWithReport, IProcessSceneWithReport, IPostprocessBuildWithReport
	{
		private static readonly List<PlayerPayload.PathField> Fields = new List<PlayerPayload.PathField>();
		private static readonly List<string> ProcessedScenes = new List<string>();
		private static bool _active;
		private static GitState _git;

		// First of all build steps: the git state must be the tree's before any other step writes into it. The performance
		// testing package, for one, adds files under Assets/Resources for the length of every player build.
		public int callbackOrder => int.MinValue;

		public void OnPreprocessBuild(BuildReport report)
		{
			Fields.Clear();
			ProcessedScenes.Clear();
			_active = PlayerPayload.Supports(report.summary.platform);
			if (_active)
			{
				_git = GitState.Read(PlayerPayload.RepoRoot);
			}
		}

		public void OnProcessScene(Scene scene, BuildReport report)
		{
			// The report is null when the editor enters play mode.
			if (report == null || !_active)
			{
				return;
			}

			ProcessedScenes.Add(scene.path);
			Fields.AddRange(PlayerPayload.FieldsIn(scene));
		}

		public void OnPostprocessBuild(BuildReport report)
		{
			if (!_active)
			{
				return;
			}

			_active = false;
			try
			{
				var enabled = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path);
				var unprocessed = PlayerPayload.UnprocessedScenes(enabled, ProcessedScenes);
				var fields = new List<PlayerPayload.PathField>(Fields);
				fields.AddRange(PlayerPayload.FieldsInScenes(unprocessed));
				var files = PlayerPayload.DataFiles(fields, PlayerPayload.RepoRoot);
				var streaming = PlayerPayload.StreamingAssetsFolder(report.summary.platform, report.summary.outputPath);
				var info = new PlayerBuildInfo
				{
					product = PlayerSettings.productName,
					version = PlayerSettings.bundleVersion,
					commit = _git.Commit,
					dirty = _git.Dirty,
					target = report.summary.platform.ToString(),
					built_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
					unity = Application.unityVersion,
				};
				PlayerPayload.Package(PlayerPayload.RepoRoot, files, streaming, info);
				Debug.Log($"[FerrySim] Packaged {files.Count} data file(s) into {streaming}: {string.Join(", ", files)}. Read {ProcessedScenes.Count} built scene(s) " +
					$"and opened {unprocessed.Count} build-settings scene(s) the build did not process. Build {info.Describe()}.");
			}
			catch (Exception error) when (!(error is BuildFailedException))
			{
				throw new BuildFailedException(error);
			}
		}
	}
}
