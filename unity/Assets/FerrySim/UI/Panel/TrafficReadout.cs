using System;
using System.Globalization;
using FerrySim.Core.Traffic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The CPA alarm, on runs with traffic: one line docked to the top edge of the wheelhouse panel, right-aligned above the
	/// starboard column, so every instrument below keeps its place. It shows the alarm state and the nearest threatening vessel's
	/// CPA, TCPA and true bearing, and its name: ALARM (a vessel closing to the alarm's CPA within its TCPA) and COLLISION (the hull
	/// overlapping a vessel's rectangle) fill it in the alarm colour, steady (the depth alarm stays the panel's only flashing
	/// element); CLEAR shows the nearest vessel within a nautical mile, plain; NO TRAFFIC NEAR says so, dim. The limits are
	/// estimates pending crew review (Core Traffic/README.md, Alarm thresholds). Its text is rebuilt only when what it shows
	/// changes, so a refresh costs nothing per frame.
	/// </summary>
	public sealed class TrafficReadout : VisualElement, IPaletteAware
	{
		private readonly Label _state;
		private readonly Label _cpa;
		private readonly Label _tcpa;
		private readonly Label _bearing;
		private readonly Label _name;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private TrafficAlarmState _shownState = (TrafficAlarmState)(-1);
		private bool _shownData = true;
		private bool _shown = true;
		private ShownValues _values;

		public TrafficReadout()
		{
			name = "traffic";
			AddToClassList("fs-traffic");
			pickingMode = PickingMode.Ignore;
			_state = Part("traffic-state", "fs-caption", "fs-strong", "fs-traffic-state");
			_cpa = Part("traffic-cpa", "fs-value-xs");
			_tcpa = Part("traffic-tcpa", "fs-value-xs");
			_bearing = Part("traffic-bearing", "fs-value-xs");
			_name = Part("traffic-name", "fs-caption", "fs-traffic-name");
			SetShown(false);
		}

		/// <summary>ALARM, COLLISION, CLEAR or NO TRAFFIC NEAR.</summary>
		public string StateText => _state.text;

		/// <summary>CPA, TCPA and bearing, for example <c>CPA 45 m  TCPA 1:12  BRG 045°T</c>.</summary>
		public string ValueText => _cpa.text + "  " + _tcpa.text + "  " + _bearing.text;

		/// <summary>The target's AIS name (or its MMSI).</summary>
		public string NameText => _name.text;

		/// <summary>The state shown, or −1 before anything was.</summary>
		public TrafficAlarmState ShownState => _shownState;

		/// <summary>True while the strip is shown (a run with traffic).</summary>
		public bool Showing => _shown;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			Restyle();
		}

		/// <summary>Shows the strip for a run with traffic, or hides it.</summary>
		public void SetShown(bool on)
		{
			if (on == _shown)
			{
				return;
			}

			_shown = on;
			style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
		}

		/// <summary>The readout for the latest step, CPA in the panel's distance unit.</summary>
		public void Show(in TrafficAlarmReadout readout, DistanceUnit unit)
		{
			var values = new ShownValues(readout, unit);
			if (_shownData && values.Equals(_values) && readout.State == _shownState)
			{
				return;
			}

			_values = values;
			_shownData = true;
			if (readout.State == TrafficAlarmState.None || readout.Target == null)
			{
				PaletteRoles.SetText(_state, "NO TRAFFIC NEAR");
				PaletteRoles.SetText(_cpa, "CPA " + InstrumentFormat.NoValue);
				PaletteRoles.SetText(_tcpa, "TCPA " + InstrumentFormat.NoValue);
				PaletteRoles.SetText(_bearing, "BRG " + InstrumentFormat.NoValue);
				PaletteRoles.SetText(_name, string.Empty);
			}
			else
			{
				var target = readout.Target;
				PaletteRoles.SetText(_state, readout.State == TrafficAlarmState.Collision ? "COLLISION" :
					readout.State == TrafficAlarmState.Alarm ? (readout.Alarms > 1 ? "ALARM ×" + readout.Alarms.ToString(CultureInfo.InvariantCulture) : "ALARM") : "CLEAR");
				PaletteRoles.SetText(_cpa, "CPA " + InstrumentFormat.Distance(readout.Cpa, unit) + " " + DisplayUnits.Symbol(unit));
				PaletteRoles.SetText(_tcpa, "TCPA " + Tcpa(readout.Tcpa));
				PaletteRoles.SetText(_bearing, "BRG " + InstrumentFormat.Compass(readout.Bearing) + "°T");
				PaletteRoles.SetText(_name, string.IsNullOrEmpty(target.Name) ? "MMSI " + target.Mmsi.ToString(CultureInfo.InvariantCulture) : target.Name);
			}

			if (readout.State != _shownState)
			{
				_shownState = readout.State;
				Restyle();
			}
		}

		public void ShowNoData()
		{
			if (!_shownData)
			{
				return;
			}

			_shownData = false;
			PaletteRoles.SetText(_state, "TRAFFIC");
			PaletteRoles.SetText(_cpa, "CPA " + InstrumentFormat.NoValue);
			PaletteRoles.SetText(_tcpa, "TCPA " + InstrumentFormat.NoValue);
			PaletteRoles.SetText(_bearing, "BRG " + InstrumentFormat.NoValue);
			PaletteRoles.SetText(_name, string.Empty);
			_shownState = TrafficAlarmState.None;
			Restyle();
		}

		/// <summary>Minutes and seconds to the closest point, or PAST once it is behind.</summary>
		public static string Tcpa(double seconds)
		{
			if (!InstrumentFormat.IsFinite(seconds))
			{
				return InstrumentFormat.NoValue;
			}

			if (seconds < 0.0)
			{
				return "PAST";
			}

			var whole = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
			return whole >= 6000 ? ">99 min" : (whole / 60).ToString(CultureInfo.InvariantCulture) + ":" + (whole % 60).ToString("00", CultureInfo.InvariantCulture);
		}

		private Label Part(string partName, params string[] classes)
		{
			var label = PaletteRoles.Label(string.Empty, null, classes);
			label.name = partName;
			Add(label);
			return label;
		}

		private void Restyle()
		{
			var alarm = _shownState == TrafficAlarmState.Alarm || _shownState == TrafficAlarmState.Collision;
			var text = alarm ? _palette.AlarmText : _shownState == TrafficAlarmState.Clear ? _palette.Text : _palette.TextDim;
			style.backgroundColor = alarm ? _palette.Alarm : _palette.Console;
			PaletteRoles.SetBorderColor(this, alarm ? _palette.Alarm : _palette.Bezel);
			_state.style.color = text;
			_cpa.style.color = text;
			_tcpa.style.color = text;
			_bearing.style.color = text;
			_name.style.color = alarm ? _palette.AlarmText : _palette.TextDim;
		}

		/// <summary>What the strip shows, at its displayed resolution, compared without boxing.</summary>
		private readonly struct ShownValues : IEquatable<ShownValues>
		{
			public ShownValues(in TrafficAlarmReadout readout, DistanceUnit unit)
			{
				State = readout.State;
				Mmsi = readout.Target?.Mmsi ?? 0;
				Alarms = readout.Alarms;
				// The CPA's text has a decimal below 100 (InstrumentFormat.Distance): compared at that resolution.
				var cpa = DisplayUnits.Distance(readout.Cpa, unit);
				Cpa = InstrumentFormat.IsFinite(cpa) ? Math.Round(cpa, Math.Abs(cpa) < 99.95 ? 1 : 0, MidpointRounding.AwayFromZero) : double.NaN;
				Tcpa = readout.Tcpa < 0.0 ? -1.0 : Round(readout.Tcpa);
				Bearing = Round(DisplayUnits.CompassDegrees(readout.Bearing));
				Unit = unit;
			}

			public TrafficAlarmState State { get; }
			public int Mmsi { get; }
			public int Alarms { get; }
			public double Cpa { get; }
			public double Tcpa { get; }
			public double Bearing { get; }
			public DistanceUnit Unit { get; }

			public bool Equals(ShownValues other) =>
				State == other.State && Mmsi == other.Mmsi && Alarms == other.Alarms && Cpa.Equals(other.Cpa) && Tcpa.Equals(other.Tcpa) && Bearing.Equals(other.Bearing) && Unit == other.Unit;

			private static double Round(double value) => InstrumentFormat.IsFinite(value) ? Math.Round(value, 0, MidpointRounding.AwayFromZero) : double.NaN;
		}
	}
}
