using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// Which HUD layout is in force, and edit mode. The choice lives in <see cref="SettingsStore.HudLayout"/> and the saved
	/// layouts in a <see cref="HudLayoutStore"/>; this resolves the name to a <see cref="HudLayout"/> (a preset, or a saved
	/// layout over its preset), follows both stores when they change, and runs an <see cref="HudEditSession"/> while the
	/// player arranges widgets. A chosen name that no longer exists falls back to <see cref="HudPresets.FirstLaunch"/> with a
	/// warning and leaves the setting alone. Changes made in edit mode stay for the session until saved under a name
	/// (<see cref="SaveAs"/>), which also chooses the saved layout. Nothing here pauses the run.
	/// </summary>
	public sealed class HudController : IDisposable
	{
		private readonly HudWidgetRegistry _registry;
		private readonly List<string> _choices = new List<string>();
		private HudLayout _current;
		private bool _modified;
		private bool _quiet;
		private string _chosen;
		private bool _disposed;

		public HudController(HudWidgetRegistry registry, SettingsStore settings, HudLayoutStore layouts)
		{
			_registry = registry ?? throw new ArgumentNullException(nameof(registry));
			Settings = settings ?? throw new ArgumentNullException(nameof(settings));
			Layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
			Settings.HudChanged += OnSettingsChanged;
			Layouts.Changed += OnLayoutsChanged;
			_current = ResolveChosen();
			RebuildChoices();
		}

		/// <summary>Raised after the layout in force changed: with a widget's id when only it moved, or null for the whole layout.</summary>
		public event Action<string> LayoutChanged;

		/// <summary>Raised with true when edit mode starts and false when it ends.</summary>
		public event Action<bool> EditModeChanged;

		/// <summary>Raised when the list of layouts the player can choose changes.</summary>
		public event Action ChoicesChanged;

		public HudWidgetRegistry Registry => _registry;

		public SettingsStore Settings { get; }

		public HudLayoutStore Layouts { get; }

		/// <summary>The layout in force; while editing, the session's working copy.</summary>
		public HudLayout Current => Session != null ? Session.Layout : _current;

		/// <summary>The presets, then the saved layouts.</summary>
		public IReadOnlyList<string> Choices => _choices;

		public bool Editing => Session != null;

		/// <summary>The edit session while <see cref="Editing"/>; null otherwise.</summary>
		public HudEditSession Session { get; private set; }

		/// <summary>True while the layout in force has changes that are not saved.</summary>
		public bool Modified => _modified || (Session != null && Session.Modified);

		/// <summary>Chooses a preset or a saved layout and applies it, discarding unsaved changes. Ends edit mode first.</summary>
		public void Choose(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return;
			}

			EndEdit();
			var before = Settings.HudLayout;
			Settings.HudLayout = name;
			if (Settings.HudLayout == before)
			{
				// The same name again: go back to what is saved.
				Apply(ResolveChosen());
			}
		}

		public bool IsSaved(string name) => Layouts.Contains(name);

		/// <summary>Starts edit mode on the layout in force.</summary>
		/// <param name="screen">The frame's size, reference pixels.</param>
		/// <param name="sizeOf">A widget's size at scale 1; null uses the registry's sizes.</param>
		public void BeginEdit(Vector2 screen, Func<string, Vector2> sizeOf)
		{
			if (_disposed || Editing)
			{
				return;
			}

			Session = new HudEditSession(_registry, _current, screen, sizeOf);
			Session.PlacementChanged += OnPlacementChanged;
			EditModeChanged?.Invoke(true);
		}

		/// <summary>Leaves edit mode, keeping its changes for the session (unsaved).</summary>
		public void EndEdit()
		{
			if (!Editing)
			{
				return;
			}

			var session = Session;
			session.CancelDrag();
			session.PlacementChanged -= OnPlacementChanged;
			_modified |= session.Modified;
			_current = session.Layout;
			Session = null;
			EditModeChanged?.Invoke(false);
		}

		/// <summary>
		/// Saves the layout in force under <paramref name="name"/> as its preset plus its differences, and chooses it. A blank
		/// name or a preset's name is refused; so is a failed write. <paramref name="problem"/> says why.
		/// </summary>
		public bool SaveAs(string name, out string problem)
		{
			problem = string.Empty;
			var trimmed = name?.Trim() ?? string.Empty;
			if (trimmed.Length == 0)
			{
				problem = "Give the layout a name.";
				return false;
			}

			if (HudPresets.IsPreset(trimmed))
			{
				problem = "'" + trimmed + "' is a preset; choose another name.";
				return false;
			}

			var entry = _registry.Differences(trimmed, Current);
			bool put;
			_quiet = true;
			try
			{
				put = Layouts.Put(entry);
			}
			finally
			{
				_quiet = false;
			}

			if (!put)
			{
				problem = Layouts.LastError;
				return false;
			}

			var saved = Current.Renamed(trimmed, entry.basedOn);
			if (Session != null)
			{
				Session.PlacementChanged -= OnPlacementChanged;
				Session = new HudEditSession(_registry, saved, Session.Screen, Session.SizeOf);
				Session.PlacementChanged += OnPlacementChanged;
			}

			_current = saved;
			_modified = false;
			_quiet = true;
			try
			{
				Settings.HudLayout = trimmed;
				_chosen = Settings.HudLayout;
			}
			finally
			{
				_quiet = false;
			}

			LayoutChanged?.Invoke(null);
			return true;
		}

		/// <summary>Deletes a saved layout; if it is in force, its preset takes over. False for a preset or a failed write.</summary>
		public bool DeleteSaved(string name)
		{
			if (HudPresets.IsPreset(name) || !Layouts.Contains(name))
			{
				return false;
			}

			var entry = Layouts.Find(name);
			var wasCurrent = string.Equals(Settings.HudLayout, entry.name, StringComparison.OrdinalIgnoreCase);
			if (!Layouts.Delete(name))
			{
				return false;
			}

			if (wasCurrent)
			{
				EndEdit();
				Settings.HudLayout = HudPresets.IsPreset(entry.basedOn) ? entry.basedOn : HudPresets.FirstLaunch;
			}

			return true;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			Settings.HudChanged -= OnSettingsChanged;
			Layouts.Changed -= OnLayoutsChanged;
			EndEdit();
		}

		private HudLayout ResolveChosen()
		{
			var name = Settings.HudLayout;
			_chosen = name;
			if (_registry.IsPreset(name))
			{
				return _registry.Preset(name.ToLowerInvariant());
			}

			var saved = Layouts.Find(name);
			if (saved != null)
			{
				return _registry.Resolve(saved);
			}

			Debug.LogWarning($"[FerrySim] HUD layout '{name}' is neither a preset nor a saved layout; using '{HudPresets.FirstLaunch}'.");
			return _registry.Preset(HudPresets.FirstLaunch);
		}

		private void Apply(HudLayout layout)
		{
			_current = layout;
			_modified = false;
			LayoutChanged?.Invoke(null);
		}

		private void OnSettingsChanged()
		{
			if (_disposed || _quiet)
			{
				return;
			}

			// The HUD section also holds the keybind popup mode: only a new layout name means a new layout.
			if (Settings.HudLayout == _chosen)
			{
				return;
			}

			EndEdit();
			Apply(ResolveChosen());
		}

		private void OnLayoutsChanged()
		{
			if (_disposed)
			{
				return;
			}

			RebuildChoices();
			// A saved layout in force follows its file, unless the player is changing it right now.
			if (!Editing && !_modified && !_quiet && !_registry.IsPreset(_current.Name) && Layouts.Contains(_current.Name))
			{
				Apply(ResolveChosen());
			}
		}

		private void OnPlacementChanged(string id) => LayoutChanged?.Invoke(id);

		private void RebuildChoices()
		{
			_choices.Clear();
			_choices.AddRange(_registry.PresetNames);
			foreach (var layout in Layouts.Layouts)
			{
				_choices.Add(layout.name);
			}

			ChoicesChanged?.Invoke();
		}
	}
}
