using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Base for the scenario picker's fields (landing, day, time, vessel): one tab stop each, a focus ring, and the
	/// keys they share. A focused field changes its value with Up and Down (and Page Up, Page Down, Home and End) and
	/// asks to start the run with Enter. It never uses Left, Right or letters, which the helm uses
	/// (Input/README.md), and it swallows the UI's directional navigation, which the Input System's default UI
	/// actions also raise for W, A, S and D, the lever keys: those never move the focus or change a value here.
	/// Tab and Shift+Tab move between fields as usual.
	/// </summary>
	public abstract class PickerField : VisualElement, IPaletteAware
	{
		protected PickerField(string fieldName)
		{
			name = fieldName;
			focusable = true;
			tabIndex = 0;
			pickingMode = PickingMode.Position;
			AddToClassList("fs-picker-field");
			RegisterCallback<KeyDownEvent>(OnKeyDown);
			RegisterCallback<NavigationMoveEvent>(OnNavigationMove);
			RegisterCallback<FocusInEvent>(_ => SetFocused(true));
			RegisterCallback<FocusOutEvent>(_ => SetFocused(false));
		}

		/// <summary>Enter was pressed in this field.</summary>
		public event Action SubmitRequested;

		/// <summary>True while the field has the keyboard focus; its border shows it.</summary>
		public bool Focused { get; private set; }

		protected InstrumentPalette Palette { get; private set; } = InstrumentPalette.Day;

		public void ApplyPalette(InstrumentPalette palette)
		{
			Palette = palette ?? InstrumentPalette.Day;
			RestyleFocus();
			Restyle();
		}

		/// <summary>Changes the value for a key; true when the key was used.</summary>
		protected abstract bool HandleKey(KeyCode key);

		/// <summary>Colors the field's content from <see cref="Palette"/>.</summary>
		protected abstract void Restyle();

		/// <summary>Styles an option or cell button: filled while selected, like an engaged gear, so it never depends on color alone.</summary>
		protected void StyleChoice(Button button, bool selected)
		{
			button.style.backgroundColor = selected ? Palette.Engaged : Palette.Face;
			button.style.color = selected ? Palette.EngagedText : Palette.Text;
			PaletteRoles.SetBorderColor(button, selected ? Palette.Engaged : Palette.Bezel);
		}

		/// <summary>A scroll bar in palette colors with no arrow buttons, so nothing shows the theme's grey light at night.</summary>
		protected void StyleScroller(Scroller scroller)
		{
			scroller.lowButton.style.display = DisplayStyle.None;
			scroller.highButton.style.display = DisplayStyle.None;
			scroller.style.backgroundColor = Palette.Face;
			PaletteRoles.SetBorderColor(scroller, Palette.Face);
			var tracker = scroller.slider.Q(className: BaseSlider<float>.trackerUssClassName);
			if (tracker != null)
			{
				tracker.style.backgroundColor = Palette.Face;
				PaletteRoles.SetBorderColor(tracker, Palette.Bezel);
			}

			var dragger = scroller.slider.Q(className: BaseSlider<float>.draggerUssClassName);
			if (dragger != null)
			{
				dragger.style.backgroundColor = Palette.Bezel;
				PaletteRoles.SetBorderColor(dragger, Palette.Scale);
			}
		}

		private void OnKeyDown(KeyDownEvent evt)
		{
			var used = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
			if (used)
			{
				SubmitRequested?.Invoke();
			}
			else
			{
				used = HandleKey(evt.keyCode);
			}

			if (used)
			{
				Consume(evt);
			}
		}

		private void OnNavigationMove(NavigationMoveEvent evt)
		{
			if (evt.direction == NavigationMoveEvent.Direction.Up || evt.direction == NavigationMoveEvent.Direction.Down ||
				evt.direction == NavigationMoveEvent.Direction.Left || evt.direction == NavigationMoveEvent.Direction.Right)
			{
				Consume(evt);
			}
		}

		private void Consume(EventBase evt)
		{
			evt.StopPropagation();
			focusController?.IgnoreEvent(evt);
		}

		private void SetFocused(bool focused)
		{
			Focused = focused;
			RestyleFocus();
		}

		private void RestyleFocus() => PaletteRoles.SetBorderColor(this, Focused ? Palette.Text : Palette.Bezel);
	}
}
