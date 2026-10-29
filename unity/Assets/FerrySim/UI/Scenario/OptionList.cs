using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// A list to pick one from (landings, berths, vessels), in the order given. Click an option, or focus the list and use
	/// Up and Down, Page Up and Page Down (five at a time), Home and End. The selected option is filled, and the list
	/// scrolls to keep it in view. An option can carry a note, shown under its name in the caption size (a landing's
	/// evidence or crew restriction); without notes the rows are one line, as before. <see cref="Filter"/> narrows the
	/// list to the options whose name contains a text, for a search box: the keys move among the options shown, and the
	/// selection moves to the first one shown when the selected option is hidden, so typing then Enter starts there.
	/// Indices are always positions in the full list, so a choice keeps its stable id whatever the filter.
	/// </summary>
	public sealed class OptionList : PickerField
	{
		public const int PageSize = 5;

		private readonly ScrollView _scroll;
		private readonly List<Button> _options = new List<Button>();
		private readonly List<Label> _names = new List<Label>();
		private readonly List<Label> _notes = new List<Label>();
		private readonly List<bool> _visible = new List<bool>();
		private int _selected = -1;
		private string _filter = string.Empty;

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

		/// <summary>Raised when the selection changes, by a click, a key, a filter or <see cref="SelectedIndex"/>.</summary>
		public event Action SelectionChanged;

		/// <summary>Every option, shown or hidden by the filter.</summary>
		public int Count => _options.Count;

		/// <summary>The options the filter shows.</summary>
		public int VisibleCount
		{
			get
			{
				var count = 0;
				foreach (var visible in _visible) if (visible) count++;
				return count;
			}
		}

		/// <summary>The selected option, -1 while the list is empty. Setting it clamps to the list.</summary>
		public int SelectedIndex
		{
			get => _selected;
			set => Select(value);
		}

		/// <summary>The text the list is narrowed to; empty shows every option.</summary>
		public string FilterText => _filter;

		public string OptionText(int index) => _names[index] != null ? _names[index].text : _options[index].text;

		/// <summary>The option's note, or empty.</summary>
		public string OptionNote(int index) => _notes[index] != null ? _notes[index].text : string.Empty;

		/// <summary>True when the filter shows the option.</summary>
		public bool IsVisible(int index) => _visible[index];

		/// <summary>The button that shows an option, for clicking in tests.</summary>
		public Button Option(int index) => _options[index];

		/// <summary>The scroll view the options sit in, for checking what is in view.</summary>
		public ScrollView Scroll => _scroll;

		/// <summary>Replaces the options and selects <paramref name="selected"/> (clamped) without raising <see cref="SelectionChanged"/>. The filter is cleared.</summary>
		public void SetOptions(IReadOnlyList<string> labels, int selected) => SetOptions(labels, null, selected);

		/// <summary>
		/// As <see cref="SetOptions(IReadOnlyList{string}, int)"/>, with a note under each option whose entry in
		/// <paramref name="notes"/> is not empty (<paramref name="notes"/> may be null).
		/// </summary>
		public void SetOptions(IReadOnlyList<string> labels, IReadOnlyList<string> notes, int selected)
		{
			_scroll.Clear();
			_options.Clear();
			_names.Clear();
			_notes.Clear();
			_visible.Clear();
			_filter = string.Empty;
			for (var i = 0; i < labels.Count; i++)
			{
				var index = i;
				var note = notes != null && i < notes.Count ? notes[i] : null;
				var option = new Button(() =>
				{
					Select(index);
					Focus();
				})
				{
					name = name + "-" + i,
					focusable = false,
				};
				option.AddToClassList("fs-option");
				if (string.IsNullOrEmpty(note))
				{
					option.text = labels[i];
					_names.Add(null);
					_notes.Add(null);
				}
				else
				{
					// Two lines: the name, and the note in the caption size, both clipped to the row.
					option.AddToClassList("fs-option-noted");
					var nameLabel = new Label(labels[i]) { pickingMode = PickingMode.Ignore };
					nameLabel.AddToClassList("fs-option-name");
					var noteLabel = new Label(note) { pickingMode = PickingMode.Ignore };
					noteLabel.AddToClassList("fs-option-note");
					option.Add(nameLabel);
					option.Add(noteLabel);
					_names.Add(nameLabel);
					_notes.Add(noteLabel);
				}

				_scroll.Add(option);
				_options.Add(option);
				_visible.Add(true);
			}

			_selected = _options.Count == 0 ? -1 : Mathf.Clamp(selected, 0, _options.Count - 1);
			Restyle();
			ScrollToSelected();
		}

		/// <summary>
		/// Shows only the options whose name contains <paramref name="text"/> (case-insensitive; empty or null shows all).
		/// When the selected option is hidden and another is shown, the first shown one is selected and
		/// <see cref="SelectionChanged"/> is raised; when nothing is shown, the selection stays.
		/// </summary>
		public void Filter(string text)
		{
			_filter = text ?? string.Empty;
			var trimmed = _filter.Trim();
			var first = -1;
			for (var i = 0; i < _options.Count; i++)
			{
				var shown = trimmed.Length == 0 || OptionText(i).IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;
				_visible[i] = shown;
				_options[i].style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
				if (shown && first < 0) first = i;
			}

			if (_selected >= 0 && !_visible[_selected] && first >= 0)
			{
				Select(first);
			}
			else
			{
				ScrollToSelected();
			}
		}

		/// <summary>Moves the selection <paramref name="delta"/> shown options on (or back), skipping hidden ones.</summary>
		public void Step(int delta)
		{
			if (_selected < 0 || delta == 0)
			{
				return;
			}

			var index = _selected;
			var steps = Math.Abs(delta);
			var direction = Math.Sign(delta);
			var last = index;
			while (steps > 0)
			{
				index += direction;
				if (index < 0 || index >= _options.Count) break;
				if (!_visible[index]) continue;
				last = index;
				steps--;
			}

			Select(last);
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
					Select(FirstVisible(0, 1));
					return true;
				case KeyCode.End:
					Select(FirstVisible(Count - 1, -1));
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
				if (_names[i] != null)
				{
					_names[i].style.color = i == _selected ? Palette.EngagedText : Palette.Text;
					_notes[i].style.color = i == _selected ? Palette.EngagedText : Palette.TextDim;
				}
			}
		}

		private int FirstVisible(int from, int direction)
		{
			for (var i = from; i >= 0 && i < _options.Count; i += direction)
			{
				if (_visible[i]) return i;
			}

			return _selected;
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

		/// <summary>Scrolls the selected option into view now, and again after the next layout, since a list just filled or filtered has no geometry yet.</summary>
		private void ScrollToSelected()
		{
			if (_selected < 0 || _scroll.panel == null)
			{
				return;
			}

			var selected = _selected;
			_scroll.ScrollTo(_options[selected]);
			_scroll.schedule.Execute(() =>
			{
				if (_selected == selected && selected < _options.Count && _visible[selected])
				{
					_scroll.ScrollTo(_options[selected]);
				}
			});
		}
	}
}
