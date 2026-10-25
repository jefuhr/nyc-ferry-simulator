using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Sim;
using FerrySim.Slice;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FerrySim.Build.Tests
{
	/// <summary>
	/// What a player build packages (<see cref="PlayerPayload"/>). Failure modes guarded: a data file the slice reads at
	/// run time left out of the player (the scene names it in a serialized <c>*File</c> field or an array of them, the
	/// scenario asset in its <c>vesselFile</c>, a prefab behind a component reference in its own fields); a scene the
	/// build did not process going unread; a field naming a file the repository does not have passing silently; a
	/// scenario file or replay packaged without the files it names in turn; an absolute path, one that climbs out of
	/// the repository, or a symbolic link out of it, copied into a player; a path the build would tidy up but a player
	/// reads as written (backslashes, surrounding spaces); files landing anywhere but StreamingAssets at the same
	/// relative path for each platform's layout; and a build record whose hashes or sizes do not match the copied bytes.
	/// For data folders (<c>*Folder</c> fields, such as the slice's environment packs): a file the pipeline adds, in any
	/// subfolder, left out because the build packaged a fixed list; a missing or empty folder passing; a symbolic link inside
	/// one followed out of the repository; and editor clutter such as .DS_Store shipped as data.
	/// </summary>
	public class PlayerPayloadTests
	{
		private const string SliceScenePath = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";

		private string _temp;

		private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		[SetUp]
		public void SetUp()
		{
			_temp = Path.Combine(Path.GetTempPath(), "ferrysim-payload-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_temp);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(_temp, true);

		[Test]
		public void TheSliceScene_NamesTheVesselFiles_TheGeoidFile_AndEveryFileOfItsEnvironmentPacks()
		{
			const string packs = "data/processed/lower-east-river/env";
			if (!Directory.Exists(Path.Combine(RepoRoot, packs)))
			{
				Assert.Ignore("The environment packs are not built (" + packs + ").");
			}

			var scene = EditorSceneManager.OpenScene(SliceScenePath, OpenSceneMode.Additive);
			try
			{
				var fields = PlayerPayload.FieldsIn(scene);
				var listing = string.Join("\n", fields.Where(field => PlayerPayload.IsDataField(field.Property) || PlayerPayload.IsFolderField(field.Property)).Select(field => field.ToString()));
				var packFiles = Directory.GetFiles(Path.Combine(RepoRoot, packs), "*", SearchOption.AllDirectories)
					.Select(path => packs + "/" + path.Substring(Path.Combine(RepoRoot, packs).Length + 1).Replace(Path.DirectorySeparatorChar, '/'))
					.Where(path => !path.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal)));
				var expected = new[] { "data/processed/lower-east-river/geoid_offset.json", "vessels/k-class.json", "vessels/t-class.json" }.Concat(packFiles)
					.OrderBy(path => path, StringComparer.Ordinal).ToArray();

				var files = PlayerPayload.DataFiles(fields, RepoRoot);

				Assert.That(files, Is.EqualTo(expected), listing);
				Assert.That(files.Count(path => path.StartsWith(packs + "/days/", StringComparison.Ordinal) && path.EndsWith("/DIGEST", StringComparison.Ordinal)), Is.EqualTo(28),
					"every scenario day's pack");
				Assert.That(files, Does.Contain(packs + "/depth/slice.f32"));
				Assert.That(fields.Any(field => field.Property == "settings.vesselFile" && field.Value == "vessels/t-class.json"), Is.True,
					"the scenario asset's vessel file is found through the runner's reference to it:\n" + listing);
				Assert.That(fields.Any(field => field.Property == "environmentFolder" && field.Value == packs), Is.True, listing);
			}
			finally
			{
				EditorSceneManager.CloseScene(scene, true);
			}
		}

		[Test]
		public void AFolderField_PackagesEveryFileUnderIt_WhateverItIs_ButNotDotFiles()
		{
			var env = Path.Combine(_temp, "data", "env");
			Directory.CreateDirectory(Path.Combine(env, "days", "2025-07-14"));
			Directory.CreateDirectory(Path.Combine(env, "depth"));
			Directory.CreateDirectory(Path.Combine(env, "currents", "fields"));
			File.WriteAllText(Path.Combine(env, "days", "2025-07-14", "manifest.json"), "{}");
			File.WriteAllText(Path.Combine(env, "days", "2025-07-14", "DIGEST"), "x");
			File.WriteAllText(Path.Combine(env, "depth", "slice.f32"), "x");
			File.WriteAllText(Path.Combine(env, "currents", "fields", "new-file-a-later-pipeline-adds.bin"), "x");
			File.WriteAllText(Path.Combine(env, ".DS_Store"), "x");
			File.WriteAllText(Path.Combine(env, "depth", ".hidden"), "x");

			var files = PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "environmentFolder", "data/env") }, _temp);

			Assert.That(files, Is.EqualTo(new[]
			{
				"data/env/currents/fields/new-file-a-later-pipeline-adds.bin",
				"data/env/days/2025-07-14/DIGEST",
				"data/env/days/2025-07-14/manifest.json",
				"data/env/depth/slice.f32",
			}));
			Assert.That(PlayerPayload.IsFolderField("environmentFolder"), Is.True);
			Assert.That(PlayerPayload.IsFolderField("settings.packFolders.Array.data[2]"), Is.True);
			Assert.That(PlayerPayload.IsFolderField("vesselFile"), Is.False);
			Assert.That(PlayerPayload.IsDataField("environmentFolder"), Is.False, "a folder field is not a file field");
		}

		[Test]
		public void AMissingOrEmptyFolder_OrOneWrittenOutsideTheRepository_FailsTheBuild()
		{
			Directory.CreateDirectory(Path.Combine(_temp, "empty", "sub"));
			File.WriteAllText(Path.Combine(_temp, "a-file.json"), "{}");

			foreach (var (path, reason) in new[]
			{
				("missing", "no such file"),
				("empty", "has no files"),
				("a-file.json", "no such folder"),
				("../outside", "repository-relative"),
				("data\\env", "forward slashes"),
			})
			{
				var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "environmentFolder", path) }, _temp), path);
				Assert.That(error.Message, Does.Contain(reason), path);
				Assert.That(error.Message, Does.Contain("environmentFolder"), path);
			}
		}

		[Test]
		public void ASymbolicLinkInsideAFolder_FailsTheBuild()
		{
			if (Application.platform == RuntimePlatform.WindowsEditor)
			{
				Assert.Ignore("Makes its links with ln -s.");
			}

			var outside = Path.Combine(_temp, "outside");
			Directory.CreateDirectory(outside);
			File.WriteAllText(Path.Combine(outside, "secret.json"), "{}");
			var repo = Path.Combine(_temp, "repo");
			Directory.CreateDirectory(Path.Combine(repo, "env"));
			File.WriteAllText(Path.Combine(repo, "env", "manifest.json"), "{}");
			Symlink(Path.Combine(outside, "secret.json"), Path.Combine(repo, "env", "linked.json"));

			var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "environmentFolder", "env") }, repo));

			Assert.That(error.Message, Does.Contain("symbolic link"));
		}

		[Test]
		public void OnlyFieldsNamedLikeFiles_AreData_AndEmptyOnesAreSkipped()
		{
			File.WriteAllText(Path.Combine(_temp, "a.json"), "{}");
			var files = PlayerPayload.DataFiles(new[]
			{
				new PlayerPayload.PathField("owner", "vesselFile", "a.json"),
				new PlayerPayload.PathField("owner", "settings.vesselFile", "a.json"),
				new PlayerPayload.PathField("owner", "landingContactsPath", "not/a/data/file.json"),
				new PlayerPayload.PathField("owner", "names.Array.data[0]", "missing.json"),
				new PlayerPayload.PathField("owner", "scenarioJsonFile", string.Empty),
				new PlayerPayload.PathField("owner", "replayRecording", "   "),
			}, _temp);

			Assert.That(files, Is.EqualTo(new[] { "a.json" }));
		}

		// Each names further files (a vessel file, a replay's inputs) that the build would not package.
		[TestCase("scenarioJsonFile", "scenarios/approach.json")]
		[TestCase("replayRecording", "runs/run.csv")]
		public void AScenarioFileOrAReplay_FailsTheBuild(string property, string path)
		{
			Directory.CreateDirectory(Path.Combine(_temp, Path.GetDirectoryName(path) ?? string.Empty));
			File.WriteAllText(Path.Combine(_temp, path), "x");

			var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", property, path) }, _temp));

			Assert.That(error.Message, Does.Contain(property));
			Assert.That(error.Message, Does.Contain("players cannot"));
		}

		[Test]
		public void AComponentReferenceIntoAPrefab_IsFollowed()
		{
			const string prefabPath = "Assets/FerrySim/Build/Tests/EditMode/PayloadTraversalTemporary.prefab";
			var source = new GameObject("prefab source");
			var user = new GameObject("referencing object");
			try
			{
				SetProperty(source.AddComponent<VesselSimulationRunner>(), "scenarioJsonFile", property => property.stringValue = "scenarios/probe.json");
				var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
				SetProperty(user.AddComponent<SliceBootstrap>(), "runner", property => property.objectReferenceValue = prefab.GetComponent<VesselSimulationRunner>());

				var fields = PlayerPayload.FieldsIn(new[] { user }, "test/");

				Assert.That(fields.Any(field => field.Property == "scenarioJsonFile" && field.Value == "scenarios/probe.json" && field.Owner.StartsWith(prefabPath, StringComparison.Ordinal)), Is.True,
					string.Join("\n", fields.Select(field => field.ToString())));
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(source);
				UnityEngine.Object.DestroyImmediate(user);
				AssetDatabase.DeleteAsset(prefabPath);
			}
		}

		[Test]
		public void TheBuildSettingsScenesTheBuildDidNotProcess_AreTheOnesToOpen()
		{
			Assert.That(PlayerPayload.UnprocessedScenes(new[] { "a.unity", "b.unity", "c.unity" }, new[] { "b.unity" }), Is.EqualTo(new[] { "a.unity", "c.unity" }));
			Assert.That(PlayerPayload.UnprocessedScenes(new[] { "a.unity" }, new[] { "a.unity" }), Is.Empty);
		}

		[Test]
		public void TheElementsOfFileArrays_AreData()
		{
			File.WriteAllText(Path.Combine(_temp, "a.json"), "{}");
			File.WriteAllText(Path.Combine(_temp, "b.json"), "{}");
			var files = PlayerPayload.DataFiles(new[]
			{
				new PlayerPayload.PathField("owner", "vesselFile.Array.data[0]", "a.json"),
				new PlayerPayload.PathField("owner", "settings.vesselFiles.Array.data[1]", "b.json"),
				new PlayerPayload.PathField("owner", "names.Array.data[0]", "missing.json"),
			}, _temp);

			Assert.That(files, Is.EqualTo(new[] { "a.json", "b.json" }));
		}

		// SimPaths.Resolve reads a field as written: a player on macOS takes a backslash as part of a file name and keeps spaces.
		[TestCase("vessels\\t-class.json")]
		[TestCase(" vessels/t-class.json")]
		[TestCase("vessels/t-class.json ")]
		public void PathsAPlayerWouldReadOtherwise_FailTheBuild(string path)
		{
			Directory.CreateDirectory(Path.Combine(_temp, "vessels"));
			File.WriteAllText(Path.Combine(_temp, "vessels", "t-class.json"), "{}");

			var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "vesselFile", path) }, _temp));

			Assert.That(error.Message, Does.Contain("forward slashes"));
		}

		[Test]
		public void ASymbolicLinkOutOfTheRepository_FailsTheBuild()
		{
			if (Application.platform == RuntimePlatform.WindowsEditor)
			{
				Assert.Ignore("Makes its links with ln -s.");
			}

			var repo = Path.Combine(_temp, "repo");
			var outside = Path.Combine(_temp, "outside");
			Directory.CreateDirectory(repo);
			Directory.CreateDirectory(outside);
			File.WriteAllText(Path.Combine(outside, "secret.json"), "{}");
			Symlink(Path.Combine(outside, "secret.json"), Path.Combine(repo, "link.json"));
			Symlink(outside, Path.Combine(repo, "linked"));

			foreach (var path in new[] { "link.json", "linked/secret.json" })
			{
				var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "vesselFile", path) }, repo), path);
				Assert.That(error.Message, Does.Contain("symbolic link"), path);
			}
		}

		[Test]
		public void AMissingFile_FailsTheBuild_NamingTheFieldAndThePath()
		{
			var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(
				new[] { new PlayerPayload.PathField("Slice/Runner (VesselSimulationRunner)", "vesselFile", "vessels/missing.json") }, _temp));

			Assert.That(error.Message, Does.Contain("Slice/Runner (VesselSimulationRunner)"));
			Assert.That(error.Message, Does.Contain("vesselFile"));
			Assert.That(error.Message, Does.Contain("vessels/missing.json"));
		}

		[TestCase("/etc/hosts")]
		[TestCase("../outside.json")]
		[TestCase("vessels/../../outside.json")]
		public void PathsOutsideTheRepository_FailTheBuild(string path)
		{
			var error = Assert.Throws<InvalidOperationException>(() => PlayerPayload.DataFiles(new[] { new PlayerPayload.PathField("owner", "vesselFile", path) }, _temp));

			Assert.That(error.Message, Does.Contain("repository-relative"));
			Assert.That(error.Message, Does.Contain(path));
		}

		[Test]
		public void StreamingAssets_FollowsEachPlatformsLayout()
		{
			Assert.That(PlayerPayload.StreamingAssetsFolder(BuildTarget.StandaloneOSX, "/out/NYC Ferry Simulator.app"),
				Is.EqualTo(Path.Combine("/out/NYC Ferry Simulator.app", "Contents", "Resources", "Data", "StreamingAssets")));
			Assert.That(PlayerPayload.StreamingAssetsFolder(BuildTarget.StandaloneWindows64, "/out/NYC Ferry Simulator/NYC Ferry Simulator.exe"),
				Is.EqualTo(Path.Combine("/out/NYC Ferry Simulator", "NYC Ferry Simulator_Data", "StreamingAssets")));
			Assert.That(PlayerPayload.StreamingAssetsFolder(BuildTarget.StandaloneLinux64, "/out/sim/sim.x86_64"),
				Is.EqualTo(Path.Combine("/out/sim", "sim_Data", "StreamingAssets")));
			Assert.Throws<NotSupportedException>(() => PlayerPayload.StreamingAssetsFolder(BuildTarget.Android, "/out/sim.apk"));
			Assert.That(PlayerPayload.Supports(BuildTarget.StandaloneOSX) && PlayerPayload.Supports(BuildTarget.StandaloneWindows64), Is.True);
			Assert.That(PlayerPayload.Supports(BuildTarget.Android), Is.False);
		}

		[Test]
		public void Packaging_CopiesEachFileUnderItsRelativePath_AndRecordsItsHash()
		{
			var repo = Path.Combine(_temp, "repo");
			Directory.CreateDirectory(Path.Combine(repo, "vessels"));
			File.WriteAllText(Path.Combine(repo, "vessels", "t-class.json"), "{\"id\": \"t-class\"}");
			var streaming = Path.Combine(_temp, "player", "StreamingAssets");
			var info = new PlayerBuildInfo { product = "NYC Ferry Simulator", version = "0.1.0", commit = "abc", target = "StandaloneOSX" };

			PlayerPayload.Package(repo, new[] { "vessels/t-class.json" }, streaming, info);

			var copied = Path.Combine(streaming, "vessels", "t-class.json");
			Assert.That(File.ReadAllText(copied), Is.EqualTo("{\"id\": \"t-class\"}"));
			Assert.That(PlayerBuildInfo.TryLoad(streaming, out var record, out var problem), Is.True, problem);
			Assert.That(record.version, Is.EqualTo("0.1.0"));
			Assert.That(record.payload, Has.Length.EqualTo(1));
			Assert.That(record.payload[0].path, Is.EqualTo("vessels/t-class.json"));
			Assert.That(record.payload[0].bytes, Is.EqualTo(new FileInfo(copied).Length));
			Assert.That(record.payload[0].sha256, Is.EqualTo(Sha256(copied)));
		}

		private static void SetProperty(UnityEngine.Object target, string name, Action<SerializedProperty> set)
		{
			var serialized = new SerializedObject(target);
			var property = serialized.FindProperty(name);
			Assert.That(property, Is.Not.Null, $"{target.GetType().Name} has no serialized {name}");
			set(property);
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		private static void Symlink(string target, string link)
		{
			using (var process = Process.Start(new ProcessStartInfo("/bin/ln", $"-s \"{target}\" \"{link}\"") { UseShellExecute = false }))
			{
				process.WaitForExit();
				Assert.That(process.ExitCode, Is.Zero, $"ln -s {target} {link}");
			}
		}

		private static string Sha256(string path)
		{
			using (var sha = SHA256.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
			}
		}
	}
}
