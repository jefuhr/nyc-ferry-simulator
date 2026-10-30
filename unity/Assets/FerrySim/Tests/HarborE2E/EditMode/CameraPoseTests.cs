using System;
using CesiumForUnity;
using FerrySim.Harbor.Player.Journeys;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.HarborE2E.Tests
{
	public class CameraPoseTests
	{
		[Test]
		public void ManualCameraPoseRetainsMillimetresWithoutAllocatingAfterWarmup()
		{
			var root=new GameObject("camera pose allocation regression");
			try
			{
				var geo=root.AddComponent<CesiumGeoreference>();
				geo.SetOriginLongitudeLatitudeHeight(-74,40.7,0);
				var host=new GameObject("manual camera");host.transform.SetParent(root.transform,false);
				var camera=host.AddComponent<Camera>();camera.enabled=false;
				var anchor=host.AddComponent<CesiumGlobeAnchor>();
				var first=geo.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(-74,40.7,120));
				var second=first+new double3(.001,.002,.003);var direction=new double3(0,0,1);
				for(var i=0;i<10;i++)HarborBenchmark.ApplyCameraSweepPose(anchor,camera,geo,(i&1)==0?first:second,direction);
				Assert.That(math.distance(anchor.positionGlobeFixed,second),Is.LessThan(.001));
				var chars=new char[16];var recorder=Recorder.Get("GC.Alloc");recorder.enabled=false;recorder.FilterToCurrentThread();
				try
				{
					recorder.enabled=true;var control=new string(chars);recorder.enabled=false;
					var calibration=recorder.sampleBlockCount;GC.KeepAlive(control);
					Assert.That(calibration,Is.EqualTo(1),"one deliberate allocation");
					recorder.enabled=true;
					for(var i=0;i<1000;i++)HarborBenchmark.ApplyCameraSweepPose(anchor,camera,geo,(i&1)==0?first:second,direction);
					recorder.enabled=false;var measured=recorder.sampleBlockCount;
					TestContext.WriteLine("positive control="+calibration+"; manual camera pose GC.Alloc samples="+measured);
					Assert.That(measured,Is.Zero);
				}
				finally {recorder.enabled=false;recorder.CollectFromAllThreads();}
			}
			finally {UnityEngine.Object.DestroyImmediate(root);}
		}
	}
}
