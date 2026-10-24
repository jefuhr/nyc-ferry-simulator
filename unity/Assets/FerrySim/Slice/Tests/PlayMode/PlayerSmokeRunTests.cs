using System;
using System.Collections;
using System.IO;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The smoke run a release build performs with <c>-ferrysim-smoke</c> (<see cref="PlayerSmokeRun"/>), here in the
	/// editor on the real slice scene, so the check that gates a release is itself checked. Failure modes guarded: a run
	/// that passes without the vessel moving, a recording or screenshot reported but not written, an error in the log
	/// that does not fail the run, a vessel file read from the repository passing where a player must read StreamingAssets,
	/// a run that never quits, and a quit code that disagrees with the report. Evidence:
	/// <c>artifacts/unity/slice/player-smoke/smoke.json</c> and its screenshots.
	/// </summary>
	public class PlayerSmokeRunTests
	{
		// The release run's drive: from rest, the shift and the engines' lag take most of the first few seconds.
		private const double DriveSeconds = PlayerSmokeOptions.DefaultDriveSeconds;

		private GameObject _host;

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			if (_host != null)
			{
				Object.Destroy(_host);
			}

			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator TheSmokeRun_DrivesTheSlice_AndPasses()
		{
			var folder = SliceScene.FreshFolder("player-smoke");
			int? exitCode = null;
			yield return StartSmokeRun(folder, code => exitCode = code, requirePackagedData: false);
			yield return WaitForQuit(() => exitCode.HasValue);
			var report = ReadReport(folder);

			Assert.That(exitCode, Is.EqualTo(0), "failures: " + string.Join("; ", report.failures));
			Assert.That(report.passed, Is.True);
			Assert.That(report.failures, Is.Empty);
			Assert.That(report.landing_segments, Is.GreaterThan(0));
			Assert.That(report.drive_simulated_s, Is.GreaterThanOrEqualTo(DriveSeconds));
			Assert.That(report.displacement_m, Is.GreaterThan(PlayerSmokeRun.MinimumDisplacementMetres));
			Assert.That(report.max_speed_over_ground_mps, Is.GreaterThan(0.0));
			Assert.That(report.state_finite, Is.True);
			Assert.That(report.recording, Does.StartWith(Path.Combine(folder, "runs")), "the drive was recorded where it was told");
			Assert.That(File.Exists(report.recording), Is.True, report.recording);
			Assert.That(report.recording_bytes, Is.EqualTo(new FileInfo(report.recording).Length).And.GreaterThan(0));
			Assert.That(report.screenshots, Has.Length.EqualTo(2));
			foreach (var screenshot in report.screenshots)
			{
				Assert.That(File.Exists(screenshot), Is.True, screenshot);
			}

			Assert.That(report.min_screenshot_colours, Is.GreaterThan(PlayerSmokeRun.MinimumScreenshotColours));
			Assert.That(report.errors, Is.Zero);
			Assert.That(report.vessel_file_packaged, Is.False, "the editor reads the repository's vessel file");
			Assert.That(report.build, Is.EqualTo("editor"));
		}

		[UnityTest]
		public IEnumerator AnErrorInTheLog_FailsTheRun_AndQuitsWithOne()
		{
			var folder = SliceScene.FreshFolder("player-smoke-error");
			int? exitCode = null;
			yield return StartSmokeRun(folder, code => exitCode = code, requirePackagedData: false);
			LogAssert.Expect(LogType.Error, "[test] injected error");
			Debug.LogError("[test] injected error");
			yield return WaitForQuit(() => exitCode.HasValue);
			var report = ReadReport(folder);

			Assert.That(exitCode, Is.EqualTo(1));
			Assert.That(report.passed, Is.False);
			Assert.That(report.errors, Is.EqualTo(1));
			Assert.That(report.error_messages, Has.Some.Contains("[test] injected error"));
			Assert.That(report.failures, Has.Some.Contains("error"));
			Assert.That(report.displacement_m, Is.GreaterThan(PlayerSmokeRun.MinimumDisplacementMetres), "the rest of the run still ran");
		}

		[UnityTest]
		public IEnumerator AVesselFileFromTheRepository_FailsWhereAPlayerMustReadStreamingAssets()
		{
			// The editor resolves the vessel file from the repository: what a player whose build left it out would fall back to.
			var folder = SliceScene.FreshFolder("player-smoke-unpackaged");
			int? exitCode = null;
			yield return StartSmokeRun(folder, code => exitCode = code, requirePackagedData: true);
			yield return WaitForQuit(() => exitCode.HasValue);
			var report = ReadReport(folder);

			Assert.That(exitCode, Is.EqualTo(1));
			Assert.That(report.vessel_file_packaged, Is.False);
			Assert.That(report.failures, Has.Some.Contains("StreamingAssets"));
		}

		[UnityTest]
		public IEnumerator EachView_IsSelectedBeforeItIsCaptured_WhicheverViewTheSceneIsIn()
		{
			var folder = SliceScene.FreshFolder("player-smoke-views");
			int? exitCode = null;
			yield return StartSmokeRun(folder, code => exitCode = code, requirePackagedData: false, beforeStart: scene => scene.Rig.CurrentMode = CameraRig.Mode.Chase);
			yield return WaitForQuit(() => exitCode.HasValue);
			var report = ReadReport(folder);

			Assert.That(exitCode, Is.EqualTo(0), "failures: " + string.Join("; ", report.failures));
			Assert.That(report.screenshot_views, Is.EqualTo(new[] { "Wheelhouse", "Chase" }));
		}

		[UnityTest]
		public IEnumerator ASliceWithoutItsCameraRig_FailsTheRun()
		{
			// Whatever else complains once the rig is gone is not what this test is about.
			LogAssert.ignoreFailingMessages = true;
			var folder = SliceScene.FreshFolder("player-smoke-no-rig");
			int? exitCode = null;
			yield return StartSmokeRun(folder, code => exitCode = code, requirePackagedData: false, beforeStart: scene => Object.Destroy(scene.Rig));
			yield return WaitForQuit(() => exitCode.HasValue);
			var report = ReadReport(folder);

			Assert.That(exitCode, Is.EqualTo(1));
			Assert.That(report.failures, Has.Some.Contains("camera rig"));
			Assert.That(report.screenshots, Is.Empty);
		}

		private IEnumerator StartSmokeRun(string folder, Action<int> quit, bool requirePackagedData, Action<SliceScene> beforeStart = null)
		{
			var scene = new SliceScene();
			yield return scene.Load();
			scene.StepsPerFrame(10);
			beforeStart?.Invoke(scene);
			_host = new GameObject("player smoke run under test");
			_host.AddComponent<PlayerSmokeRun>().Configure(new PlayerSmokeOptions(folder, DriveSeconds), quit, requirePackagedData, RenderCamera, Path.Combine(folder, "runs"));
		}

		private static IEnumerator WaitForQuit(Func<bool> quit)
		{
			for (var frame = 0; frame < 3000 && !quit(); frame++)
			{
				yield return null;
			}

			Assert.That(quit(), Is.True, "the smoke run never quit");
		}

		private static PlayerSmokeReport ReadReport(string folder)
		{
			var path = Path.Combine(folder, PlayerSmokeRun.ReportFileName);
			Assert.That(File.Exists(path), Is.True, "the smoke run wrote " + path);
			return JsonUtility.FromJson<PlayerSmokeReport>(File.ReadAllText(path));
		}

		/// <summary>
		/// The view as the camera renders it: play mode in batch mode has no screen to capture. Shaders compile
		/// synchronously meanwhile, so a cold Library draws real colours rather than the placeholder.
		/// </summary>
		private static Texture2D RenderCamera(Camera camera)
		{
			const int width = 640;
			const int height = 360;
			var target = RenderTexture.GetTemporary(width, height, 24);
			var previousTarget = camera.targetTexture;
			var previousActive = RenderTexture.active;
#if UNITY_EDITOR
			var asyncCompilation = UnityEditor.ShaderUtil.allowAsyncCompilation;
			UnityEditor.ShaderUtil.allowAsyncCompilation = false;
#endif
			try
			{
				camera.targetTexture = target;
				camera.Render();
				RenderTexture.active = target;
				var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
				texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				texture.Apply();
				return texture;
			}
			finally
			{
#if UNITY_EDITOR
				UnityEditor.ShaderUtil.allowAsyncCompilation = asyncCompilation;
#endif
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				RenderTexture.ReleaseTemporary(target);
			}
		}
	}
}
