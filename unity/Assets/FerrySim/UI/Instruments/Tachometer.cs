using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Engine tachometer: a 240° dial from 0 to the vessel's tach scale with a redline band above rated
	/// speed (only when the vessel file states the rating), a needle, and a digital rpm readout in the gap
	/// at the bottom of the dial. The face is drawn once; the needle is a separate layer turned with
	/// <c>style.rotate</c>, so a moving needle does not regenerate the dial.
	/// </summary>
	public sealed class Tachometer : VisualElement, IPaletteAware
	{
		public const float SweepStart = -120f;
		public const float SweepEnd = 120f;

		private readonly ReadoutText _rpmText = new ReadoutText();
		private readonly Face _face;
		private readonly Needle _needle;
		private readonly Label _readout;
		private readonly Label _multiplier;
		private readonly List<Label> _numerals = new List<Label>();
		private readonly List<double> _numeralValues = new List<double>();
		private InstrumentScales _scales;
		private InstrumentPalette _palette = InstrumentPalette.Day;

		public Tachometer()
		{
			AddToClassList("fs-tach");
			_face = new Face(this);
			Add(_face);
			_multiplier = PaletteRoles.Label("×100 RPM", PaletteRoles.Dim, "fs-tach-multiplier");
			Add(_multiplier);
			_readout = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-tach-readout");
			_readout.name = "readout";
			Add(_readout);
			_needle = new Needle();
			Add(_needle);
			SetScales(InstrumentScales.Generic);
			RegisterCallback<GeometryChangedEvent>(_ => LayoutLabels());
		}

		/// <summary>Engine speed last shown, rpm; NaN when there is no reading.</summary>
		public double Rpm { get; private set; } = double.NaN;

		/// <summary>Dial angle of the needle, degrees clockwise from 12 o'clock.</summary>
		public float NeedleDegrees { get; private set; } = SweepStart;

		public bool NeedleVisible => _needle.resolvedStyle.display != DisplayStyle.None;

		public string ReadoutText => _readout.text;

		internal InstrumentScales Scales => _scales;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			_face.ApplyPalette(_palette);
			_needle.ApplyPalette(_palette);
		}

		public void SetScales(InstrumentScales scales)
		{
			scales = scales ?? InstrumentScales.Generic;
			if (ReferenceEquals(scales, _scales))
			{
				return;
			}

			_scales = scales;
			foreach (var numeral in _numerals)
			{
				numeral.RemoveFromHierarchy();
			}

			_numerals.Clear();
			_numeralValues.Clear();
			for (var rpm = 0.0; rpm <= scales.TachMaxRpm + 1e-6; rpm += 500.0)
			{
				var numeral = PaletteRoles.Label((rpm / 100.0).ToString("0", CultureInfo.InvariantCulture), PaletteRoles.Scale, "fs-tach-numeral");
				// Rebuilt after the palette walk (when the vessel's scale arrives), so color it here.
				numeral.style.color = _palette.Scale;
				Insert(1, numeral);
				_numerals.Add(numeral);
				_numeralValues.Add(rpm);
			}

			_face.MarkDirtyRepaint();
			LayoutLabels();
			SetEngineSpeed(Units.RpmToRadiansPerSecond(Rpm));
		}

		/// <summary>Shows an engine speed in rad/s (converted to rpm here); NaN shows "---" and hides the needle.</summary>
		public void SetEngineSpeed(double radiansPerSecond)
		{
			PaletteRoles.SetText(_readout, _rpmText.Rpm(radiansPerSecond));
			if (!InstrumentFormat.IsFinite(radiansPerSecond))
			{
				Rpm = double.NaN;
				_needle.style.display = DisplayStyle.None;
				return;
			}

			Rpm = DisplayUnits.Rpm(radiansPerSecond);
			var degrees = DialDegrees(Rpm, _scales.TachMaxRpm);
			_needle.style.display = DisplayStyle.Flex;
			if (Math.Abs(degrees - NeedleDegrees) > 0.05f)
			{
				NeedleDegrees = degrees;
				_needle.style.rotate = new Rotate(new Angle(degrees, AngleUnit.Degree));
			}
		}

		/// <summary>Needle angle for an rpm on a dial of full scale <paramref name="maxRpm"/>; pegs at both ends.</summary>
		public static float DialDegrees(double rpm, double maxRpm)
		{
			var fraction = maxRpm > 0.0 ? Math.Max(0.0, Math.Min(1.0, rpm / maxRpm)) : 0.0;
			return (float)(SweepStart + (SweepEnd - SweepStart) * fraction);
		}

		private void LayoutLabels()
		{
			var rect = contentRect;
			if (rect.width < 1f || float.IsNaN(rect.width))
			{
				return;
			}

			var centre = rect.center;
			var radius = Mathf.Min(rect.width, rect.height) * 0.5f;
			for (var i = 0; i < _numerals.Count; i++)
			{
				var point = VectorDraw.Polar(centre, radius * 0.62f, DialDegrees(_numeralValues[i], _scales.TachMaxRpm));
				Place(_numerals[i], point);
			}

			Place(_multiplier, new Vector2(centre.x, centre.y - radius * 0.36f));
			Place(_readout, new Vector2(centre.x, centre.y + radius * 0.58f));
		}

		private static void Place(VisualElement label, Vector2 point)
		{
			label.style.position = Position.Absolute;
			label.style.left = point.x;
			label.style.top = point.y;
			label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
		}

		private sealed class Face : InstrumentElement
		{
			private readonly Tachometer _owner;

			public Face(Tachometer owner)
			{
				_owner = owner;
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var scales = _owner._scales;
				var centre = rect.center;
				var radius = Mathf.Min(rect.width, rect.height) * 0.5f - 1f;
				VectorDraw.Circle(painter, centre, radius, Palette.Bezel);
				VectorDraw.Circle(painter, centre, radius - 2f, Palette.Face);
				if (scales.HasRedline && scales.RatedRpm < scales.TachMaxRpm)
				{
					VectorDraw.ArcBand(painter, centre, radius * 0.86f, radius * 0.95f,
						DialDegrees(scales.RatedRpm, scales.TachMaxRpm), SweepEnd, Palette.Redline);
				}

				for (var rpm = 0.0; rpm <= scales.TachMaxRpm + 1e-6; rpm += 100.0)
				{
					var major = Math.Abs(rpm % 500.0) < 1e-6;
					var degrees = DialDegrees(rpm, scales.TachMaxRpm);
					var inner = radius * (major ? 0.78f : 0.86f);
					VectorDraw.Line(painter, VectorDraw.Polar(centre, inner, degrees), VectorDraw.Polar(centre, radius * 0.95f, degrees),
						Palette.Scale, major ? 2.5f : 1.2f);
				}
			}
		}

		private sealed class Needle : InstrumentElement
		{
			public Needle()
			{
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				// Drawn pointing at 12 o'clock; the element is rotated to the value.
				var centre = rect.center;
				var radius = Mathf.Min(rect.width, rect.height) * 0.5f;
				var tip = new Vector2(centre.x, centre.y - radius * 0.86f);
				var tail = new Vector2(centre.x, centre.y + radius * 0.14f);
				var halfWidth = Mathf.Max(2f, radius * 0.035f);
				VectorDraw.Quad(painter,
					new Vector2(centre.x - halfWidth, centre.y), new Vector2(centre.x - 0.8f, tip.y),
					new Vector2(centre.x + 0.8f, tip.y), new Vector2(centre.x + halfWidth, centre.y), Palette.Needle);
				VectorDraw.Line(painter, centre, tail, Palette.Needle, halfWidth * 2f);
				VectorDraw.Circle(painter, centre, radius * 0.08f, Palette.Needle);
				VectorDraw.Circle(painter, centre, radius * 0.035f, Palette.Face);
			}
		}
	}
}
