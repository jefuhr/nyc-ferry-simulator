using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Reviews a recorded run (a CLI or Unity telemetry CSV, or a recording handed over in memory): the
	/// track, speed, heading, controls and rudder against time, gear shifts, contacts and depth alarms
	/// marked where they happened, and a cursor that can be scrubbed from any plot, the track or the
	/// timeline, with every value at the cursor listed beside the track. A note under the events says
	/// whether the depth alarms are the recorded ones or a retrospective reconstruction at the current
	/// threshold, and when that threshold is the unconfirmed default.
	/// </summary>
	public sealed class DebriefScreen : VisualElement, IPaletteAware
	{
		private readonly Label _subtitle;
		private readonly Label _error;
		private readonly Label _eventsNote;
		private readonly TextField _path;
		private readonly TrackPlot _track;
		private readonly TimeSeriesPlot _speed;
		private readonly TimeSeriesPlot _heading;
		private readonly TimeSeriesPlot _controls;
		private readonly TimeSeriesPlot _rudder;
		private readonly EventTimeline _timeline;
		private readonly Label _timeLabel;
		private readonly ScrollView _eventList;
		private readonly Dictionary<string, Label> _readouts = new Dictionary<string, Label>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private double _threshold = DepthAlarm.DefaultThresholdMetres;
		private List<DebriefEvent> _events = new List<DebriefEvent>();

		public DebriefScreen()
		{
			name = "debrief";
			AddToClassList("fs-debrief");
			AddToClassList(PaletteRoles.ConsoleBackground);
			focusable = true;

			var header = Row("fs-debrief-header");
			header.Add(PaletteRoles.Label("DEBRIEF", PaletteRoles.Text, "fs-value-s", "fs-strong"));
			_subtitle = PaletteRoles.Label("No recording loaded", PaletteRoles.Dim, "fs-caption", "fs-debrief-subtitle");
			_subtitle.name = "debrief-subtitle";
			header.Add(_subtitle);
			_path = new TextField { name = "debrief-path", value = UIRepoPaths.SampleCrashStopCsv };
			_path.AddToClassList("fs-path-field");
			_path.AddToClassList(PaletteRoles.Text);
			header.Add(_path);
			header.Add(Button("Load CSV", () => TryLoad(_path.value), "debrief-load"));
			header.Add(Button("Close", () => CloseRequested?.Invoke(), "debrief-close"));
			Add(header);
			_error = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-debrief-error");
			_error.name = "debrief-error";
			_error.style.display = DisplayStyle.None;
			Add(_error);

			var body = Row("fs-debrief-body");
			var left = Column("fs-debrief-left");
			_track = new TrackPlot();
			_track.Scrubbed += SetCursorTime;
			left.Add(_track);
			var readouts = Column("fs-debrief-readouts");
			AddReadoutRow(readouts, ("time", "TIME"), ("sog", "SOG"), ("stw", "STW"));
			AddReadoutRow(readouts, ("heading", "HDG"), ("cog", "COG"), ("depth", "DEPTH"));
			AddReadoutRow(readouts, ("port", "PORT"), ("stbd", "STBD"), ("contact", "CONTACT"));
			AddReadoutRow(readouts, ("rudder", "RUDDER"), ("helm", "HELM"), ("levers", "LEVERS"));
			left.Add(readouts);
			left.Add(PaletteRoles.Label("EVENTS", PaletteRoles.Dim, "fs-caption"));
			_eventsNote = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-debrief-note");
			_eventsNote.name = "debrief-events-note";
			left.Add(_eventsNote);
			_eventList = new ScrollView(ScrollViewMode.Vertical) { name = "debrief-events" };
			_eventList.AddToClassList("fs-event-list");
			left.Add(_eventList);
			body.Add(left);

			var right = Column("fs-debrief-right");
			_speed = Plot("SPEED (kn)", "plot-speed", right);
			_heading = Plot("HEADING (° true)", "plot-heading", right);
			_controls = Plot("CONTROLS (% of travel, + ahead / starboard)", "plot-controls", right);
			_rudder = Plot("RUDDER ANGLE (°, + starboard)", "plot-rudder", right);
			_rudder.ShowTimeAxis = true;
			body.Add(right);
			Add(body);

			var footer = Row("fs-debrief-footer");
			footer.Add(Button("<", () => Step(-1), "debrief-step-back"));
			_timeline = new EventTimeline();
			_timeline.Scrubbed += SetCursorTime;
			footer.Add(_timeline);
			footer.Add(Button(">", () => Step(1), "debrief-step-forward"));
			_timeLabel = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s", "fs-debrief-time");
			_timeLabel.name = "debrief-cursor-time";
			footer.Add(_timeLabel);
			Add(footer);

			RegisterCallback<KeyDownEvent>(OnKeyDown);
		}

		public event Action CloseRequested;

		public TelemetryRecording Recording { get; private set; }

		public VesselParameters Parameters { get; private set; }

		public IReadOnlyList<DebriefEvent> Events => _events;

		public double CursorTime { get; private set; } = double.NaN;

		public int CursorIndex { get; private set; } = -1;

		public TrackPlot Track => _track;

		public TimeSeriesPlot SpeedPlot => _speed;

		public TimeSeriesPlot HeadingPlot => _heading;

		public TimeSeriesPlot ControlsPlot => _controls;

		public TimeSeriesPlot RudderPlot => _rudder;

		public EventTimeline Timeline => _timeline;

		public string ErrorText => _error.text;

		public DistanceUnit DistanceUnit
		{
			get => _unit;
			set
			{
				_unit = value;
				_track.DistanceUnit = value;
				RebuildEventList();
				UpdateEventsNote();
				UpdateReadouts();
			}
		}

		public double DepthAlarmThresholdMetres
		{
			get => _threshold;
			set
			{
				_threshold = value;
				DetectEvents();
			}
		}

		/// <summary>The text of a cursor readout by key (time, sog, stw, heading, cog, depth, port, stbd, contact, rudder, helm, levers).</summary>
		public string ReadoutText(string key) => _readouts.TryGetValue(key, out var label) ? label.text : null;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			this.Query<Button>(className: "fs-button").ForEach(button => StyleButton(button, _palette));
			var input = _path.Q(className: TextField.inputUssClassName);
			if (input != null)
			{
				input.style.backgroundColor = _palette.Face;
				input.style.color = _palette.Text;
				PaletteRoles.SetBorderColor(input, _palette.Bezel);
			}

			RebuildEventList();
		}

		/// <summary>Buttons take their colors from the palette too, so nothing shows neutral grey light at night.</summary>
		internal static void StyleButton(Button button, InstrumentPalette palette)
		{
			button.style.color = palette.Text;
			button.style.backgroundColor = palette.Face;
			PaletteRoles.SetBorderColor(button, palette.Bezel);
		}

		/// <summary>Shows a recording; <paramref name="parameters"/> may be null (the boat is then drawn at a minimum size).</summary>
		public void Show(TelemetryRecording recording, VesselParameters parameters)
		{
			Recording = recording ?? throw new ArgumentNullException(nameof(recording));
			Parameters = parameters;
			_error.style.display = DisplayStyle.None;
			var timeRange = new AxisRange(recording.StartTime, recording.StartTime + Math.Max(recording.Duration, 1e-3));
			var count = recording.Count;
			var sog = new double[count];
			var stw = new double[count];
			var heading = new double[count];
			var cog = new double[count];
			var portLever = new double[count];
			var starboardLever = new double[count];
			var helm = new double[count];
			var portRudder = new double[count];
			var starboardRudder = new double[count];
			for (var i = 0; i < count; i++)
			{
				sog[i] = DisplayUnits.Knots(recording.SpeedOverGround[i]);
				stw[i] = DisplayUnits.Knots(recording.SpeedThroughWater[i]);
				heading[i] = DisplayUnits.CompassDegrees(recording.Heading[i]);
				cog[i] = Math.Abs(sog[i]) >= InstrumentFormat.MinimumSpeedForCourseKnots ? DisplayUnits.CompassDegrees(recording.CourseOverGround[i]) : double.NaN;
				portLever[i] = recording.PortLever[i] * 100.0;
				starboardLever[i] = recording.StarboardLever[i] * 100.0;
				helm[i] = recording.Helm[i] * 100.0;
				portRudder[i] = DisplayUnits.Degrees(recording.PortRudder[i]);
				starboardRudder[i] = DisplayUnits.Degrees(recording.StarboardRudder[i]);
			}

			var time = recording.Time;
			var maneuverStart = recording.HeaderDouble("maneuver_start_s", double.NaN);
			foreach (var plot in new[] { _speed, _heading, _controls, _rudder })
			{
				plot.MarkTime = maneuverStart;
			}

			_speed.SetData(time, new[]
			{
				new PlotSeriesData("SOG", sog, SeriesStyle.PrimarySolid),
				new PlotSeriesData("STW", stw, SeriesStyle.SecondaryDashed),
			}, timeRange, null, 0.0, false);
			_heading.SetData(time, new[]
			{
				new PlotSeriesData("HDG", heading, SeriesStyle.PrimarySolid),
				new PlotSeriesData("COG", cog, SeriesStyle.SecondaryDashed),
			}, timeRange, new AxisRange(0.0, 360.0), 90.0, true);
			_controls.SetData(time, new[]
			{
				new PlotSeriesData("port lever", portLever, SeriesStyle.PortSolid, stepped: true),
				new PlotSeriesData("stbd lever", starboardLever, SeriesStyle.StarboardDashed, stepped: true),
				new PlotSeriesData("helm", helm, SeriesStyle.NeutralThin, stepped: true),
			}, timeRange, new AxisRange(-100.0, 100.0), 50.0, false);
			var rudderLimit = 10.0;
			for (var i = 0; i < count; i++)
			{
				rudderLimit = Math.Max(rudderLimit, Math.Max(Math.Abs(portRudder[i]), Math.Abs(starboardRudder[i])));
			}

			var rudderRange = PlotScale.NiceRange(-rudderLimit, rudderLimit, 4, out var rudderStep);
			_rudder.SetData(time, new[]
			{
				new PlotSeriesData("port rudder", portRudder, SeriesStyle.PortSolid),
				new PlotSeriesData("stbd rudder", starboardRudder, SeriesStyle.StarboardDashed),
			}, timeRange, rudderRange, rudderStep, false);

			var length = parameters?.Particulars?.LengthOverall;
			_track.SetData(recording, _events, length != null && length.Unit == "m" ? length.Value : double.NaN);
			var vessel = InstrumentScales.From(parameters).VesselName;
			var maneuver = recording.HeaderValue("maneuver");
			PaletteRoles.SetText(_subtitle, string.Join("  ·  ", new[]
			{
				recording.Label, vessel, maneuver ?? "run",
				recording.Duration.ToString("F1", CultureInfo.InvariantCulture) + " s", recording.Count + " samples",
			}));
			DetectEvents();
			SetCursorTime(recording.StartTime);
		}

		/// <summary>Loads a CSV (absolute or repository-relative) and the vessel it names. Errors are shown on screen and returned false.</summary>
		public bool TryLoad(string path)
		{
			try
			{
				var recording = TelemetryCsv.Load(UIRepoPaths.Resolve(path ?? string.Empty));
				var parameters = VesselFiles.TryLoad(recording.VesselId, out _);
				_path.SetValueWithoutNotify(path);
				Show(recording, parameters);
				return true;
			}
			catch (Exception error) when (error is TelemetryCsvException || error is System.IO.IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				PaletteRoles.SetText(_error, error.Message);
				_error.style.display = DisplayStyle.Flex;
				return false;
			}
		}

		public void SetCursorTime(double time)
		{
			if (Recording == null || double.IsNaN(time))
			{
				return;
			}

			CursorTime = Math.Max(Recording.StartTime, Math.Min(Recording.EndTime, time));
			CursorIndex = Recording.IndexAtOrBefore(CursorTime);
			foreach (var plot in new[] { _speed, _heading, _controls, _rudder })
			{
				plot.SetCursor(CursorTime);
			}

			_timeline.SetCursor(CursorTime);
			_track.SetCursorIndex(CursorIndex);
			UpdateReadouts();
		}

		/// <summary>Moves the cursor by whole samples.</summary>
		public void Step(int samples)
		{
			if (Recording == null)
			{
				return;
			}

			var index = Math.Max(0, Math.Min(Recording.Count - 1, Math.Max(0, CursorIndex) + samples));
			SetCursorTime(Recording.Time[index]);
		}

		private void DetectEvents()
		{
			_events = Recording != null ? DebriefEvents.Detect(Recording, _threshold) : new List<DebriefEvent>();
			foreach (var plot in new[] { _speed, _heading, _controls, _rudder })
			{
				plot.SetEvents(_events);
			}

			_track.SetEvents(_events);
			if (Recording != null)
			{
				_timeline.SetData(new AxisRange(Recording.StartTime, Recording.StartTime + Math.Max(Recording.Duration, 1e-3)), _events);
			}

			RebuildEventList();
			UpdateEventsNote();
		}

		/// <summary>Says where the depth alarm marks come from, so a recalculation is never mistaken for a record.</summary>
		private void UpdateEventsNote()
		{
			string text;
			if (Recording == null)
			{
				text = string.Empty;
			}
			else if (DebriefEvents.UsesRecordedThresholds(Recording))
			{
				text = "Depth alarms as recorded: each uses the threshold in force at the time.";
				foreach (var threshold in Recording.DepthAlarmThreshold)
				{
					if (DepthAlarm.IsDefaultThreshold(threshold))
					{
						text += $" The {InstrumentFormat.Distance(threshold, _unit)} {DisplayUnits.Symbol(_unit)} default among them is a placeholder, pending crew input.";
						break;
					}
				}
			}
			else
			{
				text = $"Depth alarms are retrospective, using the current threshold: below {InstrumentFormat.Distance(_threshold, _unit)} {DisplayUnits.Symbol(_unit)}" +
					(DepthAlarm.IsDefaultThreshold(_threshold) ? $" ({DepthAlarm.PlaceholderNote})." : ".");
			}

			PaletteRoles.SetText(_eventsNote, text);
			_eventsNote.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void RebuildEventList()
		{
			_eventList.Clear();
			if (_events.Count == 0)
			{
				var none = PaletteRoles.Label(Recording == null ? string.Empty : "No gear shifts, contacts or depth alarms.", null, "fs-caption");
				none.style.color = _palette.TextDim;
				_eventList.Add(none);
				return;
			}

			foreach (var e in _events)
			{
				var time = e.Time;
				var item = new Button(() => SetCursorTime(time))
				{
					text = time.ToString("F1", CultureInfo.InvariantCulture) + " s   " + e.Describe(_unit),
				};
				item.AddToClassList("fs-event-item");
				item.style.color = _palette.Text;
				item.style.backgroundColor = _palette.Face;
				PaletteRoles.SetBorderColor(item, TimeSeriesPlot.EventColor(_palette, e.Kind));
				_eventList.Add(item);
			}
		}

		private void UpdateReadouts()
		{
			if (Recording == null || CursorIndex < 0)
			{
				return;
			}

			var sample = Recording.Sample(CursorIndex);
			var state = sample.State;
			var telemetry = sample.Telemetry;
			var controls = sample.Controls;
			var elapsed = state.Time - Recording.StartTime;
			PaletteRoles.SetText(_timeLabel, $"t = {state.Time.ToString("F1", CultureInfo.InvariantCulture)} s  ({InstrumentFormat.Elapsed(elapsed)})");
			Set("time", state.Time.ToString("F1", CultureInfo.InvariantCulture) + " s");
			Set("sog", InstrumentFormat.Speed(telemetry.SpeedOverGround) + " kn");
			Set("stw", InstrumentFormat.Speed(telemetry.SpeedThroughWater) + " kn");
			Set("heading", InstrumentFormat.Compass(state.Heading) + "°T");
			var course = InstrumentFormat.Course(telemetry.CourseOverGround, telemetry.SpeedOverGround);
			Set("cog", course == InstrumentFormat.NoValue ? course : course + "°T");
			Set("depth", InstrumentFormat.Distance(telemetry.DepthUnderKeel, _unit) + " " + DisplayUnits.Symbol(_unit));
			Set("port", EngineText(telemetry.Port));
			Set("stbd", EngineText(telemetry.Starboard));
			Set("contact", ContactText(telemetry.ContactForce));
			Set("rudder", InstrumentFormat.Rudder(telemetry.PortRudderAngle) + " / " + InstrumentFormat.Rudder(telemetry.StarboardRudderAngle));
			Set("helm", HelmText(controls.Helm));
			Set("levers", LeverText(controls.PortLever) + " / " + LeverText(controls.StarboardLever));
		}

		/// <summary>An unknown (non-finite) force is "---", not "none": the recording cannot say.</summary>
		private static string ContactText(double newtons) =>
			!InstrumentFormat.IsFinite(newtons) ? InstrumentFormat.NoValue :
			newtons > 0.0 ? InstrumentFormat.Force(newtons) + " kN" :
			"none";

		private static string EngineText(EngineTelemetry engine)
		{
			var gear = GearReadout.From(engine);
			return gear.Label + "  " + InstrumentFormat.Rpm(engine.EngineSpeed) + " rpm";
		}

		private static string LeverText(double lever)
		{
			var direction = InstrumentFormat.LeverDirection(lever);
			return direction == "NEUTRAL" ? "N" : InstrumentFormat.LeverPercent(lever) + (direction == "AHEAD" ? " AHD" : " AST");
		}

		private static string HelmText(double helm)
		{
			var percent = Math.Round(helm * 100.0, MidpointRounding.AwayFromZero);
			return percent == 0.0 ? "midships" : Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture) + "% " + (percent > 0.0 ? "S" : "P");
		}

		private void Set(string key, string text)
		{
			if (_readouts.TryGetValue(key, out var label))
			{
				PaletteRoles.SetText(label, text);
			}
		}

		private void AddReadoutRow(VisualElement parent, params (string key, string caption)[] cells)
		{
			var row = Row("fs-debrief-readout-row");
			foreach (var (key, caption) in cells)
			{
				var cell = Column("fs-debrief-readout");
				cell.Add(PaletteRoles.Label(caption, PaletteRoles.Dim, "fs-caption"));
				var value = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-xs");
				value.name = "debrief-" + key;
				cell.Add(value);
				_readouts[key] = value;
				row.Add(cell);
			}

			parent.Add(row);
		}

		private TimeSeriesPlot Plot(string title, string plotName, VisualElement parent)
		{
			var plot = new TimeSeriesPlot(title, plotName);
			plot.Scrubbed += SetCursorTime;
			parent.Add(plot);
			return plot;
		}

		private void OnKeyDown(KeyDownEvent evt)
		{
			var step = evt.shiftKey ? 10 : 1;
			switch (evt.keyCode)
			{
				case KeyCode.LeftArrow:
					Step(-step);
					break;
				case KeyCode.RightArrow:
					Step(step);
					break;
				case KeyCode.Home:
					Step(int.MinValue / 2);
					break;
				case KeyCode.End:
					Step(int.MaxValue / 2);
					break;
				case KeyCode.Escape:
					CloseRequested?.Invoke();
					break;
				default:
					return;
			}

			evt.StopPropagation();
		}

		private static Button Button(string text, Action onClick, string buttonName)
		{
			var button = new Button(onClick) { text = text, name = buttonName };
			button.AddToClassList("fs-button");
			button.AddToClassList(PaletteRoles.Text);
			return button;
		}

		private static VisualElement Row(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}

		private static VisualElement Column(string className) => Row(className);
	}
}
