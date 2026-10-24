using System;
using System.IO;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>A scenario authored in the editor. JSON files with the same fields load through <see cref="FromJson"/>.</summary>
	[CreateAssetMenu(fileName = "Scenario", menuName = "FerrySim/Scenario")]
	public sealed class ScenarioAsset : ScriptableObject
	{
		[SerializeField]
		private ScenarioSettings settings = new ScenarioSettings();

		public ScenarioSettings Settings
		{
			get => settings;
			set => settings = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>Reads a scenario from JSON, for example <c>{"vesselFile": "vessels/k-class.json", "startHeadingDegrees": 45}</c>. Missing fields keep their defaults.</summary>
		public static ScenarioSettings FromJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
			{
				throw new ArgumentException("Scenario JSON is empty.", nameof(json));
			}

			var settings = new ScenarioSettings();
			JsonUtility.FromJsonOverwrite(json, settings);
			settings.Validate();
			return settings;
		}

		public static ScenarioSettings LoadJson(string path)
		{
			var resolved = SimPaths.Resolve(path);
			if (!File.Exists(resolved))
			{
				throw new FileNotFoundException($"Scenario file not found: {path} (looked at {resolved})", resolved);
			}

			return FromJson(File.ReadAllText(resolved));
		}

		public static string ToJson(ScenarioSettings settings) => JsonUtility.ToJson(settings, true);
	}
}
