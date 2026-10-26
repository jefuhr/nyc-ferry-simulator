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
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// Night and fog scenes for the crew, and what the lights cost. Writes under artifacts/night-fog/:
	/// <list type="bullet">
	/// <item><c>captures/&lt;place&gt;-&lt;condition&gt;-&lt;view&gt;.png</c>: at Pier 11 (bow on to the landing, 80 m off) and at
	/// the Brooklyn Bridge (mid-channel, 150 m down-river of the span, heading up-river to pass under it), in daytime
	/// clear, dusk, night clear, and night in fog of 500 m and 200 m, from the wheelhouse (with the panel) and the chase
	/// camera;</item>
	/// <item><c>frame-cost.json</c>: frame time under the bridge at night with every light, in its fog, and by day as the
	/// control, in batchmode.</item>
	/// </list>
	/// Needs graphics: run without -nographics.
	/// </summary>
	public class NightFogEvidenceTests
	{
		private const int Width = 1600;
		private const int Height = 900;

		private static readonly (string Name, string Utc, double Visibility)[] Conditions =
		{
			("day-clear", NightFogScene.Day, MeteorologicalVisibility.Unlimited),
			("dusk-clear", NightFogScene.Dusk, MeteorologicalVisibility.Unlimited),
			("night-clear", NightFogScene.Night, MeteorologicalVisibility.Unlimited),
			("night-fog-500m", NightFogScene.Night, 500.0),
			("night-fog-200m", NightFogScene.Night, 200.0),
		};

		private SliceScene _scene;
		private readonly List<Object> _owned = new List<Object>();

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
			foreach (var item in _owned)
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

			_owned.Clear();
			if (_scene?.Camera != null)
			{
				_scene.Camera.ResetAspect();
			}

			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator Captures_AtPier11AndUnderTheBrooklynBridge_ByDayAtDuskAtNightAndInFog()
		{
			var folder = NightFogScene.ArtifactFolder("captures");
			var uiTarget = Own(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
			uiTarget.Create();
			var uiSettings = Own(WheelhousePanelSettings.Create(uiTarget, new Color(0f, 0f, 0f, 0f)));
			_scene.UI.enabled = false;
			_scene.UI.Configure(_scene.Runner, uiSettings);
			_scene.UI.enabled = true;
			_scene.Camera.aspect = (float)Width / Height;
			_scene.StepsPerFrame(1);

			var index = new StringBuilder();
			index.AppendLine("place,condition,view,file,local_time,sun_elevation_deg,visibility_m,lights_shown,panel_palette");
			foreach (var place in new[] { "pier11", "brooklyn-bridge" })
			{
				foreach (var condition in Conditions)
				{
					var settings = place == "pier11"
						? NightFogScene.OffPier11(_scene, condition.Utc, condition.Visibility)
						: NightFogScene.UnderBrooklynBridge(condition.Utc, condition.Visibility);
					_scene.Restart(settings, new ConstantControls(ControlInputs.Neutral));
					yield return SliceScene.Frames(10);
					Assert.That(_scene.Bootstrap.StartRefusal, Is.Null, $"{place} {condition.Name} starts clear of the faces");
					var atmosphere = NightFogScene.Atmosphere();

					foreach (var view in new[] { CameraRig.Mode.Wheelhouse, CameraRig.Mode.Chase })
					{
						_scene.Rig.CurrentMode = view;
						if (view == CameraRig.Mode.Chase)
						{
							_scene.Rig.Chase.Distance = 40f;
							_scene.Rig.Chase.Height = 12f;
							_scene.Rig.Chase.Snap();
						}

						yield return SliceScene.Frames(5);
						var name = $"{place}-{condition.Name}-{(view == CameraRig.Mode.Wheelhouse ? "wheelhouse" : "chase")}.png";
						var image = Composite(uiTarget, view == CameraRig.Mode.Wheelhouse);
						File.WriteAllBytes(Path.Combine(folder, name), image.EncodeToPNG());
						AssertNotBlank(image, name);
						var local = FerrySim.World.NewYorkTime.ToLocal(atmosphere.Sun.UtcTime);
						index.AppendLine(string.Join(",", place, condition.Name, view == CameraRig.Mode.Wheelhouse ? "wheelhouse" : "chase", name,
							local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), atmosphere.Sun.Sun.ElevationDegrees.ToString("F1", CultureInfo.InvariantCulture),
							condition.Visibility.ToString(CultureInfo.InvariantCulture), atmosphere.LightsShown ? "yes" : "no", _scene.View.Palette));
					}
				}
			}

			File.WriteAllText(Path.Combine(folder, "index.csv"), index.ToString());
			Debug.Log($"Night and fog captures in {folder}:\n{index}");
		}

		[UnityTest]
		public IEnumerator FrameCost_AtNightWithEveryLight_AgainstADaytimeControl()
		{
			var report = new StringBuilder();
			var invariant = CultureInfo.InvariantCulture;
			var target = Own(new RenderTexture(Width, Height, 24) { name = "night-fog frame cost" });
			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			_scene.Camera.targetTexture = target;
			_scene.Camera.aspect = (float)Width / Height;
			report.Append("{\n");
			report.AppendFormat(invariant, "\t\"machine\": \"{0}\",\n", Escape(SystemInfo.deviceModel + ", " + SystemInfo.processorType + ", " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"));
			report.AppendFormat(invariant, "\t\"unity\": \"{0}\",\n", Application.unityVersion);
			report.AppendFormat(invariant, "\t\"context\": \"Unity editor, batchmode PlayMode test, URP, {0}x{1} render target, vSync off, wheelhouse view, T-class at rest mid-channel 150 m down-river of the Brooklyn Bridge heading up-river, calm. The night runs show every light: the vessel's 4, 18 bridge lights and the aids' points; the day run shows none. For information; a player build will be faster, and this Mac stalls a frame once a minute by itself (compare the medians).\",\n", Width, Height);
			try
			{
				SliceScene.ResetTime();
				_scene.Rig.CurrentMode = CameraRig.Mode.Wheelhouse;
				var runs = new[] { ("day_clear_control", NightFogScene.Day, 0.0), ("night_clear_every_light", NightFogScene.Night, 0.0), ("night_fog_200m_every_light", NightFogScene.Night, 200.0) };
				for (var r = 0; r < runs.Length; r++)
				{
					var (label, utc, visibility) = runs[r];
					_scene.Restart(NightFogScene.UnderBrooklynBridge(utc, visibility), new ConstantControls(ControlInputs.Neutral));
					var loopRenders = 0;
					Action<ScriptableRenderContext, Camera> counter = (context, camera) => loopRenders += camera == _scene.Camera ? 1 : 0;
					RenderPipelineManager.endCameraRendering += counter;
					yield return SliceScene.Frames(30);
					RenderPipelineManager.endCameraRendering -= counter;
					var explicitRender = loopRenders == 0;
					var atmosphere = NightFogScene.Atmosphere();
					var lit = atmosphere.OwnLights.Lights.Count(l => l.On) + atmosphere.Bridges.Sum(b => b.Lights.Count(l => l.On)) + atmosphere.Aids.Points.Count(p => p.On);
					var frames = new List<double>();
					for (var i = 0; i < 300; i++)
					{
						if (explicitRender)
						{
							_scene.Camera.Render();
						}

						yield return null;
						frames.Add(Time.unscaledDeltaTime * 1000.0);
					}

					var renders = new List<double>();
					var stopwatch = new System.Diagnostics.Stopwatch();
					for (var i = 0; i < 60; i++)
					{
						stopwatch.Restart();
						_scene.Camera.Render();
						AsyncGPUReadback.Request(target, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
						stopwatch.Stop();
						renders.Add(stopwatch.Elapsed.TotalMilliseconds);
						yield return null;
					}

					report.AppendFormat(invariant, "\t\"{0}\": {{ \"lights_on\": {1}, \"lights_shown\": {2}, \"explicit_render\": {3}, \"frame_ms\": {4}, \"render_with_gpu_wait_ms\": {5} }}{6}\n",
						label, lit, atmosphere.LightsShown ? "true" : "false", explicitRender ? "true" : "false", Stats(frames), Stats(renders), r + 1 < runs.Length ? "," : string.Empty);
				}
			}
			finally
			{
				_scene.Camera.targetTexture = null;
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}

			report.Append("}\n");
			var path = Path.Combine(NightFogScene.ArtifactFolder(string.Empty), "frame-cost.json");
			File.WriteAllText(path, report.ToString());
			Debug.Log($"Night and fog frame cost ({path}):\n{report}");
		}

		private T Own<T>(T item) where T : Object
		{
			_owned.Add(item);
			return item;
		}

		/// <summary>The camera's view, with the UI layer drawn over it when <paramref name="withUi"/>.</summary>
		private Texture2D Composite(RenderTexture ui, bool withUi)
		{
			var camera = Read(RenderCamera());
			if (!withUi)
			{
				return camera;
			}

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
			var target = Own(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
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
			var texture = Own(new Texture2D(source.width, source.height, TextureFormat.RGBA32, false));
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

		private static void AssertNotBlank(Texture2D texture, string what)
		{
			var seen = new HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
				if (seen.Count > 64)
				{
					return;
				}
			}

			Assert.Fail($"{what} looks blank: {seen.Count} colours");
		}

		private static string Stats(List<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			double Percentile(double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * (sorted.Count - 1)))];
			return string.Format(CultureInfo.InvariantCulture, "{{ \"frames\": {0}, \"mean\": {1:F2}, \"p50\": {2:F2}, \"p95\": {3:F2}, \"max\": {4:F2} }}",
				sorted.Count, sorted.Average(), Percentile(0.5), Percentile(0.95), sorted[sorted.Count - 1]);
		}

		private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
