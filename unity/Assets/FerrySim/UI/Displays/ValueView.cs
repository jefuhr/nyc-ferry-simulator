using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// A number with its caption and unit, or with <c>format: gear</c> the gear as a word. The caption sits at the top
	/// left, the number at the bottom right, the unit after it. A missing reading shows dashes, never zero.
	/// </summary>
	public sealed class ValueView : SkinElementView
	{
		private readonly Channel _channel;
		private readonly Channel _lever;
		private readonly Channel _shaft;
		private readonly Label _caption;
		private readonly Label _value;
		private readonly Label _unit;
		private readonly SkinValueText _text;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private bool _shifting;

		public ValueView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			_channel = spec.ResolveChannel(side);
			var gear = spec.Format == SkinValueFormat.Gear;
			if (gear)
			{
				GearWords.Companions(_channel, out _lever, out _shaft);
			}

			_text = new SkinValueText(spec.Decimals < 0 ? 0 : spec.Decimals);
			var rect = spec.Rect;
			var w = rect.width;
			var h = rect.height;
			var hasCaption = !string.IsNullOrEmpty(spec.Text);
			var symbol = gear ? "" : SkinUnits.Symbol(spec.Unit);
			var pad = Mathf.Max(2f, h * 0.1f);
			var top = hasCaption ? h * 0.36f : 0f;
			var numberFont = hasCaption ? h * 0.46f : h * 0.62f;
			var unitFont = Mathf.Max(8f, h * 0.22f);
			var unitWidth = symbol.Length == 0 ? 0f : Mathf.Max(h * 0.5f, symbol.Length * unitFont * 0.62f) + pad * 0.5f;
			if (hasCaption)
			{
				_caption = MakeLabel(this, spec.ResolveText(side), new Rect(pad, h * 0.04f, w - 2f * pad, h * 0.34f), Mathf.Max(8f, h * 0.22f), TextAnchor.MiddleLeft);
			}

			_value = MakeLabel(this, SkinValueText.NoValue, new Rect(pad, top, Mathf.Max(1f, w - 2f * pad - unitWidth), h - top), numberFont,
				gear ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight, bold: true, primary: true);
			_value.name = "value";
			if (unitWidth > 0f)
			{
				_unit = MakeLabel(this, symbol, new Rect(w - pad - unitWidth + pad * 0.5f, top + (h - top) * 0.3f, unitWidth - pad * 0.5f, (h - top) * 0.6f), unitFont, TextAnchor.MiddleLeft);
			}
		}

		public string ValueText => _value.text;

		public string CaptionText => _caption?.text;

		/// <summary>The unit symbol shown, or empty.</summary>
		public string UnitText => _unit?.text ?? "";

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette;
			_value.style.color = ValueColour();
			if (_caption != null)
			{
				_caption.style.color = palette.TextDim;
			}

			if (_unit != null)
			{
				_unit.style.color = palette.TextDim;
			}
		}

		internal override void RefreshValue(IInstrumentReadout readout)
		{
			if (Spec.Format != SkinValueFormat.Gear)
			{
				SetText(_value, _text.Format(Read(readout, _channel, Spec.Unit)));
				return;
			}

			var word = readout == null
				? SkinValueText.NoValue
				: GearWords.Word(readout.Value(_channel), readout.Value(_lever), readout.Value(_shaft));
			SetText(_value, word);
			var shifting = ReferenceEquals(word, GearWords.Shifting);
			if (shifting != _shifting)
			{
				_shifting = shifting;
				_value.style.color = ValueColour();
			}
		}

		// SHIFTING takes the shifting colour so it can never look like an engaged gear, as on the panel's gear badge.
		private Color ValueColour() => _shifting ? _palette.Shifting : Colour(_palette, PaletteRole.Text);
	}
}
