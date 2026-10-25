using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FerrySim.Slice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FerrySim.Build
{
	/// <summary>
	/// The repository files a player reads at run time, and how a build packages them. They are named, as
	/// repository-relative paths with forward slashes that <c>SimPaths.Resolve</c> looks up in StreamingAssets first, in
	/// serialized string fields, or arrays of them, whose names end in <c>File</c> or <c>Files</c> (<c>vesselFile</c>,
	/// <c>geoidOffsetFile</c>), or name folders in fields ending in <c>Folder</c> or <c>Folders</c>
	/// (<c>environmentFolder</c>: every file in the folder and its subfolders, whatever they are, so an environment pack
	/// travels whole, with any file a later pipeline adds): on the components of the built scenes, and on the project assets
	/// they reference under Assets/ (ScriptableObjects such as the runner's scenario, prefabs, and prefabs behind component
	/// references). A build copies each file into the player's StreamingAssets at the same relative path and records it, with
	/// its SHA-256, in <see cref="PlayerBuildInfo"/>. Not covered, so a build fails or the file must travel as a TextAsset: a
	/// scenario JSON file or a replay (each names further files), and files a script finds by a name it builds in code.
	/// </summary>
	public static class PlayerPayload
	{
		// The fields whose files name further files in turn: a scenario's vessel file, a replay's inputs and vessel.
		private static readonly string[] UnsupportedFields = { "scenarioJsonFile", "replayRecording" };

		private static readonly Regex ArrayElement = new Regex(@"\.Array\.data\[\d+\]", RegexOptions.CultureInvariant);

		/// <summary>The repository root: the editor's Application.dataPath is &lt;repo&gt;/unity/Assets.</summary>
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		/// <summary>
		/// True for a field that names data files: its name ends in File or Files, or it is replayRecording. An array
		/// element (<c>vesselFiles.Array.data[0]</c>) goes by its array's name.
		/// </summary>
		public static bool IsDataField(string propertyPath)
		{
			var name = FieldName(propertyPath);
			return name.EndsWith("File", StringComparison.Ordinal) || name.EndsWith("Files", StringComparison.Ordinal) || name == "replayRecording";
		}

		/// <summary>
		/// True for a field that names a data folder, every file of which a player reads: its name ends in Folder or Folders.
		/// An array element goes by its array's name.
		/// </summary>
		public static bool IsFolderField(string propertyPath)
		{
			var name = FieldName(propertyPath);
			return name.EndsWith("Folder", StringComparison.Ordinal) || name.EndsWith("Folders", StringComparison.Ordinal);
		}

		/// <summary>
		/// The repository-relative data files the fields name, each once, in ordinal order: the files of file fields and every
		/// file under the folders of folder fields (names starting with a dot, such as .DS_Store, are not data and are left
		/// out). Empty fields are skipped. Throws <see cref="InvalidOperationException"/> naming every field a player could not
		/// use: a scenario file or a replay; a path that is absolute, climbs out of the repository or passes through a symbolic
		/// link (a link inside a folder included); one written with backslashes or surrounding spaces (a player reads the path
		/// as written); or a file or folder the repository lacks, or a folder with no files.
		/// </summary>
		public static IReadOnlyList<string> DataFiles(IEnumerable<PathField> fields, string repoRoot)
		{
			var root = Path.GetFullPath(repoRoot);
			var files = new SortedSet<string>(StringComparer.Ordinal);
			var problems = new List<string>();
			foreach (var field in fields)
			{
				var folder = IsFolderField(field.Property);
				if ((!folder && !IsDataField(field.Property)) || string.IsNullOrWhiteSpace(field.Value))
				{
					continue;
				}

				if (folder)
				{
					var contents = FolderFiles(field.Value, root, out var folderProblem);
					if (folderProblem != null)
					{
						problems.Add($"{field}: {folderProblem}");
						continue;
					}

					files.UnionWith(contents);
					continue;
				}

				var problem = UnsupportedFields.Contains(FieldName(field.Property))
					? "players cannot load a scenario file or a replay yet: the build would not package the files it names in turn"
					: Problem(field.Value, root);
				if (problem != null)
				{
					problems.Add($"{field}: {problem}");
					continue;
				}

				files.Add(field.Value);
			}

			if (problems.Count > 0)
			{
				throw new InvalidOperationException("The player would miss data it reads at run time:\n" + string.Join("\n", problems));
			}

			return files.ToList();
		}

		/// <summary>Every serialized string field on the scene's components and, transitively, on the project assets they reference.</summary>
		public static List<PathField> FieldsIn(Scene scene) => FieldsIn(scene.GetRootGameObjects(), scene.name + "/");

		/// <summary>Every serialized string field on the components under <paramref name="roots"/> and, transitively, on the project assets they reference.</summary>
		public static List<PathField> FieldsIn(IEnumerable<GameObject> roots, string ownerPrefix)
		{
			var fields = new List<PathField>();
			var visited = new HashSet<Object>();
			var assets = new Queue<Object>();
			foreach (var root in roots)
			{
				CollectComponents(root, ownerPrefix, fields, visited, assets);
			}

			while (assets.Count > 0)
			{
				var asset = assets.Dequeue();
				var path = AssetDatabase.GetAssetPath(asset);
				if (asset is GameObject prefab)
				{
					if (visited.Add(prefab))
					{
						CollectComponents(prefab, path + ":", fields, visited, assets);
					}
				}
				else
				{
					Collect(asset, path, fields, visited, assets);
				}
			}

			return fields;
		}

		/// <summary>The fields of the given scenes, opening any that are not open and closing them again.</summary>
		public static List<PathField> FieldsInScenes(IEnumerable<string> scenePaths)
		{
			var fields = new List<PathField>();
			foreach (var path in scenePaths)
			{
				var open = SceneManager.GetSceneByPath(path);
				var opened = !open.IsValid() || !open.isLoaded;
				var scene = opened ? EditorSceneManager.OpenScene(path, OpenSceneMode.Additive) : open;
				try
				{
					fields.AddRange(FieldsIn(scene));
				}
				finally
				{
					if (opened)
					{
						EditorSceneManager.CloseScene(scene, true);
					}
				}
			}

			return fields;
		}

		/// <summary>The enabled build-settings scenes the build did not process, in build order, for reading by opening them.</summary>
		public static IReadOnlyList<string> UnprocessedScenes(IEnumerable<string> enabled, IEnumerable<string> processed)
		{
			var seen = new HashSet<string>(processed, StringComparer.Ordinal);
			return enabled.Where(scene => !seen.Contains(scene)).ToList();
		}

		public static bool Supports(BuildTarget target) =>
			target == BuildTarget.StandaloneOSX || target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneLinux64;

		/// <summary>The StreamingAssets folder inside the player built at <paramref name="playerPath"/>, as each standalone platform lays it out.</summary>
		public static string StreamingAssetsFolder(BuildTarget target, string playerPath)
		{
			switch (target)
			{
				case BuildTarget.StandaloneOSX:
					return Path.Combine(playerPath, "Contents", "Resources", "Data", "StreamingAssets");
				case BuildTarget.StandaloneWindows:
				case BuildTarget.StandaloneWindows64:
				case BuildTarget.StandaloneLinux64:
					return Path.Combine(Path.GetDirectoryName(playerPath) ?? string.Empty, Path.GetFileNameWithoutExtension(playerPath) + "_Data", "StreamingAssets");
				default:
					throw new NotSupportedException($"Player data is packaged for the standalone players only, not {target}.");
			}
		}

		/// <summary>
		/// Copies each of <paramref name="files"/> from <paramref name="repoRoot"/> into <paramref name="streamingAssets"/> at
		/// the same relative path, fills in <paramref name="info"/>'s payload from the copies and writes it there as
		/// <see cref="PlayerBuildInfo.FileName"/>. Each file is checked again, as <see cref="DataFiles"/> checks it, first.
		/// </summary>
		public static void Package(string repoRoot, IReadOnlyList<string> files, string streamingAssets, PlayerBuildInfo info)
		{
			var root = Path.GetFullPath(repoRoot);
			foreach (var file in files)
			{
				var problem = Problem(file, root);
				if (problem != null)
				{
					throw new InvalidOperationException($"Not packaging {file}: {problem}");
				}
			}

			Directory.CreateDirectory(streamingAssets);
			var packaged = new List<PlayerBuildInfo.PackagedFile>();
			foreach (var file in files)
			{
				var target = Path.Combine(streamingAssets, file);
				Directory.CreateDirectory(Path.GetDirectoryName(target) ?? streamingAssets);
				File.Copy(Path.Combine(root, file), target, true);
				var bytes = File.ReadAllBytes(target);
				packaged.Add(new PlayerBuildInfo.PackagedFile { path = file, sha256 = Sha256(bytes), bytes = bytes.LongLength });
			}

			info.payload = packaged.ToArray();
			File.WriteAllText(Path.Combine(streamingAssets, PlayerBuildInfo.FileName), JsonUtility.ToJson(info, true) + "\n");
		}

		// The field's own name: the last segment of its property path, an array element going by its array's name.
		private static string FieldName(string propertyPath)
		{
			if (string.IsNullOrEmpty(propertyPath))
			{
				return string.Empty;
			}

			var path = ArrayElement.Replace(propertyPath, string.Empty);
			return path.Substring(path.LastIndexOf('.') + 1);
		}

		// Every file under a repository-relative folder, as repository-relative paths, each checked as a file field is; or the
		// first reason a player could not use the folder.
		private static List<string> FolderFiles(string folder, string root, out string problem)
		{
			var files = new List<string>();
			problem = PathProblem(folder, root, expectFolder: true);
			if (problem != null)
			{
				return files;
			}

			var full = Path.Combine(root, folder);
			foreach (var path in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
			{
				var relative = folder.TrimEnd('/') + "/" + path.Substring(full.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
					.Replace(Path.DirectorySeparatorChar, '/');
				if (relative.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal)))
				{
					continue;
				}

				var fileProblem = Problem(relative, root);
				if (fileProblem != null)
				{
					problem = $"{relative}: {fileProblem}";
					return files;
				}

				files.Add(relative);
			}

			if (files.Count == 0)
			{
				problem = $"the folder {Path.Combine(root, folder)} has no files";
			}

			return files;
		}

		// Why a player could not read this path from its StreamingAssets copy through SimPaths.Resolve, or null.
		private static string Problem(string path, string root) => PathProblem(path, root, expectFolder: false);

		private static string PathProblem(string path, string root, bool expectFolder)
		{
			if (path.IndexOf('\\') >= 0 || path != path.Trim())
			{
				return "write it with forward slashes and no surrounding spaces, since a player reads the path as written";
			}

			var parts = path.Split('/');
			if (Path.IsPathRooted(path) || parts.Contains(".."))
			{
				return "a player carries repository-relative paths inside the repository only";
			}

			var current = root;
			foreach (var part in parts)
			{
				current = Path.Combine(current, part);
				if (!File.Exists(current) && !Directory.Exists(current))
				{
					return $"no such file in the repository ({Path.Combine(root, path)})";
				}

				// Mono and .NET both report a symbolic link as a reparse point.
				if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				{
					return $"{current} is a symbolic link; a player carries files that are in the repository only";
				}
			}

			if (expectFolder)
			{
				return Directory.Exists(current) ? null : $"no such folder in the repository ({current})";
			}

			return File.Exists(current) ? null : $"no such file in the repository ({current})";
		}

		private static void CollectComponents(GameObject root, string ownerPrefix, List<PathField> fields, HashSet<Object> visited, Queue<Object> assets)
		{
			foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
			{
				// A missing script leaves a null component.
				if (component != null)
				{
					Collect(component, $"{ownerPrefix}{HierarchyPath(component.transform)} ({component.GetType().Name})", fields, visited, assets);
				}
			}
		}

		private static void Collect(Object target, string owner, List<PathField> fields, HashSet<Object> visited, Queue<Object> assets)
		{
			if (!visited.Add(target))
			{
				return;
			}

			using (var serialized = new SerializedObject(target))
			{
				var property = serialized.GetIterator();
				var enterChildren = true;
				while (property.Next(enterChildren))
				{
					// A string's characters are not properties of their own.
					enterChildren = property.propertyType != SerializedPropertyType.String;
					if (property.propertyType == SerializedPropertyType.String)
					{
						fields.Add(new PathField(owner, property.propertyPath, property.stringValue));
					}
					else if (property.propertyType == SerializedPropertyType.ObjectReference)
					{
						var referenced = property.objectReferenceValue;

						// A component in a prefab stands for its whole prefab, which is what gets instantiated.
						if (referenced is Component component && AssetDatabase.Contains(component))
						{
							referenced = component.transform.root.gameObject;
						}

						if ((referenced is ScriptableObject || referenced is GameObject) && !visited.Contains(referenced)
							&& AssetDatabase.GetAssetPath(referenced).StartsWith("Assets/", StringComparison.Ordinal))
						{
							assets.Enqueue(referenced);
						}
					}
				}
			}
		}

		private static string HierarchyPath(Transform transform)
		{
			var names = new Stack<string>();
			for (var current = transform; current != null; current = current.parent)
			{
				names.Push(current.name);
			}

			return string.Join("/", names);
		}

		private static string Sha256(byte[] bytes)
		{
			using (var sha = SHA256.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
			}
		}

		/// <summary>A serialized string field and its value, with where it was found, for messages.</summary>
		public readonly struct PathField
		{
			public PathField(string owner, string property, string value)
			{
				Owner = owner;
				Property = property;
				Value = value;
			}

			public string Owner { get; }

			/// <summary>The serialized property path: <c>vesselFile</c>, or <c>settings.vesselFile</c> on a scenario asset.</summary>
			public string Property { get; }

			public string Value { get; }

			public override string ToString() => $"{Owner} {Property} = '{Value}'";
		}
	}
}
