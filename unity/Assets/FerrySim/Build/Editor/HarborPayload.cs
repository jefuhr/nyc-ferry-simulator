using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Serialization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Build
{
	/// <summary>Explicit offline harbor inventory, validated before Unity starts writing a player.</summary>
	public static class HarborPayload
	{
		public const string Scene = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		public const string SliceScene = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";
		public const string Configuration = "unity/Assets/FerrySim/Build/Editor/harbor-payload.json";
		public const string Harbor = "data/processed/harbor";

		public static void ValidateScenes(IEnumerable<string> scenes)
		{
			if (!scenes.SequenceEqual(new[] { Scene })) throw new InvalidOperationException("Release players must contain only " + Scene);
		}

		public static List<string> EnumerateRoots(string repo, IEnumerable<string> roots)
		{
			var result = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var root in roots)
			{
				SafePath(repo, root);
				var full = Path.Combine(repo, root);
				if (!File.Exists(full) && !Directory.Exists(full)) throw new InvalidOperationException("Missing required payload root: " + root);
				var paths = File.Exists(full) ? new[] { full } : Walk(full).ToArray();
				if (paths.Length == 0) throw new InvalidOperationException("Empty required payload root: " + root);
				foreach (var path in paths)
				{
					var relative = path.Substring(Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
					SafePath(repo, relative);
					if (relative.Split('/').Any(p => p.StartsWith(".", StringComparison.Ordinal) || p == "_sources") ||
						new[] { ".tif", ".tiff", ".log", ".md", ".pyc" }.Contains(Path.GetExtension(relative).ToLowerInvariant()))
						throw new InvalidOperationException("Excluded file inside payload root: " + relative);
					if (!result.Add(relative)) throw new InvalidOperationException("Duplicate file across payload roots: " + relative);
				}
			}
			return result.ToList();
		}

		private static IEnumerable<string> Walk(string directory)
		{
			foreach (var path in Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
			{
				if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symbolic link in payload: " + path);
				if (Directory.Exists(path)) { foreach (var child in Walk(path)) yield return child; }
				else yield return path;
			}
		}

		private static string SafePath(string root, string relative)
		{
			if (string.IsNullOrEmpty(relative) || relative != relative.Trim() || relative.Contains('\\') || Path.IsPathRooted(relative) ||
				relative.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) || relative.Contains(":"))
				throw new InvalidOperationException("Unsafe payload path: " + relative);
			var current = Path.GetFullPath(root);
			foreach (var part in relative.Split('/'))
			{
				current = Path.Combine(current, part);
				if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
					throw new InvalidOperationException("Symbolic link in payload path: " + relative);
			}
			return current;
		}

		public static void ValidateTileset(string repo, string folder)
		{
			var root = SafePath(repo, folder);
			var index = Read(Path.Combine(root, "index.json"));
			var schema = (string)index["schema"];
			if ((schema != "harbor-3dtiles-v1" && schema != "harbor-buildings-3dtiles-v1") || (string)index["tileset"] != "tileset.json")
				throw new InvalidOperationException("Unsupported tileset index schema/root: " + folder);
			var expected = new HashSet<string>(StringComparer.Ordinal) { "index.json" };
			foreach (var entry in index["files"] ?? throw new InvalidOperationException("Missing tileset file inventory: " + folder))
			{
				var path = (string)entry["path"];
				if (!expected.Add(path)) throw new InvalidOperationException("Duplicate tileset index file: " + path);
				CheckHash(root, path, (string)entry["sha256"]);
				if (new FileInfo(SafePath(root, path)).Length != (long)entry["bytes"]) throw new InvalidOperationException("Stale index size: " + folder + "/" + path);
			}
			Exact(root, expected);
			foreach (var path in expected.Where(p => p != "index.json" && p.EndsWith(".json", StringComparison.Ordinal)))
			{
				var document = Read(SafePath(root, path));
				foreach (var uri in document.SelectTokens("$..uri").Concat(document.SelectTokens("$..url")))
				{
					var value = (string)uri;
					// Match the runtime inventory reader: exports use literal local paths, never encoded/remote URLs.
					if (value == null || value.Contains("%")) throw new InvalidOperationException("Encoded/non-local tile URI: " + uri);
					SafePath(root, value);
					var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.Combine(root, path)), value));
					if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Escaping tile URI: " + uri);
					var local = target.Substring(root.Length + 1).Replace('\\', '/');
					if (!expected.Contains(local)) throw new InvalidOperationException("Unindexed tile URI: " + uri);
				}
			}
		}

		public static void ValidateRepository(string repo) => Create(repo);

		public static JObject Create(string repo)
		{
			var config = Read(Path.Combine(repo, Configuration));
			if ((string)config["schema"] != "ferrysim-player-roots/1") throw new InvalidOperationException("Unknown payload-root schema.");
			var roots = config["roots"].Values<string>().ToArray();
			var files = EnumerateRoots(repo, roots);
			var harbor = Path.Combine(repo, Harbor);
			foreach (var layer in new[] { "3dtiles", "3dtiles-buildings" })
			{
				ValidateTileset(repo, Harbor + "/" + layer);
				RequirePackaged(repo, files, Harbor + "/" + layer);
				var index = Read(Path.Combine(harbor, layer, "index.json"));
				ValidateSourceHashes(harbor, index["sources"] ?? index["harbor_sources"]);
			}
			var sourceIndex = Read(Path.Combine(harbor, "index.json"));
			if ((string)sourceIndex["schema"] != "harbor-tiles-v1") throw new InvalidOperationException("Unsupported source harbor index schema.");
			ValidateSourceHashes(harbor, sourceIndex["outputs"]);
			foreach (var tile in sourceIndex["tiles"]) ValidateSourceHashes(harbor, tile["outputs"]);
			var depth = EnvironmentPackJson.LoadVerifiedDepth(Path.Combine(harbor, "env/depth"));
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			if (runtime.EnvironmentDepthDigest != depth.Digest) throw new InvalidOperationException("Runtime links omitted/mismatched depth set.");
			var runtimeManifest = Read(Path.Combine(harbor, "runtime/manifest.json"));
			ValidateSourceHashes(harbor, runtimeManifest["sources"]);
			var runtimeNames = runtimeManifest["files"].Children<JProperty>().Select(p => p.Name).Concat(new[] { "manifest.json", "DIGEST" });
			Exact(Path.Combine(harbor, "runtime"), new HashSet<string>(runtimeNames, StringComparer.Ordinal));
			RequirePackaged(repo, files, Harbor + "/runtime");
			var envExpected = new HashSet<string>(StringComparer.Ordinal) { "depth/manifest.json", "depth/DIGEST" };
			foreach (var grid in Read(Path.Combine(harbor, "env/depth/manifest.json"))["grids"]) envExpected.Add("depth/" + (string)grid["file"]);
			var days = new JArray();
			var dates = roots.Where(p => p.StartsWith(Harbor + "/traffic/", StringComparison.Ordinal)).Select(Path.GetFileName).ToArray();
			if (dates.Length != 28 || dates.Distinct().Count() != 28) throw new InvalidOperationException("Expected all 28 supported traffic dates.");
			foreach (var date in dates)
			{
				var dayPath = Path.Combine(harbor, "env/days", date);
				var day = EnvironmentPackJson.Load(dayPath, depth);
				var manifest = Read(Path.Combine(dayPath, "manifest.json"));
				foreach (var name in manifest["files"].Children<JProperty>().Select(p => p.Name).Concat(new[] { "manifest.json", "DIGEST" })) envExpected.Add("days/" + date + "/" + name);
				if (manifest["field_digest"] != null)
				{
					var field = Path.Combine(harbor, "env/field");
					EnvironmentPackJson.LoadCurrentField(field);
					foreach (var path in Walk(field)) envExpected.Add(path.Substring(Path.Combine(harbor, "env").Length + 1).Replace('\\', '/'));
				}
				var traffic = TrafficPackJson.Load(Path.Combine(harbor, "traffic", date));
				RequirePackaged(repo, files, Harbor + "/traffic/" + date);
				var trafficManifest = Read(Path.Combine(harbor, "traffic", date, "manifest.json"));
				Exact(Path.Combine(harbor, "traffic", date), new HashSet<string>(trafficManifest["files"].Select(e => (string)e["name"]).Concat(new[] { "manifest.json" }), StringComparer.Ordinal));
				days.Add(new JObject { ["date"] = date, ["environment_digest"] = day.Digest, ["traffic_digest"] = traffic.Digest });
			}
			Exact(Path.Combine(harbor, "env"), envExpected);
			RequirePackaged(repo, files, Harbor + "/env");
			foreach (var required in new[] { Harbor + "/index.json", Harbor + "/unity/contacts.json", "vessels/t-class.json", "vessels/k-class.json", "unity/Assets/FerrySim/Harbor/Data/landing-contact-surfaces.json", "unity/Assets/FerrySim/Harbor/Data/landing-audit.json" })
				if (!files.Contains(required)) throw new InvalidOperationException("Missing required payload file: " + required);
			if (HashFile(Path.Combine(harbor, "unity/contacts.json")) != HashFile(Path.Combine(harbor, "runtime/contacts.json"))) throw new InvalidOperationException("Runtime contacts differ from source contacts.");
			ValidateSceneRoots(repo, files);
			var inventory = new JArray(files.Select(file => new JObject { ["path"] = file, ["bytes"] = new FileInfo(Path.Combine(repo, file)).Length, ["sha256"] = HashFile(Path.Combine(repo, file)) }));
			return new JObject
			{
				["schema"] = "ferrysim-harbor-player-inventory/1", ["configuration_sha256"] = HashFile(Path.Combine(repo, Configuration)),
				["schemas"] = new JObject { ["source_index"] = (string)sourceIndex["schema"], ["terrain"] = (string)Read(Path.Combine(harbor, "3dtiles/index.json"))["schema"], ["skyline"] = (string)Read(Path.Combine(harbor, "3dtiles-buildings/index.json"))["schema"], ["runtime"] = (string)runtimeManifest["format"], ["environment"] = "ferrysim-env/1", ["depth"] = "ferrysim-env-depth/1", ["traffic"] = "ferrysim-traffic/1" },
				["source_digests"] = new JObject { ["source_index_sha256"] = HashFile(Path.Combine(harbor, "index.json")), ["terrain_index_sha256"] = HashFile(Path.Combine(harbor, "3dtiles/index.json")), ["skyline_index_sha256"] = HashFile(Path.Combine(harbor, "3dtiles-buildings/index.json")), ["depth"] = depth.Digest, ["runtime"] = runtime.Digest, ["days"] = days },
				["roots"] = new JArray(roots.Select(root => RootRecord(root, inventory))), ["files"] = inventory,
				["file_count"] = files.Count, ["bytes"] = inventory.Sum(e => (long)e["bytes"]), ["exclusions"] = config["exclusions"].DeepClone(),
			};
		}

		private static void RequirePackaged(string repo, List<string> files, string root)
		{
			foreach (var required in EnumerateRoots(repo, new[] { root }))
				if (!files.Contains(required)) throw new InvalidOperationException("Pack references omitted file: " + required);
		}

		private static JObject RootRecord(string root, JArray inventory)
		{
			var entries = inventory.Where(e => (string)e["path"] == root || ((string)e["path"]).StartsWith(root + "/", StringComparison.Ordinal)).ToArray();
			var text = string.Concat(entries.Select(e => (string)e["path"] + "\0" + (string)e["sha256"] + "\0"));
			using (var sha = SHA256.Create()) return new JObject { ["path"] = root, ["files"] = entries.Length, ["bytes"] = entries.Sum(e => (long)e["bytes"]), ["inventory_sha256"] = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))) };
		}

		private static void ValidateSceneRoots(string repo, List<string> files)
		{
			var scene = Read(Path.Combine(repo, "unity/Assets/FerrySim/Harbor/Data/harbor-scene.json"));
			foreach (var field in new[] { "runtimePackFolder", "environmentFolder", "contactsFile" })
			{
				var value = (string)scene[field];
				if (!files.Any(f => f == value || f.StartsWith(value + "/", StringComparison.Ordinal))) throw new InvalidOperationException("Unpackaged scene root: " + field + " = " + value);
			}
			foreach (var layer in new[] { "terrain", "skyline" }) if (!files.Contains((string)scene[layer]["tilesetFile"])) throw new InvalidOperationException("Unpackaged scene tileset: " + layer);
			var audit = Read(Path.Combine(repo, "unity/Assets/FerrySim/Harbor/Data/landing-audit.json"));
			var depthDigest = File.ReadAllText(Path.Combine(repo, Harbor + "/env/depth/DIGEST")).Trim();
			var provenance = audit["matrix_provenance"] ?? throw new InvalidOperationException("Landing audit provenance is missing.");
			if ((string)audit["depth_digest"] != depthDigest || (string)provenance["depth_digest"] != depthDigest) throw new InvalidOperationException("Landing audit depth is stale.");
			CheckHash(repo, Harbor + "/runtime/contacts.json", (string)provenance["contacts_sha256"]);
			foreach (var vessel in provenance["vessel_sha256"].Children<JProperty>()) CheckHash(repo, vessel.Name, (string)vessel.Value);
			foreach (var day in provenance["day_digests"].Children<JProperty>())
				if (File.ReadAllText(Path.Combine(repo, Harbor + "/env/days/" + day.Name + "/DIGEST")).Trim() != (string)day.Value) throw new InvalidOperationException("Landing audit day is stale: " + day.Name);
		}

		private static void ValidateSourceHashes(string root, JToken sources)
		{
			if (sources == null) return;
			foreach (var source in sources) CheckHash(root, (string)source["file"], (string)source["sha256"]);
		}
		private static void CheckHash(string root, string path, string expected)
		{
			var full = SafePath(root, path);
			if (!File.Exists(full) || HashFile(full) != expected) throw new InvalidOperationException("Missing file or stale index/source hash: " + full);
		}
		private static void Exact(string root, HashSet<string> expected)
		{
			var actual = new HashSet<string>(Walk(root).Select(p => p.Substring(root.Length + 1).Replace('\\', '/')), StringComparer.Ordinal);
			if (!actual.SetEquals(expected)) throw new InvalidOperationException("Unlisted or missing files in " + root + ": " + string.Join(", ", actual.Except(expected).Concat(expected.Except(actual))));
		}
		private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path));
		public static string HashFile(string path) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(path)) return Hex(sha.ComputeHash(input)); }
		private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

		public static void PreflightFromCommandLine()
		{
			try
			{
				ValidateScenes(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path));
				var args = Environment.GetCommandLineArgs();
				var at = Array.IndexOf(args, "-ferrysimInventory");
				if (at < 0 || at + 1 >= args.Length || !Path.IsPathRooted(args[at + 1])) throw new ArgumentException("-ferrysimInventory needs an absolute output file.");
				var inventory = Create(PlayerPayload.RepoRoot);
				File.WriteAllText(args[at + 1], inventory.ToString() + "\n");
				Debug.Log("Harbor preflight passed: " + inventory["file_count"] + " files, " + inventory["bytes"] + " bytes.");
				EditorApplication.Exit(0);
			}
			catch (Exception error) { Debug.LogError(error); EditorApplication.Exit(1); }
		}
	}
}
