using System;
using System.Collections;
using FerrySim.Core.Instruments;
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
	/// <summary>Real slice with a frozen instrument fixture for repeatable control and lamp presentation captures.</summary>
	public class InteriorCaptureTests
	{
		[UnityTearDown]
		public IEnumerator UnloadCaptureScene()
		{
			var scene=SceneManager.GetSceneByName("LowerEastRiverSlice");
			if(scene.IsValid()&&scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}

		[UnityTest]
		public IEnumerator RealSlice_ControlsAlarmLampTestAndFocusEyes_DayNight_BothClasses()
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
				var output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../artifacts/wheelhouse-controls/captures"));Directory.CreateDirectory(output);
				foreach(var id in new[]{"k-class","t-class"})
				foreach(var night in new[]{false,true})
				{
					slice.Runner.Initialize(new ScenarioSettings { vesselFile="vessels/"+id+".json",startLatitude=40.7027,startLongitude=-74.0022,startHeadingDegrees=25,
						scenarioStartUtc=night?"2025-07-21T02:30:00Z":"2025-07-20T17:00:00Z",environment=new ScenarioEnvironment { calm=true } },new ConstantControls(ControlInputs.Neutral));
					slice.Runner.Pause();yield return null;
					var view=slice.Runner.GetComponentInChildren<WheelhouseControlsView>();Assert.That(view,Is.Not.Null);Assert.That(view.ClassId,Is.EqualTo(id));
					var mount=slice.CameraRig.WheelhouseMount;
					Assert.That(Vector3.Distance(mount.transform.position,view.transform.Find("Eye").position),Is.LessThan(.001),"real runtime call site prefers interior");
					// Hold an unacknowledged alarm at its on phase and full-ahead/rudder-over values.
					// This drives presentation only; the scene runner remains paused and untouched.
					var fixture = new CaptureReadout(slice.Runner); view.Bind(fixture);
					slice.CameraRig.CurrentMode=CameraRig.Mode.Wheelhouse;
					var controller = slice.CameraRig.Wheelhouse;
					controller.Recentre(); controller.HeadLook = new Vector2(0, 42); controller.Snap();
					for(var f=0;f<4;f++) yield return null;
					var prefix = id + "-" + (night ? "night" : "day");
					Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-Eye-alarm.png"));
					fixture.SetLampTest(true); view.Refresh();
					Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-Eye-lamp-test.png"));
					fixture.SetLampTest(false); view.Refresh();
					controller.HeadLook = new Vector2(60, 42); controller.Snap();
					Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-Eye-engine-controls.png"));
					foreach (var screen in new[] { "RadarScreen", "PlotterScreen", "PortEngineScreen", "StarboardEngineScreen" })
					{
						controller.CycleFocus();
						Assert.That(controller.FocusedScreen, Is.EqualTo(screen));
						for (var frame=0; frame<150; frame++) controller.AdvancePresentation(1f/60);
						Assert.That(Vector3.Distance(controller.transform.position,view.transform.Find(screen+"FocusEye").position),Is.LessThan(.001));
						Assert.That(Vector3.Angle(controller.transform.forward,view.transform.Find(screen).position-controller.transform.position),Is.LessThan(1));
						Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-"+screen+"FocusEye.png"));
						if (screen == "RadarScreen")
						{
							fixture.LeverDemand = 0; view.Refresh();
							Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-RadarScreenFocusEye-neutral.png"));
							fixture.LeverDemand = 1; view.Refresh();
							controller.HeadLook = new Vector2(-15, 0); controller.Snap();
							Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-RadarScreenFocusEye-look-left.png"));
							controller.HeadLook = new Vector2(15, 0); controller.Snap();
							Capture(slice.CameraRig.GetComponent<Camera>(),Path.Combine(output,prefix+"-RadarScreenFocusEye-look-right.png"));
							controller.HeadLook = Vector2.zero; controller.Snap();
						}
					}

				}
			}
			finally { if(previous.IsValid()&&previous.isLoaded) SceneManager.SetActiveScene(previous); }
			if(scene.IsValid()&&scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}

		private sealed class CaptureReadout : IVesselTelemetrySource, IInstrumentSource, IInstrumentReadout
		{
			private readonly IVesselTelemetrySource _source;
			public double LeverDemand = 1;
			public CaptureReadout(IVesselTelemetrySource source) { _source = source; }
			public bool HasTelemetry => true;
			public bool HasInstruments => true;
			public IInstrumentReadout Instruments => this;
			public VesselParameters Parameters => _source.Parameters;
			public VesselState State => VesselState.Planar(.25, 0, 0, 0);
			public VesselTelemetry Telemetry => default;
			public ControlInputs Controls => new ControlInputs(1, 1, 1);
			public DateTimeOffset ScenarioStartUtc => _source.ScenarioStartUtc;
			public long RunId => _source.RunId;
			public ContactLatch Contact => ContactLatch.None;
			public double Value(Channel channel)
			{
				switch (channel)
				{
					case Channel.LeverPort: case Channel.LeverStarboard: return LeverDemand;
					case Channel.TillerOrder: return 1;
					case Channel.RudderPort: case Channel.RudderStarboard: return Parameters.Controls.HardOverAngle.Value;
					case Channel.SteeringJog: return 0;
					case Channel.Heading: return _source.State.Heading;
					default: return double.NaN;
				}
			}
			public int AlarmCount => 1;
			public string AlarmId(int alarm) => "steering.failure";
			public AlarmGroup Group(int alarm) => AlarmGroup.Steering;
			public AlarmPhase Phase(int alarm) => AlarmPhase.Unacknowledged;
			public bool SounderOn(AlarmGroup group) => true;
			public bool LampTest { get; private set; }
			public long FirstEventSequence => 0;
			public long NextEventSequence => 0;
			public AlarmEvent EventAt(long sequence) => throw new ArgumentOutOfRangeException(nameof(sequence));
			public void AcknowledgeAlarms() { }
			public void SilenceAlarms() { }
			public void SetLampTest(bool on) => LampTest = on;
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
