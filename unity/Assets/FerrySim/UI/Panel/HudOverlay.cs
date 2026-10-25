using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Minimal overlay for external camera views: speed over ground, heading, both levers with the gear
	/// each clutch is in, and both rudders. It sits small at the bottom centre, on an opaque backing so its
	/// text keeps its contrast over bright sky or water. Like the panel it shows "---" whenever the source
	/// has no telemetry.
	/// </summary>
	public sealed class HudOverlay : VisualElement, IPaletteAware
	{
		private readonly ReadoutText _speedText = new ReadoutText();
		private readonly ReadoutText _portText = new ReadoutText();
		private readonly ReadoutText _starboardText = new ReadoutText();
		private string _lastPortText;
		private string _lastStarboardText;
		private string _rudderText;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private InstrumentScales _scales = InstrumentScales.Generic;
		private VesselParameters _parameters;
		private bool _parametersSeen;
		private bool _showingNoData;
		private IVesselTelemetrySource _shownSource;
		private VesselState _shownState;
		private VesselTelemetry _shownTelemetry;
		private ControlInputs _shownControls;
		private GearTone _portTone = GearTone.Unknown;
		private GearTone _starboardTone = GearTone.Unknown;

		public HudOverlay()
		{
			name = "hud";
			AddToClassList("fs-hud");
			AddToClassList(PaletteRoles.HudBackground);
			pickingMode = PickingMode.Ignore;

			Speed = AddReadout("SOG", "hud-sog", "kn");
			Heading = AddReadout("HDG", "hud-heading", "°T");

			var levers = Block("fs-hud-levers");
			levers.Add(PaletteRoles.Label("LEVERS", PaletteRoles.Dim, "fs-caption"));
			var pair = Block("fs-hud-lever-pair");
			PortLever = new MiniLever { name = "hud-port-lever" };
			StarboardLever = new MiniLever { name = "hud-stbd-lever" };
			PortGear = PaletteRoles.Label(InstrumentFormat.NoValue, null, "fs-hud-gear");
			PortGear.name = "hud-port-gear";
			StarboardGear = PaletteRoles.Label(InstrumentFormat.NoValue, null, "fs-hud-gear");
			StarboardGear.name = "hud-stbd-gear";
			pair.Add(LeverWithGear("P", PortLever, PortGear));
			pair.Add(LeverWithGear("S", StarboardLever, StarboardGear));
			levers.Add(pair);
			Add(levers);

			var rudder = Block("fs-hud-rudder");
			rudder.Add(PaletteRoles.Label("RUDDER", PaletteRoles.Dim, "fs-caption"));
			Rudder = new MiniRudder { name = "hud-rudder" };
			rudder.Add(Rudder);
			RudderText = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s");
			RudderText.name = "hud-rudder-text";
			rudder.Add(RudderText);
			Add(rudder);
		}

		public Label Speed { get; }

		public Label Heading { get; }

		public MiniLever PortLever { get; }

		public MiniLever StarboardLever { get; }

		public Label PortGear { get; }

		public Label StarboardGear { get; }

		public MiniRudder Rudder { get; }

		public Label RudderText { get; }

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			ColorGear(PortGear, _portTone);
			ColorGear(StarboardGear, _starboardTone);
		}

		public void Refresh(IVesselTelemetrySource source)
		{
			if (source == null || !source.HasTelemetry)
			{
				ShowNoData();
				return;
			}

			var state = source.State;
			var telemetry = source.Telemetry;
			var controls = source.Controls;
			// A sample already on screen is not formatted again (paused, or several frames per step).
			if (!_showingNoData && ReferenceEquals(source, _shownSource) && ReferenceEquals(source.Parameters, _parameters) &&
				state.Equals(_shownState) && controls.Equals(_shownControls) && SameTelemetry(telemetry, _shownTelemetry))
			{
				return;
			}

			_showingNoData = false;
			_shownSource = source;
			_shownState = state;
			_shownTelemetry = telemetry;
			_shownControls = controls;
			if (!_parametersSeen || !ReferenceEquals(source.Parameters, _parameters))
			{
				_parametersSeen = true;
				_parameters = source.Parameters;
				_scales = InstrumentScales.From(_parameters);
				Rudder.SetScale(_scales.RudderScaleDegrees);
			}

			PaletteRoles.SetText(Speed, _speedText.Speed(telemetry.SpeedOverGround));
			PaletteRoles.SetText(Heading, InstrumentFormat.Compass(state.Heading));
			PortLever.SetLever(controls.PortLever);
			StarboardLever.SetLever(controls.StarboardLever);
			var port = GearReadout.From(telemetry.Port, false);
			var starboard = GearReadout.From(telemetry.Starboard, false);
			_portTone = ShowGear(PortGear, port.ShortLabel, port.Tone);
			_starboardTone = ShowGear(StarboardGear, starboard.ShortLabel, starboard.Tone);
			Rudder.SetAngles(
				InstrumentFormat.IsFinite(telemetry.PortRudderAngle) ? DisplayUnits.Degrees(telemetry.PortRudderAngle) : double.NaN,
				InstrumentFormat.IsFinite(telemetry.StarboardRudderAngle) ? DisplayUnits.Degrees(telemetry.StarboardRudderAngle) : double.NaN);
			var portText = _portText.Rudder(telemetry.PortRudderAngle);
			var starboardText = _starboardText.Rudder(telemetry.StarboardRudderAngle);
			if (portText != _lastPortText || starboardText != _lastStarboardText)
			{
				_lastPortText = portText;
				_lastStarboardText = starboardText;
				_rudderText = ReadoutStrings.RudderPair(telemetry.PortRudderAngle, telemetry.StarboardRudderAngle) ??
					(portText == starboardText ? portText : portText + " / " + starboardText);
			}
			PaletteRoles.SetText(RudderText, _rudderText);
		}

		private void ShowNoData()
		{
			if (_showingNoData)
			{
				return;
			}

			_showingNoData = true;
			PaletteRoles.SetText(Speed, InstrumentFormat.NoValue);
			PaletteRoles.SetText(Heading, InstrumentFormat.NoValue);
			_portTone = ShowGear(PortGear, InstrumentFormat.NoValue, GearTone.Unknown);
			_starboardTone = ShowGear(StarboardGear, InstrumentFormat.NoValue, GearTone.Unknown);
			PortLever.SetLever(double.NaN);
			StarboardLever.SetLever(double.NaN);
			Rudder.SetAngles(double.NaN, double.NaN);
			PaletteRoles.SetText(RudderText, InstrumentFormat.NoValue);
		}

		private GearTone ShowGear(Label label, string text, GearTone tone)
		{
			PaletteRoles.SetText(label, text);
			ColorGear(label, tone);
			return tone;
		}

		private void ColorGear(Label label, GearTone tone) =>
			label.style.color = tone == GearTone.Engaged ? _palette.Text :
				tone == GearTone.Transition ? _palette.Shifting :
				_palette.TextDim;

		private static bool SameTelemetry(in VesselTelemetry a, in VesselTelemetry b) =>
			a.Port.EngineSpeed.Equals(b.Port.EngineSpeed) && a.Port.ShaftSpeed.Equals(b.Port.ShaftSpeed) && a.Port.Gear == b.Port.Gear &&
			a.Starboard.EngineSpeed.Equals(b.Starboard.EngineSpeed) && a.Starboard.ShaftSpeed.Equals(b.Starboard.ShaftSpeed) &&
			a.Starboard.Gear == b.Starboard.Gear && a.PortRudderAngle.Equals(b.PortRudderAngle) &&
			a.StarboardRudderAngle.Equals(b.StarboardRudderAngle) && a.SpeedOverGround.Equals(b.SpeedOverGround);

		private Label AddReadout(string caption, string valueName, string unit)
		{
			var block = Block("fs-hud-readout");
			block.Add(PaletteRoles.Label(caption, PaletteRoles.Dim, "fs-caption"));
			var row = Block("fs-value-row");
			var value = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-l");
			value.name = valueName;
			row.Add(value);
			row.Add(PaletteRoles.Label(unit, PaletteRoles.Dim, "fs-unit", unit.StartsWith("°") ? "fs-unit--tight" : "fs-unit--spaced"));
			block.Add(row);
			Add(block);
			return value;
		}

		private static VisualElement LeverWithGear(string side, MiniLever lever, Label gear)
		{
			var column = Block("fs-hud-lever");
			column.Add(PaletteRoles.Label(side, PaletteRoles.Dim, "fs-caption"));
			column.Add(lever);
			column.Add(gear);
			return column;
		}

		private static VisualElement Block(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}
	}

	/// <summary>A small vertical lever position bar for the HUD: ahead up, astern down.</summary>
	public sealed class MiniLever : InstrumentElement
	{
		public MiniLever()
		{
			AddToClassList("fs-hud-lever-bar");
		}

		public double Lever { get; private set; } = double.NaN;

		public void SetLever(double lever)
		{
			if (lever.Equals(Lever))
			{
				return;
			}

			Lever = lever;
			MarkDirtyRepaint();
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var x = rect.center.x;
			var top = rect.yMin + 4f;
			var bottom = rect.yMax - 4f;
			var middle = (top + bottom) * 0.5f;
			VectorDraw.RoundedRect(painter, new Rect(x - 3f, top, 6f, bottom - top), 3f, Palette.Bezel);
			VectorDraw.Line(painter, new Vector2(x - 8f, middle), new Vector2(x + 8f, middle), Palette.Scale, 1.5f);
			if (!InstrumentFormat.IsFinite(Lever))
			{
				return;
			}

			var y = middle - (float)Mathf.Clamp((float)Lever, -1f, 1f) * (bottom - top) * 0.5f;
			VectorDraw.Rect(painter, new Rect(x - 2f, Mathf.Min(middle, y), 4f, Mathf.Abs(y - middle)), Palette.Fill);
			VectorDraw.RoundedRect(painter, new Rect(x - 9f, y - 3f, 18f, 6f), 2f, Palette.Needle);
		}
	}

	/// <summary>A small horizontal rudder scale for the HUD: port rudder pointer above, starboard below.</summary>
	public sealed class MiniRudder : InstrumentElement
	{
		private double _scale = InstrumentScales.MinimumRudderScaleDegrees;

		public MiniRudder()
		{
			AddToClassList("fs-hud-rudder-bar");
		}

		public double PortDegrees { get; private set; } = double.NaN;

		public double StarboardDegrees { get; private set; } = double.NaN;

		public void SetScale(double degrees)
		{
			_scale = degrees > 0.0 ? degrees : InstrumentScales.MinimumRudderScaleDegrees;
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
			var left = rect.xMin + 6f;
			var right = rect.xMax - 6f;
			var middle = rect.center.y;
			var zero = (left + right) * 0.5f;
			VectorDraw.Rect(painter, new Rect(left, middle - 1.5f, zero - left, 3f), Palette.Port);
			VectorDraw.Rect(painter, new Rect(zero, middle - 1.5f, right - zero, 3f), Palette.Starboard);
			for (var degrees = -30; degrees <= 30; degrees += 10)
			{
				var x = X(degrees, left, right);
				VectorDraw.Line(painter, new Vector2(x, middle - (degrees == 0 ? 6f : 3f)), new Vector2(x, middle + (degrees == 0 ? 6f : 3f)), Palette.Scale, degrees == 0 ? 1.5f : 1f);
			}

			if (InstrumentFormat.IsFinite(PortDegrees))
			{
				var x = X(PortDegrees, left, right);
				VectorDraw.Triangle(painter, new Vector2(x, middle - 3f), new Vector2(x - 5f, middle - 12f), new Vector2(x + 5f, middle - 12f), Palette.Needle);
			}

			if (InstrumentFormat.IsFinite(StarboardDegrees))
			{
				var x = X(StarboardDegrees, left, right);
				VectorDraw.Triangle(painter, new Vector2(x, middle + 3f), new Vector2(x - 5f, middle + 12f), new Vector2(x + 5f, middle + 12f), Palette.Needle);
			}
		}

		private float X(double degrees, float left, float right)
		{
			var clamped = Mathf.Clamp((float)degrees, (float)-_scale, (float)_scale);
			return left + (float)((clamped + _scale) / (2.0 * _scale)) * (right - left);
		}
	}
}
