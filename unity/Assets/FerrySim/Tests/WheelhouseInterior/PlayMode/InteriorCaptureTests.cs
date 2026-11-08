using System.Collections;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.WheelhouseInterior.Tests
{
	/// <summary>GPU capture harness: coordinator runs on Romeo, never in the local targeted PlayMode pass.</summary>
	public class InteriorCaptureTests
	{
		[UnityTearDown]
		public IEnumerator UnloadCaptureScene()
		{
			var scene=SceneManager.GetSceneByName("LowerEastRiverSlice");
			if(scene.IsValid()&&scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}

		[UnityTest]
		public IEnumerator RealSlice_SeatedStanding_DayNight_BothClasses()
		{
			const string path="Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";
			var previous=SceneManager.GetActiveScene();
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Additive));
#else
			yield return SceneManager.LoadSceneAsync("LowerEastRiverSlice",LoadSceneMode.Additive);
#endif
			var scene=SceneManager.GetSceneByName("LowerEastRiverSlice");
			SceneManager.SetActiveScene(scene);
			try
			{
				yield return null;yield return null;
				var slice=Object.FindFirstObjectByType<SliceBootstrap>();Assert.That(slice,Is.Not.Null);Assert.That(slice.LoadError,Is.Null);
				slice.Runner.RecordRuns=false;
				var output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../artifacts/wheelhouse-interior/romeo"));Directory.CreateDirectory(output);
				foreach(var id in new[]{"k-class","t-class"})
				foreach(var night in new[]{false,true})
				{
					slice.Runner.Initialize(new ScenarioSettings { vesselFile="vessels/"+id+".json",startLatitude=40.7027,startLongitude=-74.0022,startHeadingDegrees=25,
						scenarioStartUtc=night?"2025-07-21T02:30:00Z":"2025-07-20T17:00:00Z",environment=new ScenarioEnvironment { calm=true } },new ConstantControls(ControlInputs.Neutral));
					slice.Runner.Pause();yield return null;
					var view=slice.Runner.GetComponentInChildren<WheelhouseControlsView>();Assert.That(view,Is.Not.Null);Assert.That(view.ClassId,Is.EqualTo(id));
					var mount=slice.CameraRig.WheelhouseMount;
					Assert.That(Vector3.Distance(mount.transform.position,view.transform.Find("Eye").position),Is.LessThan(.001),"real runtime call site prefers interior");
					foreach(var eyeName in new[]{"Eye","EyeStanding"})
					{
						mount.transform.position=view.transform.Find(eyeName).position;
						slice.CameraRig.CurrentMode=CameraRig.Mode.Wheelhouse;
						slice.CameraRig.Wheelhouse.HeadLook=new Vector2(0,42);slice.CameraRig.Wheelhouse.Snap();
						for(var f=0;f<4;f++) yield return null;
						Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,id+"-"+eyeName+"-"+(night?"night":"day")+".png"));
					}
				}
			}
			finally { if(previous.IsValid()&&previous.isLoaded) SceneManager.SetActiveScene(previous); }
			if(scene.IsValid()&&scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}

		private static void Capture(Camera camera,string file)
		{
			var target=new RenderTexture(1600,1000,24);var texture=new Texture2D(1600,1000,TextureFormat.RGB24,false);
			var oldTarget=camera.targetTexture;var active=RenderTexture.active;
			try
			{
				camera.targetTexture=target;camera.Render();RenderTexture.active=target;
				texture.ReadPixels(new Rect(0,0,1600,1000),0,0);texture.Apply();File.WriteAllBytes(file,texture.EncodeToPNG());
				Assert.That(new FileInfo(file).Length,Is.GreaterThan(10000),"nontrivial screenshot; still requires visual review");
			}
			finally { camera.targetTexture=oldTarget;RenderTexture.active=active;target.Release();Object.Destroy(target);Object.Destroy(texture); }
		}
	}
}
