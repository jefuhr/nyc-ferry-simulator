using System.Collections.Generic;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Night and fog on the run (World/Atmosphere/README.md). Next to the scene's <see cref="SunController"/>, which draws the
	/// sky and the fog on the run's clock and visibility, it lights the run:
	/// <list type="bullet">
	/// <item>the learner's vessel's navigation lights (<see cref="NavigationLights"/>, Rule 23 underway), placed on its greybox
	/// from the run's vessel file at every run start;</item>
	/// <item>the learner's fog signal (<see cref="FogSignal"/>, Rule 35) at the masthead;</item>
	/// <item>each bridge's nominal 33 CFR 118.65 lights (<see cref="BridgeLights"/>);</item>
	/// <item>the imported aids, flashing on the run's clock, with their points seen at range (<see cref="AidLights"/>);</item>
	/// <item>the air every light is seen through (<see cref="SignalLight.SetOptics"/>): the run's visibility, and the eye's
	/// threshold for the sun's altitude;</item>
	/// <item>the world import's unlit chart geometry (terrain, structures, landings), dimmed at dusk and night and drawn
	/// through the fog by the import's own shaders while <see cref="ChartLightingKeyword"/> is on.</item>
	/// </list>
	/// Lights show by <see cref="Daylight"/>'s rules. It does nothing until it has a run to follow, so a scene without a
	/// runner looks as it did. <see cref="SunController"/> adds it in play mode.
	/// </summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(100)]
	public sealed class Atmosphere : MonoBehaviour
	{
		public const string LightsName = "Navigation lights";

		public const string WhistleName = "Fog signal whistle";

		/// <summary>
		/// The global keyword the import's chart shaders (World/Import/Runtime DepthColour and SolidColour) read: on, they
		/// take <see cref="SunController.ChartLight"/> and the run's fog; off, they draw exactly as imported.
		/// </summary>
		public const string ChartLightingKeyword = "FERRYSIM_CHART_LIGHTING";

		private readonly List<BridgeLights> _bridges = new List<BridgeLights>(4);
		private SunController _sun;
		private VesselSimulationRunner _runner;
		private NavigationLights _ownLights;
		private FogSignal _fogSignal;
		private AidLights _aids;
		private Vector2 _optics = new Vector2(float.NaN, float.NaN);

		public SunController Sun => _sun;

		public VesselSimulationRunner Runner => _runner;

		/// <summary>The learner's lights, or null before the first run.</summary>
		public NavigationLights OwnLights => _ownLights;

		/// <summary>The learner's fog signal, or null before the first run.</summary>
		public FogSignal FogSignal => _fogSignal;

		public IReadOnlyList<BridgeLights> Bridges => _bridges;

		public AidLights Aids => _aids;

		/// <summary>Whether the import's chart geometry is drawn dimmed and fogged this frame (<see cref="ChartLightingKeyword"/>).</summary>
		public bool ChartLightingOn { get; private set; }

		/// <summary>Whether navigation and bridge lights show this frame (Rule 20, 33 CFR 118.55).</summary>
		public bool LightsShown { get; private set; }

		/// <summary>The extinction (1/m) and threshold (lux) the lights are seen with this frame.</summary>
		public Vector2 Optics => _optics;

		/// <summary>Follows <paramref name="runner"/> (null: none), with the sky of <paramref name="sun"/>.</summary>
		public void Configure(SunController sun, VesselSimulationRunner runner)
		{
			_sun = sun;
			if (ReferenceEquals(runner, _runner))
			{
				return;
			}

			if (_runner != null)
			{
				_runner.RunStarted -= OnRunStarted;
			}

			_runner = runner;
			if (_runner == null)
			{
				_fogSignal?.Follow(null);
				return;
			}

			_runner.RunStarted += OnRunStarted;
			if (_runner.IsInitialized)
			{
				OnRunStarted();
			}
		}

		private void Start()
		{
			if (_sun == null)
			{
				_sun = GetComponent<SunController>();
			}

			if (_runner == null && _sun != null)
			{
				Configure(_sun, _sun.Runner != null ? _sun.Runner : FindFirstObjectByType<VesselSimulationRunner>());
			}

			if (_runner == null)
			{
				return;
			}

			foreach (var bridge in FindObjectsByType<BridgeGreybox>(FindObjectsSortMode.InstanceID))
			{
				if (BridgeLights.TryAdd(bridge, out var lights))
				{
					_bridges.Add(lights);
				}
			}

			_aids = AidLights.Find(null);
		}

		private void OnDestroy()
		{
			if (_runner != null)
			{
				_runner.RunStarted -= OnRunStarted;
			}

			SetChartLighting(false);
		}

		private void OnRunStarted()
		{
			if (_runner.Parameters == null)
			{
				return;
			}

			var vessel = _runner.transform;
			if (_ownLights == null)
			{
				var host = new GameObject(LightsName);
				host.layer = vessel.gameObject.layer;
				host.transform.SetParent(vessel, false);
				_ownLights = host.AddComponent<NavigationLights>();
			}

			var layout = VesselLightLayout.ForGreybox(GreyboxDimensions.From(_runner.Parameters));
			_ownLights.Configure(layout, VesselLightStatus.Underway);

			if (_fogSignal == null)
			{
				var whistle = new GameObject(WhistleName);
				whistle.transform.SetParent(vessel, false);
				_fogSignal = whistle.AddComponent<FogSignal>();
			}

			// Annex III 86.01(e)(ii): "as high as practicable": at the masthead.
			_fogSignal.transform.localPosition = layout.Masthead;
			_fogSignal.Follow(_runner);
		}

		// After the runner's step (execution order -100) and the sky's (0), so the frame's state is whole by the end of Update.
		private void Update()
		{
			RefreshLights();
			RefreshAids();
		}

		private void OnDisable() => SetChartLighting(false);

		/// <summary>
		/// Switches the vessel's and the bridges' lights for this frame's sky and visibility, the import's chart geometry's
		/// night and fog (<see cref="ChartLightingKeyword"/>), and the air every light is seen through. Runs every frame after
		/// the sky has followed the run; allocation-free.
		/// </summary>
		public void RefreshLights()
		{
			if (_runner == null || !_runner.IsInitialized || _sun == null)
			{
				SetChartLighting(false);
				return;
			}

			var sun = _sun.Sun;
			var visibility = _sun.VisibilityMetres;
			LightsShown = Daylight.ShowsNavigationLights(sun, visibility);
			if (_ownLights != null)
			{
				_ownLights.On = LightsShown;
			}

			for (var i = 0; i < _bridges.Count; i++)
			{
				_bridges[i].On = LightsShown;
			}

			// The import's unlit chart geometry, dimmed and fogged with the rest of the scene; as imported by day in clear air.
			SetChartLighting(_sun.ChartLight < 1f || MeteorologicalVisibility.IsLimited(visibility));

			// Exact comparisons: Vector2's == is approximate, and thresholds are a few times 1e-7.
			var extinction = (float)SignalLightOptics.LightExtinction(visibility);
			var threshold = (float)SignalLightOptics.ThresholdLux(sun.ApparentElevationDegrees);
			if (extinction != _optics.x || threshold != _optics.y)
			{
				_optics = new Vector2(extinction, threshold);
				SignalLight.SetOptics(extinction, threshold);
			}
		}

		/// <summary>
		/// Puts the aids at the run's simulated time (<see cref="AidLights.Show"/>). Allocation-free on the lights with a
		/// chart sequence; the import's character code allocates for a light without one (World/Atmosphere/README.md).
		/// </summary>
		public void RefreshAids()
		{
			if (_runner == null || !_runner.IsInitialized || _sun == null || _aids == null)
			{
				return;
			}

			_aids.Show(_runner.Driver.SimulationTime, Daylight.ShowsAidLights(_sun.Sun));
		}

		/// <summary>
		/// Turns the import's chart shaders' night and fog on (<see cref="ChartLightingKeyword"/>, a global keyword), or off,
		/// which draws them exactly as imported. Off whenever this is disabled or destroyed, so no other scene inherits it.
		/// </summary>
		private void SetChartLighting(bool on)
		{
			// The string forms: a GlobalKeyword may not be created while a MonoBehaviour is being constructed.
			if (on == ChartLightingOn && on == Shader.IsKeywordEnabled(ChartLightingKeyword))
			{
				return;
			}

			ChartLightingOn = on;
			if (on)
			{
				Shader.EnableKeyword(ChartLightingKeyword);
			}
			else
			{
				Shader.DisableKeyword(ChartLightingKeyword);
			}
		}
	}
}
