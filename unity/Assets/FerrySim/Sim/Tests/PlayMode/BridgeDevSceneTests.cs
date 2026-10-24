using System;
using System.Collections;
using System.IO;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace FerrySim.Sim.PlayModeTests
{
	/// <summary>
	/// End to end in the dev scene: a simulated keyboard puts the levers ahead, the bridge drives the vessel
	/// underway, the chase camera follows, the readout reports it, and a screenshot lands in
	/// artifacts/unity/bridge-dev.png. Failure modes guarded: scene wiring broken (runner without controls,
	/// camera without a target, readout without a source), keyboard notches not reaching the physics, and a
	/// scene that renders nothing.
	/// </summary>
	public class BridgeDevSceneTests : InputTestFixture
	{
		private const string ScenePath = "Assets/FerrySim/Sim/Scenes/BridgeDev.unity";
		private const int Width = 1280;
		private const int Height = 720;

		private float _savedCaptureDeltaTime;
		private Scene _scene;
		private AsyncOperation _sceneUnload;

		public override void Setup()
		{
			base.Setup();
			_savedCaptureDeltaTime = Time.captureDeltaTime;
		}

		public override void TearDown()
		{
			Time.captureDeltaTime = _savedCaptureDeltaTime;
			try { BeginSceneCleanup(); }
			finally { base.TearDown(); }
		}

		private void BeginSceneCleanup()
		{
			if (_sceneUnload != null) return;
			// Additive loading leaves the test host scene available while these owned roots unload.
			var scene = _scene.IsValid() ? _scene : SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				foreach (var root in scene.GetRootGameObjects())
				{
					foreach (var runner in root.GetComponentsInChildren<VesselSimulationRunner>(true)) runner.EndRun();
				}
				_sceneUnload = SceneManager.UnloadSceneAsync(scene);
			}
			_scene = default;
		}

		[UnityTearDown]
		public IEnumerator WaitForSceneCleanup()
		{
			BeginSceneCleanup();
			if (_sceneUnload != null) yield return _sceneUnload;
			_sceneUnload = null;
		}

		[UnityTest]
		public IEnumerator KeyboardAhead_PutsTheVesselUnderway_AndTheSceneIsCaptured()
		{
#if UNITY_EDITOR
			Assert.That(File.Exists(Path.Combine(PlayModeSupport.RepoRoot, "unity", ScenePath)), Is.True,
				"BridgeDev.unity is missing; build it with -executeMethod FerrySim.Sim.Authoring.BridgeDevSceneBuilder.BuildFromCommandLine");
			var keyboard = InputSystem.AddDevice<Keyboard>();
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
			_scene = SceneManager.GetSceneByPath(ScenePath);
			var runner = UnityEngine.Object.FindAnyObjectByType<VesselSimulationRunner>();
			Assert.That(runner, Is.Not.Null, "the scene has a runner");
			yield return PlayModeSupport.Frames(() => runner.IsInitialized, 30);
			Assert.That(runner.IsInitialized, Is.True);
			var controller = runner.GetComponent<HelmInputController>();
			Assert.That(controller, Is.Not.Null, "the runner has the wheelhouse controls");
			Assert.That(runner.ControlSource, Is.SameAs(controller));
			var chase = UnityEngine.Object.FindAnyObjectByType<ChaseCamera>();
			Assert.That(chase.Target, Is.SameAs(runner.transform));
			Assert.That(runner.transform.Find(VesselGreybox.RootName), Is.Not.Null, "greybox built");
			var start = runner.State;

			// In a [UnityTest] the fixture only queues events; the input update runs on the next frame.
			for (var press = 0; press < 5; press++)
			{
				Press(keyboard.wKey);
				yield return null;
				Release(keyboard.wKey);
				yield return null;
			}

			Assert.That(controller.Mapper.PortLever, Is.EqualTo(1.0));
			Assert.That(controller.Mapper.StarboardLever, Is.EqualTo(1.0));
			Time.captureDeltaTime = 1f / 30f;
			yield return PlayModeSupport.Frames(() => runner.State.Time >= 40.0, 3000);

			var state = runner.State;
			var along = (state.North - start.North) * Math.Cos(start.Heading) + (state.East - start.East) * Math.Sin(start.Heading);
			var knots = Units.MetresPerSecondToKnots(runner.Telemetry.SpeedThroughWater);
			Assert.That(state.Time, Is.GreaterThanOrEqualTo(40.0));
			Assert.That(runner.Controls, Is.EqualTo(new ControlInputs(1.0, 1.0, 0.0)), "the keyboard notches reached the physics");
			Assert.That(along, Is.GreaterThan(150.0), "metres made good along the start heading");
			Assert.That(knots, Is.GreaterThan(15.0), "underway at speed");
			var readout = UnityEngine.Object.FindAnyObjectByType<SimDebugReadout>();
			readout.Refresh();
			StringAssert.Contains("levers port +1.00  stbd +1.00", readout.Text);
			StringAssert.Contains("camera: chase", readout.Text);
			StringAssert.Contains("REC ", readout.Text);

			// A second readout renders into a texture so the overlay shows in the capture.
			RenderTexture target = null;
			PanelSettings panel = null;
			GameObject capture = null;
			Texture2D overlay = null, scene = null, image = null;
			try
			{
				target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
				panel = UnityEngine.Object.Instantiate(readout.GetComponent<UIDocument>().panelSettings);
				capture = new GameObject("CaptureReadout");
				var document = capture.AddComponent<UIDocument>();
				panel.targetTexture = target;
				panel.clearColor = true;
				panel.colorClearValue = Color.clear;
				document.panelSettings = panel;
				var captureReadout = capture.AddComponent<SimDebugReadout>();
				captureReadout.Runner = runner;
				Time.captureDeltaTime = _savedCaptureDeltaTime;
				yield return null;
				yield return null;
				yield return null;

				overlay = Read(target);
				scene = RenderScene(Camera.main);
				image = Composite(scene, overlay);
				var path = Path.Combine(PlayModeSupport.RepoRoot, "artifacts", "unity", "bridge-dev.png");
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllBytes(path, image.EncodeToPNG());
				TestContext.WriteLine($"wrote {path}: t={state.Time:F1} s, {knots:F1} kn through the water, {along:F0} m along the start heading");

				Assert.That(DistinctColours(scene), Is.GreaterThan(50), "the 3D view has content");
				Assert.That(CoveredPixels(overlay), Is.GreaterThan(1000), "the readout rendered");
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(capture);
				if (panel != null) panel.targetTexture = null;
				UnityEngine.Object.DestroyImmediate(panel);
				UnityEngine.Object.DestroyImmediate(overlay);
				UnityEngine.Object.DestroyImmediate(scene);
				UnityEngine.Object.DestroyImmediate(image);
				if (target != null)
				{
					target.Release();
					UnityEngine.Object.DestroyImmediate(target);
				}
			}
#else
			Assert.Ignore("Loads the scene through the editor.");
			yield break;
#endif
		}

		private static Texture2D RenderScene(Camera camera)
		{
			var previous = camera.targetTexture;
			var target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
			try
			{
				camera.targetTexture = target;
				camera.Render();
				return Read(target);
			}
			finally
			{
				camera.targetTexture = previous;
				RenderTexture.ReleaseTemporary(target);
			}
		}

		private static Texture2D Read(RenderTexture source)
		{
			var previous = RenderTexture.active;
			Texture2D texture = null;
			try
			{
				RenderTexture.active = source;
				texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
				texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
				texture.Apply();
				return texture;
			}
			catch
			{
				UnityEngine.Object.DestroyImmediate(texture);
				throw;
			}
			finally { RenderTexture.active = previous; }
		}

		/// <summary>The UI texture over the scene; UI Toolkit writes premultiplied alpha.</summary>
		private static Texture2D Composite(Texture2D scene, Texture2D overlay)
		{
			var below = scene.GetPixels32();
			var above = overlay.GetPixels32();
			var result = new Color32[below.Length];
			for (var i = 0; i < below.Length; i++)
			{
				var a = above[i].a / 255f;
				result[i] = new Color32(
					(byte)Mathf.Min(255f, above[i].r + below[i].r * (1f - a)),
					(byte)Mathf.Min(255f, above[i].g + below[i].g * (1f - a)),
					(byte)Mathf.Min(255f, above[i].b + below[i].b * (1f - a)),
					255);
			}

			var image = new Texture2D(scene.width, scene.height, TextureFormat.RGBA32, false);
			try
			{
				image.SetPixels32(result);
				image.Apply();
				return image;
			}
			catch
			{
				UnityEngine.Object.DestroyImmediate(image);
				throw;
			}
		}

		private static int DistinctColours(Texture2D texture)
		{
			var seen = new System.Collections.Generic.HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
			}

			return seen.Count;
		}

		private static int CoveredPixels(Texture2D texture)
		{
			var count = 0;
			foreach (var pixel in texture.GetPixels32())
			{
				count += pixel.a > 0 ? 1 : 0;
			}

			return count;
		}
	}
}
