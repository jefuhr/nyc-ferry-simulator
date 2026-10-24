using System;
using FerrySim.Core.Contracts.Geodesy;
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
	/// <see cref="SetUtc"/> (for example scenario start plus simulation time).
	/// </para>
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	public sealed class SunController : MonoBehaviour
	{
		private static readonly int SkyZenithId = Shader.PropertyToID("_FerrySimSkyZenith");
		private static readonly int SkyHorizonId = Shader.PropertyToID("_FerrySimSkyHorizon");
		private static readonly int SkyGroundId = Shader.PropertyToID("_FerrySimSkyGround");
		private static readonly int SunDirectionId = Shader.PropertyToID("_FerrySimSunDirection");
		private static readonly int SunColorId = Shader.PropertyToID("_FerrySimSunColor");
		private static readonly int SunParamsId = Shader.PropertyToID("_FerrySimSunParams");

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

		private DateTime? _utcOverride;

		/// <summary>The instant the lighting shows, UTC.</summary>
		public DateTime UtcTime => _utcOverride ?? NewYorkTime.ToUtc(LocalDateTime());

		/// <summary>Sun position at the last <see cref="Apply"/>.</summary>
		public SolarPosition Sun { get; private set; }

		/// <summary>Sky colours and sun strength at the last <see cref="Apply"/>.</summary>
		public SkyGradient.Sample Sky { get; private set; }

		public Light SunLight => sun;

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

			_utcOverride = utc;
			var local = NewYorkTime.ToLocal(utc);
			year = local.Year;
			month = local.Month;
			day = local.Day;
			timeOfDayHours = (float)local.TimeOfDay.TotalHours;
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

		/// <summary>Recomputes the sun and applies the lighting now.</summary>
		public void Apply()
		{
			var position = SolarPosition.Compute(UtcTime, latitude, longitude);
			var sky = SkyGradient.Evaluate(position.ApparentElevationDegrees);
			Sun = position;
			Sky = sky;

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
				sun.intensity = noonIntensity * sky.SunStrength;
				sun.color = sky.SunColor;
				sun.shadows = sky.SunStrength > 0.01f ? LightShadows.Hard : LightShadows.None;
				RenderSettings.sun = sun;
			}

			// Trilight ambient ignores RenderSettings.ambientIntensity, so the colours carry the scale.
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = sky.Zenith * ambientIntensity;
			RenderSettings.ambientEquatorColor = sky.Horizon * ambientIntensity;
			RenderSettings.ambientGroundColor = sky.Ground * ambientIntensity;

			if (driveFog)
			{
				RenderSettings.fog = true;
				RenderSettings.fogMode = FogMode.ExponentialSquared;
				RenderSettings.fogDensity = fogDensity;
				RenderSettings.fogColor = sky.Horizon;
			}

			Shader.SetGlobalColor(SkyZenithId, sky.Zenith);
			Shader.SetGlobalColor(SkyHorizonId, sky.Horizon);
			Shader.SetGlobalColor(SkyGroundId, sky.Ground);
			Shader.SetGlobalVector(SunDirectionId, toSun);
			Shader.SetGlobalColor(SunColorId, sky.SunColor);
			Shader.SetGlobalVector(SunParamsId, new Vector4(sky.SunDisk, sky.SunStrength, sky.HorizonGlow, 0f));
		}

		private DateTime LocalDateTime()
		{
			var daysInMonth = DateTime.DaysInMonth(year, month);
			var date = new DateTime(year, month, Mathf.Clamp(day, 1, daysInMonth), 0, 0, 0, DateTimeKind.Unspecified);
			return date.AddHours(Mathf.Clamp(timeOfDayHours, 0f, 24f));
		}

		private void OnEnable() => Apply();

		private void Update()
		{
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
