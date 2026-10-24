using System;
using System.Collections;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FerrySim.Tests.PlayMode
{
	/// <summary>
	/// Proves the sim steps from FixedUpdate in a player-loop scene and that rendering works: it
	/// writes a screenshot of a trivial camera view to artifacts/unity/smoke.png. Needs graphics, so
	/// the batchmode run must not pass -nographics.
	/// </summary>
	public class CoreSmokePlayModeTests
	{
		private const string SmokeSceneName = "FerrySimSmoke";
		private const int TargetSteps = 200;
		private const float TimeoutSeconds = 30f;

		private float _savedFixedDeltaTime;
		private Scene _previousScene;
		private Scene _scene;
		private Texture2D _capturedTexture;

		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		[SetUp]
		public void SetUp()
		{
			_savedFixedDeltaTime = Time.fixedDeltaTime;
			_previousScene = SceneManager.GetActiveScene();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			Time.fixedDeltaTime = _savedFixedDeltaTime;
			if (_capturedTexture != null)
			{
				UnityEngine.Object.Destroy(_capturedTexture);
				_capturedTexture = null;
			}

			if (_previousScene.IsValid() && _previousScene.isLoaded)
			{
				SceneManager.SetActiveScene(_previousScene);
			}

			if (_scene.IsValid() && _scene.isLoaded)
			{
				yield return SceneManager.UnloadSceneAsync(_scene);
			}

			_scene = default;
			// Destroy is deferred until the end of the frame, even without a scene to unload.
			yield return null;
		}

		[UnityTest]
		public IEnumerator CleanupAfterForcedAssertion_UnloadsSceneAndDestroysCapturedTexture()
		{
			Camera camera = null;
			Light light = null;
			SmokeVesselDriver driver = null;
			Texture2D texture = null;
			Assert.Throws<AssertionException>(() =>
			{
				CreateSmokeScene();
				camera = new GameObject("Camera").AddComponent<Camera>();
				light = new GameObject("Sun").AddComponent<Light>();
				driver = new GameObject("Vessel").AddComponent<SmokeVesselDriver>();
				texture = Capture(camera, 16, 16);
				Time.fixedDeltaTime = 0.01f;
				Assert.Fail("Forced assertion after capture, before scene cleanup");
			});

			// Exercise the same teardown the runner invokes after an aborted UnityTest.
			yield return TearDown();
			Assert.That(SceneManager.GetSceneByName(SmokeSceneName).IsValid(), Is.False, "smoke scene survived failure");
			Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(_previousScene));
			Assert.That(camera == null && light == null && driver == null, Is.True, "smoke objects survived failure");
			Assert.That(texture == null, Is.True, "captured texture survived failure");
			Assert.That(Time.fixedDeltaTime, Is.EqualTo(_savedFixedDeltaTime).Within(1e-7f));
		}

		[UnityTest]
		public IEnumerator CleanupAfterForcedCaptureFailure_RestoresTargetsAndReleasesTemporaryTexture()
		{
			var originalActive = RenderTexture.active;
			var previousTarget = new RenderTexture(16, 16, 24);
			var previousActive = new RenderTexture(16, 16, 24);
			using var temporaryTextures = new RecordingTemporaryTextureProvider();
			try
			{
				CreateSmokeScene();
				var camera = new GameObject("Camera").AddComponent<Camera>();
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				Texture2D texture = null;
				Assert.Throws<IOException>(() => Capture(camera, 19, 23, () =>
				{
					texture = _capturedTexture;
					throw new IOException("Forced capture failure after texture allocation");
				}, temporaryTextures));

				Assert.That(temporaryTextures.ReleaseCount, Is.EqualTo(1), "Capture must release its temporary target exactly once");
				Assert.That(temporaryTextures.ReleasedTexture, Is.SameAs(temporaryTextures.AllocatedTexture),
					"Capture released a different temporary target");

				var cameraTargetRestored = camera.targetTexture == previousTarget;
				var activeTargetRestored = RenderTexture.active == previousActive;
				yield return TearDown();
				Assert.That(cameraTargetRestored, Is.True, "camera target was not restored by Capture");
				Assert.That(activeTargetRestored, Is.True, "active target was not restored by Capture");
				Assert.That(texture == null, Is.True, "partially captured texture survived failure");
				Assert.That(SceneManager.GetSceneByName(SmokeSceneName).IsValid(), Is.False);
			}
			finally
			{
				RenderTexture.active = originalActive;
				UnityEngine.Object.Destroy(previousTarget);
				UnityEngine.Object.Destroy(previousActive);
			}
		}

		private void CreateSmokeScene()
		{
			_scene = SceneManager.CreateScene(SmokeSceneName);
			SceneManager.SetActiveScene(_scene);
		}

		[UnityTest]
		public IEnumerator SimStepsInFixedUpdate_MovesVesselAndRendersScreenshot()
		{
			Time.fixedDeltaTime = (float)SimulationClock.FixedTimeStep;
			CreateSmokeScene();

			var parameters = VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json"));
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.0);
			var simulation = new VesselDynamicsFactory().Create(parameters, UniformEnvironment.Calm, initial);

			var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
			water.transform.localScale = new Vector3(10f, 1f, 10f);
			var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
			hull.transform.localScale = new Vector3(5f, 2f, 26f);
			var driver = hull.AddComponent<SmokeVesselDriver>();
			driver.Simulation = simulation;

			var lightObject = new GameObject("Sun");
			lightObject.AddComponent<Light>().type = LightType.Directional;
			lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

			var cameraObject = new GameObject("Camera");
			var camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = new Color(0.45f, 0.62f, 0.78f);
			cameraObject.transform.position = new Vector3(40f, 25f, -40f);
			cameraObject.transform.LookAt(hull.transform);

			// FixedUpdate is paced by real time, so the timeout is wall-clock, not a frame count.
			var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
			while (driver.Steps < TargetSteps && Time.realtimeSinceStartup < deadline)
			{
				yield return null;
			}

			Assert.That(driver.Steps, Is.GreaterThanOrEqualTo(TargetSteps), "FixedUpdate did not drive the sim");
			var state = simulation.State;
			Assert.That(state.IsFinite(), Is.True);
			Assert.That(state.North, Is.GreaterThan(initial.North), "vessel did not move north (heading 0)");
			Assert.That(state.Time, Is.EqualTo(SimulationClock.TimeAtStep(driver.Steps)).Within(1e-9));

			yield return null;
			var path = Path.Combine(RepoRoot, "artifacts", "unity", "smoke.png");
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var texture = Capture(camera, 640, 360);
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Assert.That(HasVariation(texture), Is.True, "screenshot is a single flat color");
		}

		private interface ITemporaryTextureProvider
		{
			RenderTexture GetTemporary(int width, int height, int depth);
			void ReleaseTemporary(RenderTexture texture);
		}

		private sealed class TemporaryTextureProvider : ITemporaryTextureProvider
		{
			public RenderTexture GetTemporary(int width, int height, int depth) => RenderTexture.GetTemporary(width, height, depth);
			public void ReleaseTemporary(RenderTexture texture) => RenderTexture.ReleaseTemporary(texture);
		}

		private sealed class RecordingTemporaryTextureProvider : ITemporaryTextureProvider, IDisposable
		{
			private readonly TemporaryTextureProvider _provider = new TemporaryTextureProvider();
			private bool _allocationReleased;

			public RenderTexture AllocatedTexture { get; private set; }
			public RenderTexture ReleasedTexture { get; private set; }
			public int ReleaseCount { get; private set; }

			public RenderTexture GetTemporary(int width, int height, int depth)
			{
				return AllocatedTexture = _provider.GetTemporary(width, height, depth);
			}

			public void ReleaseTemporary(RenderTexture texture)
			{
				ReleaseCount++;
				ReleasedTexture = texture;
				_allocationReleased |= ReferenceEquals(texture, AllocatedTexture);
				_provider.ReleaseTemporary(texture);
			}

			public void Dispose()
			{
				// Keep even a failed release assertion from leaking the test's real allocation.
				if (!_allocationReleased && AllocatedTexture != null)
				{
					_provider.ReleaseTemporary(AllocatedTexture);
					_allocationReleased = true;
				}
			}
		}

		private Texture2D Capture(Camera camera, int width, int height, Action afterAllocation = null,
			ITemporaryTextureProvider temporaryTextures = null)
		{
			temporaryTextures = temporaryTextures ?? new TemporaryTextureProvider();
			var previousActive = RenderTexture.active;
			var previousTarget = camera.targetTexture;
			var target = temporaryTextures.GetTemporary(width, height, 24);
			try
			{
				camera.targetTexture = target;
				camera.Render();
				RenderTexture.active = target;
				_capturedTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
				afterAllocation?.Invoke();
				_capturedTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				_capturedTexture.Apply();
				return _capturedTexture;
			}
			finally
			{
				if (camera != null)
				{
					camera.targetTexture = previousTarget;
				}

				RenderTexture.active = previousActive;
				temporaryTextures.ReleaseTemporary(target);
			}
		}

		private static bool HasVariation(Texture2D texture)
		{
			var pixels = texture.GetPixels32();
			var first = pixels[0];
			foreach (var pixel in pixels)
			{
				if (pixel.r != first.r || pixel.g != first.g || pixel.b != first.b)
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>Test-only driver: steps the sim once per FixedUpdate with both levers ahead and mirrors the state onto its transform.</summary>
	public sealed class SmokeVesselDriver : MonoBehaviour
	{
		public ISimulation Simulation { get; set; }

		public int Steps { get; private set; }

		private void FixedUpdate()
		{
			if (Simulation == null)
			{
				return;
			}

			Simulation.Step(new ControlInputs(1.0, 1.0, 0.0));
			Steps++;
			var state = Simulation.State;
			// World (north, east, down) -> Unity (x = east, y = -down, z = north); heading is yaw about +y.
			transform.position = new Vector3((float)state.East, 1f - (float)state.Down, (float)state.North);
			transform.rotation = Quaternion.Euler(0f, (float)(state.Heading * 180.0 / Math.PI), 0f);
		}
	}
}
