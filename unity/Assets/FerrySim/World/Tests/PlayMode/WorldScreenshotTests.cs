using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace FerrySim.World.Tests.PlayMode
{
	/// <summary>
	/// Renders the slice from fixed viewpoints into artifacts/unity/world-*.png and checks each picture
	/// shows what it should: the subject is on screen and actually drawn there (a render without it
	/// differs at its projected position), the water fills the lower part of the frame, and dusk is
	/// darker than noon from the same viewpoint. Then measures frame time and writes
	/// artifacts/unity/world-performance.json. Needs graphics: run without -nographics.
	/// </summary>
	public class WorldScreenshotTests
	{
		private const int Width = 1280;
		private const int Height = 720;
		private const int Year = 2026;
		private const int Month = 6;
		private const int Day = 15;

		private WorldTestScene _scene;
		private readonly List<UnityEngine.Object> _temporaryObjects = new List<UnityEngine.Object>();

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new WorldTestScene();
			yield return _scene.Load();
			Directory.CreateDirectory(WorldTestScene.ArtifactDirectory);

			// Every capture and projection uses the screenshot's aspect ratio.
			_scene.Camera.aspect = (float)Width / Height;
		}

		[TearDown]
		public void TearDown()
		{
			foreach (var item in _temporaryObjects)
			{
				if (item != null) UnityEngine.Object.Destroy(item);
			}

			_temporaryObjects.Clear();
			if (_scene?.Camera != null)
			{
				_scene.Camera.ResetAspect();
			}
		}

		[UnityTest]
		public IEnumerator Pier11FromTheWater_AtNoon()
		{
			var pier = _scene.Barge("87");
			var faceMiddle = new Vector2((float)(0.5 * (pier.FaceStartEast + pier.FaceEndEast)), (float)(0.5 * (pier.FaceStartNorth + pier.FaceEndNorth)));
			var outward = new Vector2((float)pier.OutwardNormalEast, (float)pier.OutwardNormalNorth);
			var eye = faceMiddle + outward * 50f;
			_scene.Sun.SetLocalTime(Year, Month, Day, 12.0);
			yield return _scene.LookFrom(_scene.OnWater(eye.y, eye.x, 5f), pier.transform.localPosition + new Vector3(0f, 2f, 0f));

			var noon = Capture();
			Save(noon, "world-pier11-noon.png");
			AssertSubjectDrawn(noon, pier.transform.Find("Hull").gameObject, "Pier 11 barge hull");
			AssertSubjectDrawn(noon, pier.transform.Find("BerthingFace").gameObject, "Pier 11 berthing face");
			AssertWaterBelowHorizon(noon);
			Assert.That(_scene.Sun.Sun.ApparentElevationDegrees, Is.GreaterThan(60.0), "noon sun in mid-June");
			UnityEngine.Object.Destroy(noon);
		}

		[UnityTest]
		public IEnumerator BrooklynBridgeFromMidstream()
		{
			var bridge = _scene.Bridge("Brooklyn Bridge");
			var deck = bridge.transform.Find("Deck");
			var centre = new Vector2(bridge.transform.localPosition.x, bridge.transform.localPosition.z);
			var downstream = Downstream(bridge);
			var eye = centre + downstream * 450f;
			_scene.Sun.SetLocalTime(Year, Month, Day, 13.0);
			yield return _scene.LookFrom(_scene.OnWater(eye.y, eye.x, 4f), new Vector3(centre.x, deck.localPosition.y - 10f, centre.y));

			var image = Capture();
			Save(image, "world-brooklyn-bridge-midstream.png");
			AssertSubjectDrawn(image, deck.gameObject, "Brooklyn Bridge deck");
			AssertSubjectDrawn(image, bridge.transform.Find("Tower A").gameObject, "Brooklyn Bridge tower A");
			AssertSubjectDrawn(image, bridge.transform.Find("Tower B").gameObject, "Brooklyn Bridge tower B");

			AssertWaterBelowHorizon(image);
			UnityEngine.Object.Destroy(image);
		}

		[UnityTest]
		public IEnumerator BridgesFromUpstream_AtDusk()
		{
			var manhattan = _scene.Bridge("Manhattan Bridge");
			var deck = manhattan.transform.Find("Deck");
			var centre = new Vector2(manhattan.transform.localPosition.x, manhattan.transform.localPosition.z);
			var target = new Vector3(centre.x, deck.localPosition.y - 15f, centre.y);

			// Mid-channel at NOAA CO-OPS current station NYH1921 (Corlears Hook, 40.709499 N 73.976402 W),
			// looking west-southwest at the Manhattan Bridge with the Brooklyn Bridge behind it and the
			// sunset (azimuth about 302 deg) toward the right of the frame.
			var station = WorldFrame.LocalFromGeodetic(40.70949935913086, -73.97640228271484, 0.0);
			var eye = new Vector2(station.x, station.z);

			// About 15 minutes after sunset (20:30 EDT on 15 June 2026).
			_scene.Sun.SetLocalTime(Year, Month, Day, 20.75);
			yield return _scene.LookFrom(_scene.OnWater(eye.y, eye.x, 6f), target);
			var dusk = Capture();
			Save(dusk, "world-dusk.png");
			var elevation = _scene.Sun.Sun.ApparentElevationDegrees;
			Assert.That(elevation, Is.InRange(-6.0, 0.0), "dusk means civil twilight");
			Assert.That(_scene.Sun.SunLight.intensity, Is.EqualTo(0f), "no direct sun after sunset");
			AssertSubjectDrawn(dusk, deck.gameObject, "Manhattan Bridge deck at dusk");

			_scene.Sun.SetLocalTime(Year, Month, Day, 13.0);
			yield return null;
			var noon = Capture();
			Assert.That(MeanLuminance(dusk), Is.LessThan(0.6 * MeanLuminance(noon)), "dusk should be clearly darker than noon from the same viewpoint");
			UnityEngine.Object.Destroy(dusk);
			UnityEngine.Object.Destroy(noon);
		}

		[UnityTest]
		public IEnumerator WaterTint_ShowsShallowGroundAndHidesDeepGround()
		{
			// A grey slab under the water in front of the camera: 0.8 m down it must show through the
			// shallow tint; 30 m down the deep colour must hide it. Without the camera depth texture
			// (copied after opaques) every pixel would get the deep colour and the shallow case fails.
			var pier = _scene.Barge("87");
			var outward = new Vector2((float)pier.OutwardNormalEast, (float)pier.OutwardNormalNorth);
			var faceMiddle = new Vector2((float)(0.5 * (pier.FaceStartEast + pier.FaceEndEast)), (float)(0.5 * (pier.FaceStartNorth + pier.FaceEndNorth)));
			var eye = faceMiddle + outward * 120f;
			var spot = faceMiddle + outward * 95f;
			_scene.Sun.SetLocalTime(Year, Month, Day, 12.0);
			yield return _scene.LookFrom(_scene.OnWater(eye.y, eye.x, 5f), _scene.OnWater(spot.y, spot.x, 0f));

			var slab = CreateSlab();

			// Both slabs sit on the view ray through the image centre, so they cover the same pixels:
			// the deep one farther out and larger.
			var camera = _scene.OnWater(eye.y, eye.x, 5f);
			var target = _scene.OnWater(spot.y, spot.x, 0f);
			var ray = (target - camera).normalized;
			var changed = new Dictionary<float, float>();
			foreach (var depth in new[] { 0.8f, 30f })
			{
				var distance = (camera.y - (target.y - depth)) / -ray.y;
				var centre = camera + ray * distance;
				var size = 0.6f * distance;
				slab.transform.localPosition = centre - new Vector3(0f, 0.1f, 0f);
				slab.transform.localScale = new Vector3(size, 0.2f, size);
				slab.SetActive(true);
				yield return null;
				var with = Capture();
				if (depth < 1f)
				{
					Save(with, "world-water-depth-tint.png");
				}

				slab.SetActive(false);
				var without = Capture();
				changed[depth] = ChangedFractionOver(with, without, _scene.ToUnity(centre), 0.08f);
				UnityEngine.Object.Destroy(with);
				UnityEngine.Object.Destroy(without);
			}

			UnityEngine.Object.Destroy(slab);
			Debug.Log($"Water depth tint: {changed[0.8f]:P0} of pixels over the slab change at 0.8 m, {changed[30f]:P0} at 30 m");
			Assert.That(changed[0.8f], Is.GreaterThan(0.5f), "shallow ground should show through the water");
			Assert.That(changed[30f], Is.LessThan(0.1f), "deep ground should be hidden by the deep colour");
		}

		// Failure modes: failed readback must restore existing targets and destroy the partial image;
		// failed assertions must still release successful captures and temporary scene geometry.
		[UnityTest]
		public IEnumerator Capture_WhenReadbackFails_RestoresTargetsAndDestroysTexture()
		{
			var camera = _scene.Camera;
			var originalTarget = camera.targetTexture;
			var originalActive = RenderTexture.active;
			var previousTarget = new RenderTexture(16, 16, 0);
			var previousActive = new RenderTexture(16, 16, 0);
			Texture2D partial = null;
			RenderTexture temporary = null;
			try
			{
				previousActive.Create();
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				Assert.Throws<AssertionException>(() => Capture(texture =>
				{
					partial = texture;
					temporary = camera.targetTexture;
					Assert.Fail("forced readback failure");
				}));
				// Check render state before the next player-loop render changes the active target.
				var cameraRestored = camera.targetTexture == previousTarget;
				var activeRestored = RenderTexture.active == previousActive;
				yield return null; // Destroy is deferred in PlayMode.
				Assert.That(cameraRestored && activeRestored && partial == null,
					Is.True, $"cleanup after readback failure: camera restored={cameraRestored}, active restored={activeRestored}, partial image destroyed={partial == null}");
			}
			finally
			{
				// Keep the regression's deliberately broken baseline from poisoning later tests.
				var leaked = camera.targetTexture == temporary;
				camera.targetTexture = originalTarget;
				RenderTexture.active = originalActive;
				if (leaked && temporary != null) RenderTexture.ReleaseTemporary(temporary);
				if (partial != null) UnityEngine.Object.Destroy(partial);
				previousTarget.Release();
				previousActive.Release();
				UnityEngine.Object.Destroy(previousTarget);
				UnityEngine.Object.Destroy(previousActive);
			}
		}

		[UnityTest]
		public IEnumerator TearDown_WhenImageAssertionFails_DestroysCapturesAndSlab()
		{
			var slab = CreateSlab();
			var image = Capture();
			try
			{
				try
				{
					Assert.Throws<AssertionException>(() => Assert.Fail("forced screenshot assertion failure"));
				}
				finally
				{
					TearDown();
				}

				yield return null;
				Assert.That(image == null && slab == null, Is.True,
					$"cleanup after assertion failure: image destroyed={image == null}, slab destroyed={slab == null}");
			}
			finally
			{
				if (image != null) UnityEngine.Object.Destroy(image);
				if (slab != null) UnityEngine.Object.Destroy(slab);
			}
		}

		private GameObject CreateSlab()
		{
			var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
			_temporaryObjects.Add(slab);
			UnityEngine.Object.Destroy(slab.GetComponent<Collider>());
			slab.transform.SetParent(_scene.Frame.transform, false);
			return slab;
		}

		// Fraction of pixels within a square (side = fraction of image height) around a projected point that differ.
		private float ChangedFractionOver(Texture2D a, Texture2D b, Vector3 position, float size)
		{
			var viewport = _scene.Camera.WorldToViewportPoint(position);
			Assert.That(viewport.z, Is.GreaterThan(0f));
			var half = Mathf.RoundToInt(size * Height * 0.5f);
			var centreX = Mathf.RoundToInt(viewport.x * Width);
			var centreY = Mathf.RoundToInt(viewport.y * Height);
			var pixelsA = a.GetPixels32();
			var pixelsB = b.GetPixels32();
			var changed = 0;
			var total = 0;
			for (var y = Mathf.Max(0, centreY - half); y < Mathf.Min(Height, centreY + half); y++)
			{
				for (var x = Mathf.Max(0, centreX - half); x < Mathf.Min(Width, centreX + half); x++)
				{
					var p = pixelsA[y * Width + x];
					var q = pixelsB[y * Width + x];
					total++;
					if (Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b) > 15)
					{
						changed++;
					}
				}
			}

			Assert.That(total, Is.GreaterThan(100), "probe square is off screen");
			return (float)changed / total;
		}

		[UnityTest]
		public IEnumerator FrameTime_InTheSlice()
		{
			var report = new StringBuilder();
			report.Append("{\n");
			report.AppendFormat(CultureInfo.InvariantCulture, "\t\"machine\": \"{0}\",\n", Escape(SystemInfo.deviceModel + ", " + SystemInfo.processorType + ", " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")"));
			report.AppendFormat(CultureInfo.InvariantCulture, "\t\"unity\": \"{0}\",\n", Application.unityVersion);
			report.Append("\t\"context\": \"Unity editor, batchmode PlayMode test, URP, ").Append(Width * 3 / 2).Append('x').Append(Height * 3 / 2).Append(" render target, vSync off. A player build will be faster.\",\n");
			report.Append("\t\"viewpoints\": [\n");

			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			var previousTarget = _scene.Camera.targetTexture;
			var target = new RenderTexture(Width * 3 / 2, Height * 3 / 2, 24) { name = "PerformanceTarget" };
			var results = new List<(string Name, Stats Frame, Stats Render)>();
			var explicitRender = false;
			try
			{
				var pier = _scene.Barge("87");
				var brooklyn = _scene.Bridge("Brooklyn Bridge");
				var viewpoints = new (string Name, Vector3 Eye, Vector3 Target)[]
				{
					("pier11", _scene.OnWater(pier.FaceStartNorth + pier.OutwardNormalNorth * 75.0, pier.FaceStartEast + pier.OutwardNormalEast * 75.0, 6f), pier.transform.localPosition),
					("brooklyn-bridge", _scene.OnWater(brooklyn.transform.localPosition.z + Downstream(brooklyn).y * 450f, brooklyn.transform.localPosition.x + Downstream(brooklyn).x * 450f, 4f), brooklyn.transform.localPosition + new Vector3(0f, 30f, 0f)),
					("overview", _scene.OnWater(2400.0, 2100.0, 400f), new Vector3(2100f, -31f, 1200f)),
				};

				_scene.Sun.SetLocalTime(Year, Month, Day, 12.0);
				_scene.Camera.targetTexture = target;

				// Does the player loop render the camera in this (batchmode) editor? If not, render it
				// explicitly once per frame so the frame time includes rendering.
				var loopRenders = 0;
				Action<ScriptableRenderContext, Camera> counter = (context, camera) =>
				{
					if (camera == _scene.Camera)
					{
						loopRenders++;
					}
				};
				RenderPipelineManager.endCameraRendering += counter;
				try
				{
					for (var i = 0; i < 30; i++)
					{
						yield return null;
					}
				}
				finally
				{
					RenderPipelineManager.endCameraRendering -= counter;
				}
				explicitRender = loopRenders == 0;

				foreach (var viewpoint in viewpoints)
				{
					yield return _scene.LookFrom(viewpoint.Eye, viewpoint.Target);
					for (var i = 0; i < 30; i++)
					{
						yield return null;
					}

					// Pipelined frame time: the whole player loop plus one camera render, as the simulator would run.
					var frames = new List<double>();
					for (var i = 0; i < 240; i++)
					{
						if (explicitRender)
						{
							_scene.Camera.Render();
						}

						yield return null;
						frames.Add(Time.unscaledDeltaTime * 1000.0);
					}

					// Render time with the GPU waited on: one camera render plus a readback, serialised.
					var renders = new List<double>();
					var stopwatch = new Stopwatch();
					for (var i = 0; i < 40; i++)
					{
						stopwatch.Restart();
						_scene.Camera.Render();
						AsyncGPUReadback.Request(target, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
						stopwatch.Stop();
						renders.Add(stopwatch.Elapsed.TotalMilliseconds);
						yield return null;
					}

					results.Add((viewpoint.Name, Stats.Of(frames), Stats.Of(renders)));
				}
			}
			finally
			{
				_scene.Camera.targetTexture = previousTarget;
				target.Release();
				UnityEngine.Object.Destroy(target);
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}

			for (var i = 0; i < results.Count; i++)
			{
				var (name, frame, render) = results[i];
				report.AppendFormat(CultureInfo.InvariantCulture, "\t\t{{ \"name\": \"{0}\", \"frame_ms\": {1}, \"render_with_gpu_wait_ms\": {2} }}{3}\n", name, frame.ToJson(), render.ToJson(), i < results.Count - 1 ? "," : "");
				Debug.Log($"World performance, {name}: frame {frame}; render with GPU wait {render}");
			}

			report.Append("\t],\n");
			report.AppendFormat(CultureInfo.InvariantCulture, "\t\"camera_rendered_by\": \"{0}\"\n", explicitRender ? "explicit Camera.Render each frame (the batchmode player loop does not render it)" : "the player loop");
			report.Append("}\n");
			File.WriteAllText(Path.Combine(WorldTestScene.ArtifactDirectory, "world-performance.json"), report.ToString());
			Assert.That(results.Count, Is.EqualTo(3));
			Assert.That(results.All(r => r.Frame.Mean > 0.0 && r.Render.Mean > 0.0), Is.True);
		}

		// Perpendicular to the bridge span, pointing south-west: downstream, toward the harbour.
		private static Vector2 Downstream(BridgeGreybox bridge)
		{
			var span = bridge.transform.localRotation * Vector3.forward;
			var across = new Vector2(span.z, -span.x);
			return across.y > 0f ? -across : across;
		}

		private Texture2D Capture(Action<Texture2D> beforeReadback = null)
		{
			var camera = _scene.Camera;
			var target = RenderTexture.GetTemporary(Width, Height, 24);
			var previousTarget = camera.targetTexture;
			var previousActive = RenderTexture.active;
			Texture2D texture = null;
			try
			{
				camera.targetTexture = target;
				camera.Render();
				RenderTexture.active = target;
				texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
				_temporaryObjects.Add(texture);
				beforeReadback?.Invoke(texture);
				texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
				texture.Apply();
				return texture;
			}
			catch
			{
				if (texture != null) UnityEngine.Object.Destroy(texture);
				throw;
			}
			finally
			{
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				RenderTexture.ReleaseTemporary(target);
			}
		}

		private Texture2D CaptureWithout(IEnumerable<Renderer> renderers)
		{
			var states = renderers.Select(renderer => (Renderer: renderer, Enabled: renderer.enabled)).ToList();
			try
			{
				states.ForEach(state => state.Renderer.enabled = false);
				return Capture();
			}
			finally
			{
				states.ForEach(state => state.Renderer.enabled = state.Enabled);
			}
		}

		private static void Save(Texture2D texture, string name)
		{
			var path = Path.Combine(WorldTestScene.ArtifactDirectory, name);
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Debug.Log("Wrote " + path);
		}

		/// <summary>
		/// The subject is in frame and drawn where it projects: hiding it changes enough pixels inside its
		/// projected bounding rectangle, and few outside it.
		/// </summary>
		private void AssertSubjectDrawn(Texture2D with, GameObject subject, string what)
		{
			var renderers = subject.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToList();
			Assert.That(renderers, Is.Not.Empty, what + " has no visible renderer");
			var bounds = renderers[0].bounds;
			renderers.ForEach(r => bounds.Encapsulate(r.bounds));

			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			for (var corner = 0; corner < 8; corner++)
			{
				var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
				var viewport = _scene.Camera.WorldToViewportPoint(point);
				Assert.That(viewport.z, Is.GreaterThan(0f), what + " is partly behind the camera");
				minX = Mathf.Min(minX, viewport.x * Width);
				maxX = Mathf.Max(maxX, viewport.x * Width);
				minY = Mathf.Min(minY, viewport.y * Height);
				maxY = Mathf.Max(maxY, viewport.y * Height);
			}

			const int margin = 2;
			var left = Mathf.Max(0, Mathf.FloorToInt(minX) - margin);
			var right = Mathf.Min(Width - 1, Mathf.CeilToInt(maxX) + margin);
			var bottom = Mathf.Max(0, Mathf.FloorToInt(minY) - margin);
			var top = Mathf.Min(Height - 1, Mathf.CeilToInt(maxY) + margin);
			Assert.That(right > left && top > bottom, Is.True, $"{what} projects outside the frame ({minX:F0}..{maxX:F0}, {minY:F0}..{maxY:F0})");

			var without = CaptureWithout(renderers);

			var withPixels = with.GetPixels32();
			var withoutPixels = without.GetPixels32();
			var inside = 0;
			var outside = 0;
			for (var y = 0; y < Height; y++)
			{
				for (var x = 0; x < Width; x++)
				{
					// GetPixels32 rows run bottom to top, like viewport y. 0.06 in 0..1 per channel sum is 15 in bytes.
					var a = withPixels[y * Width + x];
					var b = withoutPixels[y * Width + x];
					if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) <= 15)
					{
						continue;
					}

					if (x >= left && x <= right && y >= bottom && y <= top)
					{
						inside++;
					}
					else
					{
						outside++;
					}
				}
			}

			UnityEngine.Object.Destroy(without);
			Assert.That(inside, Is.GreaterThanOrEqualTo(20), $"{what} is not visibly drawn ({inside} pixels change inside its projected rectangle)");
			Assert.That(inside, Is.GreaterThanOrEqualTo(4 * outside), $"{what} changes pixels away from where it projects ({inside} inside, {outside} outside)");
		}

		/// <summary>The lower third of the frame is water (and the greybox), not sky: the water is at a level the camera sits just above.</summary>
		private void AssertWaterBelowHorizon(Texture2D with)
		{
			var renderer = _scene.Water.GetComponent<MeshRenderer>();
			var without = CaptureWithout(new[] { renderer });
			var changed = 0;
			var total = 0;
			for (var y = 4; y < Height / 3; y += 8)
			{
				for (var x = 4; x < Width; x += 8)
				{
					total++;
					var a = with.GetPixel(x, y);
					var b = without.GetPixel(x, y);
					if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.06f)
					{
						changed++;
					}
				}
			}

			UnityEngine.Object.Destroy(without);
			Assert.That(changed, Is.GreaterThan(total / 2), $"water does not cover the lower third of the frame ({changed} of {total} samples)");
		}

		private static double MeanLuminance(Texture2D texture)
		{
			var pixels = texture.GetPixels32();
			double sum = 0.0;
			foreach (var pixel in pixels)
			{
				sum += 0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b;
			}

			return sum / pixels.Length / 255.0;
		}

		private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

		private readonly struct Stats
		{
			private Stats(double mean, double median, double p95, double max, int count)
			{
				Mean = mean;
				Median = median;
				P95 = p95;
				Max = max;
				Count = count;
			}

			public double Mean { get; }

			public double Median { get; }

			public double P95 { get; }

			public double Max { get; }

			public int Count { get; }

			public static Stats Of(List<double> samples)
			{
				var sorted = samples.OrderBy(s => s).ToList();
				return new Stats(sorted.Average(), sorted[sorted.Count / 2], sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1], sorted[sorted.Count - 1], sorted.Count);
			}

			public string ToJson() => string.Format(CultureInfo.InvariantCulture, "{{ \"mean\": {0:F2}, \"median\": {1:F2}, \"p95\": {2:F2}, \"max\": {3:F2}, \"samples\": {4} }}", Mean, Median, P95, Max, Count);

			public override string ToString() => string.Format(CultureInfo.InvariantCulture, "mean {0:F2} ms, median {1:F2}, p95 {2:F2}, max {3:F2} ({4} samples)", Mean, Median, P95, Max, Count);
		}
	}
}
