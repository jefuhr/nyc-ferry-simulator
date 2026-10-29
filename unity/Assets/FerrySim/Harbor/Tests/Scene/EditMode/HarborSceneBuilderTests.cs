using System.IO;
using System.Linq;
using CesiumForUnity;
using FerrySim.Controls;
using FerrySim.Harbor.EditorTools;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests
{
	public class HarborSceneBuilderTests
	{
		[TearDown] public void TearDown() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

		[Test]
		public void TwoBuildsHaveIdenticalBytes_OneOfEachService_AndNoSlicePayload()
		{
			var buildSettings = File.ReadAllBytes("ProjectSettings/EditorBuildSettings.asset");
			HarborSceneBuilder.Build();
			var first = File.ReadAllBytes(HarborSceneBuilder.ScenePath);
			HarborSceneBuilder.Build();
			Assert.That(File.ReadAllBytes(HarborSceneBuilder.ScenePath), Is.EqualTo(first), "scene bytes, not an object-count approximation");
			Assert.That(File.ReadAllBytes("ProjectSettings/EditorBuildSettings.asset"), Is.EqualTo(buildSettings), "packaging remains coordinator-owned");
			EditorSceneManager.OpenScene(HarborSceneBuilder.ScenePath);
			Assert.That(All<VesselSimulationRunner>().Length, Is.EqualTo(1));
			Assert.That(All<WheelhouseUI>().Length, Is.EqualTo(1));
			Assert.That(All<HelmInputController>().Length, Is.EqualTo(1));
			Assert.That(All<CameraRig>().Length, Is.EqualTo(1));
			Assert.That(All<Camera>().Length, Is.EqualTo(1));
			Assert.That(All<HarborBootstrap>().Length, Is.EqualTo(1));
			var configuration = HarborSceneConfiguration.Parse(File.ReadAllText(HarborSceneBuilder.ConfigurationPath));
			Assert.That(All<WheelhouseCameraMount>().Single().FieldOfView, Is.EqualTo(configuration.wheelhouseFieldOfView));
			Assert.That(All<Camera>().Single().farClipPlane, Is.EqualTo(configuration.cameraFarClipMetres));
			var reference = All<CesiumGeoreference>().Single();
			Assert.That(reference.latitude, Is.EqualTo(40.7));
			Assert.That(reference.longitude, Is.EqualTo(-74.015));
			Assert.That(reference.scale, Is.EqualTo(1));
			Assert.That(All<CesiumOriginShift>().Single().distance, Is.EqualTo(100));
			Assert.That(All<HarborTileset>().Length, Is.EqualTo(2));
			Assert.That(All<Cesium3DTileset>().Sum(layer => layer.maximumCachedBytes), Is.EqualTo(256L * 1024 * 1024));
			foreach (var layer in All<Cesium3DTileset>())
			{
				Assert.That(layer.createPhysicsMeshes, Is.False);
				Assert.That(layer.tilesetSource, Is.EqualTo(CesiumDataSource.FromUrl));
				Assert.That(layer.ionAccessToken, Is.Empty);
				Assert.That(layer.url, Is.Empty, "the checked scene has no machine-specific path");
				Assert.That(layer.opaqueMaterial.shader.name, Is.EqualTo("FerrySim/HarborSurface"));
			}
			foreach (var path in AssetDatabase.GetDependencies(HarborSceneBuilder.ScenePath, true))
			{
				Assert.That(path, Does.Not.Contain("World/Generated/"), path);
				Assert.That(path, Does.Not.Contain("World/Greybox/"), path);
				Assert.That(path, Does.Not.Contain("Slice/Scenes/"), path);
				Assert.That(path, Does.Not.Contain("LowerEastRiver"), path);
				Assert.That(path, Does.Not.Contain("lower-east-river"), path);
			}
			Assert.That(File.ReadAllText(HarborSceneBuilder.ScenePath), Does.Not.Contain("lower-east-river"));
		}

		private static T[] All<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
	}
}
