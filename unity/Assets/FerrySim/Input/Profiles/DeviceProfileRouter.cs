using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace FerrySim.Controls.Profiles
{
	/// <summary>
	/// Routes raw layout axes before smoothing. Input events preserve last-moved priority with the Helm
	/// action map; unchanged calibrated values (including detent noise) never take control.
	/// </summary>
	public sealed class DeviceProfileRouter : IDisposable
	{
		private sealed class Route
		{
			public AxisControl control;
			public AxisAssignment assignment;
			public float last;
		}
		private readonly HelmInputMapper _mapper;
		private readonly DeviceProfileStore _store;
		private readonly List<Route> _routes = new List<Route>();
		private readonly HashSet<int> _owned = new HashSet<int>();
		private bool _disposed;
		public DeviceProfileStore Store => _store;
		public DeviceProfileRouter(HelmInputMapper mapper, DeviceProfileStore store)
		{
			_mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
			_store = store ?? throw new ArgumentNullException(nameof(store));
			_mapper.AttachProfileRouter(this);
			_store.Changed += OnProfilesChanged;
			InputSystem.onDeviceChange += OnDeviceChange;
			InputSystem.onEvent += OnEvent;
			Refresh();
		}

		public bool OwnsDevice(InputDevice device) => device != null && _owned.Contains(device.deviceId);
		/// <summary>Assigned targets and reassigned physical axes replace only their overlapping legacy bindings.</summary>
		public bool OwnsInput(InputControl control, ProfileTarget target)
		{
			if (control == null || !OwnsDevice(control.device)) return false;
			foreach (var route in _routes)
			{
				if (route.control.device != control.device) continue;
				if (route.control == control || route.assignment.target == target
					|| (route.assignment.target == ProfileTarget.BothLevers && target != ProfileTarget.Helm)) return true;
			}
			return false;
		}

		public void Refresh()
		{
			var previous = _routes.ToArray();
			_store.ClearRoutingError();
			_routes.Clear();
			_owned.Clear();
			foreach (var device in InputSystem.devices)
			{
				var profile = _store.Find(DeviceIdentity.Key(device)) ?? DeviceProfileDefaults.Create(device);
				foreach (var assignment in profile.axes)
				{
					var control = device.TryGetChildControl<AxisControl>(assignment.path);
					if (!DeviceProfileDefaults.IsAnalogAxis(control))
					{
						_store.ReportRoutingError(assignment.path + " is unavailable or is not an analog axis on " + device.displayName);
						continue;
					}
					var existing = Array.Find(previous, old => old.control == control && SameAssignment(old.assignment, assignment));
					_routes.Add(existing ?? new Route { control = control, assignment = assignment, last = assignment.calibration.Apply(control.ReadUnprocessedValue(), assignment.inverted) });
					_owned.Add(device.deviceId);
				}
			}
			if (_mapper.Map.enabled)
				foreach (var old in previous)
					if (!_routes.Contains(old)) _mapper.ReleaseProfileInput(old);
		}

		private void OnProfilesChanged()
		{
			var previous = _routes.ToArray();
			Refresh();
			if (!_mapper.Map.enabled) return;
			foreach (var route in _routes)
			{
				if (route.assignment.relative || Array.Exists(previous, old => old.control == route.control && SameAssignment(old.assignment, route.assignment))) continue;
				// Saving is an explicit takeover for changed assignments only. Unchanged idle axes must
				// not steal controls last moved from the keyboard or another device.
				_mapper.ApplyProfileInput(route.assignment.target, route.last, route);
			}
		}
		private static bool SameAssignment(AxisAssignment a, AxisAssignment b) => a.path == b.path && a.target == b.target
			&& a.inverted == b.inverted && a.relative == b.relative && a.relativeStep == b.relativeStep
			&& a.calibration.astern == b.calibration.astern && a.calibration.neutral == b.calibration.neutral
			&& a.calibration.ahead == b.calibration.ahead && a.calibration.neutralBand == b.calibration.neutralBand;

		public void Resync()
		{
			foreach (var route in _routes)
			{
				if (route.assignment.relative) continue;
				route.last = route.assignment.calibration.Apply(route.control.ReadUnprocessedValue(), route.assignment.inverted);
				_mapper.ApplyProfileInput(route.assignment.target, route.last, route);
			}
		}

		private void OnEvent(InputEventPtr inputEvent, InputDevice device)
		{
			if (!_mapper.Map.enabled || !device.enabled || !OwnsDevice(device)
				|| (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>())) return;
			foreach (var route in _routes)
			{
				if (route.control.device != device || !route.control.ReadUnprocessedValueFromEvent(inputEvent, out var raw)) continue;
				var value = route.assignment.calibration.Apply(raw, route.assignment.inverted);
				if (route.assignment.relative)
				{
					if (value != 0f) _mapper.ApplyProfileDelta(route.assignment.target, value * route.assignment.relativeStep, route);
				}
				else if (value != route.last)
				{
					route.last = value;
					_mapper.ApplyProfileInput(route.assignment.target, value, route);
				}
			}
		}
		private void OnDeviceChange(InputDevice device, InputDeviceChange change)
		{
			if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed || change == InputDeviceChange.Reconnected
				|| change == InputDeviceChange.Disconnected || change == InputDeviceChange.ConfigurationChanged || change == InputDeviceChange.Enabled)
				Refresh();
		}
		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			_store.Changed -= OnProfilesChanged;
			InputSystem.onDeviceChange -= OnDeviceChange;
			InputSystem.onEvent -= OnEvent;
			_mapper.DetachProfileRouter(this);
			_routes.Clear();
			_owned.Clear();
		}
	}
}
