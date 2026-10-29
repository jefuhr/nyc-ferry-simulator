using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
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
		private static JObject _inventory;
		private static IReadOnlyList<string> _files;
		public static string[] RequestedScenes { get; set; }

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
				var scenes = RequestedScenes ?? EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
				_inventory = scenes.Contains(HarborPayload.Scene) ? HarborPayload.Create(PlayerPayload.RepoRoot) : null;
				var discovered = PlayerPayload.DataFiles(PlayerPayload.FieldsInScenes(scenes), PlayerPayload.RepoRoot);
				_files = (_inventory == null ? discovered : discovered.Concat(_inventory["files"].Select(entry => (string)entry["path"])))
					.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
				if (_inventory != null && scenes.SequenceEqual(new[] { HarborPayload.Scene }) && !_files.SequenceEqual(_inventory["files"].Select(entry => (string)entry["path"])))
					throw new BuildFailedException("A harbor scene field reads data outside the configured inventory.");
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
				var enabled = RequestedScenes ?? EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
				var unprocessed = PlayerPayload.UnprocessedScenes(enabled, ProcessedScenes);
				var fields = new List<PlayerPayload.PathField>(Fields);
				fields.AddRange(PlayerPayload.FieldsInScenes(unprocessed));
				var files = _files;
				var processedFiles = PlayerPayload.DataFiles(fields, PlayerPayload.RepoRoot);
				if (processedFiles.Except(files).Any()) throw new BuildFailedException("Processed scenes introduced paths absent from preflight.");
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
				if (_inventory != null)
				{
					var recordPath = Path.Combine(streaming, PlayerBuildInfo.FileName);
					var record = JObject.Parse(File.ReadAllText(recordPath));
					var metadata = (JObject)_inventory.DeepClone();
					metadata.Remove("files");
					record["harbor"] = metadata;
					record["scenes"] = new JArray(enabled);
					File.WriteAllText(recordPath, record.ToString() + "\n");
					File.WriteAllText(Path.Combine(Path.GetDirectoryName(report.summary.outputPath), "payload-inventory.json"), _inventory.ToString() + "\n");
				}
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
