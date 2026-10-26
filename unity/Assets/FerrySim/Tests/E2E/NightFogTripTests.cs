using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.E2E
{
	/// <summary>
	/// The second trip leg, DUMBO up-river under the Brooklyn and Manhattan bridges, at night in 500 m fog, recorded, then
	/// replayed through the scene's runner. Failure modes guarded: the sky, the fog or the lights following wall time or
	/// frames instead of the run; a replay that draws a different sky or fog or loses the recorded visibility; the fog
	/// signal missing, mistimed or different in the replay; and lights out at night in fog. Every frame of the replay is
	/// compared with the same frame of the run, at the same physics step.
	/// </summary>
	public sealed class NightFogTripTests
	{
		private const string NightUtc = "2025-07-21T02:30:00Z";
		private const double Visibility = 500.0;
		private const long Steps = 25000;

		private VesselSimulationRunner _runner;

		[UnityTearDown]
		public IEnumerator Cleanup()
		{
			Time.captureDeltaTime = 0f;
			_runner?.EndRun();
			var scene = SceneManager.GetSceneByPath(TripData.ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				SceneManager.SetActiveScene(SceneManager.CreateScene("night-fog teardown " + Time.frameCount));
				yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		[UnityTest]
		public IEnumerator DumboUpRiver_AtNightInFog_ReplaysTheSameSkyFogLightsAndFogSignals()
		{
			var leg = TripData.DumboSouthWilliamsburg;
			var trace = ControlTrace.Load(leg.TracePath);
			Assert.That(trace.EndStep, Is.GreaterThan(Steps), "the committed trace runs past the measured span");
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TripData.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("The integrated slice currently supports the editor only.");
#endif
			yield return null;
			yield return null;
			var bootstrap = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(bootstrap, Is.Not.Null);
			Assert.That(bootstrap.LoadError, Is.Null);
			_runner = bootstrap.Runner;
			var vessel = TripData.Vessel();
			var scenario = TripData.Start(leg, bootstrap.Landings, vessel);
			scenario.name = "night-fog trip";
			scenario.scenarioStartUtc = NightUtc;
			scenario.visibilityMetres = Visibility;
			var folder = Path.Combine(TripData.Root, "artifacts", "night-fog", "e2e");
			Directory.CreateDirectory(folder);
			_runner.RecordRuns = true;
			_runner.RecordingDirectory = folder;
			_runner.MaxStepsPerFrame = 100;
			Time.captureDeltaTime = 0.5f;
			_runner.Initialize(scenario, trace, vessel);
			Assert.That(bootstrap.StartRefusal, Is.Null);

			var live = new List<string>();
			yield return Record(live);
			var atmosphere = Object.FindFirstObjectByType<Atmosphere>();
			Assert.That(atmosphere, Is.Not.Null);
			var liveSignals = atmosphere.FogSignal.Recorder.Events.ToList();
			_runner.EndRun();
			Assert.That(_runner.RecordingError, Is.Null);
			var recording = RecordedRun.Load(_runner.LastRecordingPath);
			Assert.That(recording.VisibilityMetres, Is.EqualTo(Visibility), "the recording keeps the visibility");
			Assert.That(liveSignals.Select(s => s.Step), Is.EqualTo(new[] { 0L, 12000L, 24000L }), "Rule 35: every two minutes from the start");
			Assert.That(liveSignals[0].Kind, Is.EqualTo(FogSignalKind.TwoProlonged), "at rest off DUMBO: stopped");
			Assert.That(live.All(frame => frame.Contains(" lights True ")), Is.True, "lights all through a night in fog");

			_runner.InitializeReplay(recording, vessel);
			var replay = new List<string>();
			yield return Record(replay);
			Assert.That(_runner.IsReplaying || _runner.ReplayCompleted, Is.True);
			Assert.That(atmosphere.FogSignal.Recorder.Events, Is.EqualTo(liveSignals), "the replay sounds the same signals");
			var compared = Math.Min(live.Count, replay.Count);
			Assert.That(compared, Is.GreaterThan(400));
			for (var i = 0; i < compared; i++)
			{
				Assert.That(replay[i], Is.EqualTo(live[i]), $"frame {i}");
			}

			File.WriteAllLines(Path.Combine(folder, "night-fog-trip-frames.txt"), live.Take(20).Concat(new[] { "..." }).Concat(live.Skip(live.Count - 5)));
		}

		/// <summary>One line per frame until <see cref="Steps"/>: the step, the sky, the fog and the lights, every float to the bit.</summary>
		private IEnumerator Record(List<string> frames)
		{
			var atmosphere = Object.FindFirstObjectByType<Atmosphere>();
			for (var guard = 0; guard < 2000 && _runner.Driver.StepCount < Steps; guard++)
			{
				yield return null;
				atmosphere ??= Object.FindFirstObjectByType<Atmosphere>();
				var sun = atmosphere.Sun;
				var own = atmosphere.OwnLights;
				frames.Add(string.Join(" ",
					"step", _runner.Driver.StepCount.ToString(CultureInfo.InvariantCulture),
					"utc", sun.UtcTime.Ticks.ToString(CultureInfo.InvariantCulture),
					"sun", sun.Sun.ApparentElevationDegrees.ToString("R", CultureInfo.InvariantCulture),
					"fog", RenderSettings.fogMode, RenderSettings.fogDensity.ToString("R", CultureInfo.InvariantCulture),
					"lights", atmosphere.LightsShown, own.Lights.Count(l => l.On),
					"bridges", atmosphere.Bridges.Sum(b => b.Lights.Count(l => l.On)).ToString(CultureInfo.InvariantCulture),
					"aids", atmosphere.Aids.Time.ToString("R", CultureInfo.InvariantCulture),
					"whistle", atmosphere.FogSignal.IsSounding, atmosphere.FogSignal.BlastSeconds.ToString("R", CultureInfo.InvariantCulture)));
			}

			Assert.That(_runner.Driver.StepCount, Is.GreaterThanOrEqualTo(Steps), "the run reached the measured span");
		}
	}
}
