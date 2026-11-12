using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>Review overlays on the real Harbor scene, not survey or start-safety evidence.</summary>
	public class LandingAdoptionCaptureTests
	{
		[Serializable] public class Point { public double north, east; public string name; }
		[Serializable] public class Stop { public string id, name; public Point[] before, after, slips; }
		[Serializable] public class Inputs { public string runtimeDigest; public Stop[] stops; }

		[UnityTearDown] public IEnumerator Unload() => HarborSceneFixture.Unload();

		[UnityTest, Explicit("Requires artifacts/landing-adopt/prepare-captures.py and the rebuilt harbor packs.")]
		public IEnumerator MovedStops_PlanViews_WithOriginalOutlineAndCrewSlipPoints()
		{
			HarborSceneFixture.RequireData();
			var folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/landing-adopt");
			var inputs = JsonUtility.FromJson<Inputs>(File.ReadAllText(Path.Combine(folder, "capture-inputs.json")));
			Assert.That(inputs.stops.Select(s => s.id), Is.EquivalentTo(new[] { "4", "18", "25", "89", "90", "112", "113", "115", "118", "138", "141" }));
			Directory.CreateDirectory(Path.Combine(folder, "captures"));
			yield return HarborSceneFixture.Load(consoleScreens: false);
			var bootstrap = HarborSceneFixture.Bootstrap();
			bootstrap.Runner.RecordRuns = false;
			var services = (HarborIntegrationServices)bootstrap.Services;
			Assert.That(services.Pack.Digest, Is.EqualTo(inputs.runtimeDigest), "capture overlays belong to this adopted pack");
			Assert.That(services.HarborCatalog.TryCreate(new ScenarioChoice("87", HarborSceneFixture.TrafficDay, 738, "t-class"), out var settings, out var problem), Is.True, problem);
			Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
			yield return HarborSceneFixture.WaitReady(bootstrap, "capture initialization");
			bootstrap.Runner.Pause();
			var geo = bootstrap.Context.Georeference;
			var camera = bootstrap.CameraRig.GetComponent<Camera>();
			// Hold the review camera above each stop; the scene's navigation and tiles stay live.
			foreach (var behaviour in camera.GetComponents<MonoBehaviour>()) behaviour.enabled = false;
			camera.orthographic = true;
			camera.orthographicSize = 65;
			camera.aspect = 1;
			camera.nearClipPlane = .1f;
			camera.farClipPlane = 1000;
			var material = new Material(Shader.Find("FerrySim/SolidColour"));
			try
			{
				foreach (var stop in inputs.stops)
				{
					var points = stop.before.Concat(stop.after).Concat(stop.slips).ToArray();
					var north = (points.Min(p => p.north) + points.Max(p => p.north)) / 2;
					var east = (points.Min(p => p.east) + points.Max(p => p.east)) / 2;
					var ll = LocalTangentPlane.World.ToGeodetic(north, east);
					geo.SetOriginLongitudeLatitudeHeight(ll.LongitudeDegrees, ll.LatitudeDegrees, 0);
					Assert.That(services.Pack.Geoid.TrySample(north, east, out var datum), Is.True);
					var center = Position(geo, north, east, datum);
					var up = (Position(geo, north, east, datum + 1) - center).normalized;
					var northward = (Position(geo, north + 1, east, datum) - center).normalized;
					camera.transform.SetPositionAndRotation(center + up * 180, Quaternion.LookRotation(-up, northward));
					var overlay = new GameObject("Landing adoption review overlay");
					var owned = new List<Material>();
					try
					{
						Draw(overlay, material, owned, geo, stop.before, datum + 8, new Color(1, .3f, .65f), .35f);
						Draw(overlay, material, owned, geo, stop.after, datum + 8, Color.cyan, .25f);
						foreach (var slip in stop.slips)
						{
							Draw(overlay, material, owned, geo, new[] { new Point { north = slip.north - 1.2, east = slip.east }, new Point { north = slip.north + 1.2, east = slip.east } }, datum + 9, Color.yellow, .3f);
							Draw(overlay, material, owned, geo, new[] { new Point { north = slip.north, east = slip.east - 1.2 }, new Point { north = slip.north, east = slip.east + 1.2 } }, datum + 9, Color.yellow, .3f);
						}
						for (var frame = 0; frame < 150; frame++) yield return null;
						var barge = services.Navigation.Barges.Single(b => b.Source.StopIds.Any(s => s.Text == stop.id));
						var planes = GeometryUtility.CalculateFrustumPlanes(camera);
						Assert.That(barge.GetComponentsInChildren<Renderer>().Any(r => r.enabled && r.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(planes, r.bounds)),
							Is.True, stop.id + ": the actual scene barge must render in the plan camera");
						Capture(camera, Path.Combine(folder, "captures", "stop-" + stop.id + ".png"));
						File.WriteAllText(Path.Combine(folder, "captures", "stop-" + stop.id + ".txt"),
							stop.id + " " + stop.name + "\nNorth up; 130 m square. Pink: original outline. Cyan: adopted outline. Yellow crosses: landed boat GPS.\n" +
							string.Join("\n", stop.slips.Select(p => p.name + ": " + p.north.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ", " + p.east.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) +
							"\nReal Harbor.unity scene, paused daylight run. Camera is a review viewpoint, not a berth start. Overlay heights are raised only to keep them visible.\n");
					}
					finally { Object.Destroy(overlay); foreach (var item in owned) Object.Destroy(item); }
					yield return null;
				}
			}
			finally { Object.Destroy(material); }
		}

		private static Vector3 Position(CesiumGeoreference geo, double north, double east, double height)
		{
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			return (Vector3)(float3)geo.TransformEarthCenteredEarthFixedPositionToUnity(geo.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, height)));
		}

		private static void Draw(GameObject parent, Material source, List<Material> owned, CesiumGeoreference geo, Point[] points, double height, Color color, float width)
		{
			var host = new GameObject("review line"); host.transform.SetParent(parent.transform);
			var material = new Material(source); material.SetColor("_BaseColor", color); owned.Add(material);
			var line = host.AddComponent<LineRenderer>(); line.sharedMaterial = material;
			line.widthMultiplier = width; line.positionCount = points.Length; line.useWorldSpace = true;
			line.SetPositions(points.Select(p => Position(geo, p.north, p.east, height)).ToArray());
		}

		private static void Capture(Camera camera, string filename)
		{
			var target = RenderTexture.GetTemporary(1200, 1200, 24, RenderTextureFormat.ARGB32);
			var previous = RenderTexture.active;
			var texture = new Texture2D(1200, 1200, TextureFormat.RGB24, false);
			try
			{
				camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
				texture.ReadPixels(new Rect(0, 0, 1200, 1200), 0, 0); texture.Apply();
				File.WriteAllBytes(filename, texture.EncodeToPNG());
			}
			finally { camera.targetTexture = null; RenderTexture.active = previous; Object.Destroy(texture); RenderTexture.ReleaseTemporary(target); }
		}
	}
}
