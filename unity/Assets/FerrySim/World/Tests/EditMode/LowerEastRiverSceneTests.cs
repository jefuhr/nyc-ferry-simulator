using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// The saved scene is wired the way the rest of the simulator relies on. Failure modes guarded:
	/// a camera outside the georeference or without origin shifting (precision loss far from the
	/// origin), water without depth for its tint, water or structures on the wrong layer, the greybox
	/// outside the world frame (it would drift on an origin shift), and lighting left to a bake.
	/// </summary>
	public class LowerEastRiverSceneTests
	{
		private WorldSceneFixture _fixture;

		[SetUp]
		public void SetUp()
		{
			_fixture = new WorldSceneFixture();
			_fixture.Open();
		}

		[TearDown]
		public void TearDown() => _fixture.Close();

		[Test]
		public void MainCamera_ShiftsTheOriginAndRequestsDepth()
		{
			var rig = _fixture.Find<CameraRig>();
			var camera = rig.GetComponent<Camera>();

			Assert.That(camera.CompareTag("MainCamera"), Is.True);
			Assert.That(camera.GetComponentInParent<CesiumGeoreference>(), Is.Not.Null, "Cesium origin shifting needs the camera under the georeference");
			Assert.That(camera.GetComponent<CesiumGlobeAnchor>(), Is.Not.Null);
			Assert.That(camera.GetComponent<CesiumOriginShift>(), Is.Not.Null);
			Assert.That(camera.GetComponent<CesiumOriginShift>().distance, Is.LessThanOrEqualTo(250.0));
			Assert.That(camera.GetComponent<UniversalAdditionalCameraData>().requiresDepthTexture, Is.True, "the water's depth tint reads the camera depth texture");
			Assert.That(camera.farClipPlane, Is.GreaterThanOrEqualTo(20000f), "the far bridges and the horizon must not be clipped");
		}

		[Test]
		public void CameraRig_HasAllThreeViews()
		{
			var rig = _fixture.Find<CameraRig>();

			Assert.That(rig.FreeFly, Is.Not.Null);
			Assert.That(rig.Chase, Is.Not.Null);
			Assert.That(rig.Wheelhouse, Is.Not.Null);
			Assert.That(rig.WheelhouseMount, Is.SameAs(_fixture.Find<WheelhouseCameraMount>()));
			Assert.That(rig.CurrentMode, Is.EqualTo(CameraRig.Mode.FreeFly));
		}

		[Test]
		public void Water_IsInTheWorldFrameOnTheWaterLayer()
		{
			var water = _fixture.Find<WaterSurface>();

			Assert.That(water.transform.parent.GetComponent<WorldFrame>(), Is.Not.Null);
			Assert.That(water.gameObject.layer, Is.EqualTo(WorldLayers.Water));
			Assert.That(water.Datum, Is.Not.Null);
			Assert.That(water.Datum.IsPopulated, Is.True);
			Assert.That(water.GetComponent<MeshRenderer>().sharedMaterial.shader.name, Is.EqualTo("FerrySim/Water"));
			Assert.That(water.GetComponent<MeshRenderer>().shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
			Assert.That(water.WaterLevelNavd88, Is.EqualTo(0.0));
		}

		[Test]
		public void Greybox_IsInsideTheWorldFrame()
		{
			var frame = _fixture.Find<WorldFrame>();

			foreach (var barge in _fixture.FindAll<LandingBarge>())
			{
				Assert.That(barge.GetComponentInParent<WorldFrame>(), Is.SameAs(frame), barge.name);
			}

			foreach (var bridge in _fixture.FindAll<BridgeGreybox>())
			{
				Assert.That(bridge.GetComponentInParent<WorldFrame>(), Is.SameAs(frame), bridge.name);
			}

			Assert.That(_fixture.FindAll<LandingBarge>().Length, Is.GreaterThanOrEqualTo(11));
			Assert.That(_fixture.FindAll<BridgeGreybox>().Length, Is.EqualTo(3));
		}

		[Test]
		public void Sun_IsDirectionalAndDrivenByTheController()
		{
			var sun = _fixture.Find<SunController>();

			Assert.That(sun.SunLight, Is.Not.Null);
			Assert.That(sun.SunLight.type, Is.EqualTo(LightType.Directional));
			Assert.That(RenderSettings.sun, Is.SameAs(sun.SunLight));
			Assert.That(RenderSettings.skybox, Is.Not.Null);
			Assert.That(RenderSettings.skybox.shader.name, Is.EqualTo("FerrySim/Sky"));
			Assert.That(RenderSettings.fogColor, Is.EqualTo(sun.Sky.Horizon), "fog meets the sky at the horizon");
		}

		[Test]
		public void Lighting_IsRealtimeOnly()
		{
			var settings = UnityEditor.Lightmapping.lightingSettings;

			Assert.That(settings, Is.Not.Null);
			Assert.That(settings.bakedGI, Is.False);
			Assert.That(settings.realtimeGI, Is.False);
		}
	}
}
