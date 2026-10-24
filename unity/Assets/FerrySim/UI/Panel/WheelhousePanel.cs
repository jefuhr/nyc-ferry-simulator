using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The wheelhouse instrument console along the bottom of the screen. Port engine on the left and
	/// starboard on the right (where they are on the boat); heading, speeds and the rudders in the middle,
	/// with depth and contact beside them. It reads only <see cref="IVesselTelemetrySource"/> and converts
	/// to display units at the last moment.
	/// <para>
	/// With no source, or one whose <see cref="IVesselTelemetrySource.HasTelemetry"/> is false, every readout
	/// shows "---", no alarm is shown and the status bar says NO DATA, rather than zeros or stale values.
	/// Per-run state (the depth alarm and its acknowledgement, the contact episode) starts over whenever the
	/// source or its <see cref="IVesselTelemetrySource.RunId"/> changes, but not on a dropout within a run.
	/// Contact comes from the source's per-step latch, so a touch between two frames is never missed.
	/// </para>
	/// </summary>
	public sealed class WheelhousePanel : VisualElement, IPaletteAware
	{
		private const string ZoneUnknown = "NEW YORK";

		private readonly Label _vessel;
		private readonly Label _noData;
		private readonly Label _clockNote;
		private readonly Label _clock;
		private readonly Label _zone;
		private readonly Label _elapsed;
		private VesselParameters _parameters;
		private bool _parametersSeen;
		private InstrumentScales _scales = InstrumentScales.Generic;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private IVesselTelemetrySource _runSource;
		private long _runId;
		private bool _sampleShown;
		private ShownSample _shown;

		public WheelhousePanel()
		{
			name = "wheelhouse-panel";
			AddToClassList("fs-panel");
			AddToClassList(PaletteRoles.ConsoleBackground);
			pickingMode = PickingMode.Ignore;

			var status = Container("fs-statusbar");
			_vessel = PaletteRoles.Label(InstrumentScales.UnknownVesselName, PaletteRoles.Text, "fs-caption", "fs-strong");
			_vessel.name = "vessel-name";
			status.Add(_vessel);
			_noData = PaletteRoles.Label("NO DATA", null, "fs-caption", "fs-strong", "fs-nodata");
			_noData.name = "no-data";
			status.Add(_noData);
			var spacer = Container("fs-spacer");
			status.Add(spacer);
			_clockNote = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-clock-note");
			_clockNote.name = "clock-note";
			_clockNote.style.display = DisplayStyle.None;
			status.Add(_clockNote);
			_clock = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s");
			_clock.name = "clock";
			status.Add(_clock);
			_zone = PaletteRoles.Label(ZoneUnknown, PaletteRoles.Dim, "fs-unit");
			_zone.name = "clock-zone";
			status.Add(_zone);
			_elapsed = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Dim, "fs-unit", "fs-elapsed");
			_elapsed.name = "elapsed";
			status.Add(_elapsed);
			Add(status);

			var console = Container("fs-console");
			Port = new EngineColumn(EngineSide.Port);
			console.Add(Port);

			var centre = Container("fs-centre");
			centre.AddToClassList(PaletteRoles.FaceBackground);
			var top = Container("fs-centre-top");
			Compass = new CompassRose { name = "compass" };
			top.Add(Compass);

			var nav = Container("fs-nav");
			Heading = AddReadout(nav, "HEADING", "heading", "°T", "fs-value-xl");
			SpeedOverGround = AddReadout(nav, "SOG", "sog", "kn", "fs-value-xl");
			var secondary = Container("fs-nav-pair");
			CourseOverGround = AddReadout(secondary, "COG", "cog", "°T", "fs-value-m", PaletteRoles.Course);
			SpeedThroughWater = AddReadout(secondary, "STW", "stw", "kn", "fs-value-m");
			nav.Add(secondary);
			top.Add(nav);

			var aux = Container("fs-aux");
			Depth = new DepthReadout();
			Depth.RegisterCallback<ClickEvent>(_ => AcknowledgeAlarm());
			aux.Add(Depth);
			Contact = new ContactReadout();
			aux.Add(Contact);
			top.Add(aux);
			centre.Add(top);

			Rudder = new RudderIndicator { name = "rudder" };
			centre.Add(Rudder);
			console.Add(centre);

			Starboard = new EngineColumn(EngineSide.Starboard);
			console.Add(Starboard);
			Add(console);

			ShowNoData();
		}

		public EngineColumn Port { get; }

		public EngineColumn Starboard { get; }

		public CompassRose Compass { get; }

		public Label Heading { get; }

		public Label SpeedOverGround { get; }

		public Label CourseOverGround { get; }

		public Label SpeedThroughWater { get; }

		public RudderIndicator Rudder { get; }

		public DepthReadout Depth { get; }

		public ContactReadout Contact { get; }

		public Label Clock => _clock;

		public Label ClockZone => _zone;

		public Label Elapsed => _elapsed;

		public Label VesselName => _vessel;

		public bool ShowingNoData { get; private set; }

		public DepthAlarm Alarm { get; } = new DepthAlarm();

		public ContactMonitor ContactMonitor { get; } = new ContactMonitor();

		public void ApplyPalette(InstrumentPalette palette)
		{
			// Child instruments are reached by PaletteRoles.Apply walking the tree; only the badge is styled here.
			_noData.style.color = palette.Caution;
			PaletteRoles.SetBorderColor(_noData, palette.Caution);
		}

		public void AcknowledgeAlarm() => Alarm.Acknowledge();

		/// <param name="source">May be null.</param>
		/// <param name="unit">Depth unit setting.</param>
		/// <param name="depthAlarmThresholdMetres">Shallow-water alarm threshold, metres under keel.</param>
		/// <param name="realTime">Unscaled real time in seconds, for alarm flashing only.</param>
		public void Refresh(IVesselTelemetrySource source, DistanceUnit unit, double depthAlarmThresholdMetres, double realTime)
		{
			StartOverOnNewRun(source);
			_unit = unit;
			Alarm.ThresholdMetres = depthAlarmThresholdMetres;
			if (source == null || !source.HasTelemetry)
			{
				ShowNoData();
				return;
			}

			if (ShowingNoData)
			{
				ShowingNoData = false;
				_noData.style.display = DisplayStyle.None;
			}

			// A sample the panel already shows is not formatted again (paused, or several frames per step).
			var sample = new ShownSample(source, unit, Alarm.ThresholdMetres);
			if (!_sampleShown || !sample.SameAs(_shown))
			{
				ShowSample(source, sample, realTime);
			}

			// Flashing follows real time, so it is updated every frame.
			Depth.SetAlarm(Alarm.State, Alarm.IsLit(realTime));
		}

		public void ShowNoData()
		{
			_sampleShown = false;
			// The alarm setting stays visible without data; it is cached, so this costs nothing per frame.
			Depth.ShowSetting(Alarm.ThresholdMetres, _unit);
			PaletteRoles.SetText(Depth.Unit, DisplayUnits.Symbol(_unit));
			if (ShowingNoData)
			{
				return;
			}

			ShowingNoData = true;
			_noData.style.display = DisplayStyle.Flex;
			Port.ShowNoData();
			Starboard.ShowNoData();
			Compass.SetHeading(double.NaN, double.NaN);
			PaletteRoles.SetText(Heading, InstrumentFormat.NoValue);
			PaletteRoles.SetText(CourseOverGround, InstrumentFormat.NoValue);
			PaletteRoles.SetText(SpeedOverGround, InstrumentFormat.NoValue);
			PaletteRoles.SetText(SpeedThroughWater, InstrumentFormat.NoValue);
			Rudder.SetAngles(double.NaN, double.NaN);
			Depth.ShowValue(InstrumentFormat.NoValue, _unit);
			Depth.SetAlarm(AlarmState.Normal, false);
			Contact.ShowNoData();
			ShowNoClock();
			PaletteRoles.SetText(_elapsed, InstrumentFormat.NoValue);
		}

		/// <summary>Forgets everything that belongs to the previous run when the source or its run changes.</summary>
		private void StartOverOnNewRun(IVesselTelemetrySource source)
		{
			var runId = source != null ? source.RunId : 0L;
			if (ReferenceEquals(source, _runSource) && runId == _runId)
			{
				return;
			}

			_runSource = source;
			_runId = runId;
			Alarm.Reset();
			ContactMonitor.Reset();
			_sampleShown = false;
		}

		private void ShowSample(IVesselTelemetrySource source, in ShownSample sample, double realTime)
		{
			UpdateScales(source.Parameters);
			var state = sample.State;
			var telemetry = sample.Telemetry;
			var controls = sample.Controls;

			Port.Show(telemetry.Port, controls.PortLever);
			Starboard.Show(telemetry.Starboard, controls.StarboardLever);

			var course = InstrumentFormat.Course(telemetry.CourseOverGround, telemetry.SpeedOverGround);
			Compass.SetHeading(state.Heading, course == InstrumentFormat.NoValue ? double.NaN : telemetry.CourseOverGround);
			PaletteRoles.SetText(Heading, InstrumentFormat.Compass(state.Heading));
			PaletteRoles.SetText(CourseOverGround, course);
			PaletteRoles.SetText(SpeedOverGround, InstrumentFormat.Speed(telemetry.SpeedOverGround));
			PaletteRoles.SetText(SpeedThroughWater, InstrumentFormat.Speed(telemetry.SpeedThroughWater));
			Rudder.SetAngles(telemetry.PortRudderAngle, telemetry.StarboardRudderAngle);

			Alarm.Update(telemetry.DepthUnderKeel, realTime);
			Depth.ShowValue(InstrumentFormat.Distance(telemetry.DepthUnderKeel, _unit), _unit);
			Depth.ShowSetting(Alarm.ThresholdMetres, _unit);
			// Contact comes from the latch; the reading at this step only says whether contact is measurable at all.
			ContactMonitor.Update(sample.Contact, state.Time,
				InstrumentFormat.IsFinite(telemetry.ContactForce) && InstrumentFormat.IsFinite(state.Time));
			Contact.Show(ContactMonitor);

			if (NewYorkTime.TryToLocal(sample.ScenarioStartUtc, state.Time, out var local, out var daylight))
			{
				PaletteRoles.SetText(_clock, InstrumentFormat.Clock(local));
				PaletteRoles.SetText(_zone, NewYorkTime.Abbreviation(daylight));
				// CSV playback without a recorded start uses a setting; say so rather than present it as the scenario's.
				ShowClockNote(source is FakeTelemetrySource fake && fake.ScenarioStartIsFallback ? "ASSUMED START" : string.Empty);
			}
			else
			{
				ShowNoClock();
			}

			PaletteRoles.SetText(_elapsed, InstrumentFormat.Elapsed(state.Time));
			_shown = sample;
			_sampleShown = true;
		}

		private void ShowNoClock()
		{
			PaletteRoles.SetText(_clock, InstrumentFormat.NoValue);
			PaletteRoles.SetText(_zone, ZoneUnknown);
			ShowClockNote(string.Empty);
		}

		private void ShowClockNote(string text)
		{
			if (_clockNote.text == text)
			{
				return;
			}

			_clockNote.text = text;
			_clockNote.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void UpdateScales(VesselParameters parameters)
		{
			if (_parametersSeen && ReferenceEquals(parameters, _parameters))
			{
				return;
			}

			_parametersSeen = true;
			_parameters = parameters;
			_scales = InstrumentScales.From(parameters);
			PaletteRoles.SetText(_vessel, _scales.VesselName);
			Port.SetScales(_scales);
			Starboard.SetScales(_scales);
			Rudder.SetScales(_scales);
		}

		private static Label AddReadout(VisualElement parent, string caption, string valueName, string unit, string valueClass, string captionRole = PaletteRoles.Dim)
		{
			var readout = Container("fs-readout");
			readout.Add(PaletteRoles.Label(caption, captionRole, "fs-caption"));
			var row = Container("fs-value-row");
			var value = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, valueClass);
			value.name = valueName;
			row.Add(value);
			row.Add(PaletteRoles.Label(unit, PaletteRoles.Dim, "fs-unit", unit.StartsWith("°") ? "fs-unit--tight" : "fs-unit--spaced"));
			readout.Add(row);
			parent.Add(readout);
			return value;
		}

		private static VisualElement Container(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}

		/// <summary>Everything a refresh shows, compared field by field without boxing to tell a new sample from one already on screen.</summary>
		private readonly struct ShownSample
		{
			public ShownSample(IVesselTelemetrySource source, DistanceUnit unit, double threshold)
			{
				State = source.State;
				Telemetry = source.Telemetry;
				Controls = source.Controls;
				Contact = source.Contact;
				ScenarioStartUtc = source.ScenarioStartUtc;
				Parameters = source.Parameters;
				ClockAssumed = source is FakeTelemetrySource fake && fake.ScenarioStartIsFallback;
				Unit = unit;
				Threshold = threshold;
			}

			public VesselState State { get; }

			public VesselTelemetry Telemetry { get; }

			public ControlInputs Controls { get; }

			public ContactLatch Contact { get; }

			public System.DateTimeOffset ScenarioStartUtc { get; }

			public VesselParameters Parameters { get; }

			public bool ClockAssumed { get; }

			public DistanceUnit Unit { get; }

			public double Threshold { get; }

			public bool SameAs(in ShownSample other) =>
				State.Equals(other.State) && Controls.Equals(other.Controls) && SameTelemetry(Telemetry, other.Telemetry) &&
				SameLatch(Contact, other.Contact) && ScenarioStartUtc.Equals(other.ScenarioStartUtc) &&
				ReferenceEquals(Parameters, other.Parameters) && ClockAssumed == other.ClockAssumed && Unit == other.Unit &&
				Threshold.Equals(other.Threshold);

			private static bool SameTelemetry(in VesselTelemetry a, in VesselTelemetry b) =>
				SameEngine(a.Port, b.Port) && SameEngine(a.Starboard, b.Starboard) &&
				a.PortRudderAngle.Equals(b.PortRudderAngle) && a.StarboardRudderAngle.Equals(b.StarboardRudderAngle) &&
				a.SpeedThroughWater.Equals(b.SpeedThroughWater) && a.SpeedOverGround.Equals(b.SpeedOverGround) &&
				a.CourseOverGround.Equals(b.CourseOverGround) && a.DepthUnderKeel.Equals(b.DepthUnderKeel) &&
				a.ContactForce.Equals(b.ContactForce);

			private static bool SameEngine(in EngineTelemetry a, in EngineTelemetry b) =>
				a.EngineSpeed.Equals(b.EngineSpeed) && a.ShaftSpeed.Equals(b.ShaftSpeed) && a.Gear == b.Gear && a.Throttle.Equals(b.Throttle);

			private static bool SameLatch(in ContactLatch a, in ContactLatch b) =>
				a.StepsWithContact == b.StepsWithContact && a.LastContactStep == b.LastContactStep &&
				a.LastContactForce.Equals(b.LastContactForce) && a.PeakContactForce.Equals(b.PeakContactForce) &&
				a.PeakContactStep == b.PeakContactStep;
		}
	}
}
