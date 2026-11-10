using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using FerrySim.Vessels;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.World
{
	/// <summary>Own-vessel instrument presentation only. Never supplies input, steps physics or records state.</summary>
	[DisallowMultipleComponent]
	public sealed class WheelhouseControlsView : MonoBehaviour
	{
		public const string RootName = "WheelhouseInterior";
		public string ClassId;
		public float EngineTravelDegrees = 35;
		public float HelmTravelDegrees = 35;

		/// <summary>Validated bindings for this instance. Popup consumers read ControlMap.Entries; null without a valid map.</summary>
		public ControlMapFile ControlMap { get; private set; }
		private IVesselTelemetrySource _source;
		private IInstrumentSource _instrumentSource;
		private IInstrumentReadout _boundReadout;
		private long _boundRun = long.MinValue;
		private double _rudderLimit;
		private ControlMapFile.Entry[] _entries = Array.Empty<ControlMapFile.Entry>();
		private LampBank _lamps;
		private Transform _port, _starboard, _helm;
		private Quaternion _portRest, _starboardRest, _helmRest;
		private bool _cached;

		public void Bind(IVesselTelemetrySource source)
		{
			_source = source; _instrumentSource = source as IInstrumentSource;
			_boundReadout = null; _boundRun = long.MinValue;
			_rudderLimit = source?.Parameters?.Controls?.HardOverAngle?.Value ?? double.NaN;
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
			var mounts = GetComponent<WheelhouseControlMap>();
			if (mounts != null)
			{
				try
				{
					var map = ControlMapFile.LoadShipped(mounts);
					var lamps = new LampBank(transform, mounts);
					ControlMap = map; _entries = map.BoundEntries; _lamps = lamps;
				}
				catch (FormatException error) { Debug.LogWarning("Wheelhouse control map rejected: " + error.Message, this); }
			}
			_cached = true;
		}

		/// <summary>Allocation-free in steady state. Names and mounts are resolved at bind time only.</summary>
		public void Refresh()
		{
			// Legacy callers may refresh before Bind; all later calls use cached references.
			if (!_cached) CacheMounts();
			var alive = _source != null && !(_source is Object unitySource && unitySource == null);
			var readout = alive && _instrumentSource != null && _instrumentSource.HasInstruments ? _instrumentSource.Instruments : null;
			if (readout != null && (!ReferenceEquals(readout, _boundReadout) || _boundRun != _source.RunId))
			{
				ControlMap?.BindAlarms(readout, this);
				_boundReadout = readout; _boundRun = _source.RunId;
				_rudderLimit = _source.Parameters?.Controls?.HardOverAngle?.Value ?? double.NaN;
			}
			for (var i = 0; i < _entries.Length; i++)
			{
				var entry = _entries[i];
				if (entry.Mount.LampIndex >= 0) continue;
				var angle = 0f;
				if (readout != null && entry.Channel.HasValue)
				{
					var channel = entry.Channel.Value;
					var value = readout.Value(channel);
					if (ControlMapFile.Finite(value))
					{
						var low = entry.Low; var high = entry.High;
						// A compass is circular. Signed heading plus reversed range counter-rotates its north mark.
						if (entry.IsCompass) value = Math.IEEERemainder(value, 2 * Math.PI);
						// SCR-04: provisional full-scale dial calibration follows this vessel's actual rudder limit.
						if ((channel == Channel.RudderPort || channel == Channel.RudderStarboard) && ControlMapFile.Finite(_rudderLimit) && _rudderLimit > 0)
						{ low = -_rudderLimit; high = _rudderLimit; }
						angle = Mathf.Lerp(entry.Mount.Travel.x, entry.Mount.Travel.y, (float)((value - low) / (high - low)));
					}
				}
				entry.Transform.localRotation = entry.Rest * Quaternion.AngleAxis(angle, entry.Mount.Axis);
			}
			if (readout == null)
			{
				var controls = alive && _source.HasTelemetry ? _source.Controls : ControlInputs.Neutral;
				if (_port != null) _port.localRotation = _portRest * Quaternion.Euler((float)controls.PortLever * EngineTravelDegrees, 0, 0);
				if (_starboard != null) _starboard.localRotation = _starboardRest * Quaternion.Euler((float)controls.StarboardLever * EngineTravelDegrees, 0, 0);
				if (_helm != null) _helm.localRotation = _helmRest * Quaternion.Euler(0, -(float)controls.Helm * HelmTravelDegrees, 0);
			}
			_lamps?.Refresh(_entries, readout, readout == null ? 0 : _source.State.Time);
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
