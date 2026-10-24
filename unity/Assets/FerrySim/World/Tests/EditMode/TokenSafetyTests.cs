using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CesiumForUnity;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Presets;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// No Cesium ion token or map API key may be serialized anywhere under unity/Assets (see
	/// World/README.md for the local-only token workflow). Failure modes guarded: a token pasted into
	/// CesiumRuntimeSettings or the ion server asset, a tileset or raster overlay carrying its own token
	/// or key, prefab/scene/variant overrides, preset records, unreadable serialization, and a scanner
	/// that silently matches nothing (renamed fields, wrong folder).
	/// </summary>
	public class TokenSafetyTests
	{
		private static readonly string[] ScannedExtensions = { ".asset", ".unity", ".prefab", ".preset", ".mat", ".lighting" };

		[Test]
		public void NoSerializedTokenUnderAssets()
		{
			var cesiumScripts = CesiumScriptGuids();
			Assert.That(cesiumScripts.Count, Is.GreaterThan(10), "could not list Cesium's scripts, so Cesium documents would go unrecognised");

			var findings = new List<string>();
			var scannedFiles = 0;
			var tokenFieldsSeen = 0;
			foreach (var path in Directory.EnumerateFiles(Application.dataPath, "*", SearchOption.AllDirectories))
			{
				var assetPath = "Assets/" + Path.GetRelativePath(Application.dataPath, path);
				if (!ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
					&& AssetImporter.GetAtPath(assetPath)?.GetType().Name != "NativeFormatImporter")
				{
					continue;
				}

				scannedFiles++;
				var result = TokenScanner.ScanFile(path, cesiumScripts);
				tokenFieldsSeen += result.FieldsSeen;
				findings.AddRange(result.Findings.Select(f => $"{Path.GetRelativePath(Application.dataPath, path)}: {f}"));
			}

			Assert.That(scannedFiles, Is.GreaterThan(5), "scanned too few files to mean anything");

			// The ion server asset and CesiumRuntimeSettings each hold two token fields; seeing none means
			// Cesium renamed them and this test would pass for the wrong reason.
			Assert.That(tokenFieldsSeen, Is.GreaterThanOrEqualTo(4), "no Cesium token fields recognised in Assets/CesiumSettings");
			Assert.That(findings, Is.Empty, "Serialized tokens found (clear them, rotate the token, and see World/README.md):\n" + string.Join("\n", findings));
		}

		[Test]
		public void Scanner_FlagsAPastedIonToken()
		{
			const string yaml = "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: abc, type: 3}\n  defaultIonAccessTokenId: \n  defaultIonAccessToken: TEST_ONLY_NOT_A_CREDENTIAL\n";

			var result = TokenScanner.Scan(yaml, new HashSet<string> { "abc" });

			Assert.That(result.Findings.Count, Is.EqualTo(1));
			Assert.That(result.Findings[0], Does.Contain("defaultIonAccessToken"));
			Assert.That(result.Findings[0], Does.Not.Contain("TEST_ONLY"), "never echo the token itself");
		}

		[Test]
		public void Scanner_FlagsAQuotedTokenInAnyDocument()
		{
			const string yaml = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: notcesium, type: 3}\n  _ionAccessToken: 'TEST_ONLY_NOT_A_CREDENTIAL'\n";

			Assert.That(TokenScanner.Scan(yaml, new HashSet<string>()).Findings.Count, Is.EqualTo(1));
		}

		[Test]
		public void Scanner_FlagsAGenericKeyOnlyInCesiumDocuments()
		{
			const string cesium = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: azure, type: 3}\n  _key: TEST_ONLY_NOT_A_CREDENTIAL\n";
			const string other = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: mine, type: 3}\n  _key: harmless\n";

			Assert.That(TokenScanner.Scan(cesium, new HashSet<string> { "azure" }).Findings.Count, Is.EqualTo(1));
			Assert.That(TokenScanner.Scan(other, new HashSet<string> { "azure" }).Findings, Is.Empty);
		}

		[TestCase("  _ionAccessToken: \n")]
		[TestCase("  _ionAccessToken: ''\n")]
		[TestCase("  _ionAccessToken: \"\"\n")]
		public void Scanner_AcceptsEmptyFields(string line)
		{
			var yaml = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: abc, type: 3}\n" + line;

			var result = TokenScanner.Scan(yaml, new HashSet<string> { "abc" });

			Assert.That(result.Findings, Is.Empty);
			Assert.That(result.FieldsSeen, Is.EqualTo(1));
		}

		// Failure modes: nested prefab overrides, variants, scene instances and preset property/value
		// records must not bypass the same guard that inspects direct settings fields.
		[TestCase("nested-prefab")]
		[TestCase("variant")]
		[TestCase("scene")]
		[TestCase("preset")]
		public void Scanner_FlagsGeneratedSerializedOverride(string kind)
		{
			var folder = "Assets/FerrySim/World/Tests/EditMode/TokenSafetyTemporary";
			Assert.That(AssetDatabase.IsValidFolder(folder), Is.False, "refuse to overwrite an existing fixture folder");
			AssetDatabase.CreateFolder("Assets/FerrySim/World/Tests/EditMode", "TokenSafetyTemporary");
			GameObject root = null;
			GameObject instance = null;
			GameObject container = null;
			Scene scene = default;
			try
			{
				root = new GameObject("Token-free base");
				root.SetActive(false); // Never activate a network-backed tileset.
				root.AddComponent<Cesium3DTileset>();
				var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Base.prefab");
				UnityEngine.Object.DestroyImmediate(root);
				instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
				var tileset = instance.GetComponent<Cesium3DTileset>();
				var serialized = new SerializedObject(tileset);
				serialized.FindProperty("_ionAccessToken").stringValue = "TEST_ONLY_NOT_A_CREDENTIAL";
				serialized.ApplyModifiedPropertiesWithoutUndo();
				PrefabUtility.RecordPrefabInstancePropertyModifications(tileset);
				Assert.That(PrefabUtility.GetPropertyModifications(instance).Any(m => m.propertyPath == "_ionAccessToken"), Is.True);

				string path;
				switch (kind)
				{
					case "nested-prefab":
						container = new GameObject("Nested instance");
						instance.transform.SetParent(container.transform);
						path = folder + "/Nested.prefab";
						PrefabUtility.SaveAsPrefabAsset(container, path);
						break;
					case "variant":
						path = folder + "/Variant.prefab";
						var variant = PrefabUtility.SaveAsPrefabAsset(instance, path);
						Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
						break;
					case "scene":
						scene = EditorSceneManager.OpenScene("Assets/FerrySim/World/Scenes/LowerEastRiver.unity", OpenSceneMode.Additive);
						SceneManager.MoveGameObjectToScene(instance, scene);
						path = folder + "/Override.unity";
						Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
						break;
					default:
						path = folder + "/Tileset.preset";
						AssetDatabase.CreateAsset(new Preset(tileset), path);
						AssetDatabase.SaveAssets();
						break;
				}

				var yaml = File.ReadAllText(path);
				Assert.That(yaml, Does.Contain("propertyPath: _ionAccessToken"), "fixture must exercise a serialized modification, not a direct field");
				var findings = TokenScanner.Scan(yaml, CesiumScriptGuids()).Findings;
				Assert.That(findings, Has.Count.EqualTo(1), kind + " must reject a non-empty token override");
				Assert.That(findings[0], Does.Contain("_ionAccessToken"));
				Assert.That(findings[0], Does.Not.Contain("TEST_ONLY"), "never echo the value");
			}
			finally
			{
				if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
				if (container != null) UnityEngine.Object.DestroyImmediate(container);
				if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
				if (root != null) UnityEngine.Object.DestroyImmediate(root);
				AssetDatabase.DeleteAsset(folder);
			}
		}

		[Test]
		public void Scanner_RejectsNonYamlSerializedCandidates()
		{
			var result = TokenScanner.Scan("\0binary serialized asset", new HashSet<string>());
			Assert.That(result.Findings, Has.Count.EqualTo(1), "uninspectable serialized assets must fail closed");
		}

		private static HashSet<string> CesiumScriptGuids()
		{
			var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Packages/com.cesium.unity" });
			return new HashSet<string>(guids);
		}
	}

	/// <summary>Finds non-empty token and key fields in Unity YAML documents.</summary>
	internal static class TokenScanner
	{
		// Names that only ever hold a secret: checked in every document.
		private static readonly string[] SecretFields =
		{
			"_ionAccessToken",
			"_defaultIonAccessToken",
			"_defaultIonAccessTokenID",
			"defaultIonAccessToken",
			"defaultIonAccessTokenId",
			"_bingMapsKey",
		};

		// Generic names Cesium uses for keys (Google Map Tiles, Azure Maps): checked in Cesium documents only.
		private static readonly string[] CesiumKeyFields = { "_apiKey", "_key" };

		private static readonly Regex Field = new Regex(@"^\s*(?:(- )?)([A-Za-z_][A-Za-z0-9_]*):(.*)$", RegexOptions.Compiled);
		private static readonly Regex Script = new Regex(@"(?:m_Script|m_ManagedTypePPtr):\s*\{[^}]*guid:\s*([0-9A-Za-z]+)", RegexOptions.Compiled);
		private static readonly Regex Guid = new Regex(@"guid:\s*([0-9A-Za-z]+)", RegexOptions.Compiled);
		private static readonly Regex FileId = new Regex(@"fileID:\s*(-?[0-9]+)", RegexOptions.Compiled);

		public static ScanResult ScanFile(string path, HashSet<string> cesiumScriptGuids)
		{
			try
			{
				return Scan(File.ReadAllText(path), cesiumScriptGuids);
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				// Exception messages can include file contents supplied by an importer; report only the type.
				var result = new ScanResult();
				result.Findings.Add("cannot inspect serialized asset (" + exception.GetType().Name + ")");
				return result;
			}
		}

		public static ScanResult Scan(string yaml, HashSet<string> cesiumScriptGuids)
		{
			var result = new ScanResult();
			if (!yaml.StartsWith("%YAML", StringComparison.Ordinal) || yaml.Contains('\0'))
			{
				result.Findings.Add("cannot inspect non-YAML serialized asset; reserialize as text before review");
				return result;
			}

			var documents = Regex.Split(yaml, @"^--- ", RegexOptions.Multiline);
			foreach (var document in documents)
			{
				var script = Script.Match(document);
				var isCesium = (script.Success && cesiumScriptGuids.Contains(script.Groups[1].Value)) || document.Contains("CesiumForUnity::");
				string propertyPath = null;
				string target = null;
				foreach (var line in document.Split('\n'))
				{
					var match = Field.Match(line);
					if (!match.Success) continue;

					// Unity stores prefab/scene/variant overrides and preset properties as records, with
					// the secret in `value` instead of a direct field. Never carry a path into the next record.
					if (match.Groups[1].Success)
					{
						propertyPath = null;
						target = null;
					}

					var name = match.Groups[2].Value;
					var value = match.Groups[3].Value.Trim();
					if (name == "target") target = value;
					if (name == "propertyPath")
					{
						propertyPath = value.Trim('\'', '"');
						continue;
					}

					if (name == "value" && propertyPath != null)
					{
						name = propertyPath;
						var leaf = name.Substring(name.LastIndexOf('.') + 1);
						// Generic key overrides need their source component's type. An unresolved target
						// fails closed rather than silently accepting a possibly secret value.
						Inspect(result, leaf, value, isCesium || (CesiumKeyFields.Contains(leaf) && IsCesiumTarget(target) != false));
						propertyPath = null;
					}
					else
					{
						Inspect(result, name, value, isCesium);
					}
				}
			}

			return result;
		}

		private static void Inspect(ScanResult result, string name, string value, bool isCesium)
		{
			if (!SecretFields.Contains(name) && !(isCesium && CesiumKeyFields.Contains(name))) return;
			result.FieldsSeen++;
			if (value.Length > 0 && value != "''" && value != "\"\"")
			{
				result.Findings.Add($"{name} is set ({value.Length} characters)");
			}
		}

		private static bool? IsCesiumTarget(string reference)
		{
			if (reference == null) return null;
			var guid = Guid.Match(reference);
			var id = FileId.Match(reference);
			if (!guid.Success || !id.Success || !long.TryParse(id.Groups[1].Value, out var fileId)) return null;
			var path = AssetDatabase.GUIDToAssetPath(guid.Groups[1].Value);
			if (string.IsNullOrEmpty(path)) return null;
			var objects = AssetDatabase.LoadAllAssetsAtPath(path).AsEnumerable();
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
			if (prefab != null) objects = objects.Concat(prefab.GetComponentsInChildren<Component>(true));
			foreach (var item in objects)
			{
				if (item != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string _, out long localId) && localId == fileId)
				{
					return item.GetType().Namespace == "CesiumForUnity";
				}
			}

			return null;
		}

		public sealed class ScanResult
		{
			public List<string> Findings { get; } = new List<string>();

			public int FieldsSeen { get; set; }
		}
	}
}
