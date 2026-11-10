using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>One widget's entry in a saved layout (contracts C5). Field names are the file's keys.</summary>
	[Serializable]
	public sealed class HudWidgetEntry
	{
		public string id;
		public bool visible = true;
		public string anchor;
		public float x;
		public float y;
		public float scale = 1f;

		public HudWidgetEntry Copy() => new HudWidgetEntry { id = id, visible = visible, anchor = anchor, x = x, y = y, scale = scale };
	}

	/// <summary>A saved layout: its name, the preset it is based on and its differences from that preset.</summary>
	[Serializable]
	public sealed class HudLayoutEntry
	{
		public string name;
		public string basedOn;
		public List<HudWidgetEntry> widgets = new List<HudWidgetEntry>();

		public HudLayoutEntry Copy()
		{
			var copy = new HudLayoutEntry { name = name, basedOn = basedOn };
			if (widgets != null)
			{
				foreach (var widget in widgets)
				{
					copy.widgets.Add(widget?.Copy());
				}
			}

			return copy;
		}
	}

	/// <summary>
	/// The player's saved HUD layouts, <c>hud-layouts-v1.json</c> in the persistent data folder (contracts C5), with the
	/// rules of <c>DeviceProfileStore</c>: versioned, written atomically through a temporary file, loaded all or nothing
	/// (a rejected file is ignored and left as it is, and no layouts are loaded). Changes are persisted before they are
	/// published: a save that fails changes nothing and returns false with <see cref="LastError"/>. A layout may not
	/// take a preset's name, in any case. Widget ids are not checked here; the registry drops unknown ones when it
	/// resolves the layout, so a file from a newer build loads.
	/// </summary>
	public sealed class HudLayoutStore
	{
		public const int CurrentVersion = 1;
		public const string FileName = "hud-layouts-v1.json";
		public const float MaxOffset = 4000f;
		public const float MaxScale = 10f;

		private readonly List<HudLayoutEntry> _layouts = new List<HudLayoutEntry>();

		/// <param name="path">The file; null keeps the layouts in memory only.</param>
		public HudLayoutStore(string path) => Path = path;

		public static HudLayoutStore InMemory() => new HudLayoutStore(null);

		public static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, FileName);

		public string Path { get; }

		public bool Persistent => Path != null;

		public string LastError { get; private set; } = string.Empty;

		/// <summary>Raised after a load (whatever its outcome), a put and a delete.</summary>
		public event Action Changed;

		/// <summary>The saved layouts in the order they were first saved. Read only; use <see cref="Find"/> for a copy to change.</summary>
		public IReadOnlyList<HudLayoutEntry> Layouts => _layouts;

		public bool Load()
		{
			_layouts.Clear();
			LastError = string.Empty;
			var accepted = true;
			try
			{
				if (Path != null && File.Exists(Path))
				{
					var document = new Document { version = 0 };
					JsonUtility.FromJsonOverwrite(File.ReadAllText(Path), document);
					if (document.version != CurrentVersion || document.layouts == null)
					{
						throw new InvalidDataException($"unsupported or missing version {document.version} (expected {CurrentVersion}).");
					}

					var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (var layout in document.layouts)
					{
						var problem = Problem(layout);
						if (problem != null)
						{
							throw new InvalidDataException(problem);
						}

						if (!names.Add(layout.name.Trim()))
						{
							throw new InvalidDataException("two layouts are named " + layout.name + ".");
						}
					}

					foreach (var layout in document.layouts)
					{
						var copy = layout.Copy();
						copy.name = copy.name.Trim();
						copy.basedOn = copy.basedOn.ToLowerInvariant();
						_layouts.Add(copy);
					}
				}
			}
			catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				_layouts.Clear();
				LastError = "HUD layouts ignored: " + error.Message;
				accepted = false;
			}

			Changed?.Invoke();
			return accepted;
		}

		/// <summary>A copy of the saved layout with this name (any case), or null.</summary>
		public HudLayoutEntry Find(string name)
		{
			var index = IndexOf(name);
			return index >= 0 ? _layouts[index].Copy() : null;
		}

		public bool Contains(string name) => IndexOf(name) >= 0;

		/// <summary>Saves a layout, replacing one of the same name. False, with nothing changed, if it is invalid or the write fails.</summary>
		public bool Put(HudLayoutEntry layout)
		{
			LastError = string.Empty;
			var problem = Problem(layout);
			if (problem != null)
			{
				LastError = "Layout not saved: " + problem;
				return false;
			}

			var copy = layout.Copy();
			copy.name = copy.name.Trim();
			copy.basedOn = copy.basedOn.ToLowerInvariant();
			var previous = new List<HudLayoutEntry>(_layouts);
			var index = IndexOf(copy.name);
			if (index >= 0)
			{
				_layouts[index] = copy;
			}
			else
			{
				_layouts.Add(copy);
			}

			return Persist(previous);
		}

		/// <summary>Deletes a saved layout. False if there is none of that name or the write fails (then nothing changed).</summary>
		public bool Delete(string name)
		{
			LastError = string.Empty;
			var index = IndexOf(name);
			if (index < 0)
			{
				return false;
			}

			var previous = new List<HudLayoutEntry>(_layouts);
			_layouts.RemoveAt(index);
			return Persist(previous);
		}

		private bool Persist(List<HudLayoutEntry> previous)
		{
			if (!Save())
			{
				_layouts.Clear();
				_layouts.AddRange(previous);
				return false;
			}

			Changed?.Invoke();
			return true;
		}

		private bool Save()
		{
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

				File.WriteAllText(temporary, JsonUtility.ToJson(new Document { version = CurrentVersion, layouts = _layouts }, true));
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
				LastError = "Could not save HUD layouts: " + error.Message;
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

		private int IndexOf(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return -1;
			}

			var trimmed = name.Trim();
			for (var i = 0; i < _layouts.Count; i++)
			{
				if (string.Equals(_layouts[i].name, trimmed, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Why a layout cannot be stored, or null.</summary>
		public static string Problem(HudLayoutEntry layout)
		{
			if (layout == null)
			{
				return "a layout is empty.";
			}

			if (string.IsNullOrWhiteSpace(layout.name) || layout.name.Trim().Length > SettingsStore.MaxLayoutNameLength)
			{
				return "a layout needs a name of 1 to " + SettingsStore.MaxLayoutNameLength + " characters.";
			}

			if (HudPresets.IsPreset(layout.name.Trim()))
			{
				return "'" + layout.name.Trim() + "' is a preset's name.";
			}

			if (!HudPresets.IsPreset(layout.basedOn))
			{
				return "layout '" + layout.name + "' is based on '" + layout.basedOn + "', which is not a preset.";
			}

			if (layout.widgets == null)
			{
				return "layout '" + layout.name + "' has no widget list.";
			}

			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var widget in layout.widgets)
			{
				if (widget == null || string.IsNullOrWhiteSpace(widget.id))
				{
					return "layout '" + layout.name + "' has a widget without an id.";
				}

				if (!ids.Add(widget.id))
				{
					return "layout '" + layout.name + "' lists widget '" + widget.id + "' twice.";
				}

				if (!HudAnchors.TryParse(widget.anchor, out _))
				{
					return "widget '" + widget.id + "' has an unknown anchor '" + widget.anchor + "'.";
				}

				if (!InRange(widget.x, -MaxOffset, MaxOffset) || !InRange(widget.y, -MaxOffset, MaxOffset) || !InRange(widget.scale, 0.01f, MaxScale))
				{
					return "widget '" + widget.id + "' has an offset or scale out of range.";
				}
			}

			return null;
		}

		private static bool InRange(float value, float min, float max) => !float.IsNaN(value) && value >= min && value <= max;

		[Serializable]
		private sealed class Document
		{
			public int version;
			public List<HudLayoutEntry> layouts;
		}
	}
}
