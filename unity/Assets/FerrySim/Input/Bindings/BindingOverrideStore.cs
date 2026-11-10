using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	[Serializable]
	public sealed class BindingOverride
	{
		public string action;
		public string scheme;
		public int slot = -1;
		public string path;
		public BindingOverride Copy() => new BindingOverride { action = action, scheme = scheme, slot = slot, path = path };
	}

	/// <summary>Versioned button overrides. Validation finishes before any live binding changes.</summary>
	public sealed class BindingOverrideStore
	{
		public const string FileName = "input-bindings-v1.json";
		public const int CurrentVersion = 1;
		[Serializable] private sealed class Document
		{
			public int version;
			public List<BindingOverride> overrides;
		}
		public static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, FileName);
		public string Path { get; }
		public string LastError { get; private set; } = "";
		public IReadOnlyList<string> Warnings => _warnings.AsReadOnly();
		private readonly List<string> _warnings = new List<string>();
		public BindingOverrideStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

		public bool Load(InputActionAsset defaults, out List<BindingOverride> overrides)
		{
			overrides = new List<BindingOverride>(); LastError = ""; _warnings.Clear();
			try
			{
				if (!File.Exists(Path)) return true;
				var document = JObject.Parse(File.ReadAllText(Path));
				if (document["version"]?.Type != JTokenType.Integer || document["version"].ToString() != "1" || !(document["overrides"] is JArray entries))
					throw new InvalidDataException("Unsupported or missing binding version or overrides (expected version 1).");
				var keys = new HashSet<string>(StringComparer.Ordinal);
				var validated = new List<BindingOverride>();
				foreach (var entry in entries)
				{
					if (!(entry is JObject row) || row["action"]?.Type != JTokenType.String || row["scheme"]?.Type != JTokenType.String ||
						row["path"]?.Type != JTokenType.String || row["slot"]?.Type != JTokenType.Integer ||
						!int.TryParse(row["slot"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot < 0)
						throw new InvalidDataException("Invalid binding override fields.");
					var item = new BindingOverride { action = row.Value<string>("action"), scheme = row.Value<string>("scheme"), slot = slot, path = row.Value<string>("path") };
					if (string.IsNullOrWhiteSpace(item.action) || string.IsNullOrWhiteSpace(item.scheme) || !ValidPath(item.path) ||
						!keys.Add(item.action + "\n" + item.scheme + "\n" + item.slot))
						throw new InvalidDataException("Invalid or duplicate binding override.");
					var action = defaults.FindAction(item.action);
					if (action == null || BindingSlots.Index(action, item.slot, item.scheme) < 0)
					{
						_warnings.Add("Dropped stale binding override: " + item.action + " (" + item.scheme + " slot " + item.slot + ").");
						continue;
					}
					validated.Add(item);
				}

				overrides = validated;
				return true;
			}
			catch (Exception error) when (error is InvalidDataException || error is JsonException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LastError = "Input bindings ignored: " + error.Message;
				_warnings.Clear();
				return false;
			}
		}

		public bool Save(IReadOnlyList<BindingOverride> overrides)
		{
			LastError = "";
			var temporary = Path + ".tmp";
			try
			{
				var directory = System.IO.Path.GetDirectoryName(Path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
				var copy = new List<BindingOverride>();
				foreach (var item in overrides) copy.Add(item.Copy());
				File.WriteAllText(temporary, JsonUtility.ToJson(new Document { version = CurrentVersion, overrides = copy }, true));
				if (File.Exists(Path)) File.Replace(temporary, Path, null);
				else File.Move(temporary, Path);
				return true;
			}
			catch (Exception error) when (error is InvalidDataException || error is JsonException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LastError = "Could not save input bindings: " + error.Message;
				return false;
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		internal static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith("<", StringComparison.Ordinal) &&
			path.IndexOf(">/", StringComparison.Ordinal) > 1 && path.IndexOf('*') < 0 && path.IndexOf('\n') < 0 && path.IndexOf('\r') < 0;
	}

	internal static class BindingSlots
	{
		public static bool Rebindable(InputAction action, InputBinding binding) => !binding.isComposite &&
			!(action.actionMap.name == "Helm" && (action.name == "PortLever" || action.name == "StarboardLever" || action.name == "BothLevers" || action.name == "Helm")) &&
			(action.expectedControlType == "Button" || binding.isPartOfComposite);
		public static bool InScheme(InputBinding binding, string scheme)
		{
			foreach (var group in (binding.groups ?? "").Split(';')) if (group == scheme) return true;
			return false;
		}
		public static int Index(InputAction action, int slot, string scheme)
		{
			if (slot < 0) return -1;
			var current = 0;
			for (var i = 0; i < action.bindings.Count; i++)
				if (Rebindable(action, action.bindings[i]) && InScheme(action.bindings[i], scheme) && current++ == slot) return i;
			return -1;
		}
	}
}
