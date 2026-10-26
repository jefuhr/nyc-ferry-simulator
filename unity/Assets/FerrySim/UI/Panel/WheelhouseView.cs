using System;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The whole UI layer: the instrument panel, the HUD, the scenario picker, the debrief screen and an optional
	/// development toolbar, plus the display settings they share (palette, depth unit, alarm threshold). It is a
	/// plain VisualElement so it can be hosted by <see cref="WheelhouseUI"/> or built directly in tests.
	/// </summary>
	public sealed class WheelhouseView : VisualElement
	{
		public const string StyleSheetResource = "FerrySimUI/Wheelhouse";

		private PaletteKind _palette = PaletteKind.Day;
		private PaletteKind? _suggestedPalette;
		private DistanceUnit _distanceUnit = DistanceUnit.Feet;
		private double _depthAlarmThreshold = DepthAlarm.DefaultThresholdMetres;
		private IScenarioCatalog _catalog;
		private IScenarioLauncher _launcher;

		public WheelhouseView()
		{
			name = "wheelhouse-view";
			AddToClassList("fs-view");
			pickingMode = PickingMode.Ignore;
			var sheet = Resources.Load<StyleSheet>(StyleSheetResource);
			if (sheet != null)
			{
				styleSheets.Add(sheet);
			}
			else
			{
				Debug.LogWarning($"WheelhouseView: style sheet Resources/{StyleSheetResource}.uss is missing; the layout will be wrong.");
			}

			Panel = new WheelhousePanel();
			Add(Panel);
			Hud = new HudOverlay();
			Add(Hud);
			Picker = new ScenarioPicker();
			Picker.CloseRequested += () => PickerVisible = false;
			Picker.Started += _ => PickerVisible = false;
			Add(Picker);
			Debrief = new DebriefScreen();
			Debrief.CloseRequested += () => DebriefVisible = false;
			Add(Debrief);
			Toolbar = new DevToolbar();
			Add(Toolbar);

			PanelVisible = true;
			HudVisible = false;
			Picker.style.display = DisplayStyle.None;
			DebriefVisible = false;
			ToolbarVisible = false;
			PaletteRoles.Apply(this, InstrumentPalette.For(_palette));
		}

		public WheelhousePanel Panel { get; }

		public HudOverlay Hud { get; }

		/// <summary>The scenario picker; <see cref="PickerVisible"/> shows it bound to <see cref="ConnectScenario"/>'s catalog and launcher.</summary>
		public ScenarioPicker Picker { get; }

		public DebriefScreen Debrief { get; }

		public DevToolbar Toolbar { get; }

		/// <summary>The water and the air for the panel and the HUD. Null, or a destroyed scene component, reads "---".</summary>
		public IEnvironmentReadoutSource EnvironmentSource { get; set; }

		/// <summary>Raised after any display setting changes, so hosts can persist or mirror it.</summary>
		public event Action SettingsChanged;

		/// <summary>
		/// Raised with true when the picker opens and false when it closes (Close, Escape, or a run started). The picker
		/// only uses keys the helm does not (Input/README.md), but a host may still pause or hold the helm while it is open.
		/// </summary>
		public event Action<bool> PickerVisibilityChanged;

		public PaletteKind Palette
		{
			get => _palette;
			set
			{
				_palette = value;
				PaletteRoles.Apply(this, InstrumentPalette.For(value));
				SettingsChanged?.Invoke();
			}
		}

		public DistanceUnit DistanceUnit
		{
			get => _distanceUnit;
			set
			{
				_distanceUnit = value;
				Debrief.DistanceUnit = value;
				SettingsChanged?.Invoke();
			}
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

				_depthAlarmThreshold = Math.Max(0.0, value);
				Debrief.DepthAlarmThresholdMetres = _depthAlarmThreshold;
				SettingsChanged?.Invoke();
			}
		}

		public bool PanelVisible
		{
			get => Panel.style.display != DisplayStyle.None;
			set
			{
				Panel.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
				// Lift the HUD clear of the panel when both are shown.
				Hud.EnableInClassList("fs-hud--above-panel", value);
			}
		}

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
				if (value == PickerVisible)
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
				else if (Picker.focusController?.focusedElement is VisualElement focused && Picker.Contains(focused))
				{
					// A hidden field must not keep taking keys.
					focused.Blur();
				}

				PickerVisibilityChanged?.Invoke(value);
			}
		}

		/// <summary>The catalog and launcher the picker uses; either may be null. Rebinds an open picker.</summary>
		public void ConnectScenario(IScenarioCatalog catalog, IScenarioLauncher launcher)
		{
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
		/// The palette the run's time of day suggests (the sun-altitude rule in World/Sky/Daylight.cs). It is applied only
		/// when the suggestion changes, at dusk, at dawn or at a run in another light, so a palette the captain chose by hand
		/// in between holds until then.
		/// </summary>
		public void SuggestPalette(PaletteKind palette)
		{
			if (_suggestedPalette == palette)
			{
				return;
			}

			_suggestedPalette = palette;
			if (Palette != palette)
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
			// The panel keeps running while hidden so its alarm state is current when it comes back; the HUD shows what it worked out.
			Panel.Refresh(source, environment, DistanceUnit, DepthAlarmThresholdMetres, realTime);
			if (HudVisible)
			{
				Hud.Refresh(source, Panel.EnvironmentDisplay);
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
