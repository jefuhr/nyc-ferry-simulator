using System;
using FerrySim.Controls;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The whole UI layer: the player's HUD (the v0.4 instrument panel is its <c>classic</c> preset), the external-view
	/// overlay, the scenario picker, the debrief screen, the options menu and an optional development toolbar, plus the
	/// settings they share. Settings live in a <see cref="SettingsStore"/> and saved HUD layouts in a
	/// <see cref="HudLayoutStore"/>; the parameterless constructor keeps both in memory (tests), and
	/// <see cref="WheelhouseUI"/> passes the player's files. It is a plain VisualElement so it can be hosted by
	/// <see cref="WheelhouseUI"/> or built directly in tests.
	/// <para>
	/// Nothing here pauses the run or touches audio or the console: the options menu and the scenario picker announce
	/// themselves (<see cref="OptionsVisibilityChanged"/>, <see cref="PickerVisibilityChanged"/>), HUD edit mode does too
	/// (<see cref="HudEditModeChanged"/>, and it does not pause), and the audio, console and plotter settings are read from
	/// <see cref="Settings"/> and its section events by the composition code.
	/// </para>
	/// </summary>
	public sealed class WheelhouseView : VisualElement, IDisposable
	{
		public const string StyleSheetResource = "FerrySimUI/Wheelhouse";
		public const string HudStyleSheetResource = "FerrySimUI/Hud";
		public const string OptionsStyleSheetResource = "FerrySimUI/Options";

		private PaletteKind _palette = PaletteKind.Day;
		private PaletteKind? _suggestedPalette;
		private DistanceUnit _distanceUnit;
		private double _depthAlarmThreshold;
		private bool _panelVisible;
		private PaletteSetting _paletteSetting;
		private IScenarioCatalog _catalog;
		private IScenarioLauncher _launcher;
		private bool _disposed;

		/// <summary>A view whose settings and layouts are kept in memory only.</summary>
		public WheelhouseView() : this(SettingsStore.InMemory(), HudLayoutStore.InMemory())
		{
		}

		/// <param name="settings">Loaded already; the view follows and writes it.</param>
		/// <param name="layouts">Loaded already.</param>
		public WheelhouseView(SettingsStore settings, HudLayoutStore layouts)
		{
			Settings = settings ?? SettingsStore.InMemory();
			Layouts = layouts ?? HudLayoutStore.InMemory();
			name = "wheelhouse-view";
			AddToClassList("fs-view");
			pickingMode = PickingMode.Ignore;
			AddStyleSheet(StyleSheetResource);
			AddStyleSheet(HudStyleSheetResource);
			AddStyleSheet(OptionsStyleSheetResource);

			Panel = new WheelhousePanel();
			Add(Panel);
			HudLayout = new HudController(HudWidgetRegistry.Default, Settings, Layouts);
			HudLayer = new HudLayer(HudLayout, Panel);
			HudLayer.ClassicPanelShownChanged += OnClassicPanelShownChanged;
			HudLayer.EditBar.TextEntryChanged += OnTextEntryChanged;
			HudLayout.EditModeChanged += OnHudEditModeChanged;
			Add(HudLayer);
			Hud = new HudOverlay();
			Add(Hud);
			Picker = new ScenarioPicker();
			Picker.CloseRequested += ClosePicker;
			Picker.Started += OnScenarioStarted;
			Add(Picker);
			Debrief = new DebriefScreen();
			Debrief.CloseRequested += CloseDebrief;
			Add(Debrief);
			Toolbar = new DevToolbar();
			Add(Toolbar);
			Options = new OptionsMenu();
			Options.CloseRequested += CloseOptions;
			Options.EditHudRequested += EditHud;
			Options.Bind(null, Settings, HudLayout);
			Add(Options);

			_panelVisible = true;
			UpdatePanelShown();
			HudVisible = false;
			Picker.style.display = DisplayStyle.None;
			Options.style.display = DisplayStyle.None;
			DebriefVisible = false;
			ToolbarVisible = false;
			Settings.DisplayChanged += OnDisplayChanged;
			_distanceUnit = Settings.DistanceUnit;
			_depthAlarmThreshold = Settings.DepthAlarmMetres;
			Debrief.DistanceUnit = _distanceUnit;
			Debrief.DepthAlarmThresholdMetres = _depthAlarmThreshold;
			_paletteSetting = Settings.Palette;
			_palette = _paletteSetting == PaletteSetting.Night ? PaletteKind.Night : PaletteKind.Day;
			PaletteRoles.Apply(this, InstrumentPalette.For(_palette));
		}

		/// <summary>The player's settings (settings-v1.json). Audio, console, plotter and look settings are for the composition code to read and follow.</summary>
		public SettingsStore Settings { get; }

		/// <summary>The player's saved HUD layouts (hud-layouts-v1.json).</summary>
		public HudLayoutStore Layouts { get; }

		/// <summary>The v0.4 panel: the <c>classic-panel</c> widget, used as it is.</summary>
		public WheelhousePanel Panel { get; }

		/// <summary>Which HUD layout is in force, and edit mode.</summary>
		public HudController HudLayout { get; }

		/// <summary>The player's HUD widgets over the 3D view.</summary>
		public HudLayer HudLayer { get; }

		public HudOverlay Hud { get; }

		/// <summary>The scenario picker; <see cref="PickerVisible"/> shows it bound to <see cref="ConnectScenario"/>'s catalog and launcher.</summary>
		public ScenarioPicker Picker { get; }

		public DebriefScreen Debrief { get; }

		public DevToolbar Toolbar { get; }

		/// <summary>The options menu; <see cref="OptionsVisible"/> shows it.</summary>
		public OptionsMenu Options { get; }

		/// <summary>The water and the air for the panel and the HUD. Null, or a destroyed scene component, reads "---".</summary>
		public IEnvironmentReadoutSource EnvironmentSource { get; set; }

		/// <summary>The pictures the HUD's pop-out screens show; null shows NO SIGNAL.</summary>
		public IHudScreenSource ScreenSource
		{
			get => HudLayer.ScreenSource;
			set => HudLayer.ScreenSource = value;
		}

		/// <summary>Raised after any display setting changes (palette, units, alarm threshold, vector, predictor), so hosts can mirror it.</summary>
		public event Action SettingsChanged;

		/// <summary>
		/// Raised with true when the picker opens and false when it closes (Close, Escape, or a run started). The picker
		/// only uses keys the helm does not (Input/README.md), but a host may still pause or hold the helm while it is open.
		/// </summary>
		public event Action<bool> PickerVisibilityChanged;

		/// <summary>
		/// Raised with true when the options menu opens and false when it closes. The menu pauses nothing itself: the host
		/// pauses the run and holds the helm while it is open.
		/// </summary>
		public event Action<bool> OptionsVisibilityChanged;

		/// <summary>Raised with true when HUD edit mode starts and false when it ends. Edit mode does not pause the run.</summary>
		public event Action<bool> HudEditModeChanged;

		/// <summary>
		/// True while a text field of the view has the keyboard focus (the HUD layout name), false after: letters typed
		/// there must not reach the helm, so a host holds the helm keys meanwhile.
		/// </summary>
		public event Action<bool> TextEntryChanged;

		/// <summary>The palette shown now. Setting it holds until the setting or the run's light next changes.</summary>
		public PaletteKind Palette
		{
			get => _palette;
			set
			{
				if (_disposed)
				{
					return;
				}

				_palette = value;
				PaletteRoles.Apply(this, InstrumentPalette.For(value));
				SettingsChanged?.Invoke();
			}
		}

		/// <summary>The saved palette choice: auto follows the run's light (<see cref="SuggestPalette"/>), day and night hold.</summary>
		public PaletteSetting PaletteSetting
		{
			get => Settings.Palette;
			set => Settings.Palette = value;
		}

		public DistanceUnit DistanceUnit
		{
			get => _distanceUnit;
			set => Settings.DistanceUnit = value;
		}

		/// <summary>Shallow-water alarm threshold, metres under the keel.</summary>
		public double DepthAlarmThresholdMetres
		{
			get => _depthAlarmThreshold;
			set
			{
				if (double.IsNaN(value) || double.IsInfinity(value))
				{
					return;
				}

				Settings.DepthAlarmMetres = Math.Max(0.0, value);
			}
		}

		/// <summary>
		/// Whether the wheelhouse HUD is shown at all (the harbor hides it in external views, where <see cref="HudVisible"/>
		/// shows the overlay instead). Within it, the layout in force decides which widgets show, the classic panel included.
		/// </summary>
		public bool PanelVisible
		{
			get => _panelVisible;
			set
			{
				_panelVisible = value;
				UpdatePanelShown();
			}
		}

		/// <summary>True while the classic panel is actually on screen: the HUD shown and its layout showing the panel.</summary>
		public bool ClassicPanelShown => _panelVisible && HudLayer.ClassicPanelShown;

		public bool HudVisible
		{
			get => Hud.style.display != DisplayStyle.None;
			set => Hud.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
		}

		public bool DebriefVisible
		{
			get => Debrief.style.display != DisplayStyle.None;
			set
			{
				if (value)
				{
					PickerVisible = false;
				}

				Debrief.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
				// The toolbar would cover the debrief's own buttons; it comes back when the debrief closes.
				Toolbar.style.visibility = value ? Visibility.Hidden : Visibility.Visible;
				if (value)
				{
					Debrief.Focus();
				}
			}
		}

		/// <summary>
		/// Shows the scenario picker over the 3D view, reading the catalog again and starting from the session's last
		/// choice, with the keyboard focus on its first field; or hides it.
		/// </summary>
		public bool PickerVisible
		{
			get => Picker.style.display != DisplayStyle.None;
			set
			{
				if (_disposed || value == PickerVisible)
				{
					return;
				}

				if (value)
				{
					DebriefVisible = false;
					Picker.Bind(_catalog, _launcher);
				}

				Picker.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
				if (value)
				{
					Picker.FocusFirstField();
				}
				else
				{
					BlurInside(Picker);
				}

				PickerVisibilityChanged?.Invoke(value);
			}
		}

		/// <summary>
		/// Shows the options menu over everything else with the focus on its first tab, or hides it (Close, Escape, the
		/// gamepad's east button or <c>Interface/Options</c> again). Opening it ends HUD edit mode, keeping its changes.
		/// </summary>
		public bool OptionsVisible
		{
			get => Options.style.display != DisplayStyle.None;
			set
			{
				if (_disposed || value == OptionsVisible)
				{
					return;
				}

				if (value)
				{
					HudEditing = false;
				}
				else
				{
					Options.CancelRebind();
				}

				Options.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
				if (value)
				{
					Options.BringToFront();
					Options.FocusFirst();
				}
				else
				{
					BlurInside(Options);
				}

				OptionsVisibilityChanged?.Invoke(value);
			}
		}

		public void ToggleOptions() => OptionsVisible = !OptionsVisible;

		/// <summary>HUD edit mode (<c>Interface/HudEdit</c>): drag, scale, show and hide widgets. It does not pause the run.</summary>
		public bool HudEditing
		{
			get => HudLayout.Editing;
			set
			{
				if (_disposed || value == HudLayout.Editing)
				{
					return;
				}

				if (value)
				{
					HudLayout.BeginEdit(HudLayer.FrameSize, HudLayer.SizeOf);
				}
				else
				{
					BlurInside(HudLayer);
					HudLayout.EndEdit();
				}
			}
		}

		public void ToggleHudEdit() => HudEditing = !HudEditing;

		/// <summary>Connects the options menu's Controls page to the binding service (null: the page says keys cannot be changed).</summary>
		public void ConnectBindings(IBindingService bindings)
		{
			if (!_disposed)
			{
				Options.Bind(bindings, Settings, HudLayout);
			}
		}

		/// <summary>
		/// Permanently disconnects this view from the session's stores and services. Call before discarding a directly
		/// hosted view; WheelhouseUI does so on disable and destroy. Repeated calls are harmless; use a new view to reconnect.
		/// The stores belong to the session and are neither disposed nor changed here. No view events are raised.
		/// </summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			SettingsChanged = null;
			PickerVisibilityChanged = null;
			OptionsVisibilityChanged = null;
			HudEditModeChanged = null;
			TextEntryChanged = null;
			Settings.DisplayChanged -= OnDisplayChanged;
			HudLayer.ClassicPanelShownChanged -= OnClassicPanelShownChanged;
			HudLayer.EditBar.TextEntryChanged -= OnTextEntryChanged;
			HudLayout.EditModeChanged -= OnHudEditModeChanged;
			Picker.CloseRequested -= ClosePicker;
			Picker.Started -= OnScenarioStarted;
			Debrief.CloseRequested -= CloseDebrief;
			Options.CloseRequested -= CloseOptions;
			Options.EditHudRequested -= EditHud;
			Options.Bind(null, null, null);
			HudLayer.Dispose();
			HudLayout.Dispose();
			Picker.Bind(null, null);
			_catalog = null;
			_launcher = null;
			EnvironmentSource = null;
			ScreenSource = null;
		}

		/// <summary>The catalog and launcher the picker uses; either may be null. Rebinds an open picker.</summary>
		public void ConnectScenario(IScenarioCatalog catalog, IScenarioLauncher launcher)
		{
			if (_disposed)
			{
				return;
			}

			_catalog = catalog;
			_launcher = launcher;
			Picker.Bind(catalog, launcher);
		}

		public void TogglePicker() => PickerVisible = !PickerVisible;

		public bool ToolbarVisible
		{
			get => Toolbar.style.display != DisplayStyle.None;
			set => Toolbar.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
		}

		public void TogglePalette() => Palette = Palette == PaletteKind.Day ? PaletteKind.Night : PaletteKind.Day;

		/// <summary>
		/// The palette the run's time of day suggests (the sun-altitude rule in World/Sky/Daylight.cs). With the palette
		/// setting on auto it is applied only when the suggestion changes, at dusk, at dawn or at a run in another light, so a
		/// palette chosen by hand in between holds until then. With the setting on day or night it is remembered but not applied.
		/// </summary>
		public void SuggestPalette(PaletteKind palette)
		{
			if (_suggestedPalette == palette)
			{
				return;
			}

			_suggestedPalette = palette;
			if (Settings.Palette == PaletteSetting.Auto && Palette != palette)
			{
				Palette = palette;
			}
		}

		public void ToggleDistanceUnit() => DistanceUnit = DistanceUnit == DistanceUnit.Feet ? DistanceUnit.Metres : DistanceUnit.Feet;

		/// <summary>Moves the alarm threshold one display step (1 ft or 0.5 m) up or down, snapping to that step.</summary>
		public void StepDepthAlarmThreshold(int steps)
		{
			var step = DistanceUnit == DistanceUnit.Feet ? 1.0 : 0.5;
			var current = DisplayUnits.Distance(DepthAlarmThresholdMetres, DistanceUnit);
			var next = Math.Max(0.0, Math.Round(current / step) * step + steps * step);
			DepthAlarmThresholdMetres = DisplayUnits.DistanceToMetres(next, DistanceUnit);
		}

		public void AcknowledgeAlarms() => Panel.AcknowledgeAlarm();

		/// <summary>Updates every visible instrument from <paramref name="source"/> (may be null) and <see cref="EnvironmentSource"/>.</summary>
		/// <param name="realTime">Unscaled real time in seconds; only alarm flashing uses it.</param>
		public void Refresh(IVesselTelemetrySource source, double realTime)
		{
			// A scene source that has been destroyed would keep answering with its last values: treat it as none.
			var environment = EnvironmentSource is UnityEngine.Object component && component == null ? null : EnvironmentSource;
			// The panel keeps running while hidden so its alarm state is current when it comes back; the HUD widgets and the
			// overlay show what it worked out.
			Panel.Refresh(source, environment, _distanceUnit, _depthAlarmThreshold, realTime);
			if (_panelVisible)
			{
				HudLayer.Refresh(source, _distanceUnit, _depthAlarmThreshold, realTime);
			}

			if (HudVisible)
			{
				Hud.Refresh(source, Panel.EnvironmentDisplay);
			}
		}

		private void OnDisplayChanged()
		{
			// A publisher may already have copied its invocation list when another handler disposes this view.
			if (_disposed)
			{
				return;
			}

			_distanceUnit = Settings.DistanceUnit;
			_depthAlarmThreshold = Settings.DepthAlarmMetres;
			Debrief.DistanceUnit = _distanceUnit;
			Debrief.DepthAlarmThresholdMetres = _depthAlarmThreshold;
			// Only a new palette choice changes the palette here: a unit change must not undo one picked by hand.
			if (Settings.Palette != _paletteSetting)
			{
				_paletteSetting = Settings.Palette;
				var wanted = _paletteSetting switch
				{
					PaletteSetting.Day => PaletteKind.Day,
					PaletteSetting.Night => PaletteKind.Night,
					_ => _suggestedPalette ?? _palette,
				};
				if (wanted != _palette)
				{
					Palette = wanted;
					return;
				}
			}

			SettingsChanged?.Invoke();
		}

		private void OnClassicPanelShownChanged(bool shown) => UpdatePanelShown();

		private void OnTextEntryChanged(bool typing)
		{
			if (!_disposed) TextEntryChanged?.Invoke(typing);
		}

		private void OnHudEditModeChanged(bool editing)
		{
			if (!_disposed) HudEditModeChanged?.Invoke(editing);
		}

		private void ClosePicker() => PickerVisible = false;

		private void OnScenarioStarted(ScenarioChoice choice) => PickerVisible = false;

		private void CloseDebrief() => DebriefVisible = false;

		private void CloseOptions() => OptionsVisible = false;

		private void EditHud()
		{
			OptionsVisible = false;
			HudEditing = true;
		}

		private void UpdatePanelShown()
		{
			var shown = ClassicPanelShown;
			Panel.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
			HudLayer.style.display = _panelVisible ? DisplayStyle.Flex : DisplayStyle.None;
			// Lift the overlay clear of the panel when both are shown.
			Hud.EnableInClassList("fs-hud--above-panel", shown);
		}

		private static void BlurInside(VisualElement container)
		{
			if (container.focusController?.focusedElement is VisualElement focused && container.Contains(focused))
			{
				// A hidden field must not keep taking keys.
				focused.Blur();
			}
		}

		private void AddStyleSheet(string resource)
		{
			var sheet = Resources.Load<StyleSheet>(resource);
			if (sheet != null)
			{
				styleSheets.Add(sheet);
			}
			else
			{
				Debug.LogWarning($"WheelhouseView: style sheet Resources/{resource}.uss is missing; the layout will be wrong.");
			}
		}
	}

	/// <summary>
	/// A row of buttons for the development scene, with a status line under it (the run, the alarm setting
	/// and whether the clock is assumed). The real key bindings belong to the input profiles.
	/// </summary>
	public sealed class DevToolbar : VisualElement, IPaletteAware
	{
		private readonly VisualElement _buttons;
		private InstrumentPalette _palette = InstrumentPalette.Day;

		public DevToolbar()
		{
			name = "dev-toolbar";
			AddToClassList("fs-toolbar");
			AddToClassList(PaletteRoles.HudBackground);
			_buttons = new VisualElement { name = "dev-toolbar-buttons" };
			_buttons.AddToClassList("fs-toolbar-buttons");
			Add(_buttons);
			Status = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-toolbar-status");
			Add(Status);
		}

		public Label Status { get; }

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			foreach (var child in _buttons.Children())
			{
				if (child is Button button)
				{
					DebriefScreen.StyleButton(button, _palette);
				}
			}
		}

		public Button AddButton(string text, Action onClick, string buttonName = null)
		{
			var button = new Button(onClick) { text = text, name = buttonName };
			button.AddToClassList("fs-toolbar-button");
			DebriefScreen.StyleButton(button, _palette);
			_buttons.Add(button);
			return button;
		}
	}
}
