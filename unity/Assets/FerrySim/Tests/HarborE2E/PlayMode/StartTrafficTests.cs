using System;
using System.Collections;
using System.IO;
using System.Reflection;
using FerrySim.Harbor;
using FerrySim.Harbor.Player.Journeys;
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FerrySim.HarborE2E.Tests
{
	/// <summary>A named or warm-switch benchmark must start actual traffic when its command requests traffic.</summary>
	public class StartTrafficTests
	{
		private const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		private GameObject _host;

		[UnityTearDown]
		public IEnumerator Unload()
		{
			if (_host != null) UnityEngine.Object.Destroy(_host);
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}

		[UnityTest, Category("HarborRealData")]
		public IEnumerator NamedAndWarmStarts_HonorTheTrafficCommandFlag()
		{
			var traffic = Path.GetFullPath(Path.Combine(Application.dataPath, "../../data/processed/harbor/traffic/2025-07-16"));
			if (!Directory.Exists(traffic)) Assert.Ignore("The harbor data is not copied: missing " + traffic);
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			Assert.Fail("This test loads the checked-in editor scene.");
			yield break;
#endif
			yield return null;
			var bootstrap = UnityEngine.Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(bootstrap.Problem, Is.Null);
			var services = (HarborIntegrationServices)bootstrap.Services;
			_host = new GameObject("start traffic flag regression");
			var benchmark = _host.AddComponent<HarborBenchmark>();
			benchmark.enabled = false;
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
			void Set(string field, object value) => typeof(HarborBenchmark).GetField(field, flags).SetValue(benchmark, value);
			Set("_bootstrap", bootstrap);
			Set("_services", services);
			Set("_runner", bootstrap.Runner);
			foreach (var on in new[] { true, false })
			{
				Set("_traffic", on);
				foreach (var name in new[] { "start-87", "start-89", "start-112", "switches" })
				{
					Set("_name", name);
					var sequence = (IEnumerator)typeof(HarborBenchmark).GetMethod("CatalogStarts", flags).Invoke(benchmark, null);
					try
					{
						Assert.That(sequence.MoveNext(), Is.True, "the accepted start enters its readiness wait");
						Assert.That(bootstrap.Runner.IsRunActive, Is.True, name);
						Assert.That(bootstrap.Runner.TrafficEnabled, Is.EqualTo(on), name + " must measure the traffic choice it reports");
					}
					finally { (sequence as IDisposable)?.Dispose(); }
					yield return null;
				}
			}
		}
	}
}
