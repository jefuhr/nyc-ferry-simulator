using System.Collections;
using System.Collections.Generic;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The integrated scene as a user opens it: its own scenario, the helm (no device input, so neutral), the
	/// wheelhouse and chase views, at 60 frames a second. Failure modes guarded: errors or exceptions from any part of
	/// the joined scene (georeference check, landing data, Cesium, UI, cameras), a run that never starts, and the panel
	/// left without data. Run it alone to keep a clean log:
	/// <c>-testFilter FerrySim.Slice.PlayModeTests.SliceSceneSmokeTests</c>.
	/// </summary>
	public class SliceSceneSmokeTests
	{
		private readonly List<string> _errors = new List<string>();

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			Application.logMessageReceived -= Record;
			yield return SliceScene.Unload();
		}

		private void Record(string message, string stackTrace, LogType type)
		{
			if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
			{
				_errors.Add($"{type}: {message}");
			}
		}

		[UnityTest]
		public IEnumerator TheSceneOpensAndRuns_WithoutErrors()
		{
			Application.logMessageReceived += Record;
			var scene = new SliceScene();
			yield return scene.Load();
			Assert.That(scene.Rig.CurrentMode, Is.EqualTo(CameraRig.Mode.Wheelhouse), "opens in the wheelhouse");
			Assert.That(scene.Bootstrap.StartClearance.IsClear(), Is.True, "the scenario starts clear of every face");
			Assert.That(scene.Placement.WaterSurface, Is.InstanceOf<WorldWaterHeight>());
			Assert.That(scene.Bootstrap.TerrainCorrection, Is.Not.Null, "the terrain sits on the water's datum");

			Time.captureDeltaTime = 1f / 60f;
			for (var frame = 0; frame < 300; frame++)
			{
				yield return null;
			}

			Assert.That(scene.Runner.HasTelemetry, Is.True, "the run is stepping");
			Assert.That(scene.Panel.ShowingNoData, Is.False);
			scene.Rig.CurrentMode = CameraRig.Mode.Chase;
			for (var frame = 0; frame < 120; frame++)
			{
				yield return null;
			}

			Assert.That(scene.View.HudVisible, Is.True, "the chase view shows the HUD");
			Assert.That(scene.View.PanelVisible, Is.False);
			Debug.Log($"Slice smoke: {scene.Runner.Driver.SimulationTime:F1} s simulated in 420 frames at 60 fps; landing geometry {scene.Landings.Digest}; {_errors.Count} errors.");
			Assert.That(_errors, Is.Empty, "no errors or exceptions from the scene while it loaded and ran");
		}
	}
}
