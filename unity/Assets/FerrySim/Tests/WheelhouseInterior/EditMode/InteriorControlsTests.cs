using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class InteriorControlsTests
	{
		private static VesselParameters Vessel(string id) => VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../vessels/" + id + ".json")));

		[Test]
		public void ModelEyePreferred_ClassSwitchReplaces_UnknownFallsBack_HeadLookClamped()
		{
			var owner = new GameObject("Own vessel");
			var camera = new GameObject("Test camera");
			try
			{
				var mount = new GameObject("Mount").AddComponent<WheelhouseCameraMount>();
				mount.transform.SetParent(owner.transform, false);
				foreach (var id in new[] { "k-class", "t-class", "k-class" })
				{
					WheelhouseControlsView.PlaceEye(owner.transform, mount, Vessel(id));
					var view = owner.GetComponentInChildren<WheelhouseControlsView>();
					Assert.That(view.ClassId, Is.EqualTo(id));
					Assert.That(owner.GetComponentsInChildren<WheelhouseControlsView>().Length, Is.EqualTo(1));
					Assert.That(Vector3.Distance(mount.transform.position, view.transform.Find("Eye").position), Is.LessThan(.001));
				}
				camera.AddComponent<Camera>();
				var controller = camera.AddComponent<WheelhouseCameraController>();
				controller.Mount = mount;
				controller.HeadLook = new Vector2(500,500);
				Assert.That(controller.HeadLook, Is.EqualTo(new Vector2(mount.YawLimits.y,mount.PitchLimits.y)));
				var unknown = Vessel("t-class");unknown.Id = "unknown";
				WheelhouseControlsView.PlaceEye(owner.transform, mount, unknown);
				Assert.That(owner.GetComponentInChildren<WheelhouseControlsView>(), Is.Null);
				var d = GreyboxDimensions.From(unknown);
				Assert.That(mount.transform.localPosition, Is.EqualTo(new Vector3(0,(float)d.BridgeEyeHeight,(float)d.BridgeEyeForward)));
			}
			finally { Object.DestroyImmediate(camera);Object.DestroyImmediate(owner); }
		}

		[Test]
		public void AppliedControlsRotateAboutModelPivots_NoTelemetryReturnsNeutral_NFUIsNotInvented()
		{
			var root = Object.Instantiate(Resources.Load<GameObject>("FerrySimVessels/k-class/Interior"));
			try
			{
				var view = root.GetComponent<WheelhouseControlsView>();
				var source = new Source { Inputs = new ControlInputs(1,-.5,.75), Valid = true };
				view.Bind(source);view.Refresh();
				Assert.That(Quaternion.Angle(root.transform.Find("PortLever").localRotation, Quaternion.Euler(view.EngineTravelDegrees,0,0)), Is.LessThan(.01));
				Assert.That(Quaternion.Angle(root.transform.Find("StarboardLever").localRotation, Quaternion.Euler(-view.EngineTravelDegrees*.5f,0,0)), Is.LessThan(.01));
				Assert.That(Quaternion.Angle(root.transform.Find("HelmFFU").localRotation, Quaternion.Euler(0,-view.HelmTravelDegrees*.75f,0)), Is.LessThan(.01));
				Assert.That(root.transform.Find("PortNFU").localRotation, Is.EqualTo(Quaternion.identity));
				source.Valid=false;view.Refresh();
				Assert.That(root.transform.Find("PortLever").localRotation, Is.EqualTo(Quaternion.identity));
				Assert.That(root.transform.Find("HelmFFU").localRotation, Is.EqualTo(Quaternion.identity));
			}
			finally { Object.DestroyImmediate(root); }
		}

		private sealed class Source : IVesselTelemetrySource
		{
			public ControlInputs Inputs; public bool Valid;
			public ControlInputs Controls => Inputs;
			public bool HasTelemetry => Valid;
			public VesselParameters Parameters => null;
			public VesselState State => default;
			public VesselTelemetry Telemetry => default;
			public DateTimeOffset ScenarioStartUtc => default;
			public long RunId => 1;
			public ContactLatch Contact => ContactLatch.None;
		}
	}
}
