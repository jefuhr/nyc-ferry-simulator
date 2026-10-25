using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// A list to pick one from (landings, vessels), in the order given. Click an option, or focus the list and use Up
	/// and Down, Page Up and Page Down (five at a time), Home and End. The selected option is filled.
	/// </summary>
	public sealed class OptionList : PickerField
	{
		public const int PageSize = 5;

		private readonly ScrollView _scroll;
		private readonly List<Button> _options = new List<Button>();
		private int _selected = -1;

		public OptionList(string listName)
			: base(listName)
		{
			AddToClassList("fs-options");
			_scroll = new ScrollView(ScrollViewMode.Vertical) { name = listName + "-scroll" };
			_scroll.AddToClassList("fs-options-scroll");
			_scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
			// The scroll bar is not a tab stop of its own: the list is one field.
			_scroll.verticalScroller.slider.focusable = false;
			Add(_scroll);
		}

		/// <summary>Raised when the selection changes, by a click, a key or <see cref="SelectedIndex"/>.</summary>
		public event Action SelectionChanged;

		public int Count => _options.Count;

		/// <summary>The selected option, -1 while the list is empty. Setting it clamps to the list.</summary>
		public int SelectedIndex
		{
			get => _selected;
			set => Select(value);
		}

		public string OptionText(int index) => _options[index].text;

		/// <summary>The button that shows an option, for clicking in tests.</summary>
		public Button Option(int index) => _options[index];

		/// <summary>Replaces the options and selects <paramref name="selected"/> (clamped) without raising <see cref="SelectionChanged"/>.</summary>
		public void SetOptions(IReadOnlyList<string> labels, int selected)
		{
			_scroll.Clear();
			_options.Clear();
			for (var i = 0; i < labels.Count; i++)
			{
				var index = i;
				var option = new Button(() =>
				{
					Select(index);
					Focus();
				})
				{
					text = labels[i],
					name = name + "-" + i,
					focusable = false,
				};
				option.AddToClassList("fs-option");
				_scroll.Add(option);
				_options.Add(option);
			}

			_selected = _options.Count == 0 ? -1 : Mathf.Clamp(selected, 0, _options.Count - 1);
			Restyle();
			ScrollToSelected();
		}

		public void Step(int delta)
		{
			if (_selected >= 0)
			{
				Select(_selected + delta);
			}
		}

		protected override bool HandleKey(KeyCode key)
		{
			switch (key)
			{
				case KeyCode.UpArrow:
					Step(-1);
					return true;
				case KeyCode.DownArrow:
					Step(1);
					return true;
				case KeyCode.PageUp:
					Step(-PageSize);
					return true;
				case KeyCode.PageDown:
					Step(PageSize);
					return true;
				case KeyCode.Home:
					Select(0);
					return true;
				case KeyCode.End:
					Select(Count - 1);
					return true;
				default:
					return false;
			}
		}

		protected override void Restyle()
		{
			StyleScroller(_scroll.verticalScroller);
			for (var i = 0; i < _options.Count; i++)
			{
				StyleChoice(_options[i], i == _selected);
			}
		}

		private void Select(int index)
		{
			if (_options.Count == 0)
			{
				return;
			}

			index = Mathf.Clamp(index, 0, _options.Count - 1);
			if (index == _selected)
			{
				return;
			}

			_selected = index;
			Restyle();
			ScrollToSelected();
			SelectionChanged?.Invoke();
		}

		private void ScrollToSelected()
		{
			if (_selected >= 0 && _scroll.panel != null)
			{
				_scroll.ScrollTo(_options[_selected]);
			}
		}
	}
}
