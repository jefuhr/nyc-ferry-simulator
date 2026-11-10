using System;
using System.Collections.Generic;
using FerrySim.Controls;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// One line of the options menu: a caption and a value, one tab stop. It is driven by the UI's navigation events, which
	/// the Input System raises for the keyboard (arrows, Tab, Enter, Escape) and the gamepad alike (d-pad and left stick,
	/// south and east buttons), so the menu works with either alone: Left and Right change the value, Submit acts, and Up
	/// and Down are left to the menu to move between rows. Clicking works too. Inner buttons are not tab stops.
	/// </summary>
	public abstract class OptionRow : VisualElement, IPaletteAware
	{
		private readonly Label _caption;

		protected OptionRow(string rowName, string caption)
		{
			name = rowName;
			focusable = true;
			tabIndex = 0;
			pickingMode = PickingMode.Position;
			AddToClassList("fs-menu-row");
			_caption = PaletteRoles.Label(caption, PaletteRoles.Text, "fs-menu-caption");
			Add(_caption);
			Body = new VisualElement { pickingMode = PickingMode.Ignore };
			Body.AddToClassList("fs-menu-body");
			Add(Body);
			RegisterCallback<NavigationMoveEvent>(OnNavigationMove);
			RegisterCallback<NavigationSubmitEvent>(OnSubmit);
			RegisterCallback<FocusInEvent>(_ => SetFocused(true));
			RegisterCallback<FocusOutEvent>(_ => SetFocused(false));
		}

		public string Caption => _caption.text;

		/// <summary>True while the row has the focus; its border shows it.</summary>
		public bool Focused { get; private set; }

		protected VisualElement Body { get; }

		protected InstrumentPalette Palette { get; private set; } = InstrumentPalette.Day;

		/// <summary>Left (−1) or Right (+1).</summary>
		public virtual void Adjust(int delta)
		{
		}

		/// <summary>Enter, the south button or a click.</summary>
		public virtual void Activate()
		{
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			Palette = palette ?? InstrumentPalette.Day;
			style.backgroundColor = Palette.Face;
			RestyleFocus();
			Restyle();
		}

		protected virtual void Restyle()
		{
		}

		protected Button InnerButton(string text, Action onClick, string className = "fs-menu-button")
		{
			var button = new Button(() =>
			{
				onClick();
				Focus();
			}) { text = text, focusable = false };
			button.AddToClassList(className);
			return button;
		}

		protected void StyleChoice(Button button, bool selected)
		{
			button.style.backgroundColor = selected ? Palette.Engaged : Palette.Face;
			button.style.color = selected ? Palette.EngagedText : Palette.Text;
			PaletteRoles.SetBorderColor(button, selected ? Palette.Engaged : Palette.Bezel);
		}

		private void OnNavigationMove(NavigationMoveEvent evt)
		{
			if (evt.direction == NavigationMoveEvent.Direction.Left || evt.direction == NavigationMoveEvent.Direction.Right)
			{
				Adjust(evt.direction == NavigationMoveEvent.Direction.Left ? -1 : 1);
				evt.StopPropagation();
				focusController?.IgnoreEvent(evt);
			}
		}

		private void OnSubmit(NavigationSubmitEvent evt)
		{
			Activate();
			evt.StopPropagation();
			focusController?.IgnoreEvent(evt);
		}

		private void SetFocused(bool focused)
		{
			Focused = focused;
			RestyleFocus();
			Restyle();
		}

		private void RestyleFocus() => PaletteRoles.SetBorderColor(this, Focused ? Palette.Text : Palette.Bezel);
	}

	/// <summary>One of a few named options, shown side by side; Left and Right step through them, a click picks one.</summary>
	public sealed class ChoiceRow : OptionRow
	{
		private readonly List<string> _options = new List<string>();
		private readonly List<Button> _buttons = new List<Button>();
		private readonly Action<int> _chosen;

		public ChoiceRow(string rowName, string caption, IEnumerable<string> options, Action<int> chosen) : base(rowName, caption)
		{
			_chosen = chosen;
			SetOptions(options, 0);
		}

		public int Value { get; private set; }

		public IReadOnlyList<string> Options => _options;

		public string ValueText => Value >= 0 && Value < _options.Count ? _options[Value] : string.Empty;

		public void SetOptions(IEnumerable<string> options, int value)
		{
			_options.Clear();
			_options.AddRange(options);
			foreach (var button in _buttons)
			{
				button.RemoveFromHierarchy();
			}

			_buttons.Clear();
			for (var i = 0; i < _options.Count; i++)
			{
				var index = i;
				var button = InnerButton(_options[i].ToUpperInvariant(), () => Select(index), "fs-menu-choice");
				_buttons.Add(button);
				Body.Add(button);
			}

			SetWithoutNotify(value);
		}

		/// <summary>Shows a value without reporting it, for when the setting changed elsewhere.</summary>
		public void SetWithoutNotify(int value)
		{
			Value = _options.Count == 0 ? -1 : Math.Max(0, Math.Min(_options.Count - 1, value));
			Restyle();
		}

		public void Select(int index)
		{
			if (index < 0 || index >= _options.Count)
			{
				return;
			}

			SetWithoutNotify(index);
			_chosen?.Invoke(index);
		}

		public override void Adjust(int delta)
		{
			if (_options.Count > 0)
			{
				Select(Math.Max(0, Math.Min(_options.Count - 1, Value + delta)));
			}
		}

		/// <summary>Submit steps to the next option, wrapping, so a single button works it too.</summary>
		public override void Activate()
		{
			if (_options.Count > 0)
			{
				Select((Value + 1) % _options.Count);
			}
		}

		protected override void Restyle()
		{
			for (var i = 0; i < _buttons.Count; i++)
			{
				StyleChoice(_buttons[i], i == Value);
			}
		}
	}

	/// <summary>A number with − and + : Left and Right (or the buttons) step it; the owner formats and stores it.</summary>
	public sealed class StepperRow : OptionRow
	{
		private readonly Label _value;
		private readonly Button _down;
		private readonly Button _up;
		private readonly Action<int> _step;
		private readonly Func<string> _format;

		public StepperRow(string rowName, string caption, Action<int> step, Func<string> format) : base(rowName, caption)
		{
			_step = step;
			_format = format;
			_down = InnerButton("−", () => Adjust(-1));
			Body.Add(_down);
			_value = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-value-s", "fs-menu-value");
			_value.name = rowName + "-value";
			Body.Add(_value);
			_up = InnerButton("+", () => Adjust(1));
			Body.Add(_up);
			Refresh();
		}

		public string ValueText => _value.text;

		public override void Adjust(int delta)
		{
			_step?.Invoke(delta);
			Refresh();
		}

		public void Refresh() => PaletteRoles.SetText(_value, _format?.Invoke() ?? string.Empty);

		protected override void Restyle()
		{
			DebriefScreen.StyleButton(_down, Palette);
			DebriefScreen.StyleButton(_up, Palette);
		}
	}

	/// <summary>On or off: Submit or a click toggles it, Left turns it off and Right on.</summary>
	public sealed class ToggleRow : OptionRow
	{
		private readonly Button _button;
		private readonly Action<bool> _changed;

		public ToggleRow(string rowName, string caption, Action<bool> changed) : base(rowName, caption)
		{
			_changed = changed;
			_button = InnerButton("OFF", Activate, "fs-menu-choice");
			Body.Add(_button);
			Restyle();
		}

		public bool Value { get; private set; }

		public void SetWithoutNotify(bool value)
		{
			Value = value;
			Restyle();
		}

		public override void Activate() => Set(!Value);

		public override void Adjust(int delta) => Set(delta > 0);

		private void Set(bool value)
		{
			if (value == Value)
			{
				return;
			}

			SetWithoutNotify(value);
			_changed?.Invoke(value);
		}

		protected override void Restyle()
		{
			_button.text = Value ? "ON" : "OFF";
			StyleChoice(_button, Value);
		}
	}

	/// <summary>
	/// One rebindable binding: its label, its key, and Reset. Submit (or a click on the key) starts a rebind; Right moves
	/// to Reset and Left back, so the keyboard and the gamepad can reach both.
	/// </summary>
	public sealed class BindingRowView : OptionRow
	{
		private readonly Label _key;
		private readonly Button _keyButton;
		private readonly Button _reset;
		private readonly Action<BindingRowView> _rebind;
		private readonly Action<string> _resetAction;

		public BindingRowView(BindingRow row, Action<BindingRowView> rebind, Action<string> reset)
			: base("binding-" + row.ActionId.Replace('/', '-') + "-" + row.Slot, row.Label)
		{
			Row = row;
			_rebind = rebind;
			_resetAction = reset;
			AddToClassList("fs-binding-row");
			_keyButton = InnerButton(string.Empty, () => _rebind?.Invoke(this), "fs-binding-key");
			_key = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-value-xs");
			_key.name = name + "-key";
			_keyButton.Add(_key);
			Body.Add(_keyButton);
			_reset = InnerButton("RESET", () => _resetAction?.Invoke(Row.ActionId), "fs-binding-reset");
			Body.Add(_reset);
		}

		public BindingRow Row { get; }

		public string KeyText => _key.text;

		/// <summary>0 while Submit rebinds, 1 while it resets the action.</summary>
		public int Column { get; private set; }

		public bool Pending { get; private set; }

		public void ShowKey(string text)
		{
			Pending = false;
			PaletteRoles.SetText(_key, text);
			Restyle();
		}

		public void ShowPending()
		{
			Pending = true;
			PaletteRoles.SetText(_key, OptionsMenu.PressAKey);
			Restyle();
		}

		public override void Adjust(int delta)
		{
			Column = delta > 0 ? 1 : 0;
			Restyle();
		}

		public override void Activate()
		{
			if (Column == 1)
			{
				_resetAction?.Invoke(Row.ActionId);
			}
			else
			{
				_rebind?.Invoke(this);
			}
		}

		protected override void Restyle()
		{
			StyleChoice(_keyButton, Pending || (Focused && Column == 0));
			_key.style.color = Pending || (Focused && Column == 0) ? Palette.EngagedText : Palette.Text;
			StyleChoice(_reset, Focused && Column == 1);
		}
	}
}
