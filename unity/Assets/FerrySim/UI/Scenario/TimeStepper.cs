using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The start time: local time on the scenario day, 24 h, in 5-minute steps from 00:00 to 23:55, with the day's
	/// zone beside it. It does not wrap past midnight, which would quietly change the day. Click the step buttons, or
	/// focus it and use Up and Down (5 minutes), Page Up and Page Down (an hour), Home and End.
	/// </summary>
	public sealed class TimeStepper : PickerField
	{
		public const int StepMinutes = 5;
		public const int LastMinutes = 24 * 60 - StepMinutes;

		private readonly Label _value;
		private readonly Label _zone;
		private readonly Button[] _buttons;
		private int _minutes;

		public TimeStepper(string fieldName)
			: base(fieldName)
		{
			AddToClassList("fs-time");
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList("fs-time-row");
			_value = PaletteRoles.Label("00:00", PaletteRoles.Text, "fs-value-l", "fs-time-value");
			_value.name = fieldName + "-value";
			row.Add(_value);
			_zone = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-unit", "fs-unit--spaced");
			_zone.name = fieldName + "-zone";
			row.Add(_zone);
			Add(row);
			var steps = new VisualElement { pickingMode = PickingMode.Ignore };
			steps.AddToClassList("fs-time-steps");
			_buttons = new[]
			{
				StepButton(steps, "-1 h", -60, fieldName + "-hour-down"),
				StepButton(steps, "-5 min", -StepMinutes, fieldName + "-down"),
				StepButton(steps, "+5 min", StepMinutes, fieldName + "-up"),
				StepButton(steps, "+1 h", 60, fieldName + "-hour-up"),
			};
			Add(steps);
		}

		/// <summary>Raised when the time changes, by a click, a key or <see cref="Minutes"/>.</summary>
		public event Action MinutesChanged;

		/// <summary>Minutes after local midnight. Setting it snaps down to the 5-minute step and clamps to 00:00..23:55.</summary>
		public int Minutes
		{
			get => _minutes;
			set
			{
				var minutes = Mathf.Clamp(value, 0, LastMinutes) / StepMinutes * StepMinutes;
				if (minutes == _minutes)
				{
					return;
				}

				_minutes = minutes;
				PaletteRoles.SetText(_value, Format(minutes));
				MinutesChanged?.Invoke();
			}
		}

		/// <summary>"09:05".</summary>
		public string Text => _value.text;

		/// <summary>The selected day's zone (EDT or EST), shown beside the time.</summary>
		public string Zone
		{
			get => _zone.text;
			set => PaletteRoles.SetText(_zone, value ?? string.Empty);
		}

		/// <summary>The step buttons in order: -1 h, -5 min, +5 min, +1 h.</summary>
		public Button StepButtonAt(int index) => _buttons[index];

		public void Step(int minutes) => Minutes = _minutes + minutes;

		/// <summary>Sets the time without raising <see cref="MinutesChanged"/>, when the picker restores a choice.</summary>
		public void SetWithoutNotify(int minutes)
		{
			_minutes = Mathf.Clamp(minutes, 0, LastMinutes) / StepMinutes * StepMinutes;
			PaletteRoles.SetText(_value, Format(_minutes));
		}

		public static string Format(int minutes) =>
			(minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);

		protected override bool HandleKey(KeyCode key)
		{
			switch (key)
			{
				case KeyCode.UpArrow:
					Step(StepMinutes);
					return true;
				case KeyCode.DownArrow:
					Step(-StepMinutes);
					return true;
				case KeyCode.PageUp:
					Step(60);
					return true;
				case KeyCode.PageDown:
					Step(-60);
					return true;
				case KeyCode.Home:
					Minutes = 0;
					return true;
				case KeyCode.End:
					Minutes = LastMinutes;
					return true;
				default:
					return false;
			}
		}

		protected override void Restyle()
		{
			foreach (var button in _buttons)
			{
				DebriefScreen.StyleButton(button, Palette);
			}
		}

		private Button StepButton(VisualElement parent, string text, int minutes, string buttonName)
		{
			var button = new Button(() =>
			{
				Step(minutes);
				Focus();
			})
			{
				text = text,
				name = buttonName,
				focusable = false,
			};
			button.AddToClassList("fs-button");
			button.AddToClassList("fs-time-step");
			parent.Add(button);
			return button;
		}
	}
}
