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
		private const int TargetSteps = 200;
		private const float TimeoutSeconds = 30f;

		private float _savedFixedDeltaTime;

		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		[SetUp]
		public void SetUp() => _savedFixedDeltaTime = Time.fixedDeltaTime;

		[TearDown]
		public void TearDown() => Time.fixedDeltaTime = _savedFixedDeltaTime;

		[UnityTest]
		public IEnumerator SimStepsInFixedUpdate_MovesVesselAndRendersScreenshot()
		{
			Time.fixedDeltaTime = (float)SimulationClock.FixedTimeStep;
			var scene = SceneManager.CreateScene("FerrySimSmoke");
			SceneManager.SetActiveScene(scene);

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
			UnityEngine.Object.Destroy(texture);

			yield return SceneManager.UnloadSceneAsync(scene);
		}

		private static Texture2D Capture(Camera camera, int width, int height)
		{
			var target = RenderTexture.GetTemporary(width, height, 24);
			var previousActive = RenderTexture.active;
			camera.targetTexture = target;
			camera.Render();
			RenderTexture.active = target;
			var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
			texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			texture.Apply();
			camera.targetTexture = null;
			RenderTexture.active = previousActive;
			RenderTexture.ReleaseTemporary(target);
			return texture;
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
