using System;
using FerrySim.Chartplotter;
using FerrySim.Controls;
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
	/// The wheelhouse displays of the harbor scene: the chartplotter upper left and the radar upper right, both hidden
	/// until their key shows them, both connected to the runner and the shared chart load once it is ready, and both
	/// following the scene's day and night. Until the chart is ready each shows CHART LOADING; a failed load shows the
	/// error, and the run is unaffected either way. They draw in the wheelhouse UI's own panel (the 1280×720 design
	/// scaled with the screen) below its status line, hide in the external camera views and while the scenario picker or
	/// the debrief is open, and their keys ("Displays" action map, Input/README.md) work only while they are visible.
	/// Render-only: nothing here writes to the run, the recording or the replay.
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
		private int _pendingRadarEbl, _pendingRadarVrm, _pendingRadarGain, _pendingRadarSea;
		private double _composedAt = double.NaN;
		private double _readyAt = double.NaN;
		private long _peakMonoUsedBytes;

		/// <summary>The shared chart load this scene polls.</summary>
		public HarborChartLoad Load => _load;

		public HarborChartLoadState ChartState => _load != null ? _load.State : HarborChartLoadState.Loading;

		/// <summary>True once both displays were connected to the ready chart.</summary>
		public bool Connected => _connected;

		/// <summary>Seconds from the scene's composition to the frame that connected the displays; NaN until then.</summary>
		public double ChartReadySeconds => double.IsNaN(_readyAt) ? double.NaN : _readyAt - _composedAt;

		/// <summary>The largest Mono heap use seen on the main thread while the chart was loading (Profiler.GetMonoUsedSizeLong).</summary>
		public long PeakMonoUsedBytes => _peakMonoUsedBytes;

		public RadarController Radar => _radar;

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
				return view != null && !view.PickerVisible && !view.DebriefVisible;
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
			_chartplotter.DaylightSource = _sun;
			_chartplotter.Visible = false;
			if (helm != null && helm.Actions != null)
			{
				_actions = Instantiate(helm.Actions);
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
				_mapper.RadarRangeRequested += OnRadarRange;
				_mapper.RadarOrientationRequested += OnRadarOrientation;
				_mapper.RadarPaletteRequested += OnRadarPalette;
				_mapper.RadarEblRequested += OnRadarEbl;
				_mapper.RadarVrmRequested += OnRadarVrm;
				_mapper.RadarGainRequested += OnRadarGain;
				_mapper.RadarSeaRequested += OnRadarSea;
			}

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

			AdoptPanel();
			FollowWheelhouseView();
			PollLoad();
			Apply();
			ApplyPendingKeys();
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
				}

				_followedView = view;
				if (view != null)
				{
					view.SettingsChanged += OnWheelhouseSettingsChanged;
					OnWheelhouseSettingsChanged();
				}
			}
		}

		private void OnWheelhouseSettingsChanged()
		{
			if (_followedView == null)
			{
				return;
			}

			_unit = _followedView.DistanceUnit;
			_unitKnown = true;
			if (_connected && _chartplotter.View != null && _chartplotter.View.DistanceUnit != _unit)
			{
				_chartplotter.View.DistanceUnit = _unit;
			}
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

			_radar.Connect(_runner, _load.RadarScene, _radarHost, compact: true);
			_radarStatus.style.display = DisplayStyle.None;
			_chartplotter.Connect(_runner, _load.Tessellation);
			_chartObject.SetActive(true);
			if (_unitKnown && _chartplotter.View != null)
			{
				_chartplotter.View.DistanceUnit = _unit;
			}

			if (_chartplotter.Document != null)
			{
				_chartplotter.Document.sortingOrder = ChartplotterSortingOrder;
				_chartplotter.Document.panelSettings = _settings;
			}

			_connected = true;
			_readyAt = Time.realtimeSinceStartupAsDouble;
			_forceApply = true;
		}

		private void Apply()
		{
			var allowed = DisplaysAllowed;
			var radarVisible = _radarShown && allowed;
			var chartVisible = _chartShown && allowed;
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
		/// Night holds the night palette on both displays; by day a palette chosen with the keys holds until the next
		/// night. The chartplotter does this itself from its daylight source; the radar is told here.
		/// </summary>
		private void FollowDaylight()
		{
			if (!_connected || _sun == null || _radar.View == null)
			{
				return;
			}

			var night = _sun.IsNight;
			var palette = _radar.View.Palette;
			if (night && palette != RadarPalette.Night)
			{
				_radar.View.SetPalette(RadarPalette.Night);
			}
			else if (!night && palette == RadarPalette.Night)
			{
				_radar.View.SetPalette(RadarPalette.Day);
			}
		}

		private bool ChartReady => _chartVisible && _connected && _chartplotter.View != null;

		private bool RadarReady => _radarVisible && _connected && _radar.View != null;

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
			var allowed = DisplaysAllowed;
			if (allowed)
			{
				if ((_pendingChartToggle & 1) != 0) _chartShown = !_chartShown;
				if ((_pendingRadarToggle & 1) != 0) _radarShown = !_radarShown;
				if (_pendingChartToggle != 0 || _pendingRadarToggle != 0) Apply();
			}

			_pendingChartToggle = 0;
			_pendingRadarToggle = 0;
			if (ChartReady)
			{
				var view = _chartplotter.View;
				for (; _pendingChartZoom > 0; _pendingChartZoom--) view.ZoomIn();
				for (; _pendingChartZoom < 0; _pendingChartZoom++) view.ZoomOut();
				if ((_pendingChartOrientation & 1) != 0) view.ToggleOrientation();
				if (_pendingChartCentre != 0) view.Centre();
				if (_pendingChartPanX != 0 || _pendingChartPanY != 0) view.Pan(_pendingChartPanX * PanPixels, _pendingChartPanY * PanPixels);
				for (var i = 0; i < _pendingChartPalette; i++) view.CyclePalette();
				for (var i = 0; i < _pendingChartNextTarget; i++) view.SelectNextTarget();
				if (_pendingChartClearSelection != 0) view.ClearSelection();
			}

			_pendingChartZoom = 0; _pendingChartOrientation = 0; _pendingChartCentre = 0; _pendingChartPanX = 0; _pendingChartPanY = 0;
			_pendingChartPalette = 0; _pendingChartNextTarget = 0; _pendingChartClearSelection = 0;
			if (RadarReady)
			{
				var view = _radar.View;
				if (_pendingRadarRange != 0) view.ChangeRange(_pendingRadarRange);
				if ((_pendingRadarOrientation & 1) != 0) view.SetHeadUp(!view.HeadUp);
				for (var i = 0; i < _pendingRadarPalette; i++) view.SetPalette((RadarPalette)(((int)view.Palette + 1) % 3));
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
			if (_followedView != null)
			{
				_followedView.SettingsChanged -= OnWheelhouseSettingsChanged;
				_followedView = null;
			}

			_mapper?.Dispose();
			_mapper = null;
			if (_actions != null)
			{
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
