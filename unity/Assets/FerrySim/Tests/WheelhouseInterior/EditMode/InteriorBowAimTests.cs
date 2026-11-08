using System.IO;
using System.Reflection;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.WheelhouseInterior.Tests
{
	/// <summary>Failure modes: aim from a stale greybox origin, stale posture, or changed fallback rounding.</summary>
	public class InteriorBowAimTests
	{
		[TestCase("k-class", false)]
		[TestCase("t-class", false)]
		[TestCase("k-class", true)]
		[TestCase("t-class", true)]
		public void AutomaticBowAimUsesActualEye_AndPreservesExactGreyboxDirection(string id, bool interior)
		{
			var vessel = new GameObject("Bow aim vessel");
			var camera = new GameObject("Bow aim camera");
			try
			{
				var parameters = VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../vessels/" + id + ".json")));
				var runner = vessel.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				runner.Initialize(new ScenarioSettings { vesselFile = "vessels/" + id + ".json", environment = new ScenarioEnvironment { calm = true } }, new ConstantControls(ControlInputs.Neutral), parameters);
				runner.Pause();
				var d = GreyboxDimensions.From(parameters);
				var mount = new GameObject("Mount").AddComponent<WheelhouseCameraMount>();
				mount.transform.SetParent(vessel.transform, false);
				mount.transform.localPosition = new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward);
				mount.transform.localRotation = Quaternion.Euler(8, 4, 0);
				if (interior) WheelhouseControlsView.PlaceEye(vessel.transform, mount, parameters, runner);
				camera.AddComponent<Camera>();
				var controller = camera.AddComponent<WheelhouseCameraController>();
				controller.Mount = mount;
				var refresh = typeof(WheelhouseCameraController).GetMethod("RefreshApproach", BindingFlags.NonPublic | BindingFlags.Instance);
				var bowYaw = typeof(WheelhouseCameraController).GetField("_bowYaw", BindingFlags.NonPublic | BindingFlags.Instance);
				for (var posture = 0; posture < (interior ? 2 : 1); posture++)
				{
					if (posture == 1) mount.transform.position = vessel.GetComponentInChildren<WheelhouseControlsView>().transform.Find("EyeStanding").position;
					refresh.Invoke(controller, null);
					var p = mount.transform.localPosition;
					// The fallback expression is deliberately byte-for-byte the legacy double arithmetic.
					var offset = interior
						? new Vector3(-p.x, (float)(d.Freeboard - p.y), (float)(d.LengthOverall * .45 - p.z))
						: new Vector3(0, (float)(d.Freeboard - d.BridgeEyeHeight), (float)(d.LengthOverall * .45 - d.BridgeEyeForward));
					var direction = Quaternion.Inverse(mount.transform.localRotation) * offset;
					var pitch = Mathf.Clamp(Mathf.Atan2(-direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg, mount.PitchLimits.x, mount.PitchLimits.y);
					var yaw = Mathf.Clamp(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, mount.YawLimits.x, mount.YawLimits.y);
					Assert.That(controller.BowPitch, Is.EqualTo(pitch), "exact pitch for " + id + "/" + interior + "/" + posture);
					Assert.That((float)bowYaw.GetValue(controller), Is.EqualTo(yaw), "exact yaw");
				}
			}
			finally { Object.DestroyImmediate(camera); Object.DestroyImmediate(vessel); }
		}
	}
}
