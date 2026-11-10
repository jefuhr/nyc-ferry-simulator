using System;
using System.IO;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>The palette choice: follow the run's light, or hold day or night.</summary>
	public enum PaletteSetting
	{
		Auto,
		Day,
		Night,
	}

	/// <summary>How the chartplotter draws own ship's predicted track.</summary>
	public enum PredictorKind
	{
		Straight,
		Curved,
	}

	/// <summary>When the keybind popups show: never, while the hint key is held, or always.</summary>
	public enum KeybindHintMode
	{
		Off,
		Hold,
		Always,
	}

	/// <summary>The sections of settings-v1.json; each raises its own change event.</summary>
	public enum SettingsSection
	{
		Display,
		Hud,
		Console,
		Audio,
		Controls,
	}

	/// <summary>
	/// The player's settings, <c>settings-v1.json</c> in the persistent data folder (contracts C5), following the rules of
	/// <c>DeviceProfileStore</c>: a version number, an atomic write through a temporary file, and a load that is all or
	/// nothing. A file that fails to load (unreadable, not JSON, another version, any value out of range) is ignored
	/// whole, the defaults are used and the file is left as it is. A section or field missing from an otherwise good file
	/// takes its default, so a file written before a field was appended still loads.
	/// <para>
	/// Every setter clamps its value, ignores NaN, does nothing when the value is unchanged, and otherwise saves at once
	/// and then raises the event of its section (and <see cref="Changed"/>). A failed save keeps the change for the
	/// session and reports it in <see cref="LastError"/>. A store made with a null path (<see cref="InMemory"/>) never
	/// touches the disk: tests and batch runs use one.
	/// </para>
	/// </summary>
	public sealed class SettingsStore
	{
		public const int CurrentVersion = 1;
		public const string FileName = "settings-v1.json";

		public const double DefaultVectorMinutes = 2.0;
		public const double MinVectorMinutes = 0.5;
		public const double MaxVectorMinutes = 10.0;
		/// <summary>A sanity bound only: tests and hosts set thresholds far above any real one (100 m) to force the alarm.</summary>
		public const double MaxDepthAlarmMetres = 1000.0;
		public const float DefaultLookSensitivity = 0.15f;
		public const float MinLookSensitivity = 0.01f;
		public const float MaxLookSensitivity = 2f;
		public const int MaxLayoutNameLength = 64;

		private static readonly string[] PaletteNames = { "auto", "day", "night" };
		private static readonly string[] UnitNames = { "feet", "metres" };
		private static readonly string[] PredictorNames = { "straight", "curved" };
		private static readonly string[] HintNames = { "off", "hold", "always" };

		private Document _document = Document.Defaults();

		/// <param name="path">The file; null keeps the settings in memory only.</param>
		public SettingsStore(string path) => Path = path;

		/// <summary>A store that never reads or writes a file.</summary>
		public static SettingsStore InMemory() => new SettingsStore(null);

		public static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, FileName);

		/// <summary>The file, or null for a store kept in memory.</summary>
		public string Path { get; }

		public bool Persistent => Path != null;

		/// <summary>Why the last load or save failed; empty when it worked.</summary>
		public string LastError { get; private set; } = string.Empty;

		/// <summary>Raised after any section changes, with the section, after that section's own event.</summary>
		public event Action<SettingsSection> Changed;

		public event Action DisplayChanged;

		public event Action HudChanged;

		public event Action ConsoleChanged;

		public event Action AudioChanged;

		public event Action ControlsChanged;

		public PaletteSetting Palette
		{
			get => (PaletteSetting)Index(PaletteNames, _document.display.palette);
			set => SetText(ref _document.display.palette, PaletteNames[Clamp((int)value, PaletteNames.Length)], SettingsSection.Display);
		}

		public DistanceUnit DistanceUnit
		{
			get => (DistanceUnit)Index(UnitNames, _document.display.distanceUnit);
			set => SetText(ref _document.display.distanceUnit, UnitNames[Clamp((int)value, UnitNames.Length)], SettingsSection.Display);
		}

		/// <summary>Shallow-water alarm threshold, metres under the keel (0 … <see cref="MaxDepthAlarmMetres"/>).</summary>
		public double DepthAlarmMetres
		{
			get => _document.display.depthAlarmMetres;
			set => SetNumber(ref _document.display.depthAlarmMetres, value, 0.0, MaxDepthAlarmMetres, SettingsSection.Display);
		}

		/// <summary>Chartplotter own-ship vector length, minutes.</summary>
		public double VectorMinutes
		{
			get => _document.display.vectorMinutes;
			set => SetNumber(ref _document.display.vectorMinutes, value, MinVectorMinutes, MaxVectorMinutes, SettingsSection.Display);
		}

		public PredictorKind Predictor
		{
			get => (PredictorKind)Index(PredictorNames, _document.display.predictor);
			set => SetText(ref _document.display.predictor, PredictorNames[Clamp((int)value, PredictorNames.Length)], SettingsSection.Display);
		}

		/// <summary>The chosen HUD layout: a preset name or a saved layout's name. Blank or overlong names are ignored.</summary>
		public string HudLayout
		{
			get => _document.hud.layout;
			set
			{
				var name = value?.Trim();
				if (string.IsNullOrEmpty(name) || name.Length > MaxLayoutNameLength)
				{
					return;
				}

				SetText(ref _document.hud.layout, name, SettingsSection.Hud);
			}
		}

		public KeybindHintMode KeybindHints
		{
			get => (KeybindHintMode)Index(HintNames, _document.hud.keybindHints);
			set => SetText(ref _document.hud.keybindHints, HintNames[Clamp((int)value, HintNames.Length)], SettingsSection.Hud);
		}

		/// <summary>Whether the 3D console screens are drawn.</summary>
		public bool ConsoleScreens
		{
			get => _document.console.screens;
			set
			{
				if (_document.console.screens == value)
				{
					return;
				}

				_document.console.screens = value;
				Commit(SettingsSection.Console);
			}
		}

		/// <summary>Master volume, 0 … 1.</summary>
		public float MasterVolume
		{
			get => _document.audio.master;
			set => SetFloat(ref _document.audio.master, value, 0f, 1f, SettingsSection.Audio);
		}

		public bool Muted
		{
			get => _document.audio.muted;
			set
			{
				if (_document.audio.muted == value)
				{
					return;
				}

				_document.audio.muted = value;
				Commit(SettingsSection.Audio);
			}
		}

		/// <summary>Alarm volume, 0 … 1, under the master volume.</summary>
		public float AlarmVolume
		{
			get => _document.audio.alarms;
			set => SetFloat(ref _document.audio.alarms, value, 0f, 1f, SettingsSection.Audio);
		}

		/// <summary>Head-look sensitivity, degrees per mouse count.</summary>
		public float LookSensitivity
		{
			get => _document.controls.lookSensitivity;
			set => SetFloat(ref _document.controls.lookSensitivity, value, MinLookSensitivity, MaxLookSensitivity, SettingsSection.Controls);
		}

		/// <summary>
		/// Reads the file all or nothing and raises every section's event once, whatever happened. A missing file is not
		/// an error. False when the file was rejected; the defaults are then in force and the file is not rewritten.
		/// </summary>
		public bool Load()
		{
			LastError = string.Empty;
			var accepted = true;
			if (Path != null)
			{
				try
				{
					if (File.Exists(Path))
					{
						var loaded = Document.Defaults();
						loaded.version = 0;
						JsonUtility.FromJsonOverwrite(File.ReadAllText(Path), loaded);
						Validate(loaded);
						_document = loaded;
					}
					else
					{
						_document = Document.Defaults();
					}
				}
				catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
				{
					_document = Document.Defaults();
					LastError = "Settings ignored: " + error.Message;
					accepted = false;
				}
			}

			foreach (SettingsSection section in Enum.GetValues(typeof(SettingsSection)))
			{
				Raise(section);
			}

			return accepted;
		}

		/// <summary>Writes the whole file through a temporary file. Always true for a store kept in memory.</summary>
		public bool Save()
		{
			LastError = string.Empty;
			if (Path == null)
			{
				return true;
			}

			var temporary = Path + ".tmp";
			try
			{
				var directory = System.IO.Path.GetDirectoryName(Path);
				if (!string.IsNullOrEmpty(directory))
				{
					Directory.CreateDirectory(directory);
				}

				_document.version = CurrentVersion;
				File.WriteAllText(temporary, JsonUtility.ToJson(_document, true));
				if (File.Exists(Path))
				{
					File.Replace(temporary, Path, null);
				}
				else
				{
					File.Move(temporary, Path);
				}

				return true;
			}
			catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LastError = "Could not save settings: " + error.Message;
				Debug.LogWarning("[FerrySim] " + LastError);
				return false;
			}
			finally
			{
				try
				{
					if (File.Exists(temporary))
					{
						File.Delete(temporary);
					}
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}

		private static void Validate(Document document)
		{
			if (document.version != CurrentVersion)
			{
				throw new InvalidDataException($"unsupported or missing settings version {document.version} (expected {CurrentVersion}).");
			}

			if (document.display == null || document.hud == null || document.console == null || document.audio == null || document.controls == null)
			{
				throw new InvalidDataException("a section is not an object.");
			}

			Require(Index(PaletteNames, document.display.palette) >= 0, "display.palette");
			Require(Index(UnitNames, document.display.distanceUnit) >= 0, "display.distanceUnit");
			Require(Index(PredictorNames, document.display.predictor) >= 0, "display.predictor");
			Require(InRange(document.display.depthAlarmMetres, 0.0, MaxDepthAlarmMetres), "display.depthAlarmMetres");
			Require(InRange(document.display.vectorMinutes, MinVectorMinutes, MaxVectorMinutes), "display.vectorMinutes");
			Require(!string.IsNullOrWhiteSpace(document.hud.layout) && document.hud.layout.Trim().Length <= MaxLayoutNameLength, "hud.layout");
			Require(Index(HintNames, document.hud.keybindHints) >= 0, "hud.keybindHints");
			Require(InRange(document.audio.master, 0.0, 1.0), "audio.master");
			Require(InRange(document.audio.alarms, 0.0, 1.0), "audio.alarms");
			Require(InRange(document.controls.lookSensitivity, MinLookSensitivity, MaxLookSensitivity), "controls.lookSensitivity");
			document.hud.layout = document.hud.layout.Trim();
		}

		private static void Require(bool ok, string field)
		{
			if (!ok)
			{
				throw new InvalidDataException(field + " is missing or out of range.");
			}
		}

		private static bool InRange(double value, double min, double max) => !double.IsNaN(value) && value >= min && value <= max;

		private static int Index(string[] names, string value) => Array.IndexOf(names, value);

		private static int Clamp(int index, int count) => Math.Max(0, Math.Min(count - 1, index));

		private void SetText(ref string field, string value, SettingsSection section)
		{
			if (field == value)
			{
				return;
			}

			field = value;
			Commit(section);
		}

		private void SetNumber(ref double field, double value, double min, double max, SettingsSection section)
		{
			if (double.IsNaN(value))
			{
				return;
			}

			value = Math.Max(min, Math.Min(max, value));
			if (field.Equals(value))
			{
				return;
			}

			field = value;
			Commit(section);
		}

		private void SetFloat(ref float field, float value, float min, float max, SettingsSection section)
		{
			if (float.IsNaN(value))
			{
				return;
			}

			value = Mathf.Clamp(value, min, max);
			if (field.Equals(value))
			{
				return;
			}

			field = value;
			Commit(section);
		}

		private void Commit(SettingsSection section)
		{
			Save();
			Raise(section);
		}

		private void Raise(SettingsSection section)
		{
			switch (section)
			{
				case SettingsSection.Display:
					DisplayChanged?.Invoke();
					break;
				case SettingsSection.Hud:
					HudChanged?.Invoke();
					break;
				case SettingsSection.Console:
					ConsoleChanged?.Invoke();
					break;
				case SettingsSection.Audio:
					AudioChanged?.Invoke();
					break;
				case SettingsSection.Controls:
					ControlsChanged?.Invoke();
					break;
			}

			Changed?.Invoke(section);
		}

		// Field names are the file's keys (contracts C5); do not rename them.
		[Serializable]
		private sealed class Document
		{
			public int version = CurrentVersion;
			public DisplaySection display = new DisplaySection();
			public HudSection hud = new HudSection();
			public ConsoleSection console = new ConsoleSection();
			public AudioSection audio = new AudioSection();
			public ControlsSection controls = new ControlsSection();

			public static Document Defaults() => new Document();
		}

		[Serializable]
		private sealed class DisplaySection
		{
			public string palette = "auto";
			public string distanceUnit = "feet";
			public double depthAlarmMetres = DepthAlarm.DefaultThresholdMetres;
			public double vectorMinutes = DefaultVectorMinutes;
			public string predictor = "straight";
		}

		[Serializable]
		private sealed class HudSection
		{
			public string layout = HudPresets.FirstLaunch;
			public string keybindHints = "hold";
		}

		[Serializable]
		private sealed class ConsoleSection
		{
			public bool screens = true;
		}

		[Serializable]
		private sealed class AudioSection
		{
			public float master = 1f;
			public bool muted;
			public float alarms = 1f;
		}

		[Serializable]
		private sealed class ControlsSection
		{
			public float lookSensitivity = DefaultLookSensitivity;
		}
	}
}
