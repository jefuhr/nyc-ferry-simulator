using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Own-vessel presentation only. Reads applied telemetry; never supplies input, steps the runner,
	/// changes state or participates in recording. The core has no independent NFU demand, so the
	/// two jog handles remain neutral. Screen mounts are reserved for future RenderTextures.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class WheelhouseControlsView : MonoBehaviour
	{
		public const string RootName = "WheelhouseInterior";
		public string ClassId;
		public float EngineTravelDegrees = 35;
		public float HelmTravelDegrees = 35;

		private IVesselTelemetrySource _source;
		private Transform _port, _starboard, _helm;
		private Quaternion _portRest, _starboardRest, _helmRest;
		private bool _cached;

		public void Bind(IVesselTelemetrySource source)
		{
			_source = source;
			CacheMounts();
			Refresh();
		}

		private void CacheMounts()
		{
			if (_cached) return;
			_port = transform.Find("PortLever");
			_starboard = transform.Find("StarboardLever");
			_helm = transform.Find("HelmFFU");
			_portRest = _port != null ? _port.localRotation : Quaternion.identity;
			_starboardRest = _starboard != null ? _starboard.localRotation : Quaternion.identity;
			_helmRest = _helm != null ? _helm.localRotation : Quaternion.identity;
			_cached = true;
		}

		/// <summary>Re-reads the current source, including pause/reset/replay and unavailable telemetry.</summary>
		public void Refresh()
		{
			CacheMounts();
			var alive = _source != null && !(_source is Object unitySource && unitySource == null);
			var controls = alive && _source.HasTelemetry ? _source.Controls : ControlInputs.Neutral;
			if (_port != null)
				_port.localRotation = _portRest * Quaternion.Euler((float)controls.PortLever * EngineTravelDegrees, 0, 0);
			if (_starboard != null)
				_starboard.localRotation = _starboardRest * Quaternion.Euler((float)controls.StarboardLever * EngineTravelDegrees, 0, 0);
			if (_helm != null)
				_helm.localRotation = _helmRest * Quaternion.Euler(0, -(float)controls.Helm * HelmTravelDegrees, 0);
		}

		private void LateUpdate() => Refresh();

		/// <summary>
		/// Called only at own-vessel scene/run setup, never traffic creation. Model positions already use
		/// vessel metres. Existing camera pitch/yaw limits and tilt remain on the camera mount.
		/// Unsupported/missing interior assets retain the procedural GreyboxDimensions fallback.
		/// </summary>
		public static void PlaceEye(Transform owner, WheelhouseCameraMount mount, VesselParameters parameters, IVesselTelemetrySource source = null)
		{
			if (owner == null || mount == null || parameters == null || mount.transform.parent != owner) return;
			var id = parameters.Id;
			var existing = owner.Find(RootName);
			var view = existing != null ? existing.GetComponent<WheelhouseControlsView>() : null;
			if (existing != null && (view == null || view.ClassId != id))
			{
				existing.gameObject.SetActive(false);
				existing.name = RootName + " (replaced)";
				if (Application.isPlaying) Destroy(existing.gameObject);
				else DestroyImmediate(existing.gameObject);
				view = null;
			}
			if (view == null && (id == "t-class" || id == "k-class"))
			{
				var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
				if (prefab != null)
				{
					var instance = Instantiate(prefab, owner, false);
					instance.name = RootName;
					view = instance.GetComponent<WheelhouseControlsView>();
				}
			}
			var eye = view != null ? view.transform.Find("Eye") : null;
			if (eye != null)
			{
				view.Bind(source);
				mount.transform.localPosition = owner.InverseTransformPoint(eye.position);
			}
			else
			{
				var d = GreyboxDimensions.From(parameters);
				mount.transform.localPosition = new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward);
			}
		}
	}
}
