using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Sim;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.World.HarborNavigation.Tests.PlayMode
{
	/// <summary>Isolated navigation acceptance views, not the coordinator's integrated Harbor scene or a driven trip.</summary>
	public class NavigationCaptureTests
	{
		[UnityTest, Explicit("Local rendered acceptance needs copied terrain, skyline and runtime exports.")]
		public IEnumerator RealHarborDayNightCaptures()
		{
			var repository = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var folder = Path.Combine(repository, "artifacts/harbor-navigation/captures"); Directory.CreateDirectory(folder);
			var pack = HarborRuntimePackJson.Load(Path.Combine(repository, "data/processed/harbor/runtime"));
			var root = new GameObject("navigation capture fixture");
			var owned = new List<UnityEngine.Object>();
			var previousSky = RenderSettings.skybox;
			try
			{
				var geo = root.AddComponent<CesiumGeoreference>(); geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
				var skyHost = new GameObject("single sky"); skyHost.transform.SetParent(root.transform, false);
				var light = skyHost.AddComponent<Light>(); light.type = LightType.Directional;
				var sun = skyHost.AddComponent<SunController>(); sun.Configure(light, null);
				var skyMaterial = new Material(Shader.Find("FerrySim/Sky")); owned.Add(skyMaterial); RenderSettings.skybox = skyMaterial;
				var atmosphere = skyHost.AddComponent<Atmosphere>(); atmosphere.Configure(sun, null);
				var world = root.AddComponent<HarborNavigationWorld>(); world.Build(pack, geo, new DatumWater(pack), atmosphere);
				var cameraHost = new GameObject("Main Camera") { tag = "MainCamera" }; cameraHost.transform.SetParent(root.transform, false);
				var camera = cameraHost.AddComponent<Camera>(); camera.nearClipPlane = .3f; camera.farClipPlane = 30000; camera.fieldOfView = 55; camera.aspect = 16f / 9;
				var cameraAnchor = cameraHost.AddComponent<CesiumGlobeAnchor>(); cameraAnchor.detectTransformChanges = false; cameraAnchor.adjustOrientationForGlobeWhenMoving = false;
				var tilesMaterial = new Material(Shader.Find("FerrySim/DepthColour")); owned.Add(tilesMaterial);
				foreach (var name in new[] { "3dtiles", "3dtiles-buildings" })
				{
					var host = new GameObject(name); host.SetActive(false); host.transform.SetParent(root.transform, false);
					var tiles = host.AddComponent<Cesium3DTileset>(); tiles.tilesetSource = CesiumDataSource.FromUrl;
					tiles.url = new Uri(Path.Combine(repository, "data/processed/harbor", name, "tileset.json")).AbsoluteUri;
					tiles.createPhysicsMeshes = false; tiles.showCreditsOnScreen = false; tiles.maximumCachedBytes = 64 * 1024 * 1024; tiles.maximumScreenSpaceError = 8; tiles.opaqueMaterial = tilesMaterial;
					host.SetActive(true);
				}
				var waterMaterial = new Material(Shader.Find("FerrySim/SolidColour")); waterMaterial.SetColor("_BaseColor", new Color(.05f,.2f,.25f)); owned.Add(waterMaterial);
				var index = new StringBuilder("place,condition,stop_id,north_m,east_m,fixture\n");
				var places = new[] { ("pier11", "87", 351.06, 748.43), ("brooklyn-bridge", "", 653.944, 1555.4115), ("st-george", "137", -5986.89, -5009.97) };
				foreach (var place in places)
				{
					var ll = LocalTangentPlane.World.ToGeodetic(place.Item3, place.Item4); geo.SetOriginLongitudeLatitudeHeight(ll.LongitudeDegrees, ll.LatitudeDegrees, 0);
					Assert.That(pack.Geoid.TrySample(place.Item3, place.Item4, out var datum), Is.True);
					var waterHost = new GameObject("local datum water fixture"); waterHost.transform.SetParent(root.transform, false);
					new NavigationAnchor(waterHost, geo, place.Item3, place.Item4, datum);
					var mesh = new Mesh(); mesh.vertices = new[] { new Vector3(-3000,0,-3000),new Vector3(-3000,0,3000),new Vector3(3000,0,3000),new Vector3(3000,0,-3000) }; mesh.triangles = new[] {0,1,2,0,2,3}; mesh.RecalculateNormals(); owned.Add(mesh);
					waterHost.AddComponent<MeshFilter>().sharedMesh = mesh; waterHost.AddComponent<MeshRenderer>().sharedMaterial = waterMaterial;
					var eye = LocalTangentPlane.World.ToGeodetic(place.Item3 - 130, place.Item4 + 180);
					cameraAnchor.longitudeLatitudeHeight = new double3(eye.LongitudeDegrees, eye.LatitudeDegrees, datum + 35);
					var target = geo.TransformEarthCenteredEarthFixedPositionToUnity(geo.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees,ll.LatitudeDegrees,datum + 5)));
					camera.transform.LookAt((Vector3)(float3)target);
					// Wait for local tiles to settle; navigation itself is already resident.
					for (var frame = 0; frame < 150; frame++) yield return null;
					foreach (var night in new[] { false, true })
					{
						sun.SetUtc(new DateTime(2026, 6, 15, night ? 4 : 16, 0, 0, DateTimeKind.Utc)); sun.Apply();
						world.Tick(0, camera, place.Item2, night, 0);
						for (var frame = 0; frame < 4; frame++) yield return null;
						var targetTexture = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32); targetTexture.Create();
						var image = new Texture2D(1600, 900, TextureFormat.RGB24, false); var previous = RenderTexture.active;
						camera.targetTexture = targetTexture; camera.Render(); RenderTexture.active = targetTexture;
						image.ReadPixels(new Rect(0,0,1600,900),0,0); image.Apply(); RenderTexture.active = previous; camera.targetTexture = null;
						var condition = night ? "night" : "day"; File.WriteAllBytes(Path.Combine(folder, place.Item1 + "-" + condition + ".png"), image.EncodeToPNG());
						UnityEngine.Object.Destroy(image); targetTexture.Release(); UnityEngine.Object.Destroy(targetTexture);
						index.AppendLine($"{place.Item1},{condition},{place.Item2},{place.Item3},{place.Item4},navigation with local planar datum water");
					}
					UnityEngine.Object.Destroy(waterHost); yield return null;
				}
				File.WriteAllText(Path.Combine(folder, "index.csv"), index.ToString());
			}
			finally
			{
				UnityEngine.Object.Destroy(root); foreach (var item in owned) UnityEngine.Object.Destroy(item); RenderSettings.skybox = previousSky;
			}
			yield return null;
		}
		private sealed class DatumWater : IWaterSurface
		{
			private readonly HarborRuntimePack _pack;
			public DatumWater(HarborRuntimePack pack) { _pack = pack; }
			public double EllipsoidalHeight(double n, double e, double time) => _pack.Geoid.TrySample(n,e,out var h) ? h : throw new ArgumentException("No datum");
		}
	}
}
