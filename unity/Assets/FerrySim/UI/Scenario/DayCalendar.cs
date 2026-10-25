using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The scenario days as a calendar: one column per Monday-to-Sunday week, one row per weekday, so the reading
	/// order runs down a week and on to the next. Days without wind data carry <see cref="NoWindMark"/>, explained by
	/// the legend, so the mark never depends on color. Click a day, or focus the calendar and use Up and Down (a day),
	/// Page Up and Page Down (a week), Home and End. A date that is not yyyy-MM-dd still appears, in a column of its own.
	/// </summary>
	public sealed class DayCalendar : PickerField
	{
		public const string NoWindMark = "*";
		public const string OtherTitle = "OTHER";

		private static readonly string[] Weekdays = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
		private static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

		private readonly ScrollView _scroll;
		private readonly List<Button> _cells = new List<Button>();
		private readonly List<Vector2Int> _positions = new List<Vector2Int>();
		private readonly List<string> _titles = new List<string>();
		private readonly List<int[]> _columns = new List<int[]>();
		private readonly List<Label> _columnTitles = new List<Label>();
		private int _selected = -1;

		public DayCalendar(string fieldName)
			: base(fieldName)
		{
			AddToClassList("fs-calendar");
			var body = new VisualElement { pickingMode = PickingMode.Ignore };
			body.AddToClassList("fs-calendar-body");
			var weekdays = new VisualElement { pickingMode = PickingMode.Ignore };
			weekdays.AddToClassList("fs-calendar-weekdays");
			weekdays.Add(Slot("fs-calendar-title"));
			foreach (var day in Weekdays)
			{
				var label = PaletteRoles.Label(day, PaletteRoles.Dim, "fs-caption", "fs-calendar-weekday");
				weekdays.Add(label);
			}

			body.Add(weekdays);
			_scroll = new ScrollView(ScrollViewMode.Horizontal) { name = fieldName + "-scroll" };
			_scroll.AddToClassList("fs-calendar-scroll");
			_scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
			_scroll.horizontalScroller.slider.focusable = false;
			_scroll.contentContainer.AddToClassList("fs-calendar-columns");
			body.Add(_scroll);
			Add(body);
			Legend = PaletteRoles.Label(NoWindMark + " NO WIND DATA: STILL AIR", PaletteRoles.Dim, "fs-caption", "fs-calendar-legend");
			Legend.name = fieldName + "-legend";
			Add(Legend);
		}

		/// <summary>Raised when the selection changes, by a click, a key or <see cref="SelectedIndex"/>.</summary>
		public event Action SelectionChanged;

		/// <summary>Explains <see cref="NoWindMark"/>.</summary>
		public Label Legend { get; }

		/// <summary>Days shown, in the order given.</summary>
		public int Count => _cells.Count;

		/// <summary>The selected day's index in the list given, -1 while empty. Setting it clamps.</summary>
		public int SelectedIndex
		{
			get => _selected;
			set => Select(value);
		}

		public int ColumnCount => _titles.Count;

		/// <summary>"JUL 2025": the month of the first day shown in that week.</summary>
		public string ColumnTitle(int column) => _titles[column];

		/// <summary>Where a day sits: x is its week's column, y its weekday's row (Monday 0).</summary>
		public Vector2Int CellPosition(int index) => _positions[index];

		public string CellText(int index) => _cells[index].text;

		/// <summary>The button that shows a day, for clicking in tests.</summary>
		public Button Cell(int index) => _cells[index];

		/// <summary>Lays out <paramref name="days"/> and selects <paramref name="selected"/> (clamped) without raising <see cref="SelectionChanged"/>.</summary>
		public void SetDays(IReadOnlyList<ScenarioDay> days, int selected)
		{
			_scroll.Clear();
			_cells.Clear();
			_positions.Clear();
			_titles.Clear();
			_columns.Clear();
			_columnTitles.Clear();
			var weekColumns = new SortedDictionary<DateTime, List<int>>();
			var others = new List<int>();
			var dates = new DateTime[days.Count];
			for (var i = 0; i < days.Count; i++)
			{
				if (TryParseDate(days[i].Date, out dates[i]))
				{
					var monday = dates[i].AddDays(-Row(dates[i]));
					if (!weekColumns.TryGetValue(monday, out var members))
					{
						members = new List<int>();
						weekColumns.Add(monday, members);
					}

					members.Add(i);
				}
				else
				{
					others.Add(i);
				}

				var index = i;
				var cell = new Button(() =>
				{
					Select(index);
					Focus();
				})
				{
					text = CellLabel(days[i], dates[i]),
					name = name + "-" + days[i].Date,
					focusable = false,
				};
				cell.AddToClassList("fs-calendar-cell");
				_cells.Add(cell);
				_positions.Add(new Vector2Int(-1, -1));
			}

			foreach (var week in weekColumns.Values)
			{
				var slots = NewColumn(Title(dates[week[0]]));
				foreach (var index in week)
				{
					var row = Row(dates[index]);
					if (slots[row] < 0)
					{
						Place(slots, index, row);
					}
					else
					{
						others.Add(index);
					}
				}
			}

			// Unparseable dates (and any duplicate) go in OTHER columns, seven to a column, in list order.
			others.Sort();
			for (var i = 0; i < others.Count; i++)
			{
				var slots = i % Weekdays.Length == 0 ? NewColumn(OtherTitle) : _columns[_columns.Count - 1];
				Place(slots, others[i], i % Weekdays.Length);
			}

			foreach (var slots in _columns)
			{
				var column = new VisualElement { pickingMode = PickingMode.Ignore };
				column.AddToClassList("fs-calendar-column");
				var title = PaletteRoles.Label(_titles[_columns.IndexOf(slots)], PaletteRoles.Dim, "fs-caption", "fs-calendar-title");
				_columnTitles.Add(title);
				column.Add(title);
				foreach (var index in slots)
				{
					column.Add(index >= 0 ? _cells[index] : Slot("fs-calendar-cell", "fs-calendar-cell--empty"));
				}

				_scroll.Add(column);
			}

			_selected = _cells.Count == 0 ? -1 : Mathf.Clamp(selected, 0, _cells.Count - 1);
			Restyle();
		}

		/// <summary>Moves by days in the list's order: down a week, then on to the next.</summary>
		public void StepDays(int delta)
		{
			if (_selected >= 0)
			{
				Select(_selected + delta);
			}
		}

		/// <summary>Moves by whole weeks: the same weekday in the next or previous column, or the nearest day shown there.</summary>
		public void StepWeeks(int delta)
		{
			if (_selected < 0)
			{
				return;
			}

			var position = _positions[_selected];
			var column = position.x + delta;
			if (column < 0 || column >= _columns.Count)
			{
				return;
			}

			var slots = _columns[column];
			for (var distance = 0; distance < Weekdays.Length; distance++)
			{
				foreach (var row in new[] { position.y - distance, position.y + distance })
				{
					if (row >= 0 && row < slots.Length && slots[row] >= 0)
					{
						Select(slots[row]);
						return;
					}
				}
			}
		}

		protected override bool HandleKey(KeyCode key)
		{
			switch (key)
			{
				case KeyCode.UpArrow:
					StepDays(-1);
					return true;
				case KeyCode.DownArrow:
					StepDays(1);
					return true;
				case KeyCode.PageUp:
					StepWeeks(-1);
					return true;
				case KeyCode.PageDown:
					StepWeeks(1);
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
			// The week titles are built with the days, after the palette was applied to the tree.
			foreach (var title in _columnTitles)
			{
				title.style.color = Palette.TextDim;
			}

			StyleScroller(_scroll.horizontalScroller);
			for (var i = 0; i < _cells.Count; i++)
			{
				StyleChoice(_cells[i], i == _selected);
			}
		}

		/// <summary>The date in yyyy-MM-dd, the contract's form, read with the invariant culture.</summary>
		public static bool TryParseDate(string text, out DateTime date) =>
			DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

		private static int Row(DateTime date) => ((int)date.DayOfWeek + 6) % 7;

		private static string Title(DateTime date) => Months[date.Month - 1] + " " + date.Year.ToString(CultureInfo.InvariantCulture);

		private static string CellLabel(ScenarioDay day, DateTime date) =>
			(date == default ? day.Date : date.Day.ToString(CultureInfo.InvariantCulture)) + (day.WindMissing ? NoWindMark : string.Empty);

		private int[] NewColumn(string title)
		{
			var slots = new int[Weekdays.Length];
			for (var i = 0; i < slots.Length; i++)
			{
				slots[i] = -1;
			}

			_columns.Add(slots);
			_titles.Add(title);
			return slots;
		}

		private void Place(int[] slots, int index, int row)
		{
			slots[row] = index;
			_positions[index] = new Vector2Int(_columns.IndexOf(slots), row);
		}

		private void Select(int index)
		{
			if (_cells.Count == 0)
			{
				return;
			}

			index = Mathf.Clamp(index, 0, _cells.Count - 1);
			if (index == _selected)
			{
				return;
			}

			_selected = index;
			Restyle();
			if (_scroll.panel != null)
			{
				_scroll.ScrollTo(_cells[_selected]);
			}

			SelectionChanged?.Invoke();
		}

		private static VisualElement Slot(params string[] classes)
		{
			var slot = new VisualElement { pickingMode = PickingMode.Ignore };
			foreach (var className in classes)
			{
				slot.AddToClassList(className);
			}

			return slot;
		}
	}
}
