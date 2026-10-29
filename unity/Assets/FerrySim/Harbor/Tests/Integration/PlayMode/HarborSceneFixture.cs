using System;
using System.Collections;
using System.IO;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>The checked-in harbor scene with its real services and the copied harbor data; ignored when the data is absent.</summary>
	internal static class HarborSceneFixture
	{
		public const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";

		/// <summary>A traffic day whose busiest active-tile minute is 12:18 local (Traffic/README.md).</summary>
		public const string TrafficDay = "2025-07-16";
		public const int BusiestLocalMinute = 738;

		/// <summary>Wall St/Pier 11 and St. George, as the contacts name them.</summary>
		public const string Pier11StopId = "87";
		public const string StGeorgeStopId = "137";

		/// <summary>
		/// A start 100 m inside the southern edge of covered water in the Upper Bay (15 m of water, 1.9 km from the nearest
		/// contact), heading south: it leaves every depth grid and the datum lattice at north -8000 m.
		/// </summary>
		public const double CoverageEdgeNorth = -7900;
		public const double CoverageEdgeEast = -3500;

		public static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

		public static void RequireData()
		{
			var harbor = Path.Combine(Repo, "data", "processed", "harbor");
			foreach (var folder in new[] { "runtime", "env/days", "3dtiles", "3dtiles-buildings", "traffic/" + TrafficDay })
			{
				if (!Directory.Exists(Path.Combine(harbor, folder)))
				{
					Assert.Ignore("The harbor data is not copied: missing " + Path.Combine(harbor, folder) + ".");
				}
			}
		}

		/// <summary>Loads the scene beside the test runner's, so <see cref="Unload"/> can give later suites a clean hierarchy.</summary>
		public static IEnumerator Load()
		{
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			Assert.Fail("This test loads the checked-in editor scene.");
			yield break;
#endif
			yield return null;
		}

		/// <summary>Ends every run and unloads the harbor scene; other PlayMode suites find their own runner afterwards.</summary>
		public static IEnumerator Unload()
		{
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (!scene.IsValid() || !scene.isLoaded)
			{
				yield break;
			}

			foreach (var root in scene.GetRootGameObjects())
			{
				foreach (var runner in root.GetComponentsInChildren<VesselSimulationRunner>(true))
				{
					runner.EndRun();
				}
			}

			yield return SceneManager.UnloadSceneAsync(scene);
		}

		public static HarborBootstrap Bootstrap()
		{
			var bootstrap = Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(bootstrap, Is.Not.Null, "Harbor.unity has one HarborBootstrap");
			Assert.That(bootstrap.Problem, Is.Null, "the real services connected");
			Assert.That(bootstrap.Services, Is.InstanceOf<HarborIntegrationServices>(), "the scene's factory is the composition root");
			return bootstrap;
		}

		/// <summary>A fixture start on a harbor day, not a berth: the pose is a viewpoint or a test position on the water.</summary>
		public static ScenarioSettings FixtureStart(string name, double north, double east, double heading, string localTimeUtc, bool traffic, double speedKnots = 0)
		{
			var geo = LocalTangentPlane.World.ToGeodetic(north, east);
			var settings = new ScenarioSettings
			{
				name = name,
				vesselFile = "vessels/t-class.json",
				startLatitude = geo.LatitudeDegrees,
				startLongitude = geo.LongitudeDegrees,
				startHeadingDegrees = heading,
				startSpeedKnots = speedKnots,
				scenarioStartUtc = localTimeUtc,
				environment = new ScenarioEnvironment { calm = false, pack = "harbor/" + TrafficDay }
			};
			if (traffic)
			{
				settings.traffic.day = TrafficDay;
			}

			return settings;
		}

		/// <summary>Waits for the initial view (both tilesets with content and complete) or fails naming the problem.</summary>
		public static IEnumerator WaitReady(HarborBootstrap bootstrap, string key, double timeoutSeconds = 90)
		{
			var layers = Object.FindObjectsByType<HarborTileset>(FindObjectsSortMode.None);
			var started = Time.realtimeSinceStartupAsDouble;
			while (bootstrap.ViewState != HarborViewState.Ready)
			{
				Assert.That(bootstrap.Problem, Is.Null, key);
				foreach (var layer in layers)
				{
					Assert.That(layer.Failure, Is.Null, key + ": " + layer.LayerId);
				}

				Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(timeoutSeconds), key + ": the initial harbor view did not settle");
				yield return null;
			}
		}

		public static int LandingIndex(IScenarioCatalog catalog, string stopId)
		{
			for (var i = 0; i < catalog.Landings.Count; i++)
			{
				if (catalog.Landings[i].StopId == stopId)
				{
					return i;
				}
			}

			throw new InvalidOperationException("The catalog does not list stop " + stopId + ".");
		}
	}
}
