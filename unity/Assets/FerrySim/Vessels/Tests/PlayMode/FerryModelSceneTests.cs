using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Vessels.Tests
{
	/// <summary>Guards the real scene's default/fallback, class changes, lights and bridge view; captures their rendering.</summary>
	public class FerryModelSceneTests
	{
		private const string ScenePath = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";
		private SliceBootstrap _scene;
		private static string Output => Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/ferry-models/unity"));

		[UnitySetUp]
		public IEnumerator Load()
		{
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#endif
			yield return null;
			yield return null;
			_scene = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(_scene, Is.Not.Null);
			Assert.That(_scene.LoadError, Is.Null);
			_scene.Runner.RecordRuns = false;
			Directory.CreateDirectory(Output);
		}

		[UnityTearDown]
		public IEnumerator Unload()
		{
			Time.captureDeltaTime = 0;
			var scene = SceneManager.GetSceneByPath(ScenePath);
			SceneManager.SetActiveScene(SceneManager.CreateScene("model test teardown"));
			if (scene.IsValid() && scene.isLoaded) { yield return SceneManager.UnloadSceneAsync(scene); }
		}

		[UnityTest]
		public IEnumerator OwnFerries_DayNightChaseBridgeAndGreyboxFallback()
		{
			var stats = new StringBuilder("capture,scene_draw_calls,scene_triangles,model_triangles,model_material_passes\n");
			foreach (var id in new[] { "t-class", "k-class" })
			foreach (var night in new[] { false, true })
			{
				_scene.Runner.Initialize(new ScenarioSettings
				{
					vesselFile = "vessels/" + id + ".json", startLatitude = 40.7027, startLongitude = -74.0022,
					startHeadingDegrees = 25, scenarioStartUtc = night ? "2025-07-21T02:30:00Z" : "2025-07-20T17:00:00Z",
					environment = new ScenarioEnvironment { calm = true }
				}, new ConstantControls(ControlInputs.Neutral));
				_scene.Runner.Pause();
				yield return null;
				var visual = _scene.Runner.GetComponent<VesselGreybox>();
				var model = _scene.Runner.GetComponentInChildren<FerryModel>();
				Assert.That(model, Is.Not.Null);
				Assert.That(model.ClassId, Is.EqualTo(id));
				Assert.That(model.Detail, Is.Zero);
				Assert.That(_scene.Runner.transform.Find(VesselGreybox.RootName).gameObject.activeSelf, Is.False);
				var atmosphere = Object.FindFirstObjectByType<Atmosphere>();
				Assert.That(atmosphere.OwnLights.On, Is.EqualTo(night));
				Assert.That(Vector3.Distance(atmosphere.OwnLights.Layout.Masthead, _scene.Runner.transform.InverseTransformPoint(model.Mount("Masthead").position)), Is.LessThan(.001f));
				_scene.CameraRig.CurrentMode = CameraRig.Mode.Chase;
				_scene.CameraRig.Chase.Distance = 42;
				_scene.CameraRig.Chase.Height = 19;
				_scene.CameraRig.Chase.Orbit = 35;
				_scene.CameraRig.Chase.Snap();
				yield return null;
				yield return Capture(id + (night ? "-night" : "-day") + "-chase", model, stats);
				_scene.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
				_scene.CameraRig.Wheelhouse.HeadLook = new Vector2(0, 35);
				_scene.CameraRig.Wheelhouse.Snap();
				yield return null;
				Assert.That(_scene.CameraRig.Wheelhouse.HeadLook.y, Is.EqualTo(35).Within(.01));
				yield return Capture(id + (night ? "-night" : "-day") + "-wheelhouse-down35", model, stats);
				visual.UseGreybox = true;
				Assert.That(_scene.Runner.transform.Find(VesselGreybox.RootName).gameObject.activeSelf, Is.True);
				Assert.That(model.gameObject.activeSelf, Is.False);
				visual.UseGreybox = false;
				Assert.That(model.gameObject.activeSelf, Is.True);
			}

			File.WriteAllText(Path.Combine(Output, "render-cost.csv"), stats.ToString());
		}

		[UnityTest]
		public IEnumerator AuditedTraffic_UsesSharedLod1MeshesInTheRealReplay()
		{
			var stats = new StringBuilder("capture,scene_draw_calls,scene_triangles,model_triangles,model_material_passes\n");
			foreach (var night in new[] { false, true })
			{
				_scene.Runner.Initialize(new ScenarioSettings
				{
					startLatitude = 40.7027, startLongitude = -74.0022, startHeadingDegrees = 25,
					// The July K reports end before night; January 13 at 18:00 EST has a K ferry in the slice after dark.
					scenarioStartUtc = night ? "2025-01-13T23:00:00Z" : "2025-07-20T17:00:00Z",
					environment = new ScenarioEnvironment { pack = night ? "lower-east-river/2025-01-13" : "lower-east-river/2025-07-20" },
					traffic = new ScenarioTraffic { day = night ? "2025-01-13" : "2025-07-20", leaveOut = "off" }
				}, new ConstantControls(ControlInputs.Neutral));
				_scene.Runner.Pause();
				_scene.TrafficView.ViewRange = 12000;
				_scene.TrafficView.Place();
				yield return null;
				var captured = new HashSet<string>();
				foreach (var hull in _scene.TrafficView.Hulls)
				{
					var model = hull.GetComponentInChildren<FerryModel>(true);
					var id = FerryModels.ClassForMmsi(hull.Mmsi);
					Assert.That(model != null, Is.EqualTo(id != null));
					if (model == null) { continue; }
					Assert.That(model.Detail, Is.EqualTo(1));
					Assert.That(model.ClassId, Is.EqualTo(id));
					Assert.That(model.GetComponentInChildren<MeshFilter>().sharedMesh,
						Is.SameAs(FerryModels.Load(id, 1).GetComponentInChildren<MeshFilter>().sharedMesh));
					if (!hull.Shown || captured.Contains(id)) { continue; }
					_scene.CameraRig.CurrentMode = CameraRig.Mode.Chase;
					_scene.CameraRig.Chase.Target = hull.transform;
					_scene.CameraRig.Chase.Distance = 40;
					_scene.CameraRig.Chase.Height = 16;
					_scene.CameraRig.Chase.Orbit = 30;
					_scene.CameraRig.Chase.Snap();
					yield return null;
					yield return Capture(id + (night ? "-night" : "-day") + "-traffic-" + hull.Mmsi, model, stats);
					captured.Add(id);
				}

				Assert.That(captured, Is.EquivalentTo(new[] { "t-class", "k-class" }), "real audited ferries of both classes are drawn at the capture time");
			}

			File.WriteAllText(Path.Combine(Output, "traffic-render-cost.csv"), stats.ToString());
		}

		private IEnumerator Capture(string name, FerryModel model, StringBuilder stats)
		{
			// Let the selected controller settle after a camera/run change.
			for (var frame = 0; frame < 4; frame++) { yield return null; }
			var camera = _scene.CameraRig.GetComponent<Camera>();
			var target = new RenderTexture(1600, 1000, 24);
			var image = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
			var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var wasEnabled = camera.enabled;
			try
			{
				// Render once explicitly; an enabled target camera would also render automatically this frame.
				camera.enabled = false;
				camera.targetTexture = target;
				camera.aspect = 1.6f;
				camera.Render();
				RenderTexture.active = target;
				image.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
				image.Apply();
				File.WriteAllBytes(Path.Combine(Output, name + ".png"), image.EncodeToPNG());
				// UnityStats publishes the explicit render at the next frame boundary, even after a GPU readback.
				yield return null;
#if UNITY_EDITOR
				// These are the full scene's counters for this 1600x1000 offscreen camera render.
				var drawCalls = UnityStats.drawCalls;
				var triangles = UnityStats.triangles;
				Assert.That(drawCalls, Is.GreaterThan(0), "the captured render must have a nonempty cost receipt");
				Assert.That(triangles, Is.GreaterThan(0));
				stats.Append(name).Append(',').Append(drawCalls).Append(',').Append(triangles).Append(',')
					.Append(model.GetComponentInChildren<MeshFilter>().sharedMesh.triangles.Length / 3).Append(',')
					.Append(model.GetComponentInChildren<Renderer>().sharedMaterials.Length).Append('\n');
#endif
			}
			finally
			{
				camera.enabled = wasEnabled;
				camera.targetTexture = previous;
				camera.ResetAspect();
				RenderTexture.active = active;
				target.Release();
				Object.Destroy(target);
				Object.Destroy(image);
			}

			yield return null;
		}
	}
}
