using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The UI development scene opens, finds its fake source by itself and plays the sample run onto the
	/// panel. The scene is not in the build settings, so it is loaded through the editor API. Guards against
	/// (review finding 9) a test that stops early, as a failed assertion does, leaving the scene's fake
	/// playback, camera and UI running into later tests, including when it stops while the scene is still
	/// loading. The ordered pairs below stop early on purpose and then check that nothing was left behind.
	/// </summary>
	public class UIDevScenePlayModeTests
	{
		private const string ScenePath = "Assets/FerrySim/UI/Scenes/UIDev.unity";

		private AsyncOperation _loading;

		/// <summary>
		/// Runs after every test, passed or failed: waits out a load still in progress, then unloads the scene,
		/// so no test's exit path can leave it running.
		/// </summary>
		[UnityTearDown]
		public IEnumerator UnloadTheScene()
		{
			var deadline = Time.realtimeSinceStartup + 30f;
			while (_loading != null && !_loading.isDone && Time.realtimeSinceStartup < deadline)
			{
				yield return null;
			}

			_loading = null;
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		[UnityTest]
		[Order(0)]
		public IEnumerator UIDevScene_PlaysTheSampleOntoThePanel()
		{
#if UNITY_EDITOR
			var operation = LoadScene();
			Assert.That(operation, Is.Not.Null, ScenePath + " did not start loading");
			yield return operation;
			var scene = SceneManager.GetSceneByPath(ScenePath);
			Assert.That(scene.IsValid() && scene.isLoaded, Is.True);

			WheelhouseUI ui = null;
			FakeTelemetrySource source = null;
			foreach (var root in scene.GetRootGameObjects())
			{
				ui = ui != null ? ui : root.GetComponentInChildren<WheelhouseUI>();
				source = source != null ? source : root.GetComponentInChildren<FakeTelemetrySource>();
			}

			Assert.That(ui, Is.Not.Null, "the scene has a WheelhouseUI");
			Assert.That(source, Is.Not.Null, "the scene has a FakeTelemetrySource");
			var deadline = Time.realtimeSinceStartup + 10f;
			while (Time.realtimeSinceStartup < deadline && (ui.View == null || ui.View.Panel.ShowingNoData))
			{
				yield return null;
			}

			Assert.That(ui.Source, Is.SameAs(source), "the UI found the fake source in the scene");
			Assert.That(source.HasTelemetry, Is.True, source.LoadError);
			Assert.That(ui.View.Panel.ShowingNoData, Is.False);
			Assert.That(ui.View.ToolbarVisible, Is.True, "the dev scene shows its toolbar");
			var start = source.PlaybackTime;
			yield return new WaitForSecondsRealtime(0.5f);
			Assert.That(source.PlaybackTime, Is.Not.EqualTo(start), "playback runs");
			Assert.That(ui.View.Panel.Heading.text, Does.Match("^[0-9]{3}$"));
#else
			Assert.Ignore("Needs the editor to load a scene that is not in the build settings.");
			yield break;
#endif
		}

		[UnityTest]
		[Order(1)]
		public IEnumerator UIDevScene_LoadedByATestThatStopsBeforeItsOwnCleanup()
		{
			// Stops with the scene loaded, the way a failed assertion leaves a test.
			yield return LoadScene();
			Assert.That(SceneManager.GetSceneByPath(ScenePath).isLoaded, Is.True);
		}

		[UnityTest]
		[Order(2)]
		public IEnumerator NothingIsLeftLoaded_AfterATestThatStoppedEarly()
		{
			yield return WaitWhileTheSceneLoads();
			AssertNothingLeft();
		}

		[UnityTest]
		[Order(3)]
		public IEnumerator UIDevScene_StillLoadingWhenATestStops()
		{
			Assert.That(LoadScene(), Is.Not.Null);
			yield break;
		}

		[UnityTest]
		[Order(4)]
		public IEnumerator NothingIsLeftLoaded_AfterATestThatStoppedMidLoad()
		{
			yield return WaitWhileTheSceneLoads();
			AssertNothingLeft();
		}

		/// <summary>Starts loading the scene and remembers the load for <see cref="UnloadTheScene"/>.</summary>
		private AsyncOperation LoadScene()
		{
#if UNITY_EDITOR
			_loading = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
			return _loading;
#else
			Assert.Ignore("Needs the editor to load a scene that is not in the build settings.");
			return null;
#endif
		}

		/// <summary>Lets a load that a previous test left running finish, so the check below sees its result.</summary>
		private static IEnumerator WaitWhileTheSceneLoads()
		{
			var deadline = Time.realtimeSinceStartup + 10f;
			yield return null;
			while (Time.realtimeSinceStartup < deadline && IsLoading(SceneManager.GetSceneByPath(ScenePath)))
			{
				yield return null;
			}
		}

		private static bool IsLoading(Scene scene) => scene.IsValid() && !scene.isLoaded;

		private static void AssertNothingLeft()
		{
			var scene = SceneManager.GetSceneByPath(ScenePath);
			Assert.That(scene.IsValid() && scene.isLoaded, Is.False, ScenePath + " is still loaded from an earlier test");
			Assert.That(Object.FindObjectsByType<FakeTelemetrySource>(FindObjectsSortMode.None), Is.Empty, "its fake playback is still running");
			Assert.That(Object.FindObjectsByType<WheelhouseUI>(FindObjectsSortMode.None), Is.Empty, "its UI is still running");
		}
	}
}
