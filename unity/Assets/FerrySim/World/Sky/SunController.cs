using System;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace FerrySim.World
{
	/// <summary>
	/// Points the sun light from the date, time and location with <see cref="SolarPosition"/> (NOAA), and
	/// sets simple day-to-night lighting from the sun's apparent elevation (<see cref="SkyGradient"/>):
	/// sun intensity and colour, trilight ambient, fog, and the global sky colours and sun direction
	/// that the FerrySim/Sky skybox draws and the FerrySim/Water shader reflects. Below the horizon the
	/// sun fades out and the ambient carries the scene.
	/// <para>
	/// Time comes from the inspector as a New York wall-clock date and time of day, or from code with
	/// <see cref="SetUtc"/>. In play mode, while a run is going, the sky follows the run instead (<see cref="Follow"/>):
	/// every frame it shows the scenario's start plus the run's simulated time, and the run's visibility, so a paused run's
	/// sky stands still and a replay draws the sky it was recorded under.
	/// </para>
	/// <para>
	/// A limited visibility (<see cref="VisibilityMetres"/>, World/Atmosphere/README.md) draws fog: Unity's exponential fog
	/// at the meteorological optical range's density, a fog colour lit by the sky, and the sky, the sun's disk and its
	/// light seen through the fog. Unlimited (the default) draws exactly the scene's own haze, as before fog existed.
	/// </para>
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	public sealed class SunController : MonoBehaviour, IDaylightSource
	{
		private static readonly int SkyZenithId = Shader.PropertyToID("_FerrySimSkyZenith");
		private static readonly int SkyHorizonId = Shader.PropertyToID("_FerrySimSkyHorizon");
		private static readonly int SkyGroundId = Shader.PropertyToID("_FerrySimSkyGround");
		private static readonly int SunDirectionId = Shader.PropertyToID("_FerrySimSunDirection");
		private static readonly int SunColorId = Shader.PropertyToID("_FerrySimSunColor");
		private static readonly int SunParamsId = Shader.PropertyToID("_FerrySimSunParams");
		private static readonly int ChartLightId = Shader.PropertyToID("_FerrySimChartLight");
		private static readonly int ChartFogId = Shader.PropertyToID("_FerrySimChartFog");
		private static readonly int ChartFogColourId = Shader.PropertyToID("_FerrySimChartFogColour");

		[SerializeField] private Light sun;

		[Tooltip("The sun direction is relative to true north, so it follows the world frame through origin shifts.")]
		[SerializeField] private WorldFrame worldFrame;

		[Header("Date and time, America/New_York")]
		[SerializeField] private int year = 2026;
		[Range(1, 12)]
		[SerializeField] private int month = 6;
		[Range(1, 31)]
		[SerializeField] private int day = 15;
		[Tooltip("Local time of day, hours (12.5 = 12:30).")]
		[Range(0f, 24f)]
		[SerializeField] private float timeOfDayHours = 12f;

		[Header("Observer, WGS84 degrees")]
		[SerializeField] private double latitude = WorldOrigin.LatitudeDegrees;
		[SerializeField] private double longitude = WorldOrigin.LongitudeDegrees;

		[Header("Lighting")]
		[Tooltip("Sun intensity with the sun high.")]
		[Min(0f)]
		[SerializeField] private float noonIntensity = 1.5f;
		[Min(0f)]
		[SerializeField] private float ambientIntensity = 1f;
		[SerializeField] private bool driveFog = true;
		[Tooltip("Exponential-squared fog density, 1/m.")]
		[Min(0f)]
		[SerializeField] private float fogDensity = 0.00007f;

		[Header("The run")]
		[Tooltip("In play mode, follow a run's clock and visibility. Empty runner: the first VesselSimulationRunner in the scene at startup.")]
		[SerializeField] private bool followRun = true;
		[SerializeField] private VesselSimulationRunner runner;

		[Header("Fog (limited visibility), greybox estimates")]
		[Tooltip("Depth of the fog layer the zenith and the sun are seen through, metres.")]
		[Min(1f)]
		[SerializeField] private float fogLayerDepth = 300f;
		[Tooltip("Path through the fog to the horizon, metres.")]
		[Min(1f)]
		[SerializeField] private float horizonFogPath = 5000f;

		private DateTime? _utcOverride;
		private double _visibility;

		/// <summary>The instant the lighting shows, UTC.</summary>
		public DateTime UtcTime => _utcOverride ?? NewYorkTime.ToUtc(LocalDateTime());

		/// <summary>Sun position at the last <see cref="Apply"/>.</summary>
		public SolarPosition Sun { get; private set; }

		/// <summary>Sky colours and sun strength at the last <see cref="Apply"/>, in clear air.</summary>
		public SkyGradient.Sample Sky { get; private set; }

		/// <summary>The sky as drawn at the last <see cref="Apply"/>: <see cref="Sky"/> seen through the fog, or <see cref="Sky"/> itself in unlimited visibility.</summary>
		public SkyGradient.Sample DrawnSky { get; private set; }

		public Light SunLight => sun;

		/// <summary>The panel's night palette suits the sun at the last <see cref="Apply"/> (<see cref="Daylight.PrefersNightPalette"/>).</summary>
		public bool IsNight => Daylight.PrefersNightPalette(Sun);

		/// <summary>
		/// The light falling on the world import's unlit chart geometry at the last <see cref="Apply"/>, 0 to 1: 1 by day,
		/// the direct sun plus the sky's horizon brightness through dusk and night. The import's chart shaders read it while the
		/// atmosphere's <c>FERRYSIM_CHART_LIGHTING</c> keyword is on (World/Atmosphere/README.md).
		/// </summary>
		public float ChartLight { get; private set; } = 1f;

		/// <summary>
		/// The visibility the sky is drawn in, metres: the meteorological optical range (<see cref="MeteorologicalVisibility"/>);
		/// 0 is unlimited. Takes effect at the next <see cref="Apply"/>; a followed run sets it every frame.
		/// </summary>
		public double VisibilityMetres
		{
			get => _visibility;
			set => _visibility = value;
		}

		/// <summary>The run the sky follows in play mode, or null; set it, or leave it to be found in the scene.</summary>
		public VesselSimulationRunner Runner
		{
			get => runner;
			set => runner = value;
		}

		/// <summary>Sets the scene references; the world builder calls this.</summary>
		public void Configure(Light sunLight, WorldFrame frame)
		{
			sun = sunLight;
			worldFrame = frame;
		}

		/// <summary>Shows a UTC instant (DateTimeKind.Utc) and keeps it until the inspector time changes.</summary>
		public void SetUtc(DateTime utc)
		{
			if (utc.Kind != DateTimeKind.Utc)
			{
				throw new ArgumentException("SetUtc needs DateTimeKind.Utc.", nameof(utc));
			}

			ShowUtc(utc);
			Apply();
		}

		/// <summary>Shows a New York wall-clock date and time of day (hours).</summary>
		public void SetLocalTime(int localYear, int localMonth, int localDay, double hours)
		{
			if (hours < 0.0 || hours > 24.0)
			{
				throw new ArgumentOutOfRangeException(nameof(hours), "Time of day must be 0 to 24 hours.");
			}

			_utcOverride = null;
			year = localYear;
			month = localMonth;
			day = localDay;
			timeOfDayHours = (float)hours;
			Apply();
		}

		/// <summary>
		/// Shows <paramref name="run"/>'s sky: the scenario's start plus the run's simulated time (from its step count, so
		/// frames and wall time never enter), in the run's visibility (<see cref="RunVisibility"/>; a replay's is its
		/// recording's). False, changing nothing, when the runner has no run.
		/// </summary>
		public bool Follow(VesselSimulationRunner run)
		{
			if (run == null || !run.IsInitialized)
			{
				return false;
			}

			var start = run.ScenarioStartUtc.UtcDateTime;
			_visibility = RunVisibility.Of(run);
			ShowUtc(start.AddTicks((long)Math.Round(run.Driver.SimulationTime * TimeSpan.TicksPerSecond)));
			Apply();
			return true;
		}

		/// <summary>Recomputes the sun and applies the lighting now.</summary>
		public void Apply()
		{
			var position = SolarPosition.Compute(UtcTime, latitude, longitude);
			var sky = SkyGradient.Evaluate(position.ApparentElevationDegrees);
			var limited = MeteorologicalVisibility.IsLimited(_visibility);
			var drawn = limited ? ThroughFog(sky, position) : sky;
			Sun = position;
			Sky = sky;
			DrawnSky = drawn;

			// Direction toward the sun in the world frame (x east, y up, z north), then in Unity space.
			var elevation = position.ApparentElevationDegrees * Math.PI / 180.0;
			var azimuth = position.AzimuthDegrees * Math.PI / 180.0;
			var toSunLocal = new Vector3(
				(float)(Math.Sin(azimuth) * Math.Cos(elevation)),
				(float)Math.Sin(elevation),
				(float)(Math.Cos(azimuth) * Math.Cos(elevation)));
			var frameRotation = worldFrame != null ? worldFrame.transform.rotation : Quaternion.identity;
			var toSun = frameRotation * toSunLocal;

			if (sun != null)
			{
				var up = Mathf.Abs(toSunLocal.y) > 0.999f ? Vector3.forward : Vector3.up;
				sun.transform.rotation = frameRotation * Quaternion.LookRotation(-toSunLocal, up);
				sun.intensity = noonIntensity * drawn.SunStrength;
				sun.color = drawn.SunColor;
				sun.shadows = drawn.SunStrength > 0.01f ? LightShadows.Hard : LightShadows.None;
				RenderSettings.sun = sun;
			}

			// Trilight ambient ignores RenderSettings.ambientIntensity, so the colours carry the scale.
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = drawn.Zenith * ambientIntensity;
			RenderSettings.ambientEquatorColor = drawn.Horizon * ambientIntensity;
			RenderSettings.ambientGroundColor = drawn.Ground * ambientIntensity;

			if (driveFog)
			{
				RenderSettings.fog = true;
				if (limited)
				{
					// Koschmieder: Unity's exponential fog leaves exp(-density * d), 5% at the visibility.
					RenderSettings.fogMode = FogMode.Exponential;
					RenderSettings.fogDensity = (float)MeteorologicalVisibility.ExtinctionCoefficient(_visibility);
				}
				else
				{
					RenderSettings.fogMode = FogMode.ExponentialSquared;
					RenderSettings.fogDensity = fogDensity;
				}

				// The sky shaders take the globals below as they are, while Unity converts the fog colour from sRGB to linear.
				// In fog, give the fog colour as sRGB, so the fogged scene fades to exactly the horizon the sky draws; the
				// unlimited haze keeps the colour it always had.
				RenderSettings.fogColor = limited ? drawn.Horizon.gamma : drawn.Horizon;
			}

			Shader.SetGlobalColor(SkyZenithId, drawn.Zenith);
			Shader.SetGlobalColor(SkyHorizonId, drawn.Horizon);
			Shader.SetGlobalColor(SkyGroundId, drawn.Ground);
			Shader.SetGlobalVector(SunDirectionId, toSun);
			Shader.SetGlobalColor(SunColorId, drawn.SunColor);
			Shader.SetGlobalVector(SunParamsId, new Vector4(drawn.SunDisk, drawn.SunStrength, drawn.HorizonGlow, 0f));

			// Read only by the import's chart shaders, and only while the atmosphere's FERRYSIM_CHART_LIGHTING keyword is on.
			ChartLight = Mathf.Clamp01(drawn.SunStrength + 1.25f * Luma(drawn.Horizon));
			Shader.SetGlobalVector(ChartLightId, new Vector4(ChartLight, 0f, 0f, 0f));
			Shader.SetGlobalVector(ChartFogId, new Vector4(limited ? (float)MeteorologicalVisibility.ExtinctionCoefficient(_visibility) : 0f, 0f, 0f, 0f));
			Shader.SetGlobalColor(ChartFogColourId, drawn.Horizon);
		}

		/// <summary>
		/// The fog's own colour: a grey as bright as the brighter of the sky's horizon and zenith (fog is lit by the whole
		/// sky), keeping a quarter of the horizon's hue. Colours are sRGB, like the gradient's.
		/// </summary>
		public static Color FogColour(in SkyGradient.Sample sky)
		{
			var luminance = Mathf.Max(Luma(sky.Horizon), Luma(sky.Zenith));
			return Color.Lerp(new Color(luminance, luminance, luminance), sky.Horizon, 0.25f);
		}

		/// <summary>
		/// The sky through fog of the current visibility: the zenith through <see cref="fogLayerDepth"/> of fog, the horizon
		/// and the ground through <see cref="horizonFogPath"/>, both blended toward <see cref="FogColour"/> by the fog's
		/// opacity over that path; the sun's disk and direct light dimmed by the fog's transmittance along the slant path
		/// through the layer. Greybox estimates of a fog layer, not a radiative model.
		/// </summary>
		private SkyGradient.Sample ThroughFog(in SkyGradient.Sample sky, in SolarPosition position)
		{
			var extinction = MeteorologicalVisibility.ExtinctionCoefficient(_visibility);
			var fog = FogColour(sky);
			var zenithOpacity = (float)(1.0 - Math.Exp(-extinction * fogLayerDepth));
			var horizonOpacity = (float)(1.0 - Math.Exp(-extinction * horizonFogPath));
			var sine = Math.Max(Math.Sin(Math.Max(position.ApparentElevationDegrees, 0.0) * Math.PI / 180.0), 0.05);
			var transmittance = (float)Math.Exp(-extinction * fogLayerDepth / sine);
			return new SkyGradient.Sample(
				Color.Lerp(sky.Zenith, fog, zenithOpacity),
				Color.Lerp(sky.Horizon, fog, horizonOpacity),
				Color.Lerp(sky.Ground, fog, horizonOpacity),
				sky.SunColor,
				sky.SunStrength * transmittance,
				sky.HorizonGlow * (1f - horizonOpacity),
				sky.SunDisk * transmittance);
		}

		private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

		private void ShowUtc(DateTime utc)
		{
			_utcOverride = utc;
			var local = NewYorkTime.ToLocal(utc);
			year = local.Year;
			month = local.Month;
			day = local.Day;
			timeOfDayHours = (float)local.TimeOfDay.TotalHours;
		}

		private DateTime LocalDateTime()
		{
			var daysInMonth = DateTime.DaysInMonth(year, month);
			var date = new DateTime(year, month, Mathf.Clamp(day, 1, daysInMonth), 0, 0, 0, DateTimeKind.Unspecified);
			return date.AddHours(Mathf.Clamp(timeOfDayHours, 0f, 24f));
		}

		private void OnEnable() => Apply();

		private void Start()
		{
			if (!Application.isPlaying || !followRun)
			{
				return;
			}

			if (runner == null)
			{
				runner = FindFirstObjectByType<VesselSimulationRunner>();
			}

			// A scene with a run gets its lights and fog signal (World/Atmosphere), without a scene rebuild; one without is
			// left as it was. The runner has started by now (execution order -100), so the lights see its first run.
			if (runner != null)
			{
				var atmosphere = GetComponent<Atmosphere>();
				if (atmosphere == null)
				{
					atmosphere = gameObject.AddComponent<Atmosphere>();
				}

				atmosphere.Configure(this, runner);
			}
		}

		private void Update()
		{
			// The runner steps first (execution order -100), so this frame's sky is this frame's step.
			if (Application.isPlaying && followRun && Follow(runner))
			{
				return;
			}

			if (_utcOverride == null || Application.isPlaying)
			{
				Apply();
			}
		}

		private void OnValidate()
		{
			// An inspector edit of the date or time replaces a time set from code.
			_utcOverride = null;
		}
	}
}
