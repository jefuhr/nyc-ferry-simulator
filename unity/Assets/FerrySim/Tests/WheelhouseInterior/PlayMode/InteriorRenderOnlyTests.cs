using System.Collections;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class InteriorRenderOnlyTests
	{
		[UnityTest]
		public IEnumerator AnimatedControlsLeaveEveryPhysicsStateAndInputDigestUnchanged()
		{
			var animated = new GameObject("Animated").AddComponent<VesselSimulationRunner>();
			var baseline = new GameObject("Baseline").AddComponent<VesselSimulationRunner>();
			try
			{
				foreach (var runner in new[] { animated, baseline })
				{
					runner.RecordRuns=false;
					var trace=new ControlTrace();trace.Append(0,new ControlInputs(.7,-.3,.6));trace.Append(250,new ControlInputs(-.6,.8,-.4));
					runner.Initialize(new ScenarioSettings { vesselFile="vessels/k-class.json",environment=new ScenarioEnvironment { calm=true } },trace,
						VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath,"../../vessels/k-class.json"))));
					runner.Pause();
				}
				var mount=new GameObject("Mount").AddComponent<WheelhouseCameraMount>();mount.transform.SetParent(animated.transform,false);
				WheelhouseControlsView.PlaceEye(animated.transform,mount,animated.Parameters,animated);
				var view=animated.GetComponentInChildren<WheelhouseControlsView>();
				var shown=new StateDigest();var plain=new StateDigest();
				for(var step=0;step<500;step++)
				{
					animated.Driver.Advance(.01);baseline.Driver.Advance(.01);
					view.Refresh();view.Refresh();
					shown.Add(animated.State);plain.Add(baseline.State);
					Assert.That(animated.Controls,Is.EqualTo(baseline.Controls));
					if(step%100==0) yield return null; // Includes real LateUpdate while paused.
				}
				Assert.That(shown.Text,Is.EqualTo(plain.Text));
				Assert.That(shown.Count,Is.EqualTo(500));
				Assert.That(Quaternion.Angle(view.transform.Find("PortLever").localRotation,Quaternion.identity),Is.GreaterThan(1));
				TestContext.WriteLine("500-state digest with and without render animation: "+shown.Text);
			}
			finally { Object.Destroy(animated.gameObject);Object.Destroy(baseline.gameObject); }
			yield return null;
		}
	}
}
