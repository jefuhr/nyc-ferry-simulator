using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// Evidence from the integrated scene: screenshots of the wheelhouse view with the instruments over the real
	/// world and of the chase view at South Williamsburg in contact, the frame time in batchmode, and the garbage the
	/// runner and the UI make per frame. Writes artifacts/unity/slice-wheelhouse.png, slice-chase-landing-contact.png
	/// and slice-performance.json. Needs graphics: run without -nographics.
	/// </summary>
	public class SliceEvidenceTests
	{
		private const int Width = 1600;
		private const int Height = 900;

		private static byte[] _sink;

		private SliceScene _scene;
		private readonly List<Object> _temporary = new List<Object>();

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			SliceScene.ResetTime();
			foreach (var item in _temporary)
			{
				if (item is RenderTexture texture)
				{
					texture.Release();
				}

				if (item != null)
				{
					Object.Destroy(item);
				}
			}

			_temporary.Clear();
			if (_scene?.Camera != null)
			{
				_scene.Camera.ResetAspect();
			}

			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator Screenshots_WheelhouseUnderwayAndChaseAtTheLanding()
		{
			var uiTarget = Track(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
			uiTarget.Create();
			var uiSettings = Track(WheelhousePanelSettings.Create(uiTarget, new Color(0f, 0f, 0f, 0f)));
			_scene.UI.enabled = false;
			_scene.UI.Configure(_scene.Runner, uiSettings);
			_scene.UI.enabled = true;
			_scene.Camera.aspect = (float)Width / Height;

			var controls = new LandingApproachControls(_scene.Runner, 1.0);
			_scene.StepsPerFrame(10);
			_scene.Restart(_scene.Runner.Scenario, controls);
			_scene.Rig.CurrentMode = CameraRig.Mode.Wheelhouse;
			yield return _scene.FramesUntil(() => _scene.Runner.Driver.SimulationTime >= 12.0, 400, "12 s under way");
			Assert.That(_scene.View.PanelVisible, Is.True, "the wheelhouse view shows the panel");
			Assert.That(_scene.Panel.ShowingNoData, Is.False);
			var wheelhouse = Composite(uiTarget);
			Save(wheelhouse, "slice-wheelhouse.png");
			AssertNotBlank(wheelhouse, "wheelhouse view");
			AssertWorldAboveThePanel(wheelhouse);

			_scene.StepsPerFrame(50);
			yield return _scene.FramesUntil(() => controls.HoldFromStep >= 0 && _scene.Runner.Driver.StepCount > controls.HoldFromStep + 800, 400, "held on the face");
			_scene.StepsPerFrame(1);
			_scene.Rig.CurrentMode = CameraRig.Mode.Chase;
			_scene.Rig.Chase.Distance = 45f;
			_scene.Rig.Chase.Height = 16f;
			_scene.Rig.Chase.Snap();
			yield return SliceScene.Frames(5);
			Assert.That(_scene.View.HudVisible, Is.True, "the chase view shows the HUD");
			Assert.That(_scene.View.PanelVisible, Is.False);
			Assert.That(_scene.Runner.Telemetry.ContactForce, Is.GreaterThan(0.0), "in contact with the face when photographed");
			var chase = Composite(uiTarget);
			Save(chase, "slice-chase-landing-contact.png");
			AssertNotBlank(chase, "chase view");
			var hull = _scene.Runner.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToList();
			AssertDrawnWhereItProjects(chase, hull, "the vessel");
			var barge = Object.FindObjectsByType<LandingBarge>(FindObjectsSortMode.None).Single(b => b.StopId == SliceScene.ContactTestStopId);
			AssertDrawnWhereItProjects(chase, barge.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToList(), "South Williamsburg's barge");
		}

		[UnityTest]
		public IEnumerator FrameTime_AndGarbagePerFrame()
		{
			var report = new StringBuilder();
			var invariant = CultureInfo.InvariantCulture;
			var target = Track(new RenderTexture(Width, Height, 24) { name = "slice performance target" });
			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			_scene.Camera.targetTexture = target;
			var frames = new List<double>();
			try
			{
				SliceScene.ResetTime();
				var controls = new LandingApproachControls(_scene.Runner, 1.0);
				_scene.Restart(_scene.Runner.Scenario, controls);
				var loopRenders = 0;
				Action<ScriptableRenderContext, Camera> counter = (context, camera) => loopRenders += camera == _scene.Camera ? 1 : 0;
				RenderPipelineManager.endCameraRendering += counter;
				yield return SliceScene.Frames(30);
				RenderPipelineManager.endCameraRendering -= counter;
				var explicitRender = loopRenders == 0;
				for (var i = 0; i < 300; i++)
				{
					if (explicitRender)
					{
						_scene.Camera.Render();
					}

					yield return null;
					frames.Add(Time.unscaledDeltaTime * 1000.0);
				}

				// One camera render with the GPU waited on, serialised, as the world's performance test measures it.
				var renders = new List<double>();
				var stopwatch = new System.Diagnostics.Stopwatch();
				for (var i = 0; i < 40; i++)
				{
					stopwatch.Restart();
					_scene.Camera.Render();
					AsyncGPUReadback.Request(target, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
					stopwatch.Stop();
					renders.Add(stopwatch.Elapsed.TotalMilliseconds);
					yield return null;
				}

				report.Append("{\n");
				report.AppendFormat(invariant, "\t\"machine\": \"{0}\",\n", Escape(SystemInfo.deviceModel + ", " + SystemInfo.processorType + ", " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"));
				report.AppendFormat(invariant, "\t\"unity\": \"{0}\",\n", Application.unityVersion);
				report.AppendFormat(invariant, "\t\"context\": \"Unity editor, batchmode PlayMode test, URP, {0}x{1} render target, vSync off, wheelhouse view with the panel, T-class under way off South Williamsburg (scripted approach), terrain, chart structures and landings loaded{2}. For information; a player build will be faster.\",\n",
					Width, Height, explicitRender ? ", camera rendered explicitly each frame" : string.Empty);
				report.AppendFormat(invariant, "\t\"frame_ms\": {0},\n", Stats(frames));
				report.AppendFormat(invariant, "\t\"render_with_gpu_wait_ms\": {0},\n", Stats(renders));
			}
			finally
			{
				_scene.Camera.targetTexture = null;
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}

			// Garbage, measured on the update path itself (the runner's frame advance and placement, the UI refresh).
			var runner = _scene.Runner;
			var view = _scene.View;
			var probe = Measure(() => _sink = new byte[1024], 16);
			Assert.That(probe.Allocations, Is.GreaterThanOrEqualTo(1.0), "the allocation counter sees the 1 KiB array of every call");
			_scene.Restart(runner.Scenario, new ConstantControls(new ControlInputs(0.15, 0.15, 0.0)));
			var steppingOpenWater = Measure(() => { runner.AdvanceFrame(1.0 / 60.0); runner.ApplyRenderPose(); }, 120);
			Assert.That(() => { runner.AdvanceFrame(1.0 / 60.0); runner.ApplyRenderPose(); }, Is.Not.AllocatingGCMemory(), "runner, open water");

			var held = new LandingApproachControls(runner, 1.0);
			_scene.Restart(_scene.Approach(15.0, "garbage, on the face"), held);
			var guard = 0;
			// The first touch rebounds; after 45 s ahead idle the bow rests on the face.
			while ((held.HoldFromStep < 0 || runner.Driver.StepCount < held.HoldFromStep + 4500) && guard++ < 20000)
			{
				runner.AdvanceFrame(0.25);
			}

			Assert.That(runner.Telemetry.ContactForce, Is.GreaterThan(0.0), "held on the face for the contact measurement");
			var steppingInContact = Measure(() => { runner.AdvanceFrame(1.0 / 60.0); runner.ApplyRenderPose(); }, 120);
			Assert.That(() => { runner.AdvanceFrame(1.0 / 60.0); runner.ApplyRenderPose(); }, Is.Not.AllocatingGCMemory(), "runner, in contact");

			runner.Pause();
			view.Refresh(runner, Time.realtimeSinceStartupAsDouble);
			var unchangedRefresh = Measure(() => view.Refresh(runner, 10.0), 120);
			Assert.That(() => view.Refresh(runner, 10.25), Is.Not.AllocatingGCMemory(), "UI refresh of a sample already shown");
			runner.Resume();

			var t = 20.0;
			var changingRefresh = Measure(() => { runner.AdvanceFrame(1.0 / 60.0); view.Refresh(runner, t += 1.0 / 60.0); }, 120);
			runner.RecordRuns = true;
			runner.RecordingDirectory = SliceScene.FreshFolder("garbage-recording");
			_scene.Runner.ResetRun();
			var steppingRecording = Measure(() => { runner.AdvanceFrame(1.0 / 60.0); runner.ApplyRenderPose(); }, 120);
			runner.EndRun();

			report.Append("\t\"gc_allocations_per_frame\": {\n");
			report.AppendFormat(invariant, "\t\t\"runner_advance_and_place_open_water\": {0},\n", steppingOpenWater);
			report.AppendFormat(invariant, "\t\t\"runner_advance_and_place_in_contact\": {0},\n", steppingInContact);
			report.AppendFormat(invariant, "\t\t\"runner_advance_and_place_recording_10hz\": {0},\n", steppingRecording);
			report.AppendFormat(invariant, "\t\t\"ui_refresh_unchanged_sample\": {0},\n", unchangedRefresh);
			report.AppendFormat(invariant, "\t\t\"runner_advance_and_ui_refresh_new_sample_each_frame\": {0}\n", changingRefresh);
			report.Append("\t},\n");
			report.Append("\t\"gc_note\": \"GC allocations per call of the update path at 60 fps, averaged over 120 calls and counted by the profiler's GC.Alloc marker on the main thread (as Unity's AllocatingGCMemory constraint counts them). Zero for the runner (open water and in contact, recording off) and for a UI refresh of a sample already on screen is asserted; the recording and changing-sample figures are measured, not asserted. Bytes are not reported: the editor's Mono gives no per-thread byte count.\"\n");
			report.Append("}\n");
			var path = Path.Combine(SliceScene.ArtifactDirectory, "slice-performance.json");
			File.WriteAllText(path, report.ToString());
			Debug.Log($"Slice performance ({path}):\n{report}");
		}

		private readonly struct Garbage
		{
			public Garbage(double allocations)
			{
				Allocations = allocations;
			}

			/// <summary>GC allocations per call on the main thread.</summary>
			public double Allocations { get; }

			public override string ToString() => Allocations.ToString("F2", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Average GC allocations per call after one warm-up call, counted by the profiler's GC.Alloc marker on the
		/// main thread, as Unity's AllocatingGCMemory constraint counts them. (Byte counts are not available here: the
		/// editor's Mono reports neither GC.GetAllocatedBytesForCurrentThread nor small heap growth.)
		/// </summary>
		private static Garbage Measure(Action action, int calls)
		{
			action();
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			recorder.enabled = true;
			for (var i = 0; i < calls; i++)
			{
				action();
			}

			recorder.enabled = false;
			recorder.CollectFromAllThreads();
			return new Garbage(recorder.sampleBlockCount / (double)calls);
		}

		private static string Stats(List<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			double Percentile(double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * (sorted.Count - 1)))];
			return string.Format(CultureInfo.InvariantCulture, "{{ \"frames\": {0}, \"mean\": {1:F2}, \"p50\": {2:F2}, \"p95\": {3:F2}, \"max\": {4:F2} }}",
				sorted.Count, sorted.Average(), Percentile(0.5), Percentile(0.95), sorted[sorted.Count - 1]);
		}

		private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

		private T Track<T>(T item) where T : Object
		{
			_temporary.Add(item);
			return item;
		}

		/// <summary>The camera's view with the UI layer drawn over it (the UI renders to its own transparent target).</summary>
		private Texture2D Composite(RenderTexture ui)
		{
			var camera = Read(RenderCamera());
			var overlay = Read(ui);
			var cameraPixels = camera.GetPixels32();
			var uiPixels = overlay.GetPixels32();
			for (var i = 0; i < cameraPixels.Length; i++)
			{
				var top = uiPixels[i];
				var a = top.a / 255f;
				var under = cameraPixels[i];
				cameraPixels[i] = new Color32(
					(byte)Mathf.Clamp(top.r + under.r * (1f - a), 0f, 255f),
					(byte)Mathf.Clamp(top.g + under.g * (1f - a), 0f, 255f),
					(byte)Mathf.Clamp(top.b + under.b * (1f - a), 0f, 255f),
					255);
			}

			camera.SetPixels32(cameraPixels);
			camera.Apply();
			return camera;
		}

		private RenderTexture RenderCamera()
		{
			var target = Track(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
			var previous = _scene.Camera.targetTexture;
			try
			{
				_scene.Camera.targetTexture = target;
				_scene.Camera.Render();
			}
			finally
			{
				_scene.Camera.targetTexture = previous;
			}

			return target;
		}

		private Texture2D Read(RenderTexture source)
		{
			var previous = RenderTexture.active;
			var texture = Track(new Texture2D(source.width, source.height, TextureFormat.RGBA32, false));
			try
			{
				RenderTexture.active = source;
				texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
				texture.Apply();
			}
			finally
			{
				RenderTexture.active = previous;
			}

			return texture;
		}

		private static void Save(Texture2D texture, string name)
		{
			var path = Path.Combine(SliceScene.ArtifactDirectory, name);
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Debug.Log("Wrote " + path);
		}

		private static void AssertNotBlank(Texture2D texture, string what)
		{
			var seen = new HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
				if (seen.Count > 256)
				{
					return;
				}
			}

			Assert.Fail($"{what} looks blank: {seen.Count} colours");
		}

		/// <summary>Above the panel (its top is at 42% of the height) the world is drawn, not a flat clear colour.</summary>
		private static void AssertWorldAboveThePanel(Texture2D texture)
		{
			var pixels = texture.GetPixels32();
			var seen = new HashSet<int>();
			for (var y = (int)(0.45f * Height); y < Height; y++)
			{
				for (var x = 0; x < Width; x += 3)
				{
					var pixel = pixels[y * Width + x];
					seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
				}
			}

			Assert.That(seen.Count, Is.GreaterThan(256), "the world shows above the panel");
		}

		/// <summary>Hiding the subject changes pixels inside its projected rectangle: it is in frame and drawn.</summary>
		private void AssertDrawnWhereItProjects(Texture2D with, List<Renderer> renderers, string what)
		{
			Assert.That(renderers, Is.Not.Empty, what + " has no renderer");
			var bounds = renderers[0].bounds;
			renderers.ForEach(r => bounds.Encapsulate(r.bounds));
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			for (var corner = 0; corner < 8; corner++)
			{
				var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
				var viewport = _scene.Camera.WorldToViewportPoint(point);
				Assert.That(viewport.z, Is.GreaterThan(0f), what + " is behind the camera");
				minX = Mathf.Min(minX, viewport.x * Width);
				maxX = Mathf.Max(maxX, viewport.x * Width);
				minY = Mathf.Min(minY, viewport.y * Height);
				maxY = Mathf.Max(maxY, viewport.y * Height);
			}

			var left = Mathf.Clamp(Mathf.FloorToInt(minX), 0, Width - 1);
			var right = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, Width - 1);
			var bottom = Mathf.Clamp(Mathf.FloorToInt(minY), 0, Height - 1);
			var top = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, Height - 1);
			Assert.That(right > left && top > bottom, Is.True, $"{what} projects outside the frame");

			var states = renderers.Select(r => (Renderer: r, Enabled: r.enabled)).ToList();
			Texture2D without;
			try
			{
				states.ForEach(s => s.Renderer.enabled = false);
				without = Read(RenderCamera());
			}
			finally
			{
				states.ForEach(s => s.Renderer.enabled = s.Enabled);
			}

			var a = with.GetPixels32();
			var b = without.GetPixels32();
			var changed = 0;
			var total = 0;
			for (var y = bottom; y <= top; y++)
			{
				for (var x = left; x <= right; x++)
				{
					var p = a[y * Width + x];
					var q = b[y * Width + x];
					total++;
					if (Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b) > 15)
					{
						changed++;
					}
				}
			}

			Assert.That(changed, Is.GreaterThan(Math.Max(20, total / 20)), $"{what} is drawn where it projects ({changed} of {total} pixels change without it)");
		}
	}

}
