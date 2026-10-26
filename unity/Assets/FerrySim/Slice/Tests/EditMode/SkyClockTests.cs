using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The sky, the sun, the ambient light and the fog follow the run (<see cref="SunController.Follow"/>), and the sun's
	/// altitude decides when lights show and when the panel goes to its night palette (<see cref="Daylight"/>). Failure
	/// modes guarded: the sun following wall time (or frames) instead of the run's simulated time; the sun moving while
	/// the run is paused; a replay drawing a different sky or fog; fog drawn for a run that did not ask for it, or left
	/// behind after one that did; lights switched at the wrong sun altitude; and the palette rule following anything but
	/// the run's sun.
	/// </summary>
	public class SkyClockTests
	{
		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		private static ScenarioSettings EveningOffPier11(double visibility) => new ScenarioSettings
		{
			name = "sky clock",
			vesselFile = "vessels/t-class.json",
			startLatitude = LocalTangentPlane.World.ToGeodetic(250.0, 700.0).LatitudeDegrees,
			startLongitude = LocalTangentPlane.World.ToGeodetic(250.0, 700.0).LongitudeDegrees,
			startHeadingDegrees = 49.0,
			// 20:15 EDT on 20 July 2025, ten minutes before sunset.
			scenarioStartUtc = "2025-07-21T00:15:00Z",
			environment = new ScenarioEnvironment { calm = true },
			visibilityMetres = visibility,
		};

		private (SunController Sun, VesselSimulationRunner Runner) Scene()
		{
			_host = new GameObject("sky clock (test)");
			var sunObject = new GameObject("Sun");
			sunObject.transform.SetParent(_host.transform, false);
			var light = sunObject.AddComponent<Light>();
			light.type = LightType.Directional;
			var sun = sunObject.AddComponent<SunController>();
			sun.Configure(light, null);
			var runnerObject = new GameObject("runner");
			runnerObject.transform.SetParent(_host.transform, false);
			var runner = runnerObject.AddComponent<VesselSimulationRunner>();
			runner.MaxStepsPerFrame = 100;
			return (sun, runner);
		}

		[Test]
		public void TheSun_FollowsTheRunsSimulatedTime_NotTheWallClock_AndStandsStillWhilePaused()
		{
			var (sun, runner) = Scene();
			runner.RecordRuns = false;
			runner.Initialize(EveningOffPier11(MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral), SliceTestData.LoadTClass());
			var start = runner.ScenarioStartUtc.UtcDateTime;

			Assert.That(sun.Follow(runner), Is.True);
			Assert.That(sun.UtcTime, Is.EqualTo(start));
			var startElevation = sun.Sun.ElevationDegrees;

			// Twenty simulated minutes in a moment of wall time.
			StepTo(runner, 120000);
			sun.Follow(runner);
			Assert.That(sun.UtcTime, Is.EqualTo(start.AddSeconds(1200.0)));
			Assert.That(sun.Sun.ElevationDegrees, Is.LessThan(startElevation - 2.0), "twenty minutes of an evening sun");

			runner.Pause();
			for (var frame = 0; frame < 50; frame++)
			{
				runner.AdvanceFrame(1.0);
				sun.Follow(runner);
			}

			Assert.That(sun.UtcTime, Is.EqualTo(start.AddSeconds(1200.0)), "a paused run's sky stands still");
		}

		[Test]
		public void AReplay_DrawsTheSameSkyAndFog_AtTheSameStep()
		{
			var (sun, runner) = Scene();
			runner.RecordingDirectory = SliceTestData.ArtifactFolder("sky-clock-replay");
			var vessel = SliceTestData.LoadTClass();
			runner.Initialize(EveningOffPier11(500.0), new ConstantControls(new ControlInputs(0.4, 0.4, 0.0)), vessel);
			StepTo(runner, 3000);
			sun.Follow(runner);
			var live = Snapshot(sun);
			StepTo(runner, 3500);
			runner.EndRun();

			// Something else in between, so the replay has to put the sky back itself.
			sun.SetUtc(new DateTime(2025, 1, 14, 17, 0, 0, DateTimeKind.Utc));
			sun.VisibilityMetres = MeteorologicalVisibility.Unlimited;
			sun.Apply();
			Assert.That(Snapshot(sun), Is.Not.EqualTo(live));

			runner.InitializeReplay(RecordedRun.Load(runner.LastRecordingPath), vessel);
			StepTo(runner, 3000);
			sun.Follow(runner);
			Assert.That(Snapshot(sun), Is.EqualTo(live));
		}

		[Test]
		public void FogIsDrawnOnlyForARunThatAsksForIt_AndGoesWithIt()
		{
			var (sun, runner) = Scene();
			runner.RecordRuns = false;
			var vessel = SliceTestData.LoadTClass();
			sun.SetUtc(new DateTime(2025, 7, 21, 0, 15, 0, DateTimeKind.Utc));
			var before = Snapshot(sun);

			runner.Initialize(EveningOffPier11(MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral), vessel);
			sun.Follow(runner);
			Assert.That(Snapshot(sun), Is.EqualTo(before), "an unlimited run draws exactly the scene's own haze");
			Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.ExponentialSquared));

			runner.Initialize(EveningOffPier11(200.0), new ConstantControls(ControlInputs.Neutral), vessel);
			sun.Follow(runner);
			Assert.That(sun.VisibilityMetres, Is.EqualTo(200.0));
			Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.Exponential));
			Assert.That(RenderSettings.fogDensity, Is.EqualTo((float)(Math.Log(20.0) / 200.0)).Within(1e-7f), "the meteorological optical range: 5% left at 200 m");
			// Unity converts the fog colour from sRGB to linear and the sky's globals not at all: given as sRGB, the fog
			// fades the scene to the very horizon the sky draws.
			Assert.That(RenderSettings.fogColor, Is.EqualTo(sun.DrawnSky.Horizon.gamma), "the fog still meets the drawn horizon");
			Assert.That(sun.IsNight, Is.False, "ten minutes before sunset");
			Assert.That(sun.DrawnSky.Zenith, Is.Not.EqualTo(sun.Sky.Zenith), "the sky is drawn through the fog");

			runner.Initialize(EveningOffPier11(MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral), vessel);
			sun.Follow(runner);
			Assert.That(Snapshot(sun), Is.EqualTo(before), "the fog goes with the run that asked for it");
		}

		[Test]
		public void LightsShowFromSunsetToSunrise_AndInRestrictedVisibilityByDay()
		{
			var day = SolarDay.Compute(new DateTime(2025, 7, 20, 0, 0, 0, DateTimeKind.Utc), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var justBefore = SolarPosition.Compute(day.SunsetUtc.AddMinutes(-1.0), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var justAfter = SolarPosition.Compute(day.SunsetUtc.AddMinutes(1.0), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var noon = SolarPosition.Compute(day.SolarNoonUtc, WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);

			Assert.That(Daylight.ShowsNavigationLights(justBefore, MeteorologicalVisibility.Unlimited), Is.False, "Rule 20(b): from sunset");
			Assert.That(Daylight.ShowsNavigationLights(justAfter, MeteorologicalVisibility.Unlimited), Is.True);
			Assert.That(Daylight.ShowsNavigationLights(noon, MeteorologicalVisibility.Unlimited), Is.False);
			Assert.That(Daylight.ShowsNavigationLights(noon, 1000.0), Is.True, "Rule 20(c): by day in restricted visibility");
			Assert.That(Daylight.ShowsNavigationLights(noon, 5000.0), Is.False);
			Assert.That(Daylight.ShowsAidLights(noon), Is.False, "aids' daylight controls");
			Assert.That(Daylight.ShowsAidLights(justAfter), Is.True);
		}

		[Test]
		public void ThePanelsNightPalette_FollowsTheEndOfCivilTwilight()
		{
			var day = SolarDay.Compute(new DateTime(2025, 7, 20, 0, 0, 0, DateTimeKind.Utc), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var sunset = SolarPosition.Compute(day.SunsetUtc.AddMinutes(1.0), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var dusk = SolarPosition.Compute(day.SunsetUtc.AddMinutes(25.0), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);
			var night = SolarPosition.Compute(day.SunsetUtc.AddMinutes(45.0), WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);

			Assert.That(sunset.ElevationDegrees, Is.GreaterThan(-6.0));
			Assert.That(Daylight.PrefersNightPalette(sunset), Is.False);
			Assert.That(Daylight.PrefersNightPalette(night), Is.True, $"sun at {night.ElevationDegrees:F1} deg");
			Assert.That(Daylight.PrefersNightPalette(dusk), Is.EqualTo(dusk.ElevationDegrees < Daylight.NightPaletteBelowDegrees));
		}

		/// <summary>Steps the run to exactly <paramref name="step"/>, in frames and then single steps.</summary>
		private static void StepTo(VesselSimulationRunner runner, long step)
		{
			while (runner.Driver.StepCount < step)
			{
				if (step - runner.Driver.StepCount > 60)
				{
					runner.AdvanceFrame(0.5);
				}
				else
				{
					runner.Driver.StepOnce();
				}
			}

			Assert.That(runner.Driver.StepCount, Is.EqualTo(step));
		}

		/// <summary>Everything the sky sets, every float to the bit.</summary>
		private static string Snapshot(SunController sun)
		{
			var light = sun.SunLight;
			return string.Join(" ",
				sun.UtcTime.ToString("O"), R(sun.Sun.ApparentElevationDegrees), R(sun.Sun.AzimuthDegrees), R(light.intensity), R(light.color), R(light.transform.rotation),
				RenderSettings.fog, RenderSettings.fogMode, R(RenderSettings.fogDensity), R(RenderSettings.fogColor),
				R(RenderSettings.ambientSkyColor), R(RenderSettings.ambientEquatorColor), R(RenderSettings.ambientGroundColor),
				R(Shader.GetGlobalColor("_FerrySimSkyZenith")), R(Shader.GetGlobalColor("_FerrySimSkyHorizon")), R(Shader.GetGlobalColor("_FerrySimSkyGround")),
				R(Shader.GetGlobalVector("_FerrySimSunParams")), R(Shader.GetGlobalVector("_FerrySimSunDirection")));
		}

		private static string R(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

		private static string R(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

		private static string R(Color c) => $"({R(c.r)} {R(c.g)} {R(c.b)} {R(c.a)})";

		private static string R(Vector4 v) => $"({R(v.x)} {R(v.y)} {R(v.z)} {R(v.w)})";

		private static string R(Quaternion q) => $"({R(q.x)} {R(q.y)} {R(q.z)} {R(q.w)})";
	}
}
