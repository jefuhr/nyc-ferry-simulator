using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// Night and fog in the integrated slice (World/Atmosphere). Failure modes guarded: the learner's, the bridges' or the
	/// aids' lights missing at night or lit by day; the sky or the aids following wall time or frames instead of the run's
	/// simulated time, or moving while the run is paused; a K-class carrying the T-class's lights; sidelights reversed, as
	/// seen in the drawn image from each bow; a drawn light that ignores the visibility (seen beyond its range in fog, or
	/// hidden inside it); the fog signal missing in fog, sounding in clear weather or giving the wrong signal; fog drawn for
	/// a run that did not ask for it; the panel's palette following anything but the run's sky, or overriding the captain's
	/// own choice before the light changes; and garbage made per frame by the sky and the lights.
	/// </summary>
	public class NightFogSceneTests
	{
		private const int Width = 960;
		private const int Height = 540;

		private SliceScene _scene;
		private readonly List<Object> _owned = new List<Object>();

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			foreach (var item in _owned)
			{
				if (item is RenderTexture texture)
				{
					texture.Release();
				}

				if (item != null)
				{
					Object.Destroy(item);
				}
			}

			_owned.Clear();
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator ANightRun_LightsTheVesselTheBridgesAndTheAids_AndADayRunDoesNot()
		{
			_scene.StepsPerFrame(5);
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);

			var atmosphere = NightFogScene.Atmosphere();
			Assert.That(atmosphere.Runner, Is.SameAs(_scene.Runner));
			Assert.That(atmosphere.LightsShown, Is.True);
			Assert.That(atmosphere.OwnLights.Specs.Select(s => s.Kind), Is.EqualTo(new[]
			{
				NavigationLightKind.Masthead, NavigationLightKind.PortSidelight, NavigationLightKind.StarboardSidelight, NavigationLightKind.Sternlight,
			}));
			Assert.That(atmosphere.OwnLights.Lights.All(l => l.On), Is.True);
			Assert.That(atmosphere.OwnLights.transform.parent, Is.SameAs(_scene.Runner.transform), "aboard the learner's vessel");
			Assert.That(atmosphere.Bridges.Select(b => b.Bridge.BridgeName), Is.EquivalentTo(new[] { "Brooklyn Bridge", "Manhattan Bridge", "Williamsburg Bridge" }));
			foreach (var bridge in atmosphere.Bridges)
			{
				Assert.That(bridge.Lights.Count(l => l.Colour == SignalColour.Green), Is.EqualTo(2), bridge.Bridge.BridgeName + ": the centre-span range");
				Assert.That(bridge.Lights.Count(l => l.Colour == SignalColour.Red), Is.EqualTo(4), bridge.Bridge.BridgeName + ": both ends of both piers");
				Assert.That(bridge.Lights.All(l => l.On && l.name.Contains("nominal 33 CFR 118.65")), Is.True, bridge.Bridge.BridgeName + ": lit, and labelled nominal");
			}

			Assert.That(atmosphere.ChartLightingOn, Is.True, "the import's terrain, structures and landings dimmed at night");
			Assert.That(Shader.IsKeywordEnabled(Atmosphere.ChartLightingKeyword), Is.True);
			Assert.That(atmosphere.Aids.Count, Is.GreaterThanOrEqualTo(10), "the imported aids' lights");
			for (var i = 0; i < atmosphere.Aids.Count; i++)
			{
				Assert.That(atmosphere.Aids.LensMarked(i), Is.True, atmosphere.Aids.Lamp(i).name + "'s lens is a light, not dimmed");
			}
			var everLit = new bool[atmosphere.Aids.Points.Count];
			for (var frame = 0; frame < 240; frame++)
			{
				for (var i = 0; i < everLit.Length; i++)
				{
					everLit[i] |= atmosphere.Aids.Points[i].On;
				}

				yield return null;
			}

			Assert.That(everLit.All(lit => lit), Is.True, "every aid's light shows at night within its period");

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Day, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(atmosphere.LightsShown, Is.False, "Rule 20(b): from sunset to sunrise");
			Assert.That(atmosphere.OwnLights.Lights.Any(l => l.On), Is.False);
			Assert.That(atmosphere.Bridges.SelectMany(b => b.Lights).Any(l => l.On), Is.False);
			Assert.That(atmosphere.Aids.Points.Any(p => p.On), Is.False, "the aids' daylight controls");
			Assert.That(Shader.IsKeywordEnabled(Atmosphere.ChartLightingKeyword), Is.False, "by day in clear air the import's shaders draw exactly as imported");
		}

		[UnityTest]
		public IEnumerator ThePanel_TakesItsNightPaletteAfterCivilTwilight_AndAHandChoiceHoldsUntilTheLightChanges()
		{
			_scene.StepsPerFrame(5);
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Day, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.UI.DaylightSource, Is.SameAs(NightFogScene.Atmosphere().Sun), "the panel reads the world's sky");
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Day));

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Night), "night: the sun 19 degrees down");

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Dusk, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Day), "dusk, before civil twilight ends: the day palette");

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Night));

			_scene.View.TogglePalette();
			yield return SliceScene.Frames(10);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Day), "the captain's own choice holds through the night");
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Day), "and through a new run in the same light");

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Day, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			Assert.That(_scene.View.Palette, Is.EqualTo(PaletteKind.Night), "the next nightfall offers the night palette again");
		}

		[UnityTest]
		public IEnumerator TheSkyAndTheAids_FollowTheRunsSimulatedTime_AndStandStillWhilePaused()
		{
			_scene.StepsPerFrame(50);
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Dusk, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(40);

			var atmosphere = NightFogScene.Atmosphere();
			var runner = _scene.Runner;
			var start = runner.ScenarioStartUtc.UtcDateTime;
			var time = runner.Driver.SimulationTime;
			Assert.That(time, Is.GreaterThan(15.0), "half a simulated second a frame");
			Assert.That(atmosphere.Sun.UtcTime, Is.EqualTo(start.AddTicks((long)Math.Round(time * TimeSpan.TicksPerSecond))), "the sky shows the run's clock");
			Assert.That(atmosphere.Aids.Time, Is.EqualTo(time), "the aids flash on the run's clock");
			for (var i = 0; i < atmosphere.Aids.Count; i++)
			{
				Assert.That(atmosphere.Aids.LampLit(i), Is.EqualTo(atmosphere.Aids.CharacterLitAt(i, time)), atmosphere.Aids.Lamp(i).name);
			}

			runner.Pause();
			yield return SliceScene.Frames(20);
			Assert.That(runner.Driver.SimulationTime, Is.EqualTo(time));
			Assert.That(atmosphere.Sun.UtcTime, Is.EqualTo(start.AddTicks((long)Math.Round(time * TimeSpan.TicksPerSecond))), "a paused run's sky stands still");
			Assert.That(atmosphere.Aids.Time, Is.EqualTo(time), "and so do its aids");
			runner.Resume();
		}

		[UnityTest]
		public IEnumerator AKClassRun_CarriesItsLightsOnItsOwnGreybox()
		{
			_scene.StepsPerFrame(5);
			var settings = NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited);
			settings.vesselFile = "vessels/k-class.json";
			_scene.Runner.Initialize(settings, new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);

			var atmosphere = NightFogScene.Atmosphere();
			var expected = VesselLightLayout.ForGreybox(GreyboxDimensions.From(_scene.Runner.Parameters));
			Assert.That(_scene.Runner.Parameters.Id, Is.EqualTo("k-class"));
			Assert.That(atmosphere.OwnLights.Find(NavigationLightKind.Masthead).transform.localPosition, Is.EqualTo(expected.Masthead));
			Assert.That(atmosphere.OwnLights.Find(NavigationLightKind.StarboardSidelight).transform.localPosition, Is.EqualTo(expected.StarboardSidelight));
			Assert.That(atmosphere.FogSignal.transform.localPosition, Is.EqualTo(expected.Masthead), "the whistle at the masthead");
		}

		[UnityTest]
		public IEnumerator TheDrawnSidelights_AreGreenFromTheStarboardBowAndRedFromThePort()
		{
			_scene.StepsPerFrame(1);
			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, MeteorologicalVisibility.Unlimited, 300.0), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);

			var atmosphere = NightFogScene.Atmosphere();
			var vessel = _scene.Runner.transform;
			var starboard = atmosphere.OwnLights.Find(NavigationLightKind.StarboardSidelight);
			var port = atmosphere.OwnLights.Find(NavigationLightKind.PortSidelight);

			// 150 m off each bow, 45 degrees from right ahead, with the eye 3 m above the water.
			var fromStarboard = NightFogScene.TestCamera(_owned, vessel.TransformPoint(new Vector3(106f, 3f, 106f)), starboard.transform.position);
			var fromPort = NightFogScene.TestCamera(_owned, vessel.TransformPoint(new Vector3(-106f, 3f, 106f)), port.transform.position);
			var seenStarboard = LightColour(fromStarboard, atmosphere, starboard.transform.position);
			var seenPort = LightColour(fromPort, atmosphere, port.transform.position);
			var portFromStarboard = LightColour(fromStarboard, atmosphere, port.transform.position);

			Assert.That(seenStarboard.g, Is.GreaterThan(0.3f).And.GreaterThan(3f * seenStarboard.r), $"green to starboard, drawn {seenStarboard}");
			Assert.That(seenPort.r, Is.GreaterThan(0.3f).And.GreaterThan(3f * seenPort.g), $"red to port, drawn {seenPort}");
			Assert.That(portFromStarboard.r, Is.LessThan(0.05f), $"the port light is screened from the starboard bow, drawn {portFromStarboard}");
		}

		[UnityTest]
		public IEnumerator InFog_ALightIsSeenWithinItsRangeAndNotBeyondIt()
		{
			const double visibility = 500.0;
			_scene.StepsPerFrame(1);
			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, visibility, 300.0), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);

			var atmosphere = NightFogScene.Atmosphere();
			var stern = atmosphere.OwnLights.Find(NavigationLightKind.Sternlight);
			var range = SignalLightOptics.LuminousRange(stern.IntensityCandela, SignalLightOptics.LightExtinction(visibility), SignalLightOptics.NightThresholdLux);
			Assert.That(range, Is.InRange(visibility, 3.0 * visibility), "at night a light carries past the visibility");
			Assert.That(atmosphere.Optics.x, Is.EqualTo((float)SignalLightOptics.LightExtinction(visibility)), "the lights see the run's fog");

			var vessel = _scene.Runner.transform;
			Camera Astern(double distance) => NightFogScene.TestCamera(_owned, vessel.TransformPoint(new Vector3(0f, 3f, -(float)distance)), stern.transform.position, 10f);
			var near = Astern(0.6 * range);
			var far = Astern(1.4 * range);
			var nearColour = LightColour(near, atmosphere, stern.transform.position);
			var farColour = LightColour(far, atmosphere, stern.transform.position);
			Assert.That(stern.RatioToThreshold(near.transform.position, atmosphere.Optics.x, atmosphere.Optics.y), Is.GreaterThan(1.0));
			Assert.That(stern.RatioToThreshold(far.transform.position, atmosphere.Optics.x, atmosphere.Optics.y), Is.LessThan(1.0));
			Assert.That(nearColour.r + nearColour.g + nearColour.b, Is.GreaterThan(0.15f), $"seen at {0.6 * range:F0} m in {visibility} m fog, drawn {nearColour}");
			Assert.That(farColour.r + farColour.g + farColour.b, Is.LessThan(0.02f), $"not seen at {1.4 * range:F0} m, drawn {farColour}");

			// The same light, as far off, on a clear night.
			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, MeteorologicalVisibility.Unlimited, 300.0), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(3);
			stern = atmosphere.OwnLights.Find(NavigationLightKind.Sternlight);
			var clear = LightColour(Astern(1.4 * range), atmosphere, stern.transform.position);
			Assert.That(clear.r + clear.g + clear.b, Is.GreaterThan(0.15f), $"seen at {1.4 * range:F0} m on a clear night, drawn {clear}");
		}

		[UnityTest]
		public IEnumerator InFog_TheVesselSoundsRule35OnTheRunsClock_AndInClearWeatherItDoesNot()
		{
			_scene.StepsPerFrame(100);
			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, 500.0, 600.0), new ConstantControls(new ControlInputs(0.4, 0.4, 0.0)));
			yield return SliceScene.Frames(2);

			var signal = NightFogScene.Atmosphere().FogSignal;
			Assert.That(signal.Recorder.Events, Has.Count.EqualTo(1));
			Assert.That(signal.Recorder.Events[0], Is.EqualTo(new FogSignalEvent(0, FogSignalKind.TwoProlonged)), "at rest at the start: underway but stopped");
			Assert.That(signal.IsSounding, Is.True);
			Assert.That(signal.Source.clip, Is.Not.Null);
			Assert.That(signal.Source.spatialBlend, Is.EqualTo(1f), "heard from the vessel");

			yield return _scene.FramesUntil(() => _scene.Runner.Driver.SimulationTime >= 125.0, 300, "125 s under way");
			Assert.That(signal.Recorder.Events.Select(e => e.Step), Is.EqualTo(new[] { 0L, FogSignalSchedule.IntervalSteps }));
			Assert.That(signal.Recorder.Events[1].Kind, Is.EqualTo(FogSignalKind.OneProlonged), "making way");

			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, MeteorologicalVisibility.Unlimited, 600.0), new ConstantControls(new ControlInputs(0.4, 0.4, 0.0)));
			yield return _scene.FramesUntil(() => _scene.Runner.Driver.SimulationTime >= 125.0, 300, "125 s in clear weather");
			Assert.That(signal.Recorder.Events, Is.Empty);
			Assert.That(signal.IsSounding, Is.False);
		}

		[UnityTest]
		public IEnumerator FogIsDrawnOnlyForARunThatAsksForIt()
		{
			_scene.StepsPerFrame(5);
			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(2);
			Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.ExponentialSquared), "the scene's own haze");
			Assert.That(RenderSettings.fogDensity, Is.EqualTo(0.00007f));
			Assert.That(NightFogScene.Atmosphere().Sun.VisibilityMetres, Is.EqualTo(0.0));

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, 200.0), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(2);
			Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.Exponential));
			Assert.That(RenderSettings.fogDensity, Is.EqualTo((float)(Math.Log(20.0) / 200.0)).Within(1e-7f));

			_scene.Restart(NightFogScene.OffPier11(_scene, NightFogScene.Night, MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral));
			yield return SliceScene.Frames(2);
			Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.ExponentialSquared));
			Assert.That(RenderSettings.fogDensity, Is.EqualTo(0.00007f));
		}

		[UnityTest]
		public IEnumerator TheSkyAndTheLights_MakeNoGarbagePerFrame()
		{
			_scene.StepsPerFrame(1);
			_scene.Restart(NightFogScene.UnderBrooklynBridge(NightFogScene.Night, 500.0, 300.0), new ConstantControls(new ControlInputs(0.3, 0.3, 0.0)));
			yield return SliceScene.Frames(5);

			var atmosphere = NightFogScene.Atmosphere();
			var runner = _scene.Runner;
			var sun = atmosphere.Sun;
			var signal = atmosphere.FogSignal;
			void Frame()
			{
				runner.AdvanceFrame(1.0 / 60.0);
				sun.Follow(runner);
				atmosphere.RefreshLights();
				signal.Refresh();
			}

			Frame();
			Assert.That(signal.IsSounding, Is.True, "measured with the whistle sounding");
			Assert.That(atmosphere.LightsShown, Is.True, "and every light shown");
			Assert.That(Frame, Is.Not.AllocatingGCMemory(), "the sky, the vessel's and the bridges' lights, the fog signal");

			// The aids: allocation-free on lights with a chart sequence.
			var aids = atmosphere.Aids;
			var withSequence = Enumerable.Range(0, aids.Count).First(aids.UsesChartSequence);
			var sequenced = AidLights.Find(aids.Lamp(withSequence));
			Assert.That(sequenced.Count, Is.EqualTo(1));
			sequenced.Show(1.0, true);
			var t = 1.0;
			Assert.That(() => sequenced.Show(t += 0.016, true), Is.Not.AllocatingGCMemory(), "an aid with a chart sequence");
		}

		/// <summary>The light's drawn colour where it projects: the image with it lit, less the image with it out.</summary>
		private Color LightColour(Camera camera, Atmosphere atmosphere, Vector3 light)
		{
			atmosphere.RefreshLights();
			var lit = NightFogScene.Render(_owned, camera, Width, Height);
			var shown = atmosphere.OwnLights.On;
			atmosphere.OwnLights.On = false;
			var dark = NightFogScene.Render(_owned, camera, Width, Height);
			atmosphere.OwnLights.On = shown;
			var a = NightFogScene.Brightest(lit, camera, light);
			var b = NightFogScene.Brightest(dark, camera, light);
			return new Color(Mathf.Max(0f, a.r - b.r), Mathf.Max(0f, a.g - b.g), Mathf.Max(0f, a.b - b.b));
		}
	}
}
