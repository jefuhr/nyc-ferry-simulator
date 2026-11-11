using System;
using FerrySim.Chartplotter;
using FerrySim.Core.Radar;
using FerrySim.Core.Traffic;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.UI.Displays;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Console
{
	/// <summary>Render-only composition of five shared display pictures and four physical screen surfaces.</summary>
	[DefaultExecutionOrder(160)]
	public sealed class ConsoleScreens : MonoBehaviour, IHudScreenSource
	{
		public static readonly string[] MountNames = { "RadarScreen", "PlotterScreen", "PortEngineScreen", "StarboardEngineScreen" };
		private static readonly ProfilerMarker[] RefreshMarkers =
		{
			new ProfilerMarker("FerrySim.Console.Refresh.radar"), new ProfilerMarker("FerrySim.Console.Refresh.plotter"),
			new ProfilerMarker("FerrySim.Console.Refresh.engine-port"), new ProfilerMarker("FerrySim.Console.Refresh.engine-starboard"),
			new ProfilerMarker("FerrySim.Console.Refresh.alarms")
		};
		private readonly ScreenScheduler _scheduler = new ScreenScheduler();
		private readonly ScreenPanel[] _panels = new ScreenPanel[ScreenScheduler.Count];
		private readonly Renderer[] _surfaces = new Renderer[4];
		private readonly Material[] _originals = new Material[4], _materials = new Material[4];
		private readonly int[] _refreshes = new int[ScreenScheduler.Count];
		private Transform _interior;
		private IVesselTelemetrySource _source;
		private IInstrumentSource _instruments;
		private ITargetSource _targets;
		private IEnvironmentReadoutSource _environment;
		private ChartTessellation _chart;
		private RadarScene _radarScene;
		private TrafficRun _traffic;
		private DisplaySkin _engineSkin, _alarmSkin;
		private bool _enabled = true, _built;
		private int _lastFrame = int.MinValue;
		private double _vectorMinutes = ChartplotterView.DefaultVectorMinutes;
		private PredictorMode _predictor;
		private PaletteKind _palette;
		public bool AutomaticRefresh { get; set; } = true;
		public IDaylightSource DaylightSource { get; set; }
		public RadarController Radar { get; private set; }
		public ChartplotterView Plotter { get; private set; }
		public EngineMonitorView PortEngine { get; private set; }
		public EngineMonitorView StarboardEngine { get; private set; }
		public AlarmPanelView Alarms { get; private set; }
		/// <summary>Explicit colour target storage only; excludes the radar raster, font and UI atlases and driver overhead.</summary>
		public long TargetColourBytes { get; private set; }
		public int LastRefreshFrame { get; private set; } = -1;
		public int LastRefreshScreen { get; private set; } = -1;
		public bool Enabled
		{
			get => _enabled;
			set { if (value == _enabled) return; _enabled = value; if (value && isActiveAndEnabled) Build(); else Release(); }
		}
		public double VectorMinutes
		{
			get => _vectorMinutes;
			set { if (!(value > 0) || double.IsInfinity(value)) return; _vectorMinutes = value; if (Plotter != null) Plotter.VectorMinutes = value; }
		}
		public PredictorMode Predictor
		{
			get => _predictor;
			set { if (value != PredictorMode.Straight && value != PredictorMode.Curved) return; _predictor = value; if (Plotter != null) Plotter.Predictor = value; }
		}
		public PaletteKind Palette { get => _palette; set => _palette = value; }
		/// <summary>
		/// The radar palette chosen with the radar's palette key, or null to follow day and night. A viewer
		/// preference kept on this host, so it survives the targets being released and rebuilt.
		/// </summary>
		public RadarPalette? RadarPaletteOverride { get; set; }
		/// <summary>The chartplotter palette chosen with its palette key, or null to follow day and night.</summary>
		public ChartPaletteKind? PlotterPaletteOverride { get; set; }
		public static RadarPalette RadarPaletteFor(bool night, RadarPalette? chosen) => chosen ?? (night ? RadarPalette.Night : RadarPalette.Day);
		public static ChartPaletteKind PlotterPaletteFor(bool night, ChartPaletteKind? chosen) => chosen ?? (night ? ChartPaletteKind.Night : ChartPaletteKind.Day);
		/// <summary>Supply already-loaded chart/radar indices. No scene search, pack loading or simulation writes.</summary>
		public void Connect(Transform interior, IVesselTelemetrySource source, IInstrumentSource instruments,
			ChartTessellation chart, RadarScene radarScene, ITargetSource targets = null, TrafficRun traffic = null,
			IEnvironmentReadoutSource environment = null, DisplaySkinLibrary skins = null)
		{
			if (interior == null) throw new ArgumentNullException(nameof(interior));
			if (source == null) throw new ArgumentNullException(nameof(source));
			if (radarScene == null) throw new ArgumentNullException(nameof(radarScene));
			Release();
			_interior = interior; _source = source; _instruments = instruments; _chart = chart; _radarScene = radarScene;
			_targets = targets ?? (source is VesselSimulationRunner runner ? new TrafficRunTargets(runner) : null);
			_traffic = traffic; _environment = environment;
			var library = skins ?? new DisplaySkinLibrary(DisplaySkinAlarms.From(instruments?.Instruments));
			_engineSkin = library.Load("engine", "generic-a"); _alarmSkin = library.Load("alarm-panel", "generic-a");
			if (_enabled && isActiveAndEnabled) Build();
		}
		public void SetWheelhouseVisible(bool visible) => _scheduler.SetWheelhouseVisible(visible);
		public void SetPopOutShown(string screenId, bool shown) => _scheduler.SetPopOutShown(screenId, shown);
		public void SetFocus(string screenId) => _scheduler.SetFocus(screenId);
		public int RefreshCount(string screenId) { var i = ScreenScheduler.Index(screenId); return i < 0 ? 0 : _refreshes[i]; }
		public bool TryGetScreen(string screenId, out Texture texture)
		{
			var index = ScreenScheduler.Index(screenId);
			texture = _enabled && _built && index >= 0 ? _panels[index].Target : null;
			return texture != null;
		}
		private void OnEnable() { if (_enabled) Build(); }
		private void OnDisable() => Release();
		private void OnDestroy() => Release();
		private void LateUpdate() { if (AutomaticRefresh) Tick(Time.unscaledTimeAsDouble, Time.frameCount); }
		/// <summary>Call once per render frame with monotonic wall time; simulated time still controls every instrument and lamp.</summary>
		public void Tick(double time, int frame)
		{
			if (!_enabled || !_built || frame == _lastFrame) return;
			_lastFrame = frame;
			LastRefreshFrame = frame; LastRefreshScreen = -1;
			// Keep demanded panels live: hiding between data slots invalidates retained geometry on every show.
			// Only a change of viewer demand freezes/unfreezes a panel; data refresh cadence stays independent.
			for (var i = 0; i < _panels.Length; i++) _panels[i].SetRendering(_scheduler.IsDemanded(i));
			if (_scheduler.IsDemanded(0)) Radar.AdvanceEngine();
			var slot = _scheduler.Next(time, frame);
			if (slot < 0) return;
			LastRefreshScreen = slot;
			using var refreshProfile = RefreshMarkers[slot].Auto();
			if (DaylightSource != null) _palette = DaylightSource.IsNight ? PaletteKind.Night : PaletteKind.Day;
			var night = _palette == PaletteKind.Night;
			var simulationTime = _source.HasTelemetry ? _source.State.Time : 0;
			var readout = _instruments != null && _instruments.HasInstruments ? _instruments.Instruments : null;
			switch (slot)
			{
				case 0:
					var radarPalette = RadarPaletteFor(night, RadarPaletteOverride);
					if (Radar.View.Palette != radarPalette) Radar.View.SetPalette(radarPalette);
					Radar.RefreshReadouts(); Radar.UploadPicture(); break;
				case 1:
					Plotter.Palette = PlotterPaletteFor(night, PlotterPaletteOverride);
					Plotter.Refresh(_source, _targets, simulationTime); break;
				case 2: if (PortEngine.Palette != _palette) PortEngine.Palette = _palette; PortEngine.Refresh(readout, simulationTime); break;
				case 3: if (StarboardEngine.Palette != _palette) StarboardEngine.Palette = _palette; StarboardEngine.Refresh(readout, simulationTime); break;
				case 4: if (Alarms.Palette != _palette) Alarms.Palette = _palette; Alarms.Refresh(readout, simulationTime); break;
			}
			_refreshes[slot]++;
		}
		private void Build()
		{
			if (_built || _interior == null || _source == null) return;
			// Validate all mounts before allocating resources; a broken prefab is reported, never silently skipped.
			for (var i = 0; i < 4; i++)
			{
				_surfaces[i] = FindSurface(_interior, MountNames[i] + "Surface");
				if (_surfaces[i] == null) throw new InvalidOperationException("Missing console surface " + MountNames[i] + "Surface");
				_originals[i] = _surfaces[i].sharedMaterial;
			}
			try
			{
				for (var i = 0; i < ScreenScheduler.Count; i++)
				{
					var skin = i < 2 ? null : i < 4 ? _engineSkin : _alarmSkin;
					var width = skin == null ? 1024 : Mathf.RoundToInt(skin.Reference.x);
					var height = skin == null ? 576 : Mathf.RoundToInt(skin.Reference.y);
					_panels[i] = new ScreenPanel(transform, ScreenScheduler.Id(i), width, height, skin);
					TargetColourBytes += _panels[i].ColourBytes;
				}
				Radar = _panels[0].Document.gameObject.AddComponent<RadarController>(); Radar.AutomaticRefresh = false;
				if (_source is VesselSimulationRunner runner) Radar.Connect(runner, _radarScene, _panels[0].Root, compact: true);
				else Radar.Connect(_source, _radarScene, _traffic, _environment, _panels[0].Root, compact: true);
				// The existing compact radar retains its entire interface and square PPI, scaled uniformly to fit.
				Radar.View.style.position = Position.Absolute; Radar.View.style.left = 152; Radar.View.style.top = 3;
				Radar.View.style.transformOrigin = new TransformOrigin(0, 0);
				Radar.View.style.scale = new Scale(new Vector3(1.5f, 1.5f, 1));
				Plotter = new ChartplotterView { VectorMinutes = _vectorMinutes, Predictor = _predictor };
				Plotter.style.width = 1024; Plotter.style.height = 576; Plotter.SetChart(_chart); _panels[1].Root.Add(Plotter);
				PortEngine = new EngineMonitorView(DisplaySide.Port, _engineSkin); _panels[2].Root.Add(PortEngine);
				StarboardEngine = new EngineMonitorView(DisplaySide.Starboard, _engineSkin); _panels[3].Root.Add(StarboardEngine);
				Alarms = new AlarmPanelView(_alarmSkin); if (_instruments != null) Alarms.Connect(_instruments); _panels[4].Root.Add(Alarms);
				var shader = Resources.Load<Shader>("ConsoleScreen");
				if (shader == null) throw new InvalidOperationException("ConsoleScreen shader is missing");
				for (var i = 0; i < 4; i++)
				{
					_materials[i] = new Material(shader) { name = "Console " + ScreenScheduler.Id(i), mainTexture = _panels[i].Target };
					_surfaces[i].sharedMaterial = _materials[i];
				}
				_scheduler.Reset(); _lastFrame = int.MinValue; _built = true;
			}
			catch { Release(); throw; }
		}
		private void Release()
		{
			Alarms?.Disconnect(); PortEngine?.CancelPresses(); StarboardEngine?.CancelPresses();
			Plotter?.Canvas.Dispose(); Plotter?.Targets.Dispose(); Plotter = null;
			PortEngine = null; StarboardEngine = null; Alarms = null;
			// Stop radar callbacks before destroying the owning panel; its native raster has one owner.
			if (Radar != null) { Radar.enabled = false; ScreenPanel.Release(Radar); Radar = null; }
			for (var i = 0; i < 4; i++)
			{
				if (_surfaces[i] != null && _materials[i] != null) _surfaces[i].sharedMaterial = _originals[i];
				if (_materials[i] != null) ScreenPanel.Release(_materials[i]);
				_materials[i] = null;
			}
			for (var i = 0; i < _panels.Length; i++) { _panels[i]?.Dispose(); _panels[i] = null; }
			TargetColourBytes = 0; _built = false;
		}
		private static Renderer FindSurface(Transform root, string name)
		{
			if (root.name == name) return root.GetComponent<Renderer>();
			for (var i = 0; i < root.childCount; i++) { var result = FindSurface(root.GetChild(i), name); if (result != null) return result; }
			return null;
		}
	}
}
