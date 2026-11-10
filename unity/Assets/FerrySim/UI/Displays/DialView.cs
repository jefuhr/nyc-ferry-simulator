using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// A round gauge: a static face (rim, bands, ticks and numerals, drawn once and again only when the palette
	/// changes), a needle that is a separate layer turned with <c>style.rotate</c>, and optionally a digital readout
	/// (when the skin gives decimals). The needle pegs at both ends of the range and hides when there is no reading.
	/// </summary>
	public sealed class DialView : SkinElementView
	{
		private readonly Channel _channel;
		private readonly DialFace _face;
		private readonly DialNeedle _needle;
		private readonly Label _caption;
		private readonly Label _unit;
		private readonly Label _readout;
		private readonly SkinValueText _text;
		private readonly List<Label> _numerals = new List<Label>();
		private bool _needleShown = true;

		public DialView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			_channel = spec.ResolveChannel(side);
			var rect = spec.Rect;
			var size = Mathf.Min(rect.width, rect.height);
			var centre = new Vector2(rect.width * 0.5f, rect.height * 0.5f);
			var radius = size * 0.5f;
			_face = new DialFace(this, centre, radius);
			Add(_face);
			MajorStep(spec.Low, spec.High, out var major, out var minor, out var decimals);
			_face.Ticks(major, minor);
			for (var value = Math.Ceiling(spec.Low / major - 1e-9) * major; value <= spec.High + major * 1e-6; value += major)
			{
				var text = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
				_numerals.Add(CentredLabel(this, text, VectorDraw.Polar(centre, radius * 0.64f, Angle(value)), size * 0.058f));
			}

			// The readout, unit and caption sit below the hub, where the needle passes only near the ends of a
			// symmetric sweep, so the needle never covers the words in the middle of the range.
			if (spec.Decimals >= 0)
			{
				_text = new SkinValueText(spec.Decimals);
				_readout = CentredLabel(this, SkinValueText.NoValue, new Vector2(centre.x, centre.y + radius * 0.42f), size * 0.12f, bold: true, primary: true);
				_readout.name = "readout";
			}

			var symbol = SkinUnits.Symbol(spec.Unit);
			if (symbol.Length > 0)
			{
				_unit = CentredLabel(this, symbol, new Vector2(centre.x, centre.y + radius * 0.64f), size * 0.055f);
			}

			if (!string.IsNullOrEmpty(spec.Text))
			{
				_caption = CentredLabel(this, spec.ResolveText(side), new Vector2(centre.x, centre.y + radius * 0.82f), size * 0.06f, bold: true);
			}

			_needle = new DialNeedle(centre, radius);
			Add(_needle);
			NeedleDegrees = spec.SweepStart;
			_needle.style.rotate = new Rotate(new UnityEngine.UIElements.Angle(NeedleDegrees, AngleUnit.Degree));
			SetNeedle(double.NaN);
		}

		/// <summary>Needle angle, degrees clockwise from straight up; the last angle shown while the needle is hidden.</summary>
		public float NeedleDegrees { get; private set; }

		public bool NeedleVisible => _needleShown;

		public VisualElement Needle => _needle;

		/// <summary>The digital readout, or null when the skin gives no decimals for this dial.</summary>
		public string ReadoutText => _readout?.text;

		public IReadOnlyList<Label> Numerals => _numerals;

		/// <summary>The needle angle for <paramref name="value"/> on a dial from low to high, pegged at both ends.</summary>
		public static float Angle(double value, double low, double high, float sweepStart, float sweepEnd)
		{
			var fraction = high > low ? (value - low) / (high - low) : 0.0;
			fraction = Math.Max(0.0, Math.Min(1.0, fraction));
			return (float)(sweepStart + (sweepEnd - sweepStart) * fraction);
		}

		private float Angle(double value) => Angle(value, Spec.Low, Spec.High, Spec.SweepStart, Spec.SweepEnd);

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_face.ApplyPalette(palette);
			_needle.Colour = Colour(palette, PaletteRole.Needle);
			_needle.ApplyPalette(palette);
			foreach (var numeral in _numerals)
			{
				numeral.style.color = palette.Scale;
			}

			if (_caption != null)
			{
				_caption.style.color = palette.TextDim;
			}

			if (_unit != null)
			{
				_unit.style.color = palette.TextDim;
			}

			if (_readout != null)
			{
				_readout.style.color = palette.Text;
			}
		}

		internal override void RefreshValue(IInstrumentReadout readout)
		{
			var value = Read(readout, _channel, Spec.Unit);
			SetNeedle(value);
			if (_readout != null)
			{
				SetText(_readout, _text.Format(value));
			}
		}

		private void SetNeedle(double value)
		{
			var shown = !double.IsNaN(value) && !double.IsInfinity(value);
			if (shown != _needleShown)
			{
				_needleShown = shown;
				_needle.style.visibility = shown ? Visibility.Visible : Visibility.Hidden;
			}

			if (!shown)
			{
				return;
			}

			var degrees = Angle(value);
			if (Math.Abs(degrees - NeedleDegrees) > 0.05f)
			{
				NeedleDegrees = degrees;
				_needle.style.rotate = new Rotate(new UnityEngine.UIElements.Angle(degrees, AngleUnit.Degree));
			}
		}

		/// <summary>A tick spacing of 1, 2, 2.5 or 5 times a power of ten giving 4 to 8 numbered intervals.</summary>
		public static void MajorStep(double low, double high, out double major, out double minor, out int decimals)
		{
			var span = high - low;
			var power = Math.Pow(10.0, Math.Floor(Math.Log10(span / 4.0)));
			major = power;
			foreach (var mantissa in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
			{
				major = mantissa * power;
				if (span / major <= 8.0 + 1e-9)
				{
					break;
				}
			}

			var leading = major / Math.Pow(10.0, Math.Floor(Math.Log10(major) + 1e-9));
			minor = major / (Math.Abs(leading - 2.0) < 1e-6 ? 4.0 : 5.0);
			decimals = major >= 1.0 - 1e-9 ? 0 : (int)Math.Ceiling(-Math.Log10(major) - 1e-9) + (Math.Abs(leading - 2.5) < 1e-6 ? 1 : 0);
		}

		private sealed class DialFace : InstrumentElement
		{
			private readonly DialView _owner;
			private readonly Vector2 _centre;
			private readonly float _radius;
			private double _major = 1.0;
			private double _minor = 1.0;

			public DialFace(DialView owner, Vector2 centre, float radius)
			{
				_owner = owner;
				_centre = centre;
				_radius = radius;
				Fill(this);
			}

			public void Ticks(double major, double minor)
			{
				_major = major;
				_minor = minor;
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var spec = _owner.Spec;
				var radius = _radius - 1f;
				VectorDraw.Circle(painter, _centre, radius, Palette.Bezel);
				VectorDraw.Circle(painter, _centre, radius - 2f, Palette.Face);
				foreach (var band in spec.Bands)
				{
					VectorDraw.ArcBand(painter, _centre, radius * 0.86f, radius * 0.95f, _owner.Angle(band.Low), _owner.Angle(band.High), band.Colour.Resolve(Palette));
				}

				VectorDraw.Arc(painter, _centre, radius * 0.95f, spec.SweepStart, spec.SweepEnd, Palette.Scale, 1.5f);
				var first = Math.Ceiling(spec.Low / _minor - 1e-9) * _minor;
				for (var value = first; value <= spec.High + _minor * 1e-6; value += _minor)
				{
					var ratio = value / _major;
					var isMajor = Math.Abs(ratio - Math.Round(ratio)) < 1e-6;
					var degrees = _owner.Angle(value);
					VectorDraw.Line(painter, VectorDraw.Polar(_centre, radius * (isMajor ? 0.78f : 0.86f), degrees), VectorDraw.Polar(_centre, radius * 0.95f, degrees),
						Palette.Scale, isMajor ? Mathf.Max(2f, radius * 0.018f) : Mathf.Max(1f, radius * 0.008f));
				}
			}
		}

		private sealed class DialNeedle : InstrumentElement
		{
			private readonly Vector2 _centre;
			private readonly float _radius;

			public DialNeedle(Vector2 centre, float radius)
			{
				_centre = centre;
				_radius = radius;
				Fill(this);
			}

			public Color Colour { get; set; } = Color.white;

			protected override void Draw(Painter2D painter, Rect rect)
			{
				// Drawn pointing straight up; the element is rotated to the value about its centre, the dial's centre.
				var tip = new Vector2(_centre.x, _centre.y - _radius * 0.86f);
				var tail = new Vector2(_centre.x, _centre.y + _radius * 0.14f);
				var halfWidth = Mathf.Max(2f, _radius * 0.035f);
				VectorDraw.Quad(painter,
					new Vector2(_centre.x - halfWidth, _centre.y), new Vector2(_centre.x - 0.8f, tip.y),
					new Vector2(_centre.x + 0.8f, tip.y), new Vector2(_centre.x + halfWidth, _centre.y), Colour);
				VectorDraw.Line(painter, _centre, tail, Colour, halfWidth * 2f);
				VectorDraw.Circle(painter, _centre, _radius * 0.08f, Colour);
				VectorDraw.Circle(painter, _centre, _radius * 0.035f, Palette.Face);
			}
		}
	}
}
