using System;
using System.Globalization;
using FerrySim.Core.Radar;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Radar
{
	/// <summary>Original UI Toolkit marine display. Screen placement and key routing belong to the composition root.</summary>
	public sealed class RadarView : VisualElement, IDisposable
	{
		private static readonly string[] Headings = MakeHeadings();
		private readonly RadarEngine _engine;
		private readonly Image _image;
		private readonly RadarOverlay _overlay;
		private readonly Label _heading, _range, _mode, _measurement, _status, _relative;
		private static readonly string[] Relative = MakeRelative();
		private readonly Button _orientation;
		private readonly Slider _gain, _sea, _ebl, _vrm;
		private int _lastHeading = -1, _lastRange = -1;
		private double _headingRadians;
		/// <summary>Reference pixels of the default layout (480 × 680 with a 440 px PPI) and of the compact wheelhouse layout (480 × 380 with a 280 px PPI: the wheelhouse's traffic readout band sits at 392–416 px above its panel, and the notice must stay clear of it).</summary>
		public const int DefaultWidth = 480, DefaultHeight = 680, DefaultPlotPixels = 440, CompactHeight = 380, CompactPlotPixels = 280;
		/// <param name="compact">The wheelhouse layout: one header row, the PPI at 280 px, the readouts on one line, the five sliders on one row and no buttons (the composition root's keys change range, orientation and palette). Every font stays at least the default layout's 10 px.</param>
		public RadarView(RadarEngine engine, int rasterSize = RadarRaster.DefaultSize, bool compact = false)
		{
			_engine = engine; Raster = new RadarRaster(engine, rasterSize); name = "radar"; Compact = compact; PlotPixels = compact ? CompactPlotPixels : DefaultPlotPixels;
			style.width = DefaultWidth; style.height = compact ? CompactHeight : DefaultHeight; style.paddingLeft = 16; style.paddingRight = 16; style.paddingTop = compact ? 6 : 10;
			AddToClassList(PaletteRoles.ConsoleBackground);
			_range = Text("", compact ? 12 : 14); _mode = Text("NORTH UP", compact ? 12 : 14); _heading = Text("HDG ---", compact ? 12 : 14);
			if (compact)
			{
				var header = Row(); header.Add(Text("RADAR", 14)); header.Add(_range); header.Add(_mode); header.Add(_heading); header.Add(Text("X BAND · TRAINING", 10)); Add(header);
			}
			else
			{
				var title = Row(); title.Add(Text("RADAR", 20)); title.Add(Text("X BAND · TRAINING", 12)); Add(title);
				var top = Row(); top.Add(_range); top.Add(_mode); top.Add(_heading); Add(top);
			}
			var plot = new VisualElement { name = "ppi" }; plot.style.flexShrink = 0; plot.style.width = PlotPixels; plot.style.height = PlotPixels; plot.style.marginTop = compact ? 2 : 4; if (compact) plot.style.alignSelf = Align.Center;
			plot.style.overflow = Overflow.Hidden; _image = new Image { image = Raster.Texture, scaleMode = ScaleMode.ScaleToFit };
			_image.style.width = PlotPixels; _image.style.height = PlotPixels; plot.Add(_image);
			_overlay = new RadarOverlay(this); _overlay.style.position = Position.Absolute; _overlay.style.width = PlotPixels; _overlay.style.height = PlotPixels; plot.Add(_overlay); Add(plot);
			_measurement = Text("", compact ? 11 : 12); _relative = Text("", compact ? 10 : 11);
			if (compact) { var readouts = Row(); readouts.Add(_measurement); readouts.Add(_relative); Add(readouts); }
			else { Add(_measurement); Add(_relative); }
			_orientation = new Button(() => SetHeadUp(!HeadUp)) { text = "HEAD UP" };
			if (!compact)
			{
				var buttons = Row(); buttons.Add(new Button(() => ChangeRange(-1)) { text = "RANGE −" }); buttons.Add(new Button(() => ChangeRange(1)) { text = "RANGE +" });
				buttons.Add(_orientation);
				buttons.Add(new Button(() => SetPalette((RadarPalette)(((int)Palette + 1) % 3))) { text = "PALETTE" }); Add(buttons);
			}
			var controls = Row(); controls.Add(_gain = Control("GAIN", 0, 2, 1, v => _engine.Gain = v)); controls.Add(_sea = Control("SEA", 0, 1, .35f, v => _engine.Sea = v)); controls.Add(Control("RAIN*", 0, 1, 0, v => _engine.Rain = v));
			_ebl = Control("EBL", 0, 359, 45, v => { EblTrue = v * Math.PI / 180; UpdateMeasurements(); }); _vrm = Control("VRM", 0, 1, .5f, v => { VrmFraction = v; UpdateMeasurements(); });
			if (compact) { controls.Add(_ebl); controls.Add(_vrm); Add(controls); }
			else { Add(controls); var markers = Row(); markers.Add(_ebl); markers.Add(_vrm); Add(markers); }
			_status = Text("Not for navigation · *FTC inactive, no rain data", compact ? 10 : 11); Add(_status);
			SetPalette(RadarPalette.Day); UpdateMeasurements();
		}
		/// <summary>The compact wheelhouse layout, or the default evidence layout.</summary>
		public bool Compact { get; }
		/// <summary>The PPI's side in reference pixels.</summary>
		public int PlotPixels { get; }
		/// <summary>Keyboard steps for the composition root: one range scale, 1° of EBL, 2% of the range for the VRM, 0.1 of gain, 0.05 of sea. They move the same sliders the mouse does.</summary>
		public void StepEbl(int direction) { _ebl.value = (float)(((Math.Round(_ebl.value) + direction) % 360 + 360) % 360); }
		public void StepVrm(int direction) { _vrm.value = Mathf.Clamp01(_vrm.value + direction * .02f); }
		public void StepGain(int direction) { _gain.value = Mathf.Clamp(_gain.value + direction * .1f, 0, 2); }
		public void StepSea(int direction) { _sea.value = Mathf.Clamp01(_sea.value + direction * .05f); }
		public float GainSetting => _gain.value;
		public float SeaSetting => _sea.value;
		public RadarRaster Raster { get; }
		public RadarPalette Palette { get; private set; }
		public bool HeadUp { get; private set; }
		public double EblTrue { get; private set; } = Math.PI / 4;
		public double VrmFraction { get; private set; } = .5;
		public double VrmMetres => VrmFraction * _engine.Scale.RangeMetres;
		public void ChangeRange(int direction)
		{
			_engine.SetRange(Math.Max(0, Math.Min(RadarScale.Count - 1, _engine.RangeIndex + direction)));
			Raster.RefreshAll(); UpdateMeasurements();
		}
		public void SetHeadUp(bool value)
		{
			HeadUp = value; _mode.text = value ? "HEAD UP" : "NORTH UP"; _orientation.text = value ? "NORTH UP" : "HEAD UP";
			UpdatePose(_headingRadians, true); UpdateMeasurements();
		}
		public void SetPalette(RadarPalette value)
		{
			Palette = value; var palette = value == RadarPalette.Day ? InstrumentPalette.Day : InstrumentPalette.Night;
			PaletteRoles.Apply(this, palette); style.backgroundColor = palette.Console; style.color = palette.Text;
			this.Query<Button>().ForEach(b => { b.style.backgroundColor = palette.Face; b.style.color = palette.Text; PaletteRoles.SetBorderColor(b, palette.Bezel); });
			this.Query<Slider>().ForEach(s => { s.style.color = palette.Text; s.Query<VisualElement>().ForEach(e => { if (e.name == "unity-dragger" || e.name == "unity-tracker") { e.style.backgroundColor = palette.Scale; PaletteRoles.SetBorderColor(e, palette.Bezel); } }); });
			Raster.SetPalette(value); _overlay.ApplyPalette(palette);
		}
		public void Refresh(double heading) { if (_lastRange != _engine.RangeIndex) UpdateMeasurements(); UpdatePose(heading); Raster.Refresh(); }
		public void SetAvailable(bool available) { _status.text = available ? "Not for navigation · *FTC inactive, no rain data" : "Not for navigation · waiting for run data"; }
		private void UpdatePose(double heading, bool force = false)
		{
			var changed = _headingRadians != heading; _headingRadians = heading; var degrees = (int)Math.Round(RadarMath.Wrap(heading) * 180 / Math.PI) % 360;
			if (_lastHeading != degrees) { _lastHeading = degrees; _heading.text = Headings[degrees]; }
			if (changed || force) _image.style.rotate = new Rotate(new Angle(HeadUp ? (float)(-heading * 180 / Math.PI) : 0));
			_relative.text = Relative[(int)Math.Round(RadarMath.RelativeBearing(EblTrue, heading) * 180 / Math.PI) % 360];
			if (changed || force) _overlay.MarkDirtyRepaint();
		}
		private void UpdateMeasurements()
		{
			_lastRange = _engine.RangeIndex;
			_relative.text = Relative[(int)Math.Round(RadarMath.RelativeBearing(EblTrue, _headingRadians) * 180 / Math.PI) % 360];
			var nm = _engine.Scale.RangeMetres / 1852;
			_range.text = nm.ToString("0.###", CultureInfo.InvariantCulture) + " nm";
			// EBL is stored in true bearings. Both readings stay available when changing picture orientation.
			_measurement.text = "RINGS " + (nm / 4).ToString("0.###", CultureInfo.InvariantCulture) + " nm   EBL " +
				(EblTrue * 180 / Math.PI).ToString("000", CultureInfo.InvariantCulture) + "°T   VRM " + (VrmMetres / 1852).ToString("0.000", CultureInfo.InvariantCulture) + " nm";
			_overlay.MarkDirtyRepaint();
		}
		private static string[] MakeRelative() { var result = new string[360]; for (var i = 0; i < 360; i++) result[i] = "EBL " + i.ToString("000", CultureInfo.InvariantCulture) + "°R · NO ARPA / TRAILS"; return result; }
		private static string[] MakeHeadings() { var result = new string[360]; for (var i = 0; i < 360; i++) result[i] = "HDG " + i.ToString("000", CultureInfo.InvariantCulture) + "°T"; return result; }
		private static VisualElement Row() { var r = new VisualElement(); r.style.flexDirection = FlexDirection.Row; r.style.justifyContent = Justify.SpaceBetween; r.style.alignItems = Align.Center; return r; }
		private static Label Text(string text, int size) { var l = PaletteRoles.Label(text, PaletteRoles.Text); l.style.fontSize = size; return l; }
		private static Slider Control(string title, float min, float max, float initial, Action<float> change)
		{
			var s = new Slider(title, min, max) { value = initial }; s.style.flexGrow = 1; s.style.flexBasis = 0; s.style.marginRight = 8;
			s.labelElement.style.minWidth = 32; s.labelElement.style.fontSize = 10;
			s.RegisterValueChangedCallback(e => change(e.newValue)); return s;
		}
		public void Dispose() => Raster.Dispose();
		private sealed class RadarOverlay : InstrumentElement
		{
			private readonly RadarView _view;
			internal RadarOverlay(RadarView view) { _view = view; }
			protected override void Draw(Painter2D p, Rect rect)
			{
				var c = rect.center; var radius = Mathf.Min(rect.width, rect.height) / 2 - 1;
				p.lineWidth = 1; p.strokeColor = Palette.Scale * new Color(1, 1, 1, .3f);
				for (var i = 1; i <= 4; i++) { p.BeginPath(); p.Arc(c, radius * i / 4, 0, 360); p.Stroke(); }
				var up = _view.HeadUp ? _view._headingRadians : 0;
				p.strokeColor = Palette.TextDim; Line(p, c, radius, _view._headingRadians - up);
				p.strokeColor = Palette.Text; Line(p, c, radius, _view.EblTrue - up);
				p.BeginPath(); p.Arc(c, radius * (float)_view.VrmFraction, 0, 360); p.Stroke();
				p.BeginPath(); p.MoveTo(c + new Vector2(-4, 0)); p.LineTo(c + new Vector2(4, 0)); p.MoveTo(c + new Vector2(0, -4)); p.LineTo(c + new Vector2(0, 4)); p.Stroke();
			}
			private static void Line(Painter2D p, Vector2 c, float r, double bearing) { p.BeginPath(); p.MoveTo(c); p.LineTo(c + new Vector2((float)Math.Sin(bearing) * r, -(float)Math.Cos(bearing) * r)); p.Stroke(); }
		}
	}
}
