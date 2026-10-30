using System.Collections;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.HarborE2E.Tests.PlayMode
{
	public class CameraSweepTests
	{
		[UnityTest]
		public IEnumerator ManualSweepRetainsTheCameraAfterTheControllersLateUpdate()
		{
			var host=new GameObject("manual camera sweep regression");
			var mountHost=new GameObject("stationary wheelhouse mount");
			try
			{
				var camera=host.AddComponent<Camera>();
				camera.enabled=false;
				var rig=host.AddComponent<CameraRig>();
				var free=host.AddComponent<FreeFlyCameraController>();
				var chase=host.AddComponent<ChaseCameraController>();
				var wheelhouse=host.AddComponent<WheelhouseCameraController>();
				rig.Configure(free,chase,wheelhouse);
				rig.WheelhouseMount=mountHost.AddComponent<WheelhouseCameraMount>();
				rig.CurrentMode=CameraRig.Mode.Wheelhouse;
				yield return null;
				Assert.That(host.transform.position,Is.EqualTo(mountHost.transform.position));
				HarborBenchmark.PrepareCameraSweep(rig);
				var expected=new Vector3(100,120,300);
				host.transform.position=expected;
				for(var i=0;i<3;i++)yield return null;
				Assert.That(host.transform.position,Is.EqualTo(expected),"a disabled CameraRig must not leave a following controller in control of the manual sweep");
			}
			finally { Object.Destroy(host);Object.Destroy(mountHost); }
		}
	}
}
