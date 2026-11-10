using System;
using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// A bar graph, horizontal when its rect is wider than tall, otherwise vertical. The track and its bands are drawn
	/// once; the fill grows from the low end and is scaled with <c>style.scale</c>, so a moving value never relays out
	/// the display. The fill hides when there is no reading. A caption and, when the skin gives decimals, the number
	/// sit beside the track.
	/// </summary>
	public sealed class BarView : SkinElementView
	{
		private readonly Channel _channel;
		private readonly bool _horizontal;
		private readonly VisualElement _fill;
		private readonly BarTrack _track;
		private readonly Label _caption;
		private readonly Label _readout;
		private readonly SkinValueText _text;
		private bool _fillShown = true;

		public BarView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			_channel = spec.ResolveChannel(side);
			var rect = spec.Rect;
			_horizontal = rect.width >= rect.height;
			var hasCaption = !string.IsNullOrEmpty(spec.Text);
			var hasReadout = spec.Decimals >= 0;
			Rect track;
			if (_horizontal)
			{
				var labels = hasCaption || hasReadout ? rect.height * 0.45f : 0f;
				track = new Rect(0, labels, rect.width, rect.height - labels);
				var font = Mathf.Max(8f, labels * 0.72f);
				if (hasCaption)
				{
					_caption = MakeLabel(this, CaptionWithUnit(spec, side), new Rect(0, 0, rect.width * 0.6f, labels), font, TextAnchor.MiddleLeft);
				}

				if (hasReadout)
				{
					_text = new SkinValueText(spec.Decimals);
					_readout = MakeLabel(this, SkinValueText.NoValue, new Rect(rect.width * 0.4f, 0, rect.width * 0.6f, labels), font * 1.1f, TextAnchor.MiddleRight, bold: true, primary: true);
				}
			}
			else
			{
				var band = hasCaption || hasReadout ? rect.height * 0.14f : 0f;
				track = new Rect(rect.width * 0.25f, band, rect.width * 0.5f, rect.height - 2f * band);
				var font = Mathf.Min(band * 0.72f, rect.width * 0.22f);
				if (hasCaption)
				{
					_caption = MakeLabel(this, CaptionWithUnit(spec, side), new Rect(0, 0, rect.width, band), font, TextAnchor.MiddleCenter);
				}

				if (hasReadout)
				{
					_text = new SkinValueText(spec.Decimals);
					_readout = MakeLabel(this, SkinValueText.NoValue, new Rect(0, rect.height - band, rect.width, band), font * 1.1f, TextAnchor.MiddleCenter, bold: true, primary: true);
				}
			}

			_track = new BarTrack(this, _horizontal);
			Place(_track, track);
			Add(_track);
			_fill = new VisualElement { name = "fill", pickingMode = PickingMode.Ignore };
			var inset = Mathf.Max(2f, Mathf.Min(track.width, track.height) * 0.12f);
			Place(_fill, new Rect(inset, inset, track.width - 2f * inset, track.height - 2f * inset));
			_fill.style.transformOrigin = _horizontal
				? new TransformOrigin(Length.Percent(0f), Length.Percent(50f))
				: new TransformOrigin(Length.Percent(50f), Length.Percent(100f));
			_track.Add(_fill);
			SetFraction(double.NaN);
		}

		/// <summary>How far the fill reaches, 0 at the low end and 1 at the high end; the last shown while hidden.</summary>
		public float Fraction { get; private set; } = -1f;

		public bool FillVisible => _fillShown;

		public VisualElement Fill => _fill;

		public string ReadoutText => _readout?.text;

		/// <summary>"LOAD %": the caption and the unit symbol, when the unit has one.</summary>
		public static string CaptionWithUnit(SkinElement spec, DisplaySide side)
		{
			var unit = SkinUnits.Symbol(spec.Unit);
			var text = spec.ResolveText(side);
			return unit.Length > 0 ? text + " " + unit : text;
		}

		public string CaptionText => _caption?.text;

		/// <summary>The fill fraction for <paramref name="value"/> on a bar from low to high, clamped to [0, 1].</summary>
		public static float FractionOf(double value, double low, double high) =>
			high > low ? (float)Math.Max(0.0, Math.Min(1.0, (value - low) / (high - low))) : 0f;

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_track.ApplyPalette(palette);
			_fill.style.backgroundColor = Colour(palette, PaletteRole.Needle);
			if (_caption != null)
			{
				_caption.style.color = palette.TextDim;
			}

			if (_readout != null)
			{
				_readout.style.color = palette.Text;
			}
		}

		internal override void RefreshValue(IInstrumentReadout readout)
		{
			var value = Read(readout, _channel, Spec.Unit);
			SetFraction(value);
			if (_readout != null)
			{
				SetText(_readout, _text.Format(value));
			}
		}

		private void SetFraction(double value)
		{
			var shown = !double.IsNaN(value) && !double.IsInfinity(value);
			if (shown != _fillShown)
			{
				_fillShown = shown;
				_fill.style.visibility = shown ? Visibility.Visible : Visibility.Hidden;
			}

			if (!shown)
			{
				return;
			}

			var fraction = FractionOf(value, Spec.Low, Spec.High);
			if (Math.Abs(fraction - Fraction) > 1e-4f)
			{
				Fraction = fraction;
				_fill.style.scale = new Scale(_horizontal ? new Vector3(fraction, 1f, 1f) : new Vector3(1f, fraction, 1f));
			}
		}

		private sealed class BarTrack : InstrumentElement
		{
			private readonly BarView _owner;
			private readonly bool _horizontal;

			public BarTrack(BarView owner, bool horizontal)
			{
				_owner = owner;
				_horizontal = horizontal;
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				VectorDraw.Rect(painter, rect, Palette.Face);
				var spec = _owner.Spec;
				foreach (var band in spec.Bands)
				{
					// A strip along the far edge of the track, so the fill never hides which part is the warning range.
					var from = FractionOf(band.Low, spec.Low, spec.High);
					var to = FractionOf(band.High, spec.Low, spec.High);
					var strip = Mathf.Max(3f, (_horizontal ? rect.height : rect.width) * 0.2f);
					var bandRect = _horizontal
						? new Rect(rect.xMin + rect.width * from, rect.yMax - strip, rect.width * (to - from), strip)
						: new Rect(rect.xMax - strip, rect.yMax - rect.height * to, strip, rect.height * (to - from));
					VectorDraw.Rect(painter, bandRect, band.Colour.Resolve(Palette));
				}

				VectorDraw.RectOutline(painter, new Rect(rect.xMin + 1f, rect.yMin + 1f, rect.width - 2f, rect.height - 2f), Palette.Bezel, 2f);
			}
		}
	}
}
