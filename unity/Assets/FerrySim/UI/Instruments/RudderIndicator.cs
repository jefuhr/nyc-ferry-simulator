using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Both rudder angles on one horizontal scale: port side of the scale on the left (red by day),
	/// starboard on the right (green by day), the port rudder's pointer above the scale and the starboard
	/// rudder's below, each with its own readout. Positive angles (starboard rudder) point right. Hard-over
	/// marks appear only when the vessel file gives the angle.
	/// </summary>
	public sealed class RudderIndicator : VisualElement, IPaletteAware
	{
		private readonly Scale _scale;
		private readonly Label _portValue;
		private readonly Label _starboardValue;

		public RudderIndicator()
		{
			AddToClassList("fs-rudder");
			var names = new VisualElement { pickingMode = PickingMode.Ignore };
			names.AddToClassList("fs-rudder-names");
			names.Add(PaletteRoles.Label("PORT RUDDER", PaletteRoles.Dim, "fs-caption", "fs-rudder-row"));
			names.Add(PaletteRoles.Label("STBD RUDDER", PaletteRoles.Dim, "fs-caption", "fs-rudder-row"));
			Add(names);

			_scale = new Scale();
			Add(_scale);

			var values = new VisualElement { pickingMode = PickingMode.Ignore };
			values.AddToClassList("fs-rudder-values");
			_portValue = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-m", "fs-rudder-row");
			_portValue.name = "port-rudder";
			_starboardValue = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-m", "fs-rudder-row");
			_starboardValue.name = "stbd-rudder";
			values.Add(_portValue);
			values.Add(_starboardValue);
			Add(values);
		}

		/// <summary>Port rudder angle last shown, degrees, positive to starboard; NaN when unknown.</summary>
		public double PortDegrees => _scale.PortDegrees;

		public double StarboardDegrees => _scale.StarboardDegrees;

		public string PortText => _portValue.text;

		public string StarboardText => _starboardValue.text;

		public void ApplyPalette(InstrumentPalette palette) => _scale.ApplyPalette(palette);

		public void SetScales(InstrumentScales scales) => _scale.SetScales(scales ?? InstrumentScales.Generic);

		public void SetAngles(double portRadians, double starboardRadians)
		{
			PaletteRoles.SetText(_portValue, InstrumentFormat.Rudder(portRadians));
			PaletteRoles.SetText(_starboardValue, InstrumentFormat.Rudder(starboardRadians));
			_scale.SetAngles(
				InstrumentFormat.IsFinite(portRadians) ? DisplayUnits.Degrees(portRadians) : double.NaN,
				InstrumentFormat.IsFinite(starboardRadians) ? DisplayUnits.Degrees(starboardRadians) : double.NaN);
		}

		private sealed class Scale : InstrumentElement
		{
			private const float Margin = 14f;
			private const float TrackHeight = 16f;

			private readonly List<Label> _numerals = new List<Label>();
			private readonly List<double> _numeralAngles = new List<double>();
			private readonly Label _portSide;
			private readonly Label _starboardSide;
			private InstrumentScales _scales = InstrumentScales.Generic;

			public Scale()
			{
				AddToClassList("fs-rudder-scale");
				_portSide = PaletteRoles.Label("P", PaletteRoles.Port, "fs-rudder-numeral", "fs-strong");
				_starboardSide = PaletteRoles.Label("S", PaletteRoles.Starboard, "fs-rudder-numeral", "fs-strong");
				Add(_portSide);
				Add(_starboardSide);
				RebuildNumerals();
				RegisterCallback<GeometryChangedEvent>(_ => LayoutLabels());
			}

			public double PortDegrees { get; private set; } = double.NaN;

			public double StarboardDegrees { get; private set; } = double.NaN;

			public void SetScales(InstrumentScales scales)
			{
				if (ReferenceEquals(scales, _scales))
				{
					return;
				}

				_scales = scales;
				RebuildNumerals();
				LayoutLabels();
				MarkDirtyRepaint();
			}

			public void SetAngles(double portDegrees, double starboardDegrees)
			{
				if (portDegrees.Equals(PortDegrees) && starboardDegrees.Equals(StarboardDegrees))
				{
					return;
				}

				PortDegrees = portDegrees;
				StarboardDegrees = starboardDegrees;
				MarkDirtyRepaint();
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var scale = (float)_scales.RudderScaleDegrees;
				var topTrack = new Rect(rect.xMin + Margin, rect.yMin + 2f, rect.width - 2f * Margin, TrackHeight);
				var bottomTrack = new Rect(topTrack.x, rect.yMax - 2f - TrackHeight, topTrack.width, TrackHeight);
				VectorDraw.RoundedRect(painter, topTrack, 4f, Palette.Bezel);
				VectorDraw.RoundedRect(painter, bottomTrack, 4f, Palette.Bezel);

				var zero = X(rect, 0.0);
				var bandTop = topTrack.yMax + 4f;
				VectorDraw.Rect(painter, new Rect(topTrack.xMin, bandTop, zero - topTrack.xMin, 3f), Palette.Port);
				VectorDraw.Rect(painter, new Rect(zero, bandTop, topTrack.xMax - zero, 3f), Palette.Starboard);
				for (var degrees = -scale; degrees <= scale + 1e-3f; degrees += 5f)
				{
					var x = X(rect, degrees);
					var major = Mathf.Abs(degrees % 10f) < 1e-3f;
					VectorDraw.Line(painter, new Vector2(x, bandTop + 3f), new Vector2(x, bandTop + (major ? 10f : 6f)), Palette.Scale, major ? 1.8f : 1.1f);
				}

				VectorDraw.Line(painter, new Vector2(zero, topTrack.yMin), new Vector2(zero, bottomTrack.yMax), Palette.Scale, 1f);
				if (InstrumentFormat.IsFinite(_scales.HardOverDegrees))
				{
					foreach (var side in new[] { -1.0, 1.0 })
					{
						var x = X(rect, side * _scales.HardOverDegrees);
						VectorDraw.DashedLine(painter, new Vector2(x, topTrack.yMin), new Vector2(x, bottomTrack.yMax), Palette.TextDim, 1.5f, 3f, 3f);
					}
				}

				DrawPointer(painter, rect, topTrack, PortDegrees, true);
				DrawPointer(painter, rect, bottomTrack, StarboardDegrees, false);
			}

			private void DrawPointer(Painter2D painter, Rect rect, Rect track, double degrees, bool pointsDown)
			{
				if (!InstrumentFormat.IsFinite(degrees))
				{
					return;
				}

				var zero = X(rect, 0.0);
				var x = X(rect, degrees);
				VectorDraw.Rect(painter, new Rect(Mathf.Min(zero, x), track.yMin + 3f, Mathf.Abs(x - zero), track.height - 6f), Palette.Fill);
				var apexY = pointsDown ? track.yMax + 3f : track.yMin - 3f;
				var baseY = pointsDown ? track.yMin + 1f : track.yMax - 1f;
				VectorDraw.Triangle(painter, new Vector2(x, apexY), new Vector2(x - 7f, baseY), new Vector2(x + 7f, baseY), Palette.Needle);
			}

			private float X(Rect rect, double degrees)
			{
				var scale = _scales.RudderScaleDegrees;
				var clamped = Math.Max(-scale, Math.Min(scale, degrees));
				var left = rect.xMin + Margin;
				var right = rect.xMax - Margin;
				return (float)(left + (clamped + scale) / (2.0 * scale) * (right - left));
			}

			private void RebuildNumerals()
			{
				foreach (var numeral in _numerals)
				{
					numeral.RemoveFromHierarchy();
				}

				_numerals.Clear();
				_numeralAngles.Clear();
				var limit = Math.Floor(_scales.RudderScaleDegrees / 10.0) * 10.0;
				for (var degrees = -limit; degrees <= limit + 1e-6; degrees += 10.0)
				{
					var numeral = PaletteRoles.Label(Math.Abs(degrees).ToString("0", CultureInfo.InvariantCulture), PaletteRoles.Scale, "fs-rudder-numeral");
					// Rebuilt after the palette walk (when the vessel's scale arrives), so color it here.
					numeral.style.color = Palette.Scale;
					Add(numeral);
					_numerals.Add(numeral);
					_numeralAngles.Add(degrees);
				}
			}

			private void LayoutLabels()
			{
				var rect = contentRect;
				if (rect.width < 1f || float.IsNaN(rect.width))
				{
					return;
				}

				var y = rect.center.y + 9f;
				for (var i = 0; i < _numerals.Count; i++)
				{
					Place(_numerals[i], X(rect, _numeralAngles[i]), y);
				}

				Place(_portSide, X(rect, -_scales.RudderScaleDegrees), y);
				Place(_starboardSide, X(rect, _scales.RudderScaleDegrees), y);
			}

			private static void Place(VisualElement label, float x, float y)
			{
				label.style.position = Position.Absolute;
				label.style.left = x;
				label.style.top = y;
				label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
			}
		}
	}
}
