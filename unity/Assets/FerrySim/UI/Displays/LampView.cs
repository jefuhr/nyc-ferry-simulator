using System.Collections.Generic;
using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// One annunciator lamp following one alarm through the sequence of contract C1. Lit, it fills with its colour (the
	/// alarm colour unless the skin says otherwise) and its caption takes the paired text colour; dark, it is an
	/// outlined face. A lamp whose alarm the readout does not define stays dark.
	/// </summary>
	public sealed class LampView : SkinElementView
	{
		private readonly string _alarmId;
		private readonly Label _label;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private int _alarm = -1;
		private bool _styledLit = true;

		public LampView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			_alarmId = spec.ResolveAlarm(side);
			var rect = spec.Rect;
			Border(this, 2f, Color.clear);
			var text = spec.ResolveText(side) ?? AlarmListView.Caption(_alarmId, null);
			_label = MakeLabel(this, text, new Rect(0, 0, rect.width - 4f, rect.height - 4f), Mathf.Min(rect.height * 0.42f, rect.width * 0.14f), TextAnchor.MiddleCenter, bold: true);
		}

		public bool Lit { get; private set; }

		public LampState State { get; private set; } = LampState.Off;

		public string LabelText => _label.text;

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette;
			_styledLit = !Lit;
			Restyle();
		}

		internal override void Bind(IInstrumentReadout readout) => _alarm = FindAlarm(readout, _alarmId);

		internal override void RefreshLamps(IInstrumentReadout readout, double time)
		{
			if (readout == null)
			{
				State = LampState.Off;
			}
			else if (_alarm < 0)
			{
				State = readout.LampTest ? LampState.Test : LampState.Off;
			}
			else
			{
				State = AlarmLamp.State(readout.Phase(_alarm), readout.LampTest);
			}

			Lit = AlarmLamp.IsLit(State, time);
			Restyle();
		}

		private void Restyle()
		{
			if (_styledLit == Lit)
			{
				return;
			}

			_styledLit = Lit;
			var colour = ColourSpec(PaletteRole.Alarm);
			style.backgroundColor = Lit ? colour.Resolve(_palette) : _palette.Face;
			PaletteRoles.SetBorderColor(this, Lit ? colour.Resolve(_palette) : _palette.Bezel);
			_label.style.color = Lit ? colour.TextOn(_palette) : _palette.TextDim;
		}
	}

	/// <summary>One line of an alarm list: a group heading, or an alarm with its lamp and its state in words.</summary>
	public sealed class AlarmListRow
	{
		internal AlarmListRow(bool isHeader, string text, int alarm, VisualElement root, VisualElement lamp, Label caption, Label status)
		{
			IsHeader = isHeader;
			Text = text;
			Alarm = alarm;
			Root = root;
			Lamp = lamp;
			Caption = caption;
			StatusLabel = status;
		}

		public bool IsHeader { get; }

		public string Text { get; }

		/// <summary>The alarm's index in the readout; -1 for a heading.</summary>
		public int Alarm { get; }

		public bool Lit { get; internal set; }

		public LampState State { get; internal set; } = LampState.Off;

		/// <summary>ALARM, ACK, CLEARED, TEST or empty: so the state never relies on colour or flashing alone.</summary>
		public string Status => StatusLabel?.text ?? "";

		public VisualElement Root { get; }

		/// <summary>The lamp square; null for a heading.</summary>
		public VisualElement Lamp { get; }

		internal Label Caption { get; }

		internal Label StatusLabel { get; }

		internal bool StyledLit { get; set; }
	}

	/// <summary>
	/// Every alarm the readout defines (or those of one group), headed and ordered by group (steering, engine,
	/// navigation, hull, as <see cref="AlarmGroup"/> orders them) and in the readout's order within a group. The rows
	/// come from the readout, not the skin, so an alarm added to the vessel data appears without a skin change. Rows are
	/// one-eighth of the list's height, smaller when more are needed; the skin's text shows when the vessel defines no
	/// alarm the list would show.
	/// </summary>
	public sealed class AlarmListView : SkinElementView
	{
		private const int MinimumRows = 8;

		private readonly List<AlarmListRow> _rows = new List<AlarmListRow>();
		private readonly Label _empty;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private IInstrumentReadout _bound;
		private bool _hasBound;

		public AlarmListView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			var rect = spec.Rect;
			EmptyText = spec.ResolveText(side) ?? "";
			_empty = MakeLabel(this, EmptyText, new Rect(0, 0, rect.width, Mathf.Min(rect.height, rect.height / MinimumRows * 2f)), Mathf.Min(rect.height / MinimumRows * 0.55f, 22f), TextAnchor.MiddleCenter);
			_empty.style.display = DisplayStyle.None;
		}

		public IReadOnlyList<AlarmListRow> Rows => _rows;

		public string EmptyText { get; }

		/// <summary>True when the readout defines no alarm this list shows and the skin's text says so.</summary>
		public bool EmptyTextVisible => _empty.style.display.value == DisplayStyle.Flex && EmptyText.Length > 0;

		/// <summary>An alarm id as a caption: dot and dash become spaces, upper case, the group word dropped when it heads the row.</summary>
		public static string Caption(string id, AlarmGroup? heading)
		{
			if (string.IsNullOrEmpty(id))
			{
				return "";
			}

			var text = id;
			var dot = id.IndexOf('.');
			if (heading.HasValue && dot > 0 && dot < id.Length - 1 && string.Equals(id.Substring(0, dot), heading.Value.ToString(), System.StringComparison.OrdinalIgnoreCase))
			{
				text = id.Substring(dot + 1);
			}

			return text.Replace('.', ' ').Replace('-', ' ').ToUpperInvariant();
		}

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette;
			_empty.style.color = palette.TextDim;
			foreach (var row in _rows)
			{
				row.StyledLit = !row.Lit;
				Restyle(row);
			}
		}

		internal override void Bind(IInstrumentReadout readout)
		{
			if (readout == null)
			{
				// No data: keep the rows of the last vessel, dark; nothing claims the vessel has no alarms.
				_bound = null;
				return;
			}

			if (_hasBound && ReferenceEquals(readout, _bound) && CountShown(readout) == CountRows())
			{
				return;
			}

			_bound = readout;
			_hasBound = true;
			Rebuild(readout);
		}

		internal override void RefreshLamps(IInstrumentReadout readout, double time)
		{
			for (var i = 0; i < _rows.Count; i++)
			{
				var row = _rows[i];
				if (row.IsHeader)
				{
					continue;
				}

				var state = readout == null || row.Alarm >= readout.AlarmCount ? LampState.Off : AlarmLamp.State(readout.Phase(row.Alarm), readout.LampTest);
				var lit = AlarmLamp.IsLit(state, time);
				if (state != row.State)
				{
					row.State = state;
					SetText(row.StatusLabel, AlarmLamp.Word(state));
					row.StatusLabel.style.color = state == LampState.Off ? _palette.TextDim : _palette.Text;
				}

				row.Lit = lit;
				Restyle(row);
			}
		}

		private int CountShown(IInstrumentReadout readout)
		{
			var count = 0;
			for (var i = 0; i < readout.AlarmCount; i++)
			{
				if (!Spec.Group.HasValue || readout.Group(i) == Spec.Group.Value)
				{
					count++;
				}
			}

			return count;
		}

		private int CountRows()
		{
			var count = 0;
			for (var i = 0; i < _rows.Count; i++)
			{
				if (!_rows[i].IsHeader)
				{
					count++;
				}
			}

			return count;
		}

		private void Rebuild(IInstrumentReadout readout)
		{
			foreach (var row in _rows)
			{
				row.Root.RemoveFromHierarchy();
			}

			_rows.Clear();
			var lines = new List<(bool header, string text, int alarm, AlarmGroup group)>();
			foreach (AlarmGroup group in System.Enum.GetValues(typeof(AlarmGroup)))
			{
				if (Spec.Group.HasValue && Spec.Group.Value != group)
				{
					continue;
				}

				var headed = false;
				for (var i = 0; i < readout.AlarmCount; i++)
				{
					if (readout.Group(i) != group)
					{
						continue;
					}

					if (!headed)
					{
						lines.Add((true, group.ToString().ToUpperInvariant(), -1, group));
						headed = true;
					}

					lines.Add((false, Caption(readout.AlarmId(i), group), i, group));
				}
			}

			var rect = Spec.Rect;
			var rowHeight = rect.height / Mathf.Max(MinimumRows, lines.Count);
			for (var i = 0; i < lines.Count; i++)
			{
				var line = lines[i];
				var root = new VisualElement { name = line.header ? "group-" + line.text.ToLowerInvariant() : "alarm-" + line.alarm, pickingMode = PickingMode.Ignore };
				Place(root, new Rect(0, i * rowHeight, rect.width, rowHeight));
				Add(root);
				var font = rowHeight * 0.5f;
				if (line.header)
				{
					var heading = MakeLabel(root, line.text, new Rect(0, 0, rect.width, rowHeight), font * 0.9f, TextAnchor.LowerLeft, bold: true);
					_rows.Add(new AlarmListRow(true, line.text, -1, root, null, heading, null));
					continue;
				}

				var lampSize = rowHeight * 0.62f;
				var lamp = new VisualElement { name = "lamp", pickingMode = PickingMode.Ignore };
				Place(lamp, new Rect(rowHeight * 0.4f, (rowHeight - lampSize) * 0.5f, lampSize * 1.6f, lampSize));
				Border(lamp, 2f, Color.clear);
				root.Add(lamp);
				var left = rowHeight * 0.4f + lampSize * 1.6f + rowHeight * 0.4f;
				var statusWidth = rowHeight * 3.2f;
				var caption = MakeLabel(root, line.text, new Rect(left, 0, Mathf.Max(1f, rect.width - left - statusWidth), rowHeight), font, TextAnchor.MiddleLeft, primary: true);
				var status = MakeLabel(root, "", new Rect(rect.width - statusWidth, 0, statusWidth, rowHeight), font * 0.9f, TextAnchor.MiddleRight, bold: true);
				var row = new AlarmListRow(false, line.text, line.alarm, root, lamp, caption, status) { StyledLit = true };
				_rows.Add(row);
			}

			_empty.style.display = CountRows() == 0 ? DisplayStyle.Flex : DisplayStyle.None;
			ApplyPalette(_palette);
		}

		private void Restyle(AlarmListRow row)
		{
			if (row.IsHeader)
			{
				row.Caption.style.color = _palette.TextDim;
				return;
			}

			if (row.StyledLit == row.Lit)
			{
				return;
			}

			row.StyledLit = row.Lit;
			var colour = ColourSpec(PaletteRole.Alarm);
			row.Lamp.style.backgroundColor = row.Lit ? colour.Resolve(_palette) : _palette.Face;
			PaletteRoles.SetBorderColor(row.Lamp, row.Lit ? colour.Resolve(_palette) : _palette.Bezel);
			row.Caption.style.color = _palette.Text;
			row.StatusLabel.style.color = row.State == LampState.Off ? _palette.TextDim : _palette.Text;
		}
	}
}
