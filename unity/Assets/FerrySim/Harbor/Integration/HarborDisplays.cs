using System;
using FerrySim.Chartplotter;
using FerrySim.Console;
using FerrySim.Controls;
using FerrySim.Core.Navigation;
using FerrySim.Core.Radar;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

namespace FerrySim.Harbor
{
	/// <summary>
	/// Composes the own interior's console screens, shared HUD pictures and viewer demand. Flat radar/plotter
	/// overlays remain lazy fallbacks when console screens are disabled or the vessel has no interior.
	/// All displays use the shared verified chart load; presentation never changes a run or recording.
	/// </summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(140)]
	public sealed class HarborDisplays : MonoBehaviour
	{
		/// <summary>Below the wheelhouse document (0), so its status line and picker stay on top; the chartplotter below the overlay that announces the chart.</summary>
		public const float ChartplotterSortingOrder = -3f;
		public const float OverlaySortingOrder = -2f;
		public const string LoadingText = "CHART LOADING";
		public const string ErrorText = "CHART ERROR";
		/// <summary>One pan key press moves the chart this many reference pixels.</summary>
		public const float PanPixels = 40f;

		/// <summary>Upper left, clear of the instrument panel's bottom 300 px and of the forward view's centre.</summary>
		public static readonly Rect ChartplotterPlacement = new Rect(8f, 8f, 560f, 400f);

		/// <summary>Upper right, the compact radar layout.</summary>
		public static readonly Rect RadarPlacement = new Rect(WheelhousePanelSettings.ReferenceResolution.x - 8f - RadarView.DefaultWidth, 8f, RadarView.DefaultWidth, RadarView.CompactHeight);

		private VesselSimulationRunner _runner;
		private HarborChartLoad _load;
		private WheelhouseUI _ui;
		private CameraRig _rig;
		private SunController _sun;
		private PanelSettings _ownedSettings;
		private PanelSettings _settings;
		private UIDocument _document;
		private VisualElement _radarHost;
		private VisualElement _chartHost;
		private Label _radarStatus;
		private Label _chartStatus;
		private GameObject _radarObject;
		private GameObject _chartObject;
		private RadarController _radar;
		private ChartplotterDisplay _chartplotter;
		private InputActionAsset _actions;
		private InputBindingService _bindings;
		private ConsoleScreens _screens;
		private Transform _interior;
		private long _screenRun = long.MinValue;
		private bool _flatConnected;
		private RadarPalette? _radarPaletteChoice;
		private ChartPaletteKind? _plotterPaletteChoice;
		private PaletteSetting _savedPalette;
		private bool _savedPaletteKnown;
		private readonly HarborHudDisplayInput _alarmInput = new HarborHudDisplayInput();
		private readonly HarborHudDisplayInput _portInput = new HarborHudDisplayInput();
		private readonly HarborHudDisplayInput _starboardInput = new HarborHudDisplayInput();
		private HarborTextEntryGate _textEntry;
		private CpaAlarmLimits _alarmLimits = CpaAlarmLimits.Default;
		private bool _alarmMuted;
		private ChartplotterView _sessionPlotter;
		private static readonly string[] ScreenWidgets = { "screen:radar", "screen:plotter", "screen:engine-port", "screen:engine-starboard", "screen:alarms" };
		public ConsoleScreens Screens => _screens;
		public HarborPresentation Presentation { get; private set; }
		private bool ConsoleActive => _screens != null && _screens.Enabled && _screens.Plotter != null;
		public ChartplotterView ActivePlotter => ConsoleActive ? _screens.Plotter : _chartplotter?.View;
		private DisplayInputMapper _mapper;
		private bool _radarShown;
		private bool _chartShown;
		private bool _radarVisible;
		private bool _chartVisible;
		private bool _chartHostVisible = true;
		private bool _mapperEnabled;
		private bool _connected;
		private bool _forceApply;
		private bool _failureShown;
		private WheelhouseView _followedView;
		private DistanceUnit _unit = DistanceUnit.Feet;
		private bool _unitKnown;
		// Key requests are queued by the input callbacks and applied in Tick, after everything else in the frame has had its
		// say: a key in the same update as the picker opening or the view leaving the wheelhouse does nothing.
		private int _pendingRadarToggle, _pendingChartToggle, _pendingChartZoom, _pendingChartOrientation, _pendingChartCentre, _pendingChartPanX, _pendingChartPanY;
		private int _pendingChartPalette, _pendingChartNextTarget, _pendingChartClearSelection, _pendingRadarRange, _pendingRadarOrientation, _pendingRadarPalette;
		private int _pendingChartCpaLimit, _pendingChartTcpaLimit, _pendingChartAlarmMute;
		private int _pendingRadarEbl, _pendingRadarVrm, _pendingRadarGain, _pendingRadarSea;
		private double _composedAt = double.NaN;
		private double _readyAt = double.NaN;
		private long _peakMonoUsedBytes;

		/// <summary>The shared chart load this scene polls.</summary>
		public HarborChartLoad Load => _load;

		public HarborChartLoadState ChartState => _load != null ? _load.State : HarborChartLoadState.Loading;

		/// <summary>True once both displays were connected to the ready chart.</summary>
		public bool Connected => _connected && (ConsoleActive || _flatConnected);

		/// <summary>Seconds from the scene's composition to the frame that connected the displays; NaN until then.</summary>
		public double ChartReadySeconds => double.IsNaN(_readyAt) ? double.NaN : _readyAt - _composedAt;

		/// <summary>The largest Mono heap use seen on the main thread while the chart was loading (Profiler.GetMonoUsedSizeLong).</summary>
		public long PeakMonoUsedBytes => _peakMonoUsedBytes;

		public RadarController Radar => ConsoleActive ? _screens.Radar : _radar;

		/// <summary>Releases retired traffic on every existing radar without sweeping, raster work or upload.</summary>
		public void RebindRadars()
		{
			_radar?.RebindTraffic(_runner.Traffic);
			_screens?.Radar?.RebindTraffic(_runner.Traffic);
		}

		public ChartplotterDisplay Chartplotter => _chartplotter;

		public DisplayInputMapper Mapper => _mapper;

		public UIDocument Document => _document;

		/// <summary>What the radar's overlay says while the chart is not ready, or an empty string.</summary>
		public string RadarStatusText => _radarStatus != null && _radarStatus.style.display != DisplayStyle.None ? _radarStatus.text : string.Empty;

		public string ChartplotterStatusText => _chartStatus != null && _chartHostVisible ? _chartStatus.text : string.Empty;

		/// <summary>The captain's choice: shown when the view allows it.</summary>
		public bool RadarShown
		{
			get => _radarShown;
			set => _radarShown = value;
		}

		public bool ChartplotterShown
		{
			get => _chartShown;
			set => _chartShown = value;
		}

		/// <summary>Drawn now: shown, in the wheelhouse view, with the picker and the debrief closed.</summary>
		public bool RadarVisible => _radarVisible;

		public bool ChartplotterVisible => _chartVisible;

		/// <summary>The wheelhouse view with the picker and the debrief closed; the displays and their keys live only there.</summary>
		public bool DisplaysAllowed
		{
			get
			{
				if (_rig != null && _rig.CurrentMode != CameraRig.Mode.Wheelhouse)
				{
					return false;
				}

				var view = _ui != null ? _ui.View : null;
				return view != null && !view.PickerVisible && !view.DebriefVisible && !view.OptionsVisible;
			}
		}

		public void ToggleRadar() => _radarShown = !_radarShown;

		public void ToggleChartplotter() => _chartShown = !_chartShown;

		/// <summary>The depth unit the chartplotter follows: the wheelhouse panel's, as last seen.</summary>
		public DistanceUnit DistanceUnit => _unit;

		/// <summary>Builds the overlay, the two display hosts and the key map. Called by the composition root once, before any run.</summary>
		public void Configure(VesselSimulationRunner runner, HarborChartLoad load, WheelhouseUI ui, CameraRig rig, SunController sun, HelmInputController helm)
		{
			if (_load != null)
			{
				throw new InvalidOperationException("The harbor displays are already configured.");
			}

			_runner = runner ?? throw new ArgumentNullException(nameof(runner));
			_load = load ?? throw new ArgumentNullException(nameof(load));
			_ui = ui;
			_textEntry = HarborTextEntryGate.For(ui);
			if (_textEntry != null) _textEntry.Changed += TextEntryChanged;
			_rig = rig;
			_sun = sun;
			_composedAt = Time.realtimeSinceStartupAsDouble;
			_settings = WheelhouseSettings();
			_document = gameObject.AddComponent<UIDocument>();
			_document.panelSettings = _settings;
			_document.sortingOrder = OverlaySortingOrder;
			_radarHost = Host("radar-host", RadarPlacement);
			_radarStatus = Status(_radarHost);
			_chartHost = Host("chartplotter-host", ChartplotterPlacement);
			_chartStatus = Status(_chartHost);
			EnsureHosts();
			_radarObject = new GameObject("Radar");
			_radarObject.transform.SetParent(transform, false);
			_radar = _radarObject.AddComponent<RadarController>();
			_radar.enabled = false;
			_chartObject = new GameObject("Chartplotter");
			_chartObject.transform.SetParent(transform, false);
			_chartObject.SetActive(false);
			_chartplotter = _chartObject.AddComponent<ChartplotterDisplay>();
			_chartplotter.Configure(_settings);
			_chartplotter.SetPlacement(ChartplotterPlacement);
			_chartplotter.DaylightSource = null;
			_chartplotter.Visible = false;
			if (helm != null && helm.Actions != null)
			{
				_actions = Instantiate(helm.Actions);
				_bindings = InputBindingService.Instance;
				_bindings.Register(_actions);
				_mapper = new DisplayInputMapper(_actions);
				_mapper.ChartplotterToggleRequested += OnChartplotterToggle;
				_mapper.RadarToggleRequested += OnRadarToggle;
				_mapper.ChartZoomRequested += OnChartZoom;
				_mapper.ChartOrientationRequested += OnChartOrientation;
				_mapper.ChartCentreRequested += OnChartCentre;
				_mapper.ChartPanRequested += OnChartPan;
				_mapper.ChartPaletteRequested += OnChartPalette;
				_mapper.ChartNextTargetRequested += OnChartNextTarget;
				_mapper.ChartClearSelectionRequested += OnChartClearSelection;
				_mapper.ChartCpaLimitRequested += OnChartCpaLimit;
				_mapper.ChartTcpaLimitRequested += OnChartTcpaLimit;
				_mapper.ChartAlarmMuteRequested += OnChartAlarmMute;
				_mapper.RadarRangeRequested += OnRadarRange;
				_mapper.RadarOrientationRequested += OnRadarOrientation;
				_mapper.RadarPaletteRequested += OnRadarPalette;
				_mapper.RadarEblRequested += OnRadarEbl;
				_mapper.RadarVrmRequested += OnRadarVrm;
				_mapper.RadarGainRequested += OnRadarGain;
				_mapper.RadarSeaRequested += OnRadarSea;
			}

			Presentation = gameObject.AddComponent<HarborPresentation>();
			Presentation.Configure(runner, ui, rig);
			Apply();
		}

		private void LateUpdate() => Tick();

		/// <summary>One frame of the composition: the panel, the load, the visibility, the key map and the palettes. Allocation-free once connected.</summary>
		public void Tick()
		{
			if (_load == null)
			{
				return;
			}

			RememberPlotter();
			AdoptPanel();
			FollowWheelhouseView();
			_textEntry?.Follow(_followedView);
			PollLoad();
			FollowInterior();
			FollowPlotter();
			_alarmInput.Follow(_followedView, ConsoleActive ? _screens.Alarms : null, "screen:alarms");
			_portInput.Follow(_followedView, ConsoleActive ? _screens.PortEngine : null, "screen:engine-port");
			_starboardInput.Follow(_followedView, ConsoleActive ? _screens.StarboardEngine : null, "screen:engine-starboard");
			Apply();
			ApplyPendingKeys();
			RememberPlotter();
			FollowDaylight();
		}

		/// <summary>The panel's depth unit, applied to the chartplotter when it connects and on every later change, including a replaced view.</summary>
		private void FollowWheelhouseView()
		{
			var view = _ui != null ? _ui.View : null;
			if (!ReferenceEquals(view, _followedView))
			{
				if (_followedView != null)
				{
					_followedView.SettingsChanged -= OnWheelhouseSettingsChanged;
					_followedView.Settings.Changed -= OnSavedSettingsChanged;
				}

				_followedView = view;
				if (view != null)
				{
					view.SettingsChanged += OnWheelhouseSettingsChanged;
					view.Settings.Changed += OnSavedSettingsChanged;
					OnWheelhouseSettingsChanged();
				}
			}
		}

		private void OnSavedSettingsChanged(SettingsSection section) => OnWheelhouseSettingsChanged();
		private void OnWheelhouseSettingsChanged()
		{
			if (_followedView == null) return;
			RememberPlotter();
			_unit = _followedView.DistanceUnit;
			_unitKnown = true;
			if (!_savedPaletteKnown || _savedPalette != _followedView.Settings.Palette)
			{
				_savedPaletteKnown = true; _savedPalette = _followedView.Settings.Palette;
				_radarPaletteChoice = _savedPalette == PaletteSetting.Auto ? null : _savedPalette == PaletteSetting.Night ? RadarPalette.Night : RadarPalette.Day;
				_plotterPaletteChoice = _savedPalette == PaletteSetting.Auto ? null : _savedPalette == PaletteSetting.Night ? ChartPaletteKind.Night : ChartPaletteKind.Day;
			}
			if (_chartplotter.View != null) ApplyPlotterSettings(_chartplotter.View);
			if (_screens != null)
			{
				_screens.RadarPaletteOverride = _radarPaletteChoice;
				_screens.PlotterPaletteOverride = _plotterPaletteChoice;
				_screens.Enabled = _followedView.Settings.ConsoleScreens;
				FollowPlotter();
				_screens.VectorMinutes = _followedView.Settings.VectorMinutes;
				_screens.Predictor = _followedView.Settings.Predictor == PredictorKind.Curved ? PredictorMode.Curved : PredictorMode.Straight;
				if (_screens.Plotter != null) ApplyPlotterSettings(_screens.Plotter);
			}
			_forceApply = true;
		}

		// Only the previously active view can update the session choices. Newly built views receive them first.
		private void RememberPlotter()
		{
			if (_sessionPlotter == null) return;
			_alarmLimits = _sessionPlotter.AlarmLimits;
			_alarmMuted = _sessionPlotter.AlarmMuted;
		}
		private void FollowPlotter()
		{
			var next = ActivePlotter;
			if (ReferenceEquals(next, _sessionPlotter)) return;
			_sessionPlotter = next;
			if (next == null) return;
			next.AlarmLimits = _alarmLimits;
			next.AlarmMuted = _alarmMuted;
		}

		private void ApplyPlotterSettings(ChartplotterView view)
		{
			view.DistanceUnit = _unit;
			if (_followedView == null) return;
			view.VectorMinutes = _followedView.Settings.VectorMinutes;
			view.Predictor = _followedView.Settings.Predictor == PredictorKind.Curved ? PredictorMode.Curved : PredictorMode.Straight;
		}

		private void FollowInterior()
		{
			if (!_connected) return;
			if (_screenRun != _runner.RunId)
			{
				_screenRun = _runner.RunId;
				ReleaseScreens();
				var interior = _runner.transform.Find(WheelhouseControlsView.RootName);
				_interior = interior != null && interior.gameObject.activeInHierarchy ? interior : null;
				if (_interior != null)
				{
					var host = new GameObject("Console screens"); host.transform.SetParent(transform.parent, false);
					_screens = host.AddComponent<ConsoleScreens>();
					_screens.Enabled = _followedView == null || _followedView.Settings.ConsoleScreens;
					_screens.Connect(_interior, _runner, _runner, _load.Tessellation, _load.RadarScene, environment: _runner);
					OnWheelhouseSettingsChanged();
				}
				_forceApply = true;
			}
			if (_followedView != null) _followedView.ScreenSource = _screens;
			if (!ConsoleActive) ConnectFlat();
			if (_screens == null) return;
			_screens.SetWheelhouseVisible(DisplaysAllowed);
			for (var i = 0; i < HudWidgetIds.Screens.Count; i++)
			{
				var id = HudWidgetIds.Screens[i];
				var shown = _followedView != null && _followedView.PanelVisible && !_followedView.PickerVisible &&
					!_followedView.OptionsVisible && !_followedView.DebriefVisible && _followedView.HudLayout.IsShown(ScreenWidgets[i]);
				_screens.SetPopOutShown(id, shown);
			}
			var camera = _rig != null ? _rig.GetComponent<WheelhouseCameraController>() : null;
			var mount = camera != null ? camera.FocusedScreen : null;
			var focus = mount == "RadarScreen" ? "radar" : mount == "PlotterScreen" ? "plotter" :
				mount == "PortEngineScreen" ? "engine-port" : mount == "StarboardEngineScreen" ? "engine-starboard" : null;
			_screens.SetFocus(focus);
		}

		private void ReleaseScreens()
		{
			if (_screens == null) return;
			if (_followedView != null && ReferenceEquals(_followedView.ScreenSource, _screens)) _followedView.ScreenSource = null;
			_screens.Enabled = false;
			Destroy(_screens.gameObject);
			_screens = null;
		}

		/// <summary>How many range bins of the whole picture carry an echo above <paramref name="threshold"/>; evidence for the smoke run and the tests.</summary>
		public static int CountEchoBins(RadarEngine engine, float threshold = 0.3f)
		{
			if (engine == null)
			{
				return 0;
			}

			var count = 0;
			for (var spoke = 0; spoke < engine.Configuration.Spokes; spoke++)
			{
				for (var bin = 0; bin < engine.Scale.Bins; bin++)
				{
					if (engine.Echo(spoke, bin) > threshold)
					{
						count++;
					}
				}
			}

			return count;
		}

		private PanelSettings WheelhouseSettings()
		{
			var document = _ui != null ? _ui.Document : null;
			if (document != null && document.panelSettings != null)
			{
				return document.panelSettings;
			}

			if (_ownedSettings == null)
			{
				_ownedSettings = WheelhousePanelSettings.Create();
			}

			return _ownedSettings;
		}

		/// <summary>The displays draw in the wheelhouse UI's panel; when a test gives that UI another panel (a texture), they follow.</summary>
		private void AdoptPanel()
		{
			var wanted = WheelhouseSettings();
			if (!ReferenceEquals(wanted, _settings))
			{
				_settings = wanted;
				_document.panelSettings = wanted;
				_chartplotter.Configure(wanted);
				if (_chartplotter.Document != null)
				{
					_chartplotter.Document.panelSettings = wanted;
				}
			}

			EnsureHosts();
		}

		private void EnsureHosts()
		{
			var root = _document.rootVisualElement;
			if (root == null)
			{
				return;
			}

			if (!ReferenceEquals(_chartHost.parent, root))
			{
				root.Add(_chartHost);
			}

			if (!ReferenceEquals(_radarHost.parent, root))
			{
				root.Add(_radarHost);
			}
		}

		private void PollLoad()
		{
			if (_connected)
			{
				return;
			}

			var state = _load.State;
			if (state == HarborChartLoadState.Loading)
			{
				var used = Profiler.GetMonoUsedSizeLong();
				if (used > _peakMonoUsedBytes)
				{
					_peakMonoUsedBytes = used;
				}

				return;
			}

			if (state == HarborChartLoadState.Failed)
			{
				if (!_failureShown)
				{
					_failureShown = true;
					var text = ErrorText + "\n" + _load.Error;
					_radarStatus.text = text;
					_chartStatus.text = text;
					_radarStatus.style.fontSize = 12;
					_chartStatus.style.fontSize = 12;
				}

				return;
			}

			// Ready: the Unity side of each display is created here, on the main thread, from the immutable products.
			var used2 = Profiler.GetMonoUsedSizeLong();
			if (used2 > _peakMonoUsedBytes)
			{
				_peakMonoUsedBytes = used2;
			}

			_connected = true;
			_readyAt = Time.realtimeSinceStartupAsDouble;
			_forceApply = true;
		}

		private void ConnectFlat()
		{
			if (_flatConnected) return;
			_radar.Connect(_runner, _load.RadarScene, _radarHost, compact: true);
			_radarStatus.style.display = DisplayStyle.None;
			_chartplotter.Connect(_runner, _load.Tessellation);
			_chartObject.SetActive(true);
			if (_unitKnown) ApplyPlotterSettings(_chartplotter.View);
			if (_chartplotter.Document != null)
			{
				_chartplotter.Document.sortingOrder = ChartplotterSortingOrder;
				_chartplotter.Document.panelSettings = _settings;
			}
			_flatConnected = true;
			_forceApply = true;
		}

		private void Apply()
		{
			var allowed = DisplaysAllowed;
			var radarVisible = _radarShown && allowed && !ConsoleActive;
			var chartVisible = _chartShown && allowed && !ConsoleActive;
			if (radarVisible != _radarVisible || _forceApply)
			{
				_radarVisible = radarVisible;
				_radarHost.style.display = radarVisible ? DisplayStyle.Flex : DisplayStyle.None;
				// A hidden radar does not sweep; shown again it catches up at most one revolution (RadarEngine.Advance).
				_radar.enabled = radarVisible && _connected;
			}

			if (chartVisible != _chartVisible || _forceApply)
			{
				_chartVisible = chartVisible;
				_chartplotter.Visible = chartVisible;
			}

			_forceApply = false;

			var chartHostVisible = chartVisible && !_connected;
			if (chartHostVisible != _chartHostVisible)
			{
				_chartHostVisible = chartHostVisible;
				_chartHost.style.display = chartHostVisible ? DisplayStyle.Flex : DisplayStyle.None;
			}

			allowed = InputAllowed;
			if (_mapper != null && allowed != _mapperEnabled)
			{
				_mapperEnabled = allowed;
				if (allowed)
				{
					_mapper.Enable();
				}
				else
				{
					_mapper.Disable();
				}
			}
		}

		/// <summary>
		/// Both displays follow the saved/global palette, including automatic day/night. Explicit instrument-key
		/// choices persist until the captain changes the global palette setting.
		/// </summary>
		private void FollowDaylight()
		{
			if (!_connected || _followedView == null) return;
			var palette = _followedView.Palette;
			if (_screens != null) _screens.Palette = palette;
			if (Radar?.View != null)
			{
				var wanted = _radarPaletteChoice ?? (palette == PaletteKind.Night ? RadarPalette.Night : RadarPalette.Day);
				if (Radar.View.Palette != wanted) Radar.View.SetPalette(wanted);
			}
			var chart = ActivePlotter;
			var wantedChart = _plotterPaletteChoice ?? (palette == PaletteKind.Night ? ChartPaletteKind.Night : ChartPaletteKind.Day);
			if (chart != null && chart.Palette != wantedChart) chart.Palette = wantedChart;
		}

		private bool InputAllowed => DisplaysAllowed && !(_textEntry != null && _textEntry.Held);

		private bool ChartReady => InputAllowed && (ConsoleActive && DisplaysAllowed || _chartVisible) && _connected && ActivePlotter != null;
		private bool RadarReady => InputAllowed && (ConsoleActive && DisplaysAllowed || _radarVisible) && _connected && Radar?.View != null;

		// The input callbacks only queue; ApplyPendingKeys acts in Tick.
		private void OnChartplotterToggle() => _pendingChartToggle++;
		private void OnRadarToggle() => _pendingRadarToggle++;
		private void OnChartZoom(int direction) => _pendingChartZoom += direction;
		private void OnChartOrientation() => _pendingChartOrientation++;
		private void OnChartCentre() => _pendingChartCentre++;
		private void OnChartPan(int dx, int dy) { _pendingChartPanX += dx; _pendingChartPanY += dy; }
		private void OnChartPalette() => _pendingChartPalette++;
		private void OnChartNextTarget() => _pendingChartNextTarget++;
		private void OnChartClearSelection() => _pendingChartClearSelection++;
		private void OnChartCpaLimit(int direction) => _pendingChartCpaLimit += direction;
		private void OnChartTcpaLimit(int direction) => _pendingChartTcpaLimit += direction;
		private void OnChartAlarmMute() => _pendingChartAlarmMute++;
		private void OnRadarRange(int direction) => _pendingRadarRange += direction;
		private void OnRadarOrientation() => _pendingRadarOrientation++;
		private void OnRadarPalette() => _pendingRadarPalette++;
		private void OnRadarEbl(int direction) => _pendingRadarEbl += direction;
		private void OnRadarVrm(int direction) => _pendingRadarVrm += direction;
		private void OnRadarGain(int direction) => _pendingRadarGain += direction;
		private void OnRadarSea(int direction) => _pendingRadarSea += direction;

		/// <summary>
		/// Applies the frame's key requests, then forgets them: toggles only while the displays are allowed now, adjustments
		/// only on a display visible now. A request made in the update the picker opened or the view left the wheelhouse
		/// in is therefore discarded, whichever came first in that update.
		/// </summary>
		private void ApplyPendingKeys()
		{
			var allowed = InputAllowed;
			if (allowed)
			{
				if ((_pendingChartToggle & 1) != 0)
				{
					if (ConsoleActive) _followedView.HudLayout.ToggleSessionShown(ScreenWidgets[1]);
					else _chartShown = !_chartShown;
				}
				if ((_pendingRadarToggle & 1) != 0)
				{
					if (ConsoleActive) _followedView.HudLayout.ToggleSessionShown(ScreenWidgets[0]);
					else _radarShown = !_radarShown;
				}
				if (_pendingChartToggle != 0 || _pendingRadarToggle != 0) Apply();
			}

			_pendingChartToggle = 0;
			_pendingRadarToggle = 0;
			if (ChartReady)
			{
				var view = ActivePlotter;
				for (; _pendingChartZoom > 0; _pendingChartZoom--) view.ZoomIn();
				for (; _pendingChartZoom < 0; _pendingChartZoom++) view.ZoomOut();
				if ((_pendingChartOrientation & 1) != 0) view.ToggleOrientation();
				if (_pendingChartCentre != 0) view.Centre();
				if (_pendingChartPanX != 0 || _pendingChartPanY != 0) view.Pan(_pendingChartPanX * PanPixels, _pendingChartPanY * PanPixels);
				for (var i = 0; i < _pendingChartPalette; i++) view.CyclePalette();
				if (_pendingChartPalette > 0)
				{
					_plotterPaletteChoice = view.Palette;
					if (_screens != null) _screens.PlotterPaletteOverride = _plotterPaletteChoice;
				}
				for (var i = 0; i < _pendingChartNextTarget; i++) view.SelectNextTarget();
				if (_pendingChartClearSelection != 0) view.ClearSelection();
				for (; _pendingChartCpaLimit > 0; _pendingChartCpaLimit--) view.StepCpaLimit(1);
				for (; _pendingChartCpaLimit < 0; _pendingChartCpaLimit++) view.StepCpaLimit(-1);
				for (; _pendingChartTcpaLimit > 0; _pendingChartTcpaLimit--) view.StepTcpaLimit(1);
				for (; _pendingChartTcpaLimit < 0; _pendingChartTcpaLimit++) view.StepTcpaLimit(-1);
				if ((_pendingChartAlarmMute & 1) != 0) view.ToggleAlarmMute();
			}

			_pendingChartZoom = 0; _pendingChartOrientation = 0; _pendingChartCentre = 0; _pendingChartPanX = 0; _pendingChartPanY = 0;
			_pendingChartPalette = 0; _pendingChartNextTarget = 0; _pendingChartClearSelection = 0;
			_pendingChartCpaLimit = 0; _pendingChartTcpaLimit = 0; _pendingChartAlarmMute = 0;
			if (RadarReady)
			{
				var view = Radar.View;
				if (_pendingRadarRange != 0) view.ChangeRange(_pendingRadarRange);
				if ((_pendingRadarOrientation & 1) != 0) view.SetHeadUp(!view.HeadUp);
				for (var i = 0; i < _pendingRadarPalette; i++) view.SetPalette((RadarPalette)(((int)view.Palette + 1) % 3));
				if (_pendingRadarPalette > 0)
				{
					_radarPaletteChoice = view.Palette;
					if (_screens != null) _screens.RadarPaletteOverride = _radarPaletteChoice;
				}
				for (; _pendingRadarEbl > 0; _pendingRadarEbl--) view.StepEbl(1);
				for (; _pendingRadarEbl < 0; _pendingRadarEbl++) view.StepEbl(-1);
				for (; _pendingRadarVrm > 0; _pendingRadarVrm--) view.StepVrm(1);
				for (; _pendingRadarVrm < 0; _pendingRadarVrm++) view.StepVrm(-1);
				for (; _pendingRadarGain > 0; _pendingRadarGain--) view.StepGain(1);
				for (; _pendingRadarGain < 0; _pendingRadarGain++) view.StepGain(-1);
				for (; _pendingRadarSea > 0; _pendingRadarSea--) view.StepSea(1);
				for (; _pendingRadarSea < 0; _pendingRadarSea++) view.StepSea(-1);
			}

			_pendingRadarRange = 0; _pendingRadarOrientation = 0; _pendingRadarPalette = 0; _pendingRadarEbl = 0; _pendingRadarVrm = 0; _pendingRadarGain = 0; _pendingRadarSea = 0;
		}

		private void TextEntryChanged(bool held)
		{
			if (held) ClearPendingKeys();
			Apply();
		}
		private void ClearPendingKeys()
		{
			_pendingChartToggle = 0; _pendingRadarToggle = 0;
			_pendingChartZoom = 0; _pendingChartOrientation = 0; _pendingChartCentre = 0; _pendingChartPanX = 0; _pendingChartPanY = 0;
			_pendingChartPalette = 0; _pendingChartNextTarget = 0; _pendingChartClearSelection = 0;
			_pendingChartCpaLimit = 0; _pendingChartTcpaLimit = 0; _pendingChartAlarmMute = 0;
			_pendingRadarRange = 0; _pendingRadarOrientation = 0; _pendingRadarPalette = 0;
			_pendingRadarEbl = 0; _pendingRadarVrm = 0; _pendingRadarGain = 0; _pendingRadarSea = 0;
		}

		private static VisualElement Host(string name, Rect placement)
		{
			var host = new VisualElement { name = name };
			host.style.position = Position.Absolute;
			host.style.left = placement.x;
			host.style.top = placement.y;
			host.style.width = placement.width;
			host.style.height = placement.height;
			host.style.backgroundColor = InstrumentPalette.Day.Console;
			host.style.display = DisplayStyle.None;
			return host;
		}

		private static Label Status(VisualElement host)
		{
			var label = new Label(LoadingText) { pickingMode = PickingMode.Ignore };
			label.style.position = Position.Absolute;
			label.style.left = 0;
			label.style.top = 0;
			label.style.right = 0;
			label.style.bottom = 0;
			label.style.unityTextAlign = TextAnchor.MiddleCenter;
			label.style.whiteSpace = WhiteSpace.Normal;
			label.style.fontSize = 18;
			label.style.paddingLeft = 16;
			label.style.paddingRight = 16;
			label.style.color = InstrumentPalette.Day.Text;
			host.Add(label);
			return label;
		}

		private void OnDestroy()
		{
			_alarmInput.Dispose(); _portInput.Dispose(); _starboardInput.Dispose();
			if (_textEntry != null) _textEntry.Changed -= TextEntryChanged;
			ReleaseScreens();
			if (_followedView != null)
			{
				_followedView.SettingsChanged -= OnWheelhouseSettingsChanged;
				_followedView.Settings.Changed -= OnSavedSettingsChanged;
				_followedView = null;
			}

			_mapper?.Dispose();
			_mapper = null;
			if (_actions != null)
			{
				_bindings?.Unregister(_actions);
				Destroy(_actions);
				_actions = null;
			}

			if (_ownedSettings != null)
			{
				Destroy(_ownedSettings);
				_ownedSettings = null;
			}
		}
	}
}
