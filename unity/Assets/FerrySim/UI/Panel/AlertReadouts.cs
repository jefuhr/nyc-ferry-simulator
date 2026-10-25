using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Depth under keel with its shallow-water alarm. While the alarm is unacknowledged the block flashes
	/// between alarm-filled and alarm-outlined; once acknowledged (click the block) it stays filled until
	/// the depth recovers. This is the only flashing element on the panel. The threshold line marks the
	/// 5 ft default PLACEHOLDER until crew confirm it. Beside the caption, OFF DEPTH GRID says when the depth
	/// shown is the environment pack's stand-in for water outside its depth grids rather than a surveyed depth.
	/// </summary>
	public sealed class DepthReadout : VisualElement, IPaletteAware
	{
		private readonly Label _caption;
		private readonly Label _threshold;
		private readonly Label _hint;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private bool _lit;
		private AlarmState _state;
		private double _shownThreshold = double.NaN;
		private DistanceUnit _shownThresholdUnit;

		public DepthReadout()
		{
			name = "depth";
			AddToClassList("fs-depth");
			AddToClassList("fs-alert");
			var header = new VisualElement { pickingMode = PickingMode.Ignore };
			header.AddToClassList("fs-depth-header");
			_caption = PaletteRoles.Label("DEPTH UNDER KEEL", null, "fs-caption");
			header.Add(_caption);
			Grid = new CoverageTag("OFF DEPTH GRID") { name = "depth-grid" };
			header.Add(Grid);
			Add(header);
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList("fs-value-row");
			Value = PaletteRoles.Label(InstrumentFormat.NoValue, null, "fs-value-m");
			Value.name = "depth-value";
			Unit = PaletteRoles.Label("ft", null, "fs-unit");
			Unit.name = "depth-unit";
			row.Add(Value);
			row.Add(Unit);
			Add(row);
			_threshold = PaletteRoles.Label(string.Empty, null, "fs-caption");
			_threshold.name = "depth-threshold";
			Add(_threshold);
			_hint = PaletteRoles.Label("SHALLOW - CLICK TO ACKNOWLEDGE", null, "fs-caption", "fs-alert-hint");
			Add(_hint);
			Restyle();
		}

		public Label Value { get; }

		public Label Unit { get; }

		/// <summary>The outside-the-depth-grid warning, from the environment's depth misses.</summary>
		public CoverageTag Grid { get; }

		public string ThresholdText => _threshold.text;

		/// <summary>Whether the alarm fill is showing this frame.</summary>
		public bool AlarmLit => _lit;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			Restyle();
		}

		public void ShowValue(string value, DistanceUnit unit)
		{
			PaletteRoles.SetText(Value, value);
			PaletteRoles.SetText(Unit, DisplayUnits.Symbol(unit));
		}

		public void ShowGrid(CoverageIndication indication) => Grid.Show(indication);

		/// <summary>The alarm setting line; rebuilt only when the threshold or unit changes, so it costs nothing per frame.</summary>
		public void ShowSetting(double thresholdMetres, DistanceUnit unit)
		{
			if (thresholdMetres.Equals(_shownThreshold) && unit == _shownThresholdUnit)
			{
				return;
			}

			_shownThreshold = thresholdMetres;
			_shownThresholdUnit = unit;
			var text = $"ALARM BELOW {InstrumentFormat.Distance(thresholdMetres, unit)} {DisplayUnits.Symbol(unit)}";
			PaletteRoles.SetText(_threshold, DepthAlarm.IsDefaultThreshold(thresholdMetres) ? text + " · PLACEHOLDER" : text);
		}

		public void SetAlarm(AlarmState state, bool lit)
		{
			if (state != _state || lit != _lit)
			{
				_state = state;
				_lit = lit;
				Restyle();
			}
		}

		private void Restyle()
		{
			var alarm = _state != AlarmState.Normal;
			var text = _lit ? _palette.AlarmText : _palette.Text;
			var dim = _lit ? _palette.AlarmText : _palette.TextDim;
			style.backgroundColor = _lit ? _palette.Alarm : _palette.Face;
			PaletteRoles.SetBorderColor(this, alarm ? _palette.Alarm : _palette.Bezel);
			Value.style.color = text;
			Unit.style.color = dim;
			_caption.style.color = dim;
			_threshold.style.color = dim;
			_hint.style.color = text;
			_hint.style.display = _state == AlarmState.Active ? DisplayStyle.Flex : DisplayStyle.None;
		}
	}

	/// <summary>
	/// Contact indicator. Lit (caution fill) while the latest physics step touches, outlined with the
	/// episode's peak for a few seconds afterwards so a one-step touch is not missed, dim otherwise, and
	/// "---" when the source cannot say. Never flashes: touching the fenders at a landing is normal.
	/// </summary>
	public sealed class ContactReadout : VisualElement, IPaletteAware
	{
		private readonly Label _caption;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private double _shownKilonewtons = double.NaN;

		public ContactReadout()
		{
			name = "contact";
			AddToClassList("fs-contact");
			AddToClassList("fs-alert");
			_caption = PaletteRoles.Label("CONTACT", null, "fs-caption", "fs-strong");
			Add(_caption);
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList("fs-value-row");
			Value = PaletteRoles.Label("NONE", null, "fs-value-s");
			Value.name = "contact-value";
			row.Add(Value);
			Add(row);
			Restyle();
		}

		public Label Value { get; }

		public ContactIndication Indication { get; private set; }

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			Restyle();
		}

		public void Show(ContactMonitor monitor)
		{
			var indication = monitor.Indication;
			var newtons = indication == ContactIndication.Live ? monitor.ForceNewtons : indication == ContactIndication.Recent ? monitor.PeakNewtons : double.NaN;
			// The force text is rebuilt only when the indication or the value at its displayed resolution changes.
			var kilonewtons = InstrumentFormat.IsFinite(newtons) ? DisplayUnits.Kilonewtons(Math.Abs(newtons)) : double.NaN;
			kilonewtons = Math.Round(kilonewtons, kilonewtons < 9.95 ? 1 : 0, MidpointRounding.AwayFromZero);
			if (indication == Indication && kilonewtons.Equals(_shownKilonewtons))
			{
				return;
			}

			_shownKilonewtons = kilonewtons;
			PaletteRoles.SetText(Value,
				indication == ContactIndication.Live ? (ReadoutStrings.Contact(kilonewtons, DisplayUnits.Kilonewtons(Math.Abs(newtons)) < 9.95, false) ?? InstrumentFormat.Force(newtons) + " kN") :
				indication == ContactIndication.Recent ? (ReadoutStrings.Contact(kilonewtons, DisplayUnits.Kilonewtons(Math.Abs(newtons)) < 9.95, true) ?? "PEAK " + InstrumentFormat.Force(newtons) + " kN") :
				indication == ContactIndication.Unknown ? InstrumentFormat.NoValue :
				"NONE");
			if (indication != Indication)
			{
				Indication = indication;
				Restyle();
			}
		}

		public void ShowNoData()
		{
			PaletteRoles.SetText(Value, InstrumentFormat.NoValue);
			_shownKilonewtons = double.NaN;
			if (Indication != ContactIndication.Unknown)
			{
				Indication = ContactIndication.Unknown;
				Restyle();
			}
		}

		private void Restyle()
		{
			Color fill;
			Color border;
			Color text;
			switch (Indication)
			{
				case ContactIndication.Live:
					fill = _palette.Caution;
					border = _palette.Caution;
					text = _palette.CautionText;
					break;
				case ContactIndication.Recent:
					fill = _palette.Face;
					border = _palette.Caution;
					text = _palette.Text;
					break;
				default:
					fill = _palette.Face;
					border = _palette.Bezel;
					text = _palette.TextDim;
					break;
			}

			style.backgroundColor = fill;
			PaletteRoles.SetBorderColor(this, border);
			_caption.style.color = text;
			Value.style.color = text;
		}
	}
}
