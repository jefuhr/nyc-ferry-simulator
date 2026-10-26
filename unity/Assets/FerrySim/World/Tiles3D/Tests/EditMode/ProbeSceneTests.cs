using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World.Tiles3D.EditorTools;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The saved probe scene. Failure modes guarded: a georeference away from the shared world origin or scaled; a tileset
	/// that would try Cesium ion, carry a token, cook physics meshes, keep the default 512 MB cache, or load before its
	/// file:// URL is set; a camera Cesium does not select tiles for; a probe that lost its wiring.
	/// </summary>
	public sealed class ProbeSceneTests
	{
		[OneTimeSetUp]
		public void Open()
		{
			EditorSceneManager.OpenScene(ProbeSceneBuilder.ScenePath, OpenSceneMode.Single);
		}

		[Test]
		public void TheGeoreferenceIsTheWorldOrigin()
		{
			var georeference = Object.FindObjectsByType<CesiumGeoreference>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single();

			Assert.That(georeference.latitude, Is.EqualTo(WorldOrigin.LatitudeDegrees).Within(1e-12));
			Assert.That(georeference.longitude, Is.EqualTo(WorldOrigin.LongitudeDegrees).Within(1e-12));
			Assert.That(georeference.height, Is.EqualTo(0.0));
			Assert.That(georeference.scale, Is.EqualTo(1.0));
		}

		[Test]
		public void TheTilesetIsLocalTokenFreeCappedAndWaitsForItsUrl()
		{
			var tileset = Object.FindObjectsByType<Cesium3DTileset>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single();

			Assert.That(tileset.gameObject.activeSelf, Is.False, "the probe enables it once its URL is set");
			Assert.That(tileset.tilesetSource, Is.EqualTo(CesiumDataSource.FromUrl));
			Assert.That(tileset.url, Is.Empty);
			Assert.That(tileset.ionAccessToken, Is.Empty);
			Assert.That(tileset.createPhysicsMeshes, Is.False);
			Assert.That(tileset.maximumCachedBytes, Is.EqualTo(TilesetProbe.MaximumCachedBytes));
			Assert.That(tileset.maximumCachedBytes, Is.LessThan(512L * 1024 * 1024));
			Assert.That(tileset.GetComponentInParent<CesiumGeoreference>(true), Is.Not.Null);
		}

		[Test]
		public void TheCameraIsTheMainCameraWithOriginShifting()
		{
			var camera = Camera.main;
			Assert.That(camera, Is.Not.Null);
			Assert.That(camera.GetComponent<CesiumGlobeAnchor>(), Is.Not.Null);
			Assert.That(camera.GetComponent<CesiumOriginShift>().distance, Is.EqualTo(ProbeSceneBuilder.OriginShiftDistance));
			Assert.That(camera.farClipPlane, Is.GreaterThanOrEqualTo(25000f));
		}

		[Test]
		public void TheProbeIsWired()
		{
			var probe = Object.FindObjectsByType<TilesetProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single();

			Assert.That(probe.Tileset, Is.Not.Null);
			Assert.That(probe.CameraAnchor, Is.SameAs(Camera.main.GetComponent<CesiumGlobeAnchor>()));
			Assert.That(probe.TileShader, Is.Not.Null);
			Assert.That(probe.TileShader.name, Is.EqualTo(ProbeSceneBuilder.ShaderName));
		}
	}
}
