using System;
using System.Collections.Generic;
using FerrySim.Controls;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	public enum OptionsPage
	{
		Controls,
		Hud,
		Display,
		Audio,
	}

	/// <summary>What to do when resetting one action was refused because another action now holds one of its default keys.</summary>
	public enum ResetChoice
	{
		/// <summary>Reset the action in the way, then this one.</summary>
		ResetThatOneToo,
		ResetAll,
		Cancel,
	}

	/// <summary>A refused single-action reset waiting for the player's choice.</summary>
	public sealed class PendingReset
	{
		public PendingReset(string actionId, string conflictActionId)
		{
			ActionId = actionId;
			ConflictActionId = conflictActionId;
		}

		public string ActionId { get; }

		public string ConflictActionId { get; }
	}

	/// <summary>
	/// The options menu, over the 3D view, with four pages: Controls (every rebindable key, grouped by map, for the keyboard,
	/// the gamepad or a joystick; rebind, reset one, reset all; the keybind popup mode), HUD (choose a preset or saved
	/// layout, edit the HUD, delete a saved layout), Display and units, and Audio. Every control writes the
	/// <see cref="SettingsStore"/> at once and follows it when it changes elsewhere.
	/// <para>
	/// It is driven entirely by the UI's navigation events, so the keyboard alone (arrows, Tab, Enter, Escape) or the gamepad
	/// alone (d-pad or stick, south, east) reach every control: Up and Down move between the current page's tab, its rows and
	/// the footer, wrapping, Left and Right change the row's value (and on the tab, change page), Submit acts, Back cancels a
	/// rebind or a question, and otherwise closes the menu. While a rebind listens, Escape is the binding service's own cancel.
	/// </para>
	/// <para>
	/// It never pauses anything: its host raises a visibility event and the composition code holds the run and the helm.
	/// Bindings change only through <see cref="IBindingService"/>; a refused rebind or reset changes nothing and names the
	/// action in the way.
	/// </para>
	/// </summary>
	public sealed class OptionsMenu : VisualElement, IPaletteAware
	{
		public const string Unbound = "—";
		public const string PressAKey = "PRESS A KEY";
		public const string NoBindings = "No binding service is connected, so keys cannot be changed here.";
		public const string Hint = "UP AND DOWN MOVE · LEFT AND RIGHT CHANGE · ENTER SELECTS · ESC CLOSES";

		private static readonly string[] Schemes = { "Keyboard", "Gamepad", "Joystick" };
		private static readonly string[] PageTitles = { "Controls", "HUD", "Display and units", "Audio" };

		private readonly List<Button> _tabs = new List<Button>();
		private readonly VisualElement[] _pages = new VisualElement[4];
		private readonly ScrollView _scroll;
		private readonly VisualElement _bindingList;
		private readonly List<BindingRowView> _bindingRows = new List<BindingRowView>();
		private readonly List<string> _groupNames = new List<string>();
		private readonly List<string> _groupHeadings = new List<string>();
		private readonly Label _message;
		private readonly VisualElement _prompt;
		private readonly Label _promptText;
		private readonly VisualElement _promptButtons;
		private readonly Button _close;
		private readonly Button _cancelRebind;
		private readonly List<VisualElement> _focusOrder = new List<VisualElement>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private IBindingService _bindings;
		private SettingsStore _settings;
		private HudController _hud;
		private BindingRowView _rebindRow;
		private int _lastBackFrame = -1;
		private int _rebindEndedFrame = -10;

		public OptionsMenu()
		{
			name = "options-menu";
			AddToClassList("fs-menu");
			AddToClassList(PaletteRoles.ConsoleBackground);
			pickingMode = PickingMode.Position;

			var header = Row("fs-menu-header");
			Title = PaletteRoles.Label("OPTIONS", PaletteRoles.Text, "fs-value-s", "fs-strong");
			Title.name = "options-title";
			header.Add(Title);
			header.Add(PaletteRoles.Label(Hint, PaletteRoles.Dim, "fs-caption", "fs-menu-hint"));
			Add(header);

			var tabs = Row("fs-menu-tabs");
			for (var i = 0; i < PageTitles.Length; i++)
			{
				var page = (OptionsPage)i;
				var tab = new Button(() => ShowPage(page)) { text = PageTitles[i].ToUpperInvariant(), name = "options-tab-" + page.ToString().ToLowerInvariant() };
				tab.AddToClassList("fs-button");
				tab.AddToClassList("fs-menu-tab");
				tab.RegisterCallback<NavigationMoveEvent>(evt => OnTabMove(evt, page));
				_tabs.Add(tab);
				tabs.Add(tab);
			}

			Add(tabs);

			_scroll = new ScrollView(ScrollViewMode.Vertical) { name = "options-scroll" };
			_scroll.AddToClassList("fs-menu-scroll");
			_scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
			Add(_scroll);
			for (var i = 0; i < _pages.Length; i++)
			{
				_pages[i] = new VisualElement { name = "options-page-" + ((OptionsPage)i).ToString().ToLowerInvariant(), pickingMode = PickingMode.Ignore };
				_pages[i].AddToClassList("fs-menu-page");
				_scroll.Add(_pages[i]);
			}

			// Controls.
			SchemeChoice = new ChoiceRow("options-scheme", "Device", Schemes, index => SetScheme(Schemes[index]));
			_pages[0].Add(SchemeChoice);
			KeybindHints = new ChoiceRow("options-keybind-hints", "Key hints", new[] { "Off", "While held", "Always" },
				index => { if (_settings != null) _settings.KeybindHints = (KeybindHintMode)index; });
			_pages[0].Add(KeybindHints);
			ResetAll = ActionButton("options-reset-all", "Reset all keys", ResetAllBindings);
			_pages[0].Add(ResetAll);
			_bindingList = new VisualElement { name = "options-bindings", pickingMode = PickingMode.Ignore };
			_pages[0].Add(_bindingList);

			// HUD.
			Layout = new ChoiceRow("options-layout", "Layout", Array.Empty<string>(), index => ChooseLayout(index));
			Layout.AddToClassList("fs-menu-row--wrap");
			_pages[1].Add(Layout);
			EditHud = ActionButton("options-edit-hud", "Arrange the HUD", RequestHudEdit);
			_pages[1].Add(EditHud);
			DeleteLayout = ActionButton("options-delete-layout", "Delete this saved layout", DeleteChosenLayout);
			_pages[1].Add(DeleteLayout);

			// Display and units.
			Palette = new ChoiceRow("options-palette", "Palette", new[] { "Auto", "Day", "Night" },
				index => { if (_settings != null) _settings.Palette = (PaletteSetting)index; });
			DistanceUnit = new ChoiceRow("options-units", "Depth and distance", new[] { "Feet", "Metres" },
				index => { if (_settings != null) _settings.DistanceUnit = (UI.DistanceUnit)index; });
			DepthAlarm = new StepperRow("options-depth-alarm", "Shallow-water alarm below", StepDepthAlarm, DepthAlarmText);
			VectorMinutes = new StepperRow("options-vector", "Plotter vector length", steps =>
			{
				if (_settings != null) _settings.VectorMinutes = Math.Round((_settings.VectorMinutes + steps * 0.5) * 2.0) / 2.0;
			}, () => _settings == null ? InstrumentFormat.NoValue : _settings.VectorMinutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " min");
			Predictor = new ChoiceRow("options-predictor", "Plotter predictor", new[] { "Straight", "Curved" },
				index => { if (_settings != null) _settings.Predictor = (PredictorKind)index; });
			ConsoleScreens = new ToggleRow("options-console-screens", "Console screens", on => { if (_settings != null) _settings.ConsoleScreens = on; });
			foreach (var row in new VisualElement[] { Palette, DistanceUnit, DepthAlarm, VectorMinutes, Predictor, ConsoleScreens })
			{
				_pages[2].Add(row);
			}

			// Audio.
			MasterVolume = new StepperRow("options-master-volume", "Master volume", steps => { if (_settings != null) _settings.MasterVolume = StepVolume(_settings.MasterVolume, steps); },
				() => Percent(_settings?.MasterVolume));
			Mute = new ToggleRow("options-mute", "Mute", on => { if (_settings != null) _settings.Muted = on; });
			AlarmVolume = new StepperRow("options-alarm-volume", "Alarm volume", steps => { if (_settings != null) _settings.AlarmVolume = StepVolume(_settings.AlarmVolume, steps); },
				() => Percent(_settings?.AlarmVolume));
			foreach (var row in new VisualElement[] { MasterVolume, Mute, AlarmVolume })
			{
				_pages[3].Add(row);
			}

			var footer = Column("fs-menu-footer");
			_prompt = Row("fs-menu-prompt");
			_prompt.name = "options-prompt";
			_prompt.AddToClassList(PaletteRoles.FaceBackground);
			_promptText = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-menu-prompt-text");
			_promptText.style.whiteSpace = WhiteSpace.Normal;
			_prompt.Add(_promptText);
			_promptButtons = Row("fs-menu-prompt-buttons");
			_prompt.Add(_promptButtons);
			_prompt.style.display = DisplayStyle.None;
			footer.Add(_prompt);
			var bottom = Row("fs-menu-bottom");
			_message = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-menu-message");
			_message.name = "options-message";
			_message.style.whiteSpace = WhiteSpace.Normal;
			bottom.Add(_message);
			bottom.Add(Row("fs-spacer"));
			_cancelRebind = FooterButton("Cancel rebind", CancelRebind, "options-cancel-rebind");
			_cancelRebind.style.display = DisplayStyle.None;
			bottom.Add(_cancelRebind);
			_close = FooterButton("Close", RequestClose, "options-close");
			bottom.Add(_close);
			footer.Add(bottom);
			Add(footer);

			RegisterCallback<NavigationMoveEvent>(OnNavigationMove);
			RegisterCallback<NavigationCancelEvent>(evt =>
			{
				BackFromInput();
				evt.StopPropagation();
			});
			RegisterCallback<KeyDownEvent>(evt =>
			{
				if (evt.keyCode == KeyCode.Escape)
				{
					BackFromInput();
					evt.StopPropagation();
				}
			});
			RegisterCallback<FocusInEvent>(evt => ScrollToFocused(evt.target as VisualElement));
			ShowPage(OptionsPage.Controls);
			Bind(null, null, null);
		}

		/// <summary>The Close button, or Back with nothing left to cancel.</summary>
		public event Action CloseRequested;

		/// <summary>"Arrange the HUD": the host closes the menu and starts HUD edit mode.</summary>
		public event Action EditHudRequested;

		public Label Title { get; }

		public OptionsPage Page { get; private set; }

		/// <summary>The control scheme the Controls page lists: Keyboard, Gamepad or Joystick.</summary>
		public string Scheme { get; private set; } = "Keyboard";

		public IReadOnlyList<BindingRowView> BindingRows => _bindingRows;

		/// <summary>The maps the Controls page groups its rows under, in order.</summary>
		public IReadOnlyList<string> GroupNames => _groupNames;

		/// <summary>The heading shown for each of <see cref="GroupNames"/>, in the same order (the page prints them in capitals).</summary>
		public IReadOnlyList<string> GroupHeadings => _groupHeadings;

		public bool Rebinding => _rebindRow != null;

		/// <summary>The status line: the last rebind or reset, a refusal and who is in the way, or why nothing can be changed.</summary>
		public string Message => _message.text;

		public PendingReset PendingReset { get; private set; }

		/// <summary>The saved layout waiting for a delete confirmation, or null.</summary>
		public string PendingDelete { get; private set; }

		public ChoiceRow SchemeChoice { get; }

		public ChoiceRow KeybindHints { get; }

		public Button ResetAll { get; }

		public ChoiceRow Layout { get; }

		public Button EditHud { get; }

		public Button DeleteLayout { get; }

		public ChoiceRow Palette { get; }

		public ChoiceRow DistanceUnit { get; }

		public StepperRow DepthAlarm { get; }

		public StepperRow VectorMinutes { get; }

		public ChoiceRow Predictor { get; }

		public ToggleRow ConsoleScreens { get; }

		public StepperRow MasterVolume { get; }

		public ToggleRow Mute { get; }

		public StepperRow AlarmVolume { get; }

		public Button CloseButton => _close;

		/// <summary>Every tab stop on the current page, in the order Up and Down visit them: the tabs, the page, the footer.</summary>
		public IReadOnlyList<VisualElement> FocusOrder
		{
			get
			{
				RebuildFocusOrder();
				return _focusOrder;
			}
		}

		/// <summary>Connects the menu; any argument may be null (the pages it feeds then show nothing to change).</summary>
		public void Bind(IBindingService bindings, SettingsStore settings, HudController hud)
		{
			CancelRebind();
			if (_bindings != null) _bindings.BindingsChanged -= RefreshKeys;
			if (_settings != null) _settings.Changed -= OnSettingsChanged;
			if (_hud != null)
			{
				_hud.ChoicesChanged -= RefreshLayouts;
				_hud.LayoutChanged -= OnLayoutChanged;
			}

			_bindings = bindings;
			_settings = settings;
			_hud = hud;
			if (_bindings != null) _bindings.BindingsChanged += RefreshKeys;
			if (_settings != null) _settings.Changed += OnSettingsChanged;
			if (_hud != null)
			{
				_hud.ChoicesChanged += RefreshLayouts;
				_hud.LayoutChanged += OnLayoutChanged;
			}

			PendingReset = null;
			PendingDelete = null;
			ShowPrompt(null);
			BuildBindingRows();
			RefreshSettings();
			RefreshLayouts();
			ShowMessage(_bindings == null ? NoBindings : string.Empty);
		}

		public void ShowPage(OptionsPage page)
		{
			Page = page;
			for (var i = 0; i < _pages.Length; i++)
			{
				_pages[i].style.display = i == (int)page ? DisplayStyle.Flex : DisplayStyle.None;
				_tabs[i].EnableInClassList("fs-menu-tab--selected", i == (int)page);
			}

			RestyleTabs();
			_scroll.scrollOffset = Vector2.zero;
		}

		/// <summary>Puts the focus on the current page's tab, for when the menu opens.</summary>
		public void FocusFirst() => _tabs[(int)Page].Focus();

		public void SetScheme(string scheme)
		{
			var index = Array.IndexOf(Schemes, scheme);
			if (index < 0 || scheme == Scheme)
			{
				return;
			}

			CancelRebind();
			Scheme = scheme;
			SchemeChoice.SetWithoutNotify(index);
			BuildBindingRows();
		}

		/// <summary>Listens for the next key or button for the row's slot.</summary>
		public void BeginRebind(BindingRowView row)
		{
			if (_bindings == null || row == null)
			{
				return;
			}

			CancelRebind();
			ClearQuestions();
			_rebindRow = row;
			row.ShowPending();
			_cancelRebind.style.display = DisplayStyle.Flex;
			ShowMessage($"Press a key or button for {row.Row.Label}. Escape or Cancel rebind stops.");
			_bindings.BeginRebind(row.Row.ActionId, row.Row.Slot, row.Row.Scheme, outcome => OnRebound(row, outcome));
		}

		public void CancelRebind()
		{
			if (_rebindRow != null)
			{
				_bindings?.CancelRebind();
			}

			// A service that never calls back must not leave the row waiting.
			if (_rebindRow != null)
			{
				OnRebound(_rebindRow, new RebindOutcome(RebindResult.Cancelled, null));
			}
		}

		/// <summary>Resets one action to its default keys; when that would take a key from another action, asks what to do.</summary>
		public void ResetAction(string actionId)
		{
			if (_bindings == null || string.IsNullOrEmpty(actionId))
			{
				return;
			}

			CancelRebind();
			ClearQuestions();
			if (_bindings.TryReset(actionId, out var conflict))
			{
				ShowMessage(Pretty(actionId) + " is back to its default keys.");
				RefreshKeys();
				return;
			}

			PendingReset = new PendingReset(actionId, conflict);
			var question = $"{Pretty(conflict)} now uses a default key of {Pretty(actionId)}, so nothing changed. Reset {Pretty(conflict)} too, reset all keys, or leave them?";
			ShowMessage(question);
			ShowPrompt(question,
				("Reset that one too", () => ResolvePendingReset(ResetChoice.ResetThatOneToo)),
				("Reset all", () => ResolvePendingReset(ResetChoice.ResetAll)),
				("Leave them", () => ResolvePendingReset(ResetChoice.Cancel)));
		}

		public void ResolvePendingReset(ResetChoice choice)
		{
			var pending = PendingReset;
			if (pending == null)
			{
				return;
			}

			PendingReset = null;
			ShowPrompt(null);
			switch (choice)
			{
				case ResetChoice.ResetAll:
					ResetAllBindings();
					break;
				case ResetChoice.Cancel:
					ShowMessage("Nothing changed.");
					break;
				default:
					if (!_bindings.TryReset(pending.ConflictActionId, out var next))
					{
						// The action in the way is itself blocked: ask about that one.
						ResetAction(pending.ConflictActionId);
						return;
					}

					ResetAction(pending.ActionId);
					break;
			}

			RefreshKeys();
		}

		public void ResetAllBindings()
		{
			if (_bindings == null)
			{
				return;
			}

			CancelRebind();
			ClearQuestions();
			_bindings.ResetAll();
			ShowMessage("Every key is back to its default.");
			RefreshKeys();
		}

		public void RequestHudEdit()
		{
			CancelRebind();
			EditHudRequested?.Invoke();
		}

		/// <summary>Asks before deleting the chosen saved layout.</summary>
		public void DeleteChosenLayout()
		{
			var name = ChosenSavedLayout();
			if (name == null)
			{
				return;
			}

			ClearQuestions();
			PendingDelete = name;
			var question = $"Delete the saved layout '{name}'? This cannot be undone.";
			ShowMessage(question);
			ShowPrompt(question, ("Delete", () => ConfirmDelete(true)), ("Keep it", () => ConfirmDelete(false)));
		}

		public void ConfirmDelete(bool delete)
		{
			var name = PendingDelete;
			PendingDelete = null;
			ShowPrompt(null);
			if (name == null)
			{
				return;
			}

			if (!delete)
			{
				ShowMessage("Nothing deleted.");
				return;
			}

			ShowMessage(_hud != null && _hud.DeleteSaved(name) ? $"Deleted '{name}'." : $"Could not delete '{name}': {_hud?.Layouts.LastError}");
			RefreshLayouts();
		}

		public void RequestClose()
		{
			CancelRebind();
			ClearQuestions();
			CloseRequested?.Invoke();
		}

		/// <summary>Escape or the gamepad's east button: cancels a rebind or a question first, and otherwise closes.</summary>
		public void Back()
		{
			if (Rebinding)
			{
				CancelRebind();
			}
			else if (PendingReset != null)
			{
				ResolvePendingReset(ResetChoice.Cancel);
			}
			else if (PendingDelete != null)
			{
				ConfirmDelete(false);
			}
			else
			{
				RequestClose();
			}
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			DebriefScreen.StyleButton(_close, _palette);
			DebriefScreen.StyleButton(_cancelRebind, _palette);
			foreach (var button in new[] { ResetAll, EditHud, DeleteLayout })
			{
				DebriefScreen.StyleButton(button, _palette);
			}

			_promptButtons.Query<Button>().ForEach(button => DebriefScreen.StyleButton(button, _palette));
			RestyleTabs();
			StyleScroller(_scroll.verticalScroller);
		}

		/// <summary>Moves the focus to the next (+1) or previous (−1) tab stop, wrapping.</summary>
		public void MoveFocus(int direction)
		{
			RebuildFocusOrder();
			if (_focusOrder.Count == 0)
			{
				return;
			}

			var focused = focusController?.focusedElement as VisualElement;
			var index = focused != null ? _focusOrder.IndexOf(focused) : -1;
			var next = index < 0 ? (direction > 0 ? 0 : _focusOrder.Count - 1) : (index + direction + _focusOrder.Count) % _focusOrder.Count;
			_focusOrder[next].Focus();
		}

		private void RebuildFocusOrder()
		{
			_focusOrder.Clear();
			this.Query<VisualElement>().ForEach(element =>
			{
				// Rows and buttons only: the scroll bar's slider is focusable too, and would swallow Up and Down.
				// Only the current page's tab: Left and Right on it change page.
				var otherTab = element is Button tab && _tabs.Contains(tab) && _tabs.IndexOf(tab) != (int)Page;
				if (!otherTab && (element is OptionRow || element is Button) && element.focusable && element.tabIndex >= 0 && element.enabledInHierarchy &&
					element.GetFirstAncestorOfType<Scroller>() == null && IsShown(element))
				{
					_focusOrder.Add(element);
				}
			});
		}

		private bool IsShown(VisualElement element)
		{
			for (var e = element; e != null && e != this; e = e.parent)
			{
				if (e.style.display == DisplayStyle.None || e.resolvedStyle.display == DisplayStyle.None && e.panel != null)
				{
					return false;
				}
			}

			return true;
		}

		private void OnNavigationMove(NavigationMoveEvent evt)
		{
			switch (evt.direction)
			{
				case NavigationMoveEvent.Direction.Up:
				case NavigationMoveEvent.Direction.Previous:
					MoveFocus(-1);
					break;
				case NavigationMoveEvent.Direction.Down:
				case NavigationMoveEvent.Direction.Next:
					MoveFocus(1);
					break;
				default:
					// Left and Right on a button: nothing to change; never let them move the focus out of the menu.
					break;
			}

			evt.StopPropagation();
			focusController?.IgnoreEvent(evt);
		}

		private void OnTabMove(NavigationMoveEvent evt, OptionsPage page)
		{
			if (evt.direction != NavigationMoveEvent.Direction.Left && evt.direction != NavigationMoveEvent.Direction.Right)
			{
				return;
			}

			var next = ((int)page + (evt.direction == NavigationMoveEvent.Direction.Left ? -1 : 1) + _tabs.Count) % _tabs.Count;
			ShowPage((OptionsPage)next);
			_tabs[next].Focus();
			evt.StopPropagation();
			focusController?.IgnoreEvent(evt);
		}

		/// <summary>
		/// Escape arrives both as a key and as a navigation cancel, in the same frame: act once. Escape that ended a rebind
		/// (the binding service's own cancel) is not also a request to close the menu.
		/// </summary>
		private void BackFromInput()
		{
			if (Rebinding)
			{
				// Cancelling twice is harmless; the service may have cancelled on the same Escape already.
				CancelRebind();
				_lastBackFrame = Application.isPlaying ? Time.frameCount : -1;
				return;
			}

			if (Application.isPlaying)
			{
				var frame = Time.frameCount;
				if (frame == _lastBackFrame || frame - _rebindEndedFrame <= 1)
				{
					return;
				}

				_lastBackFrame = frame;
			}

			Back();
		}

		private void OnRebound(BindingRowView row, RebindOutcome outcome)
		{
			if (_rebindRow != row)
			{
				return;
			}

			_rebindRow = null;
			_rebindEndedFrame = Application.isPlaying ? Time.frameCount : -10;
			_cancelRebind.style.display = DisplayStyle.None;
			RefreshKeys();
			switch (outcome.Result)
			{
				case RebindResult.Applied:
					ShowMessage($"{row.Row.Label}: {row.KeyText}");
					break;
				case RebindResult.Conflict:
					ShowMessage($"{Pretty(outcome.ConflictActionId)} already uses that key, so nothing changed.");
					break;
				default:
					ShowMessage("Rebind cancelled; nothing changed.");
					break;
			}

			if (row.panel != null)
			{
				row.Focus();
			}
		}

		private void BuildBindingRows()
		{
			if (_rebindRow != null)
			{
				CancelRebind();
			}

			_bindingList.Clear();
			_bindingRows.Clear();
			_groupNames.Clear();
			_groupHeadings.Clear();
			if (_bindings == null)
			{
				return;
			}

			string group = null;
			foreach (var row in _bindings.Rows)
			{
				if (row.Scheme != Scheme)
				{
					continue;
				}

				var map = row.ActionId.Substring(0, Math.Max(0, row.ActionId.IndexOf('/')));
				if (map != group)
				{
					group = map;
					_groupNames.Add(map);
					var heading = BindingLabels.GroupName(map);
					_groupHeadings.Add(heading);
					var header = PaletteRoles.Label(heading.ToUpperInvariant(), PaletteRoles.Dim, "fs-caption", "fs-menu-group");
					_bindingList.Add(header);
				}

				var view = new BindingRowView(row, BeginRebind, ResetAction);
				_bindingRows.Add(view);
				_bindingList.Add(view);
			}

			if (_bindingRows.Count == 0)
			{
				_bindingList.Add(PaletteRoles.Label("Nothing to rebind for this device.", PaletteRoles.Dim, "fs-caption", "fs-menu-group"));
			}

			RefreshKeys();
			PaletteRoles.Apply(_bindingList, _palette);
		}

		private void RefreshKeys()
		{
			if (_bindings == null)
			{
				return;
			}

			foreach (var view in _bindingRows)
			{
				if (view == _rebindRow)
				{
					continue;
				}

				view.ShowKey(KeyFor(view.Row));
			}
		}

		/// <summary>The key shown on a row: its own slot's text from the binding service, or <see cref="Unbound"/>.</summary>
		private string KeyFor(BindingRow row)
		{
			var text = _bindings.DisplayString(row.ActionId, row.Slot, row.Scheme);
			return string.IsNullOrEmpty(text) ? Unbound : text;
		}

		private string Pretty(string actionId)
		{
			if (actionId == "Interface/Navigation")
			{
				return "menu navigation";
			}

			if (_bindings != null)
			{
				foreach (var row in _bindings.Rows)
				{
					if (row.ActionId == actionId)
					{
						return row.Label;
					}
				}
			}

			if (string.IsNullOrEmpty(actionId))
			{
				return "another action";
			}

			var slash = actionId.IndexOf('/');
			return slash > 0 ? actionId.Substring(slash + 1) + " (" + actionId.Substring(0, slash) + ")" : actionId;
		}

		private void OnSettingsChanged(SettingsSection section) => RefreshSettings();

		private void RefreshSettings()
		{
			var settings = _settings;
			KeybindHints.SetWithoutNotify(settings != null ? (int)settings.KeybindHints : (int)KeybindHintMode.Hold);
			Palette.SetWithoutNotify(settings != null ? (int)settings.Palette : 0);
			DistanceUnit.SetWithoutNotify(settings != null ? (int)settings.DistanceUnit : 0);
			Predictor.SetWithoutNotify(settings != null ? (int)settings.Predictor : 0);
			ConsoleScreens.SetWithoutNotify(settings == null || settings.ConsoleScreens);
			Mute.SetWithoutNotify(settings != null && settings.Muted);
			DepthAlarm.Refresh();
			VectorMinutes.Refresh();
			MasterVolume.Refresh();
			AlarmVolume.Refresh();
			foreach (var row in new VisualElement[] { KeybindHints, Palette, DistanceUnit, Predictor, ConsoleScreens, Mute, DepthAlarm, VectorMinutes, MasterVolume, AlarmVolume })
			{
				row.SetEnabled(settings != null);
			}

			RefreshLayouts();
		}

		private void OnLayoutChanged(string widgetId)
		{
			if (widgetId == null)
			{
				RefreshLayouts();
			}
		}

		private void RefreshLayouts()
		{
			if (_hud == null)
			{
				Layout.SetOptions(Array.Empty<string>(), 0);
				Layout.SetEnabled(false);
				EditHud.SetEnabled(false);
				DeleteLayout.SetEnabled(false);
				return;
			}

			var choices = _hud.Choices;
			var chosen = 0;
			for (var i = 0; i < choices.Count; i++)
			{
				if (string.Equals(choices[i], _hud.Current.Name, StringComparison.OrdinalIgnoreCase))
				{
					chosen = i;
				}
			}

			if (!SameOptions(choices))
			{
				Layout.SetOptions(choices, chosen);
				PaletteRoles.Apply(Layout, _palette);
			}
			else
			{
				Layout.SetWithoutNotify(chosen);
			}

			Layout.SetEnabled(true);
			EditHud.SetEnabled(true);
			DeleteLayout.SetEnabled(ChosenSavedLayout() != null);
		}

		private bool SameOptions(IReadOnlyList<string> choices)
		{
			if (choices.Count != Layout.Options.Count)
			{
				return false;
			}

			for (var i = 0; i < choices.Count; i++)
			{
				if (choices[i] != Layout.Options[i])
				{
					return false;
				}
			}

			return true;
		}

		private void ChooseLayout(int index)
		{
			if (_hud == null || index < 0 || index >= _hud.Choices.Count)
			{
				return;
			}

			_hud.Choose(_hud.Choices[index]);
			RefreshLayouts();
		}

		private string ChosenSavedLayout()
		{
			if (_hud == null)
			{
				return null;
			}

			var name = _hud.Current.Name;
			return !HudPresets.IsPreset(name) && _hud.IsSaved(name) ? name : null;
		}

		private void StepDepthAlarm(int steps)
		{
			if (_settings == null)
			{
				return;
			}

			var unit = _settings.DistanceUnit;
			var step = unit == UI.DistanceUnit.Feet ? 1.0 : 0.5;
			var current = DisplayUnits.Distance(_settings.DepthAlarmMetres, unit);
			var next = Math.Max(0.0, Math.Round(current / step) * step + steps * step);
			_settings.DepthAlarmMetres = DisplayUnits.DistanceToMetres(next, unit);
		}

		private string DepthAlarmText()
		{
			if (_settings == null)
			{
				return InstrumentFormat.NoValue;
			}

			var unit = _settings.DistanceUnit;
			return InstrumentFormat.Distance(_settings.DepthAlarmMetres, unit) + " " + DisplayUnits.Symbol(unit);
		}

		private static float StepVolume(float volume, int steps) => Mathf.Round((volume + steps * 0.05f) * 20f) / 20f;

		private static string Percent(float? volume) => volume.HasValue ? Mathf.RoundToInt(volume.Value * 100f) + " %" : InstrumentFormat.NoValue;

		private void ShowMessage(string text) => PaletteRoles.SetText(_message, text ?? string.Empty);

		private void ClearQuestions()
		{
			PendingReset = null;
			PendingDelete = null;
			ShowPrompt(null);
		}

		private void ShowPrompt(string question, params (string text, Action action)[] buttons)
		{
			_promptButtons.Clear();
			// The question is the status too; show it once.
			_message.style.display = question == null ? DisplayStyle.Flex : DisplayStyle.None;
			if (question == null)
			{
				_prompt.style.display = DisplayStyle.None;
				return;
			}

			PaletteRoles.SetText(_promptText, question);
			Button first = null;
			foreach (var (text, action) in buttons)
			{
				var button = FooterButton(text, action, "options-prompt-" + text.Replace(' ', '-').ToLowerInvariant());
				_promptButtons.Add(button);
				first ??= button;
			}

			_prompt.style.display = DisplayStyle.Flex;
			PaletteRoles.Apply(_prompt, _palette);
			ApplyPalette(_palette);
			if (first != null && panel != null)
			{
				first.Focus();
			}
		}

		private void ScrollToFocused(VisualElement element)
		{
			if (element != null && _scroll.contentContainer.Contains(element) && element.panel != null)
			{
				_scroll.ScrollTo(element);
			}
		}

		private void RestyleTabs()
		{
			for (var i = 0; i < _tabs.Count; i++)
			{
				var selected = i == (int)Page;
				_tabs[i].style.backgroundColor = selected ? _palette.Engaged : _palette.Face;
				_tabs[i].style.color = selected ? _palette.EngagedText : _palette.Text;
				PaletteRoles.SetBorderColor(_tabs[i], selected ? _palette.Engaged : _palette.Bezel);
			}
		}

		private void StyleScroller(Scroller scroller)
		{
			scroller.lowButton.style.display = DisplayStyle.None;
			scroller.highButton.style.display = DisplayStyle.None;
			scroller.style.backgroundColor = _palette.Face;
			PaletteRoles.SetBorderColor(scroller, _palette.Face);
			var tracker = scroller.slider.Q(className: BaseSlider<float>.trackerUssClassName);
			if (tracker != null)
			{
				tracker.style.backgroundColor = _palette.Face;
				PaletteRoles.SetBorderColor(tracker, _palette.Bezel);
			}

			var dragger = scroller.slider.Q(className: BaseSlider<float>.draggerUssClassName);
			if (dragger != null)
			{
				dragger.style.backgroundColor = _palette.Bezel;
				PaletteRoles.SetBorderColor(dragger, _palette.Scale);
			}
		}

		private static Button ActionButton(string buttonName, string text, Action onClick)
		{
			var button = new Button(onClick) { text = text.ToUpperInvariant(), name = buttonName };
			button.AddToClassList("fs-button");
			button.AddToClassList("fs-menu-action");
			return button;
		}

		private static Button FooterButton(string text, Action onClick, string buttonName)
		{
			var button = new Button(onClick) { text = text, name = buttonName };
			button.AddToClassList("fs-button");
			return button;
		}

		private static VisualElement Row(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}

		private static VisualElement Column(string className) => Row(className);
	}
}
