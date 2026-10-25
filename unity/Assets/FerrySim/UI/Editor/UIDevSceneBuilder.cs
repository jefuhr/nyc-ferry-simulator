using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FerrySim.UI.Editor
{
	/// <summary>
	/// Builds UI/Scenes/UIDev.unity: a camera, a <see cref="FakeTelemetrySource"/> playing the committed
	/// sample, a <see cref="FakeEnvironmentSource"/> (synthetic water and air), a <see cref="FakeScenarioHost"/>
	/// (a fake catalog and launcher for the scenario picker), and a <see cref="WheelhouseUI"/> with the
	/// development toolbar. The saved scene is rewritten with object ids derived from object names and types
	/// (Unity picks random ones), so rebuilding gives the same bytes and the scene diffs cleanly.
	/// <code>
	/// Unity -batchmode -nographics -quit -projectPath unity -executeMethod FerrySim.UI.Editor.UIDevSceneBuilder.Build
	/// </code>
	/// </summary>
	public static class UIDevSceneBuilder
	{
		public const string ScenePath = "Assets/FerrySim/UI/Scenes/UIDev.unity";

		private static readonly Regex BlockHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)(.*)$", RegexOptions.Multiline);
		private static readonly Regex FileIdReference = new Regex(@"fileID: (-?\d+)");
		private static readonly Regex TrailingSpace = new Regex(@"[ \t]+$", RegexOptions.Multiline);

		[MenuItem("FerrySim/UI/Rebuild UIDev Scene")]
		public static void Build()
		{
			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			var cameraObject = new GameObject("Camera");
			cameraObject.tag = "MainCamera";
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = new Color(0.36f, 0.45f, 0.54f, 1f);

			var telemetryObject = new GameObject("Telemetry (fake CSV playback)");
			var source = telemetryObject.AddComponent<FakeTelemetrySource>();

			var environmentObject = new GameObject("Environment (fake, synthetic water and air)");
			var environment = environmentObject.AddComponent<FakeEnvironmentSource>();
			Set(environment, "telemetry", source);

			var scenarioObject = new GameObject("Scenarios (fake catalog and launcher)");
			var scenarios = scenarioObject.AddComponent<FakeScenarioHost>();
			Set(scenarios, "telemetry", source);
			Set(scenarios, "environment", environment);

			var uiObject = new GameObject("Wheelhouse UI");
			var ui = uiObject.AddComponent<WheelhouseUI>();
			var serialized = new SerializedObject(ui);
			serialized.FindProperty("telemetrySource").objectReferenceValue = source;
			serialized.FindProperty("environmentSource").objectReferenceValue = environment;
			serialized.FindProperty("scenarioCatalog").objectReferenceValue = scenarios;
			serialized.FindProperty("scenarioLauncher").objectReferenceValue = scenarios;
			serialized.FindProperty("showDevToolbar").boolValue = true;
			serialized.ApplyModifiedPropertiesWithoutUndo();

			if (!EditorSceneManager.SaveScene(scene, ScenePath))
			{
				throw new IOException("Could not save " + ScenePath);
			}

			var path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", ScenePath);
			File.WriteAllText(path, Canonicalize(File.ReadAllText(path)), new UTF8Encoding(false));
			AssetDatabase.ImportAsset(ScenePath, ImportAssetOptions.ForceUpdate);
			EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
			Debug.Log("UIDevSceneBuilder: wrote " + ScenePath);
		}

		private static void Set(UnityEngine.Object component, string property, UnityEngine.Object value)
		{
			var serialized = new SerializedObject(component);
			serialized.FindProperty(property).objectReferenceValue = value;
			serialized.ApplyModifiedPropertiesWithoutUndo();
		}

		/// <summary>
		/// Replaces every object id defined in the scene with one hashed from the object's class, its
		/// GameObject's name and (for scripts) the script reference, then sorts the objects by id. Small
		/// fixed ids (the scene settings objects) are kept. Trailing spaces (Unity writes "m_Name: " for an
		/// empty name) are dropped: YAML ignores them, and the scene then passes git diff --check.
		/// </summary>
		internal static string Canonicalize(string yaml)
		{
			yaml = TrailingSpace.Replace(yaml, string.Empty);
			var headers = BlockHeader.Matches(yaml).Cast<Match>().ToList();
			if (headers.Count == 0)
			{
				return yaml;
			}

			var preamble = yaml.Substring(0, headers[0].Index);
			var blocks = new List<(string classId, string id, string text)>();
			for (var i = 0; i < headers.Count; i++)
			{
				var end = i + 1 < headers.Count ? headers[i + 1].Index : yaml.Length;
				blocks.Add((headers[i].Groups[1].Value, headers[i].Groups[2].Value, yaml.Substring(headers[i].Index, end - headers[i].Index)));
			}

			var gameObjectNames = new Dictionary<string, string>();
			foreach (var block in blocks.Where(b => b.classId == "1"))
			{
				gameObjectNames[block.id] = Field(block.text, "m_Name") ?? string.Empty;
			}

			var map = new Dictionary<string, string>();
			var used = new HashSet<string>();
			foreach (var block in blocks)
			{
				if (long.TryParse(block.id, out var existing) && existing > 0 && existing < 1000)
				{
					map[block.id] = block.id;
					used.Add(block.id);
					continue;
				}

				var owner = block.classId == "1" ? block.id : Reference(block.text, "m_GameObject");
				var ownerName = owner != null && gameObjectNames.TryGetValue(owner, out var name) ? name : string.Empty;
				var key = block.classId + "|" + ownerName + "|" + (Field(block.text, "m_Script") ?? string.Empty);
				var id = StableId(key);
				while (!used.Add(id))
				{
					id = StableId(key + "+");
					key += "+";
				}

				map[block.id] = id;
			}

			var rewritten = blocks
				.Select(block =>
				{
					var text = FileIdReference.Replace(block.text, match =>
						map.TryGetValue(match.Groups[1].Value, out var replacement) ? "fileID: " + replacement : match.Value);
					text = BlockHeader.Replace(text, match =>
						$"--- !u!{match.Groups[1].Value} &{map[match.Groups[2].Value]}{match.Groups[3].Value}", 1);
					return (id: long.Parse(map[block.id]), text);
				})
				.OrderBy(block => block.id)
				.Select(block => block.text);
			return preamble + string.Concat(rewritten);
		}

		private static string Field(string block, string field)
		{
			var match = Regex.Match(block, "^\\s*" + Regex.Escape(field) + ": (.*)$", RegexOptions.Multiline);
			return match.Success ? match.Groups[1].Value.Trim() : null;
		}

		private static string Reference(string block, string field)
		{
			var value = Field(block, field);
			if (value == null)
			{
				return null;
			}

			var match = FileIdReference.Match(value);
			return match.Success ? match.Groups[1].Value : null;
		}

		/// <summary>FNV-1a over the key, folded into a positive id well away from the small fixed ones.</summary>
		private static string StableId(string key)
		{
			unchecked
			{
				var hash = 14695981039346656037UL;
				foreach (var c in Encoding.UTF8.GetBytes(key))
				{
					hash = (hash ^ c) * 1099511628211UL;
				}

				return (1000000000L + (long)(hash % 8000000000000000000UL)).ToString(System.Globalization.CultureInfo.InvariantCulture);
			}
		}
	}
}
