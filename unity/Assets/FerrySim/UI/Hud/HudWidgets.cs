using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>What every widget reads on a frame: the telemetry, and what the panel already worked out (alarm, environment).</summary>
	public readonly struct HudFrame
	{
		public HudFrame(IVesselTelemetrySource source, WheelhousePanel panel, DistanceUnit unit, double thresholdMetres, double realTime, IHudScreenSource screens)
		{
			Source = source;
			Panel = panel;
			Unit = unit;
			ThresholdMetres = thresholdMetres;
			RealTime = realTime;
			Screens = screens;
			HasTelemetry = source != null && source.HasTelemetry;
		}

		/// <summary>May be null.</summary>
		public IVesselTelemetrySource Source { get; }

		/// <summary>The classic panel, refreshed first every frame (shown or not): its depth alarm, contact monitor and environment display are shared.</summary>
		public WheelhousePanel Panel { get; }

		public DistanceUnit Unit { get; }

		public double ThresholdMetres { get; }

		public double RealTime { get; }

		/// <summary>May be null.</summary>
		public IHudScreenSource Screens { get; }

		public bool HasTelemetry { get; }
	}

	/// <summary>
	/// One HUD widget: a frame holding a new instance of an existing instrument (or, for the classic panel, the panel itself).
	/// Widgets are created the first time a layout shows them and refreshed every frame while shown; each one formats again only
	/// when what it shows changed, so a steady state allocates nothing.
	/// </summary>
	public abstract class HudWidget
	{
		protected HudWidget(HudWidgetDefinition definition, VisualElement element)
		{
			Definition = definition;
			Element = element;
		}

		public HudWidgetDefinition Definition { get; }

		public string Id => Definition.Id;

		/// <summary>The element the layer places.</summary>
		public VisualElement Element { get; }

		/// <summary>The edit-mode overlay that takes drags; null for a widget that cannot move.</summary>
		public VisualElement EditOverlay { get; protected set; }

		public Label EditLabel { get; protected set; }

		public abstract void Refresh(in HudFrame frame);

		/// <summary>The USS class of a widget's frame, from its id (<c>screen:radar</c> → <c>fs-widget--screen-radar</c>).</summary>
		public static string ClassFor(string id) => "fs-widget--" + id.Replace(':', '-');

		/// <summary>Makes the widget the registry describes.</summary>
		public static HudWidget Create(HudWidgetDefinition definition, WheelhousePanel panel)
		{
			switch (definition.Id)
			{
				case HudWidgetIds.ClassicPanel: return new ClassicPanelWidget(definition, panel);
				case HudWidgetIds.Compass: return new CompassWidget(definition);
				case HudWidgetIds.Heading: return new HeadingWidget(definition);
				case HudWidgetIds.Speed: return new SpeedWidget(definition);
				case HudWidgetIds.Depth: return new DepthWidget(definition);
				case HudWidgetIds.Rudder: return new RudderWidget(definition);
				case HudWidgetIds.PortEngine: return new EngineWidget(definition, EngineSide.Port);
				case HudWidgetIds.StarboardEngine: return new EngineWidget(definition, EngineSide.Starboard);
				case HudWidgetIds.Environment: return new EnvironmentWidget(definition);
				case HudWidgetIds.Clock: return new ClockWidget(definition);
				case HudWidgetIds.TrafficAlarm: return new TrafficWidget(definition);
				default: return definition.IsScreen ? new ScreenWidget(definition) : null;
			}
		}
	}

	/// <summary>The v0.4 panel, used as it is: it stays where the view put it and the layer only shows or hides it.</summary>
	public sealed class ClassicPanelWidget : HudWidget
	{
		public ClassicPanelWidget(HudWidgetDefinition definition, WheelhousePanel panel) : base(definition, panel)
		{
		}

		// The view refreshes the panel itself every frame, shown or not.
		public override void Refresh(in HudFrame frame)
		{
		}
	}

	/// <summary>Base for a movable widget: a framed box with an edit-mode overlay naming it.</summary>
	public abstract class FramedWidget : HudWidget
	{
		protected FramedWidget(HudWidgetDefinition definition, bool bare) : base(definition, NewFrame(definition, bare))
		{
			Content = new VisualElement { name = "hud-w-" + ClassSuffix(definition.Id) + "-content", pickingMode = PickingMode.Ignore };
			Content.AddToClassList("fs-widget-content");
			Element.Add(Content);
			EditOverlay = new VisualElement { name = "hud-w-" + ClassSuffix(definition.Id) + "-edit", pickingMode = PickingMode.Position };
			EditOverlay.AddToClassList("fs-widget-edit");
			EditLabel = PaletteRoles.Label(definition.Title.ToUpperInvariant(), null, "fs-caption", "fs-widget-edit-label");
			EditOverlay.Add(EditLabel);
			EditOverlay.style.display = DisplayStyle.None;
			Element.Add(EditOverlay);
		}

		protected VisualElement Content { get; }

		private static string ClassSuffix(string id) => id.Replace(':', '-');

		private static VisualElement NewFrame(HudWidgetDefinition definition, bool bare)
		{
			// Dynamic transform: edit mode drags with translate, and scale changes stay off the layout.
			var frame = new VisualElement { name = "hud-w-" + ClassSuffix(definition.Id), pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
			frame.AddToClassList("fs-widget");
			frame.AddToClassList(ClassFor(definition.Id));
			if (definition.IsScreen)
			{
				frame.AddToClassList("fs-widget--screen");
			}

			if (bare)
			{
				frame.AddToClassList("fs-widget--bare");
			}
			else
			{
				frame.AddToClassList(PaletteRoles.HudBackground);
			}

			return frame;
		}

		protected static Label Readout(VisualElement parent, string caption, string valueName, string unit, string valueClass, string captionRole = PaletteRoles.Dim)
		{
			var readout = new VisualElement { pickingMode = PickingMode.Ignore };
			readout.AddToClassList("fs-readout");
			readout.Add(PaletteRoles.Label(caption, captionRole, "fs-caption"));
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList("fs-value-row");
			var value = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, valueClass);
			value.name = valueName;
			row.Add(value);
			row.Add(PaletteRoles.Label(unit, PaletteRoles.Dim, "fs-unit", unit.StartsWith("°") ? "fs-unit--tight" : "fs-unit--spaced"));
			readout.Add(row);
			parent.Add(readout);
			return value;
		}
	}

	public sealed class CompassWidget : FramedWidget
	{
		private double _heading = double.NegativeInfinity;
		private double _course = double.NegativeInfinity;

		public CompassWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			Compass = new CompassRose { name = "hud-w-compass-rose" };
			Content.Add(Compass);
		}

		public CompassRose Compass { get; }

		public override void Refresh(in HudFrame frame)
		{
			var heading = double.NaN;
			var course = double.NaN;
			if (frame.HasTelemetry)
			{
				var telemetry = frame.Source.Telemetry;
				heading = frame.Source.State.Heading;
				course = HeadingWidget.CourseShown(telemetry.CourseOverGround, telemetry.SpeedOverGround);
			}

			if (heading.Equals(_heading) && course.Equals(_course))
			{
				return;
			}

			_heading = heading;
			_course = course;
			Compass.SetHeading(heading, course);
		}
	}

	public sealed class HeadingWidget : FramedWidget
	{
		private double _heading = double.NegativeInfinity;
		private double _course = double.NegativeInfinity;
		private double _speed = double.NegativeInfinity;

		public HeadingWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			Heading = Readout(Content, "HEADING", "hud-w-heading-value", "°T", "fs-value-xl");
			CourseOverGround = Readout(Content, "COG", "hud-w-cog-value", "°T", "fs-value-m", PaletteRoles.Course);
		}

		public Label Heading { get; }

		public Label CourseOverGround { get; }

		/// <summary>The course the compass draws: NaN when the course readout would show "---" (too slow for a course).</summary>
		public static double CourseShown(double course, double speedOverGround) =>
			InstrumentFormat.IsFinite(speedOverGround) && System.Math.Abs(DisplayUnits.Knots(speedOverGround)) >= InstrumentFormat.MinimumSpeedForCourseKnots
				? course
				: double.NaN;

		public override void Refresh(in HudFrame frame)
		{
			var heading = double.NaN;
			var course = double.NaN;
			var speed = double.NaN;
			if (frame.HasTelemetry)
			{
				var telemetry = frame.Source.Telemetry;
				heading = frame.Source.State.Heading;
				course = telemetry.CourseOverGround;
				speed = telemetry.SpeedOverGround;
			}

			if (heading.Equals(_heading) && course.Equals(_course) && speed.Equals(_speed))
			{
				return;
			}

			_heading = heading;
			_course = course;
			_speed = speed;
			PaletteRoles.SetText(Heading, frame.HasTelemetry ? InstrumentFormat.Compass(heading) : InstrumentFormat.NoValue);
			PaletteRoles.SetText(CourseOverGround, frame.HasTelemetry ? InstrumentFormat.Course(course, speed) : InstrumentFormat.NoValue);
		}
	}

	public sealed class SpeedWidget : FramedWidget
	{
		private readonly ReadoutText _sog = new ReadoutText();
		private readonly ReadoutText _stw = new ReadoutText();

		public SpeedWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			SpeedOverGround = Readout(Content, "SOG", "hud-w-sog-value", "kn", "fs-value-xl");
			SpeedThroughWater = Readout(Content, "STW", "hud-w-stw-value", "kn", "fs-value-m");
		}

		public Label SpeedOverGround { get; }

		public Label SpeedThroughWater { get; }

		public override void Refresh(in HudFrame frame)
		{
			if (!frame.HasTelemetry)
			{
				PaletteRoles.SetText(SpeedOverGround, InstrumentFormat.NoValue);
				PaletteRoles.SetText(SpeedThroughWater, InstrumentFormat.NoValue);
				return;
			}

			var telemetry = frame.Source.Telemetry;
			PaletteRoles.SetText(SpeedOverGround, _sog.Speed(telemetry.SpeedOverGround));
			PaletteRoles.SetText(SpeedThroughWater, _stw.Speed(telemetry.SpeedThroughWater));
		}
	}

	/// <summary>Depth and the live C1 alarm; telemetry-only sources keep the panel's legacy alarm. A click acknowledges the shared source.</summary>
	public sealed class DepthWidget : FramedWidget
	{
		private readonly ReadoutText _depth = new ReadoutText();
		private WheelhousePanel _panel;
		private IInstrumentSource _instruments;

		public DepthWidget(HudWidgetDefinition definition) : base(definition, true)
		{
			Depth = new DepthReadout { name = "hud-w-depth-readout" };
			Depth.RegisterCallback<ClickEvent>(_ =>
			{
				if (_instruments != null && _instruments.HasInstruments) _instruments.AcknowledgeAlarms();
				else _panel?.AcknowledgeAlarm();
			});
			Content.Add(Depth);
		}

		public DepthReadout Depth { get; }

		public override void Refresh(in HudFrame frame)
		{
			_panel = frame.Panel;
			_instruments = frame.HasTelemetry && frame.Source is IInstrumentSource instruments && instruments.HasInstruments ? instruments : null;
			Depth.ShowSetting(frame.ThresholdMetres, frame.Unit);
			Depth.ShowGrid(frame.Panel != null ? frame.Panel.EnvironmentDisplay.DepthGridIndication : CoverageIndication.None);
			if (!frame.HasTelemetry || (_instruments == null && frame.Panel == null))
			{
				Depth.ShowValue(InstrumentFormat.NoValue, frame.Unit);
				Depth.SetAlarm(AlarmState.Normal, false);
				return;
			}

			Depth.ShowValue(_depth.Distance(frame.Source.Telemetry.DepthUnderKeel, frame.Unit), frame.Unit);
			var alarm = frame.Panel != null ? frame.Panel.DepthAlarmShown : ReadStandaloneAlarm(frame);
			Depth.SetAlarm(alarm.State, alarm.IsLit);
		}

		private DepthAlarmPresentation ReadStandaloneAlarm(in HudFrame frame)
		{
			_instruments.SetShallowWaterThreshold(frame.ThresholdMetres);
			return WheelhousePanel.ReadInstrumentDepthAlarm(_instruments, frame.Source.State.Time);
		}
	}

	public sealed class RudderWidget : FramedWidget
	{
		private VesselParameters _parameters;
		private bool _parametersSeen;
		private double _port = double.NegativeInfinity;
		private double _starboard = double.NegativeInfinity;

		public RudderWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			Rudder = new RudderIndicator { name = "hud-w-rudder-indicator" };
			Content.Add(Rudder);
		}

		public RudderIndicator Rudder { get; }

		public override void Refresh(in HudFrame frame)
		{
			var port = double.NaN;
			var starboard = double.NaN;
			if (frame.HasTelemetry)
			{
				var parameters = frame.Source.Parameters;
				if (!_parametersSeen || !ReferenceEquals(parameters, _parameters))
				{
					_parametersSeen = true;
					_parameters = parameters;
					Rudder.SetScales(InstrumentScales.From(parameters));
				}

				var telemetry = frame.Source.Telemetry;
				port = telemetry.PortRudderAngle;
				starboard = telemetry.StarboardRudderAngle;
			}

			if (port.Equals(_port) && starboard.Equals(_starboard))
			{
				return;
			}

			_port = port;
			_starboard = starboard;
			Rudder.SetAngles(port, starboard);
		}
	}

	/// <summary>One engine's lever, gear, tachometer and shaft: the panel's engine column, as its own widget.</summary>
	public sealed class EngineWidget : FramedWidget
	{
		private VesselParameters _parameters;
		private bool _parametersSeen;
		private bool _shown;
		private EngineTelemetry _engine;
		private double _lever;

		public EngineWidget(HudWidgetDefinition definition, EngineSide side) : base(definition, true)
		{
			Side = side;
			Column = new EngineColumn(side);
			Column.name = "hud-w-" + Column.name;
			Content.Add(Column);
			Column.ShowNoData();
		}

		public EngineSide Side { get; }

		public EngineColumn Column { get; }

		public override void Refresh(in HudFrame frame)
		{
			if (!frame.HasTelemetry)
			{
				if (_shown)
				{
					_shown = false;
					Column.ShowNoData();
				}

				return;
			}

			var parameters = frame.Source.Parameters;
			if (!_parametersSeen || !ReferenceEquals(parameters, _parameters))
			{
				_parametersSeen = true;
				_parameters = parameters;
				Column.SetScales(InstrumentScales.From(parameters));
				_shown = false;
			}

			var telemetry = frame.Source.Telemetry;
			var controls = frame.Source.Controls;
			var engine = Side == EngineSide.Port ? telemetry.Port : telemetry.Starboard;
			var lever = Side == EngineSide.Port ? controls.PortLever : controls.StarboardLever;
			if (_shown && engine.EngineSpeed.Equals(_engine.EngineSpeed) && engine.ShaftSpeed.Equals(_engine.ShaftSpeed) && engine.Gear == _engine.Gear &&
				engine.Throttle.Equals(_engine.Throttle) && lever.Equals(_lever))
			{
				return;
			}

			_shown = true;
			_engine = engine;
			_lever = lever;
			Column.Show(engine, lever);
		}
	}

	/// <summary>Tide, set and drift, and wind, as the panel's top strip shows them, in two rows.</summary>
	public sealed class EnvironmentWidget : FramedWidget
	{
		private static readonly EnvironmentDisplay NoEnvironment = new EnvironmentDisplay();

		public EnvironmentWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			Readouts = new EnvironmentReadouts("hud-w-env");
			Content.Add(Readouts);
		}

		public EnvironmentReadouts Readouts { get; }

		public override void Refresh(in HudFrame frame) => Readouts.Show(frame.Panel != null ? frame.Panel.EnvironmentDisplay : NoEnvironment);
	}

	/// <summary>The run's local clock and its zone, as the panel shows them.</summary>
	public sealed class ClockWidget : FramedWidget
	{
		public ClockWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			var row = new VisualElement { pickingMode = PickingMode.Ignore };
			row.AddToClassList("fs-value-row");
			Clock = PaletteRoles.Label(InstrumentFormat.NoValue, PaletteRoles.Text, "fs-value-s");
			Clock.name = "hud-w-clock-value";
			row.Add(Clock);
			Zone = PaletteRoles.Label(EnvironmentDisplay.ZoneUnknown, PaletteRoles.Dim, "fs-unit");
			Zone.name = "hud-w-clock-zone";
			row.Add(Zone);
			Content.Add(row);
		}

		public Label Clock { get; }

		public Label Zone { get; }

		public override void Refresh(in HudFrame frame)
		{
			var display = frame.Panel?.EnvironmentDisplay;
			PaletteRoles.SetText(Clock, display != null ? display.Clock : InstrumentFormat.NoValue);
			PaletteRoles.SetText(Zone, display != null ? display.Zone : EnvironmentDisplay.ZoneUnknown);
		}
	}

	/// <summary>The CPA alarm line, shown only on runs with traffic, as on the panel.</summary>
	public sealed class TrafficWidget : FramedWidget
	{
		public TrafficWidget(HudWidgetDefinition definition) : base(definition, true)
		{
			Traffic = new TrafficReadout { name = "hud-w-traffic-readout" };
			Content.Add(Traffic);
		}

		public TrafficReadout Traffic { get; }

		public override void Refresh(in HudFrame frame)
		{
			var traffic = frame.Source as ITrafficReadoutSource;
			Traffic.SetShown(traffic != null && traffic.TrafficEnabled);
			if (frame.HasTelemetry && traffic != null && traffic.HasTraffic)
			{
				Traffic.Show(traffic.TrafficAlarm, frame.Unit);
			}
			else
			{
				Traffic.ShowNoData();
			}
		}
	}

	/// <summary>A pop-out of one console screen: the texture <see cref="IHudScreenSource"/> supplies, or a neutral NO SIGNAL face.</summary>
	public sealed class ScreenWidget : FramedWidget
	{
		public const string NoSignalText = "NO SIGNAL";

		private Texture _shown;
		private bool _signal = true;

		public ScreenWidget(HudWidgetDefinition definition) : base(definition, false)
		{
			ScreenId = definition.ScreenId;
			Content.Add(PaletteRoles.Label(definition.Title.ToUpperInvariant(), PaletteRoles.Dim, "fs-caption", "fs-widget-screen-title"));
			var face = new VisualElement { pickingMode = PickingMode.Ignore };
			face.AddToClassList("fs-widget-screen-face");
			face.AddToClassList(PaletteRoles.ConsoleBackground);
			Picture = new Image { name = "hud-w-screen-" + ScreenId + "-image", pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit };
			Picture.AddToClassList("fs-widget-screen-image");
			face.Add(Picture);
			NoSignal = PaletteRoles.Label(NoSignalText, PaletteRoles.Dim, "fs-caption", "fs-widget-no-signal");
			NoSignal.name = "hud-w-screen-" + ScreenId + "-no-signal";
			face.Add(NoSignal);
			Content.Add(face);
			Show(null);
		}

		public string ScreenId { get; }

		public Image Picture { get; }

		public Label NoSignal { get; }

		public bool HasSignal => _signal;

		public override void Refresh(in HudFrame frame)
		{
			Texture texture = null;
			if (frame.Screens != null && !frame.Screens.TryGetScreen(ScreenId, out texture))
			{
				texture = null;
			}

			if (texture == null && _shown == null && !_signal)
			{
				return;
			}

			if (!ReferenceEquals(texture, _shown) || (texture == null) == _signal)
			{
				Show(texture);
			}
		}

		private void Show(Texture texture)
		{
			_shown = texture;
			_signal = texture != null;
			Picture.image = texture;
			Picture.style.display = _signal ? DisplayStyle.Flex : DisplayStyle.None;
			NoSignal.style.display = _signal ? DisplayStyle.None : DisplayStyle.Flex;
		}
	}
}
