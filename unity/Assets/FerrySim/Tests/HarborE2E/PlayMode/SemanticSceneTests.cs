using System;
using System.Collections;
using CesiumForUnity;
using FerrySim.Core.World;
using FerrySim.Harbor;
using FerrySim.World.HarborTiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FerrySim.HarborE2E.Tests.PlayMode
{
	public class SemanticSceneTests
	{
		private Scene _scene;
		[UnityTearDown] public IEnumerator Unload()
		{
			if (_scene.IsValid() && _scene.isLoaded)
			{
				foreach (var root in _scene.GetRootGameObjects()) foreach (var runner in root.GetComponentsInChildren<FerrySim.Sim.VesselSimulationRunner>(true)) runner.EndRun();
				yield return SceneManager.UnloadSceneAsync(_scene);
			}
		}
		[UnityTest, Explicit("Real catalog and scene, expected near-field landing renderer at all offered berths")]
		public IEnumerator OfferedStartsHaveAnEnabledLandingInTheActualCameraFrustum()
		{
			yield return SceneManager.LoadSceneAsync("Assets/FerrySim/Harbor/Scenes/Harbor.unity",LoadSceneMode.Additive);
			_scene=SceneManager.GetSceneByPath("Assets/FerrySim/Harbor/Scenes/Harbor.unity");
			HarborBootstrap bootstrap=null;
			foreach(var root in _scene.GetRootGameObjects()) {var found=root.GetComponentInChildren<HarborBootstrap>(true);if(found!=null)bootstrap=found;}
			while(bootstrap.Services==null && bootstrap.Problem==null)yield return null;
			Assert.That(bootstrap.Problem,Is.Null);
			var services=(HarborIntegrationServices)bootstrap.Services;
			var camera=bootstrap.CameraRig.GetComponentInChildren<Camera>();camera.aspect=1920f/1080f;
			foreach(var landing in services.HarborCatalog.Landings)
			foreach(var berth in landing.Berths)
			foreach(var vessel in services.HarborCatalog.Vessels)
			{
				var choice=new FerrySim.Sim.ScenarioChoice(landing.StopId,"2025-07-16",738,vessel.Id,berth.Id);
				if(!services.HarborCatalog.TryCreate(choice,out var settings,out var problem))continue;
				Assert.That(bootstrap.TryStart(settings,out problem),Is.True,problem);
				bootstrap.WheelhouseUI.View.PickerVisible=false;bootstrap.WheelhouseUI.View.DebriefVisible=false;
				while(bootstrap.ViewState!=HarborViewState.Ready && bootstrap.Problem==null)yield return null;
				bootstrap.Runner.Pause();
				var planes=GeometryUtility.CalculateFrustumPlanes(camera);var visible=false;
				foreach(var barge in services.Navigation.Barges)
				{
					var matches=false;foreach(var id in barge.Source.StopIds)if(id.Text==landing.StopId)matches=true;
					if(!matches)continue;
					foreach(var renderer in barge.GetComponentsInChildren<Renderer>())
						visible|=renderer.enabled && renderer.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(planes,renderer.bounds);
				}
				Assert.That(visible,Is.True,landing.Name+" "+berth.Name+" "+vessel.Id+": expected landing must be in the selected view");
			}
		}

		[UnityTest, Explicit("Real harbor packs; verifies renderer and floating-origin independence in the actual scene")]
		public IEnumerator QueriesDoNotChangeWhenTilesCameraAndOriginChange()
		{
			yield return SceneManager.LoadSceneAsync("Assets/FerrySim/Harbor/Scenes/Harbor.unity",LoadSceneMode.Additive);
			_scene=SceneManager.GetSceneByPath("Assets/FerrySim/Harbor/Scenes/Harbor.unity");
			HarborBootstrap bootstrap=null;
			foreach (var root in _scene.GetRootGameObjects()) {var found=root.GetComponentInChildren<HarborBootstrap>(true);if(found!=null)bootstrap=found;}
			var until=Time.realtimeSinceStartupAsDouble+120;
			while (bootstrap.Services==null && bootstrap.Problem==null && Time.realtimeSinceStartupAsDouble<until) yield return null;
			Assert.That(bootstrap.Problem,Is.Null);
			var services=(HarborIntegrationServices)bootstrap.Services;
			var a=new HarborFootprint[services.Pack.Footprints.Count];var b=new HarborFootprint[a.Length];
			var coordinates=new double[,] {{390,980},{4000,4000},{8000,6400},{9168,7847},{11584,14032},{-5850,-4700}};
			var geo=bootstrap.Context.Georeference;
			for(var p=0;p<coordinates.GetLength(0);p++)
			{
				var count=services.Navigation.Queries.Query(coordinates[p,0],coordinates[p,1],200,a,out var saturated);
				Assert.That(saturated,Is.False);
				foreach(var root in _scene.GetRootGameObjects()) foreach(var layer in root.GetComponentsInChildren<HarborTileset>(true))layer.Tileset.enabled=false;
				bootstrap.CameraRig.transform.Rotate(0,180,0);
				geo.SetOriginLongitudeLatitudeHeight(-73.95,40.77,0);
				yield return null;
				var changed=services.Navigation.Queries.Query(coordinates[p,0],coordinates[p,1],200,b,out saturated);
				Assert.That(saturated,Is.False);Assert.That(changed,Is.EqualTo(count));
				for(var i=0;i<count;i++)Assert.That(b[i].Id,Is.EqualTo(a[i].Id));
			}
		}
	}
}
