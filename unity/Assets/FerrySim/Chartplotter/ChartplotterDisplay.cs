using System;
using Unity.Profiling;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using FerrySim.Sim;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter
{
	/// <summary>
	/// Hosts the <see cref="ChartplotterView"/> in its own UIDocument (the wheelhouse panel settings, so it scales with the
	/// screen from the 1280×720 design) at a placement the scene chooses, and refreshes it after every frame's physics
	/// from the connected own ship and targets. It never searches the scene or loads data: <see cref="Connect"/> takes
	/// the runner (or any telemetry source), the loaded chart and runtime packs, and the targets. Render-only: nothing
	/// here writes to the run, the recording or the replay.
	/// </summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(150)]
	public sealed class ChartplotterDisplay : MonoBehaviour
	{
		[Tooltip("Optional. Empty: runtime panel settings scaled from a 1280x720 design, shared with the wheelhouse.")]
		[SerializeField]
		private PanelSettings panelSettings;

		[Tooltip("Where the display sits, in 1280x720 reference pixels.")]
		[SerializeField]
		private Rect placement = new Rect(8f, 8f, 560f, 400f);

		[SerializeField]
		private ChartPaletteKind palette = ChartPaletteKind.Day;

		[SerializeField]
		private DistanceUnit distanceUnit = DistanceUnit.Feet;

		[Tooltip("Follow the run's light: the night palette at night, the day palette otherwise, when a daylight source is set.")]
		[SerializeField]
		private bool followDaylight = true;

		private static readonly ProfilerMarker RefreshMarker = new ProfilerMarker("FerrySim.Chartplotter.Refresh");
		private PanelSettings _ownedSettings;
		private bool _visible = true;
		private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
		private double _totalMilliseconds;
		private double _lastTime;

		/// <summary>Own ship, as connected.</summary>
		public IVesselTelemetrySource OwnShip { get; private set; }

		/// <summary>The targets, as connected.</summary>
		public ITargetSource Targets { get; private set; }

		/// <summary>The chart as tessellated at connect.</summary>
		public ChartTessellation Chart { get; private set; }

		/// <summary>The run's light; set by the scene. With <c>followDaylight</c>, night gives the night palette.</summary>
		public IDaylightSource DaylightSource { get; set; }

		public ChartplotterView View { get; private set; }

		public UIDocument Document { get; private set; }

		public double LastRefreshMilliseconds { get; private set; }

		public double MaximumRefreshMilliseconds { get; private set; }

		public long Frames { get; private set; }

		public double MeanRefreshMilliseconds => Frames > 0 ? _totalMilliseconds / Frames : 0.0;

		/// <summary>The placement in 1280×720 reference pixels.</summary>
		public Rect Placement => placement;

		/// <summary>Sets the panel settings before the component is enabled (tests render to a texture this way).</summary>
		public void Configure(PanelSettings settings) => panelSettings = settings;

		/// <summary>Moves or resizes the display, in reference pixels.</summary>
		public void SetPlacement(Rect reference)
		{
			placement = reference;
			ApplyPlacement();
		}

		/// <summary>
		/// Connects the runner as own ship and its traffic as the targets. The chart and runtime packs are the scene's
		/// loaded, verified copies: the chart is tessellated once here.
		/// </summary>
		public void Connect(VesselSimulationRunner runner, HarborChartPack chart, HarborRuntimePack runtime)
		{
			if (runner == null) throw new ArgumentNullException(nameof(runner));
			Connect(runner, chart, runtime, new TrafficRunTargets(runner));
		}

		/// <summary>Connects any own-ship source and target source; either may be null (NO POSITION, no targets).</summary>
		public void Connect(IVesselTelemetrySource ownShip, HarborChartPack chart, HarborRuntimePack runtime, ITargetSource targets)
		{
			Connect(ownShip, chart != null && runtime != null ? ChartTessellation.Build(chart, runtime) : null, targets);
		}

		/// <summary>The composition root's seam: a tessellation built ahead (off the main thread, with the chart load) for the runner and its traffic.</summary>
		public void Connect(VesselSimulationRunner runner, ChartTessellation chart)
		{
			if (runner == null) throw new ArgumentNullException(nameof(runner));
			Connect(runner, chart, new TrafficRunTargets(runner));
		}

		/// <summary>Connects with a tessellation built ahead; the meshes are generated here, on the main thread, when the view next renders.</summary>
		public void Connect(IVesselTelemetrySource ownShip, ChartTessellation chart, ITargetSource targets)
		{
			OwnShip = ownShip;
			Targets = targets;
			Chart = chart;
			View?.SetChart(Chart);
		}

		/// <summary>
		/// Shown (the default) or hidden. Hidden, the view is not drawn and <see cref="Refresh"/> does nothing, while the
		/// view, its chart and its settings stay as they are; disabling the component would tear the view down instead.
		/// </summary>
		public bool Visible
		{
			get => _visible;
			set
			{
				_visible = value;
				ApplyVisibility();
			}
		}

		private void OnEnable()
		{
			if (panelSettings == null && _ownedSettings == null)
			{
				_ownedSettings = WheelhousePanelSettings.Create();
			}

			Document = GetComponent<UIDocument>();
			if (Document == null)
			{
				Document = gameObject.AddComponent<UIDocument>();
			}

			Document.panelSettings = panelSettings != null ? panelSettings : _ownedSettings;
			BuildView();
		}

		private void OnDisable()
		{
			if (View != null)
			{
				View.RemoveFromHierarchy();
				View.Canvas.Dispose();
				View.Targets.Dispose();
				View = null;
			}
		}

		private void OnDestroy()
		{
			if (_ownedSettings != null)
			{
				Destroy(_ownedSettings);
				_ownedSettings = null;
			}
		}

		private void BuildView()
		{
			var root = Document != null ? Document.rootVisualElement : null;
			if (root == null || View != null)
			{
				return;
			}

			View = new ChartplotterView { Palette = palette, DistanceUnit = distanceUnit };
			View.SetChart(Chart);
			root.Add(View);
			ApplyPlacement();
			ApplyVisibility();
		}

		private void ApplyVisibility()
		{
			if (View != null)
			{
				View.style.display = _visible ? DisplayStyle.Flex : DisplayStyle.None;
			}
		}

		private void ApplyPlacement()
		{
			if (View == null)
			{
				return;
			}

			View.style.position = Position.Absolute;
			View.style.left = placement.x;
			View.style.top = placement.y;
			View.style.width = placement.width;
			View.style.height = placement.height;
		}

		private void LateUpdate() => Refresh();

		/// <summary>One frame of the display; tests call it directly.</summary>
		public void Refresh()
		{
			if (View == null)
			{
				BuildView();
				if (View == null)
				{
					return;
				}
			}

			if (!_visible)
			{
				return;
			}

			_watch.Restart();
			RefreshMarker.Begin();
			// A destroyed scene component would keep answering with its last values: treat it as none.
			var own = OwnShip is UnityEngine.Object component && component == null ? null : OwnShip;
			if (own != null && own.HasTelemetry)
			{
				_lastTime = own.State.Time;
			}

			var daylight = DaylightSource is UnityEngine.Object sky && sky == null ? null : DaylightSource;
			if (followDaylight && daylight != null)
			{
				var wanted = daylight.IsNight ? ChartPaletteKind.Night : View.Palette == ChartPaletteKind.Night ? ChartPaletteKind.Day : View.Palette;
				View.Palette = wanted;
			}

			View.Refresh(own, Targets, _lastTime);
			RefreshMarker.End();
			palette = View.Palette;
			distanceUnit = View.DistanceUnit;
			_watch.Stop();
			LastRefreshMilliseconds = _watch.Elapsed.TotalMilliseconds;
			MaximumRefreshMilliseconds = Math.Max(MaximumRefreshMilliseconds, LastRefreshMilliseconds);
			_totalMilliseconds += LastRefreshMilliseconds;
			Frames++;
		}

		/// <summary>Forgets the timing statistics (after a warm-up).</summary>
		public void ResetTiming()
		{
			MaximumRefreshMilliseconds = 0.0;
			_totalMilliseconds = 0.0;
			Frames = 0;
		}
	}
}
