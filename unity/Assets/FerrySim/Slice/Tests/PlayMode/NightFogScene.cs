using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>Night and fog scenarios at the slice's places, and the parts of the scene that light them.</summary>
	internal static class NightFogScene
	{
		/// <summary>Sunday 20 July 2025, a scenario day; times UTC (EDT is UTC-4).</summary>
		public const string Day = "2025-07-20T17:00:00Z";

		/// <summary>20:45 EDT: about 20 minutes after sunset (20:24), the sun some 3 degrees down.</summary>
		public const string Dusk = "2025-07-21T00:45:00Z";

		/// <summary>22:30 EDT: night, the sun 15 degrees down.</summary>
		public const string Night = "2025-07-21T02:30:00Z";

		public const string Pier11StopId = "87";

		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ArtifactFolder(string name)
		{
			var path = Path.Combine(RepoRoot, "artifacts", "night-fog", name);
			Directory.CreateDirectory(path);
			return path;
		}

		public static Atmosphere Atmosphere()
		{
			var atmosphere = Object.FindFirstObjectByType<Atmosphere>();
			Assert.That(atmosphere, Is.Not.Null, "the scene's sun adds the atmosphere");
			return atmosphere;
		}

		/// <summary>A calm scenario at a world position (north, east, metres) and heading, at a UTC time, in a visibility.</summary>
		public static ScenarioSettings At(string name, double north, double east, double headingDegrees, string startUtc, double visibility, string vesselFile = "vessels/t-class.json")
		{
			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new ScenarioSettings
			{
				name = name,
				vesselFile = vesselFile,
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = headingDegrees,
				scenarioStartUtc = startUtc,
				environment = new ScenarioEnvironment { calm = true },
				visibilityMetres = visibility,
			};
		}

		/// <summary>Bow on to Pier 11's southern berth (the original night/fog evidence), <paramref name="bowClearance"/> metres off, at a time and visibility.</summary>
		public static ScenarioSettings OffPier11(SliceScene scene, string startUtc, double visibility, double bowClearance = 80.0)
		{
			var settings = LandingApproach.BowOn(scene.Landings.BerthFace(Pier11StopId, "enc:0226015B0512B198"), scene.Vessel, bowClearance, "night-fog, Pier 11", "vessels/t-class.json", startUtc);
			settings.visibilityMetres = visibility;
			return settings;
		}

		/// <summary>
		/// At the Brooklyn Bridge: in the middle of its channel, <paramref name="southOfSpan"/> metres down-river of the
		/// span's axis, heading up-river along the channel to pass under it.
		/// </summary>
		public static ScenarioSettings UnderBrooklynBridge(string startUtc, double visibility, double southOfSpan = 150.0)
		{
			var (middle, upRiver) = BrooklynBridgeChannel();
			var start = middle - upRiver * (float)southOfSpan;
			var heading = Math.Atan2(upRiver.x, upRiver.z) * 180.0 / Math.PI;
			return At("night-fog, under the Brooklyn Bridge", start.z, start.x, (heading + 360.0) % 360.0, startUtc, visibility);
		}

		/// <summary>The Brooklyn Bridge channel's middle in world (east, 0, north) metres, and the unit vector up-river along it.</summary>
		public static (Vector3 Middle, Vector3 UpRiver) BrooklynBridgeChannel()
		{
			var bridge = Object.FindObjectsByType<BridgeGreybox>(FindObjectsSortMode.InstanceID).Single(b => b.BridgeName == "Brooklyn Bridge");
			var frame = bridge.GetComponentInParent<WorldFrame>();
			Assert.That(frame, Is.Not.Null);
			var a = bridge.transform.Find("Tower A").localPosition;
			var b = bridge.transform.Find("Tower B").localPosition;

			// In the world frame's coordinates, x east and z north (World/README.md); the bridge's x runs along the channel.
			var middle = frame.transform.InverseTransformPoint(bridge.transform.TransformPoint(new Vector3(0f, 0f, 0.5f * (a.z + b.z))));
			var across = frame.transform.InverseTransformDirection(bridge.transform.TransformDirection(Vector3.right));
			across.y = 0f;
			var upRiver = across.z >= 0f ? across : -across;
			return (new Vector3(middle.x, 0f, middle.z), upRiver.normalized);
		}

		/// <summary>A camera of the given size rendering the scene from <paramref name="position"/> toward <paramref name="target"/>.</summary>
		public static Camera TestCamera(List<Object> owned, Vector3 position, Vector3 target, float fieldOfView = 30f)
		{
			var host = new GameObject("night-fog test camera");
			owned.Add(host);
			var camera = host.AddComponent<Camera>();
			camera.transform.position = position;
			camera.transform.LookAt(target);
			camera.fieldOfView = fieldOfView;
			camera.nearClipPlane = 0.5f;
			camera.farClipPlane = 20000f;
			camera.enabled = false;
			return camera;
		}

		/// <summary>Renders <paramref name="camera"/> to a texture of the given size and reads it back.</summary>
		public static Texture2D Render(List<Object> owned, Camera camera, int width, int height)
		{
			var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			owned.Add(target);
			var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
			owned.Add(texture);
			try
			{
				camera.targetTexture = target;
				camera.aspect = (float)width / height;
				camera.Render();
				RenderTexture.active = target;
				texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				texture.Apply();
			}
			finally
			{
				camera.targetTexture = previous;
				RenderTexture.active = active;
			}

			return texture;
		}

		/// <summary>The brightest pixel's colour within <paramref name="radius"/> pixels of where <paramref name="world"/> projects.</summary>
		public static Color Brightest(Texture2D texture, Camera camera, Vector3 world, int radius = 3)
		{
			var screen = camera.WorldToViewportPoint(world);
			Assert.That(screen.z, Is.GreaterThan(0f), "the point is in front of the camera");
			var cx = Mathf.RoundToInt(screen.x * texture.width);
			var cy = Mathf.RoundToInt(screen.y * texture.height);
			var best = Color.black;
			for (var y = cy - radius; y <= cy + radius; y++)
			{
				for (var x = cx - radius; x <= cx + radius; x++)
				{
					if (x < 0 || y < 0 || x >= texture.width || y >= texture.height)
					{
						continue;
					}

					var pixel = texture.GetPixel(x, y);
					if (pixel.r + pixel.g + pixel.b > best.r + best.g + best.b)
					{
						best = pixel;
					}
				}
			}

			return best;
		}
	}
}
