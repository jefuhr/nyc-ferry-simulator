using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace FerrySim.Controls.Profiles
{
	/// <summary>Mouse-operated calibration wizard. All edits stay in a draft until Save device succeeds.</summary>
	public sealed class CalibrationView : VisualElement, IDisposable
	{
		private readonly DeviceProfileStore _store;
		private readonly VisualElement _devices, _axisValues;
		private readonly Label _deviceName, _identityNote, _profileName, _status;
		private readonly DropdownField _axis;
		private readonly EnumField _target;
		private readonly Toggle _inverted, _relative;
		private readonly FloatField _band, _relativeStep;
		private readonly Button _captureButton, _applyButton, _saveButton;
		private static readonly string[] StepLabels = { "1  ASTERN / PORT END", "2  NEUTRAL / CENTRE", "3  AHEAD / STARBOARD END" };
		private const string FormatThree = "+0.000;-0.000;0.000", FormatFour = "+0.0000;-0.0000;0.0000";
		private sealed class LiveAxis
		{
			public AxisControl control;
			public Label label;
			public string prefix;
			public float displayed = float.NaN;
		}
		private readonly List<Label> _steps = new List<Label>();
		private readonly List<LiveAxis> _live = new List<LiveAxis>();
		private readonly Dictionary<string, AxisControl> _controls = new Dictionary<string, AxisControl>();
		private readonly Dictionary<ProfileTarget, float> _displayedOutputs = new Dictionary<ProfileTarget, float>();
		private readonly Dictionary<ProfileTarget, Label> _outputs = new Dictionary<ProfileTarget, Label>();
		private readonly Dictionary<ProfileTarget, ProgressBar> _bars = new Dictionary<ProfileTarget, ProgressBar>();
		private InputDevice _device;
		private DeviceProfile _draft;
		private AxisCalibrationCapture _capture;
		private AxisAssignment _working;
		private AxisCalibrationCapture _displayedCapture;
		private int _displayedCount = -1;
		private bool _displayedConnected;
		private AxisControl _workingControl;
		private float _pulseSample;
		private readonly float[] _capturedValues = new float[3];
		private readonly Dictionary<ProfileTarget, float> _relativePreview = new Dictionary<ProfileTarget, float>();
		public int CaptureCount => _capture?.Count ?? 0;
		public InputDevice SelectedDevice => _device;

		public CalibrationView(DeviceProfileStore store)
		{
			_store = store ?? throw new ArgumentNullException(nameof(store));
			name = "input-calibration";
			AddToClassList("input-calibration");
			var shared = Resources.Load<StyleSheet>("FerrySimUI/Wheelhouse");
			if (shared != null) styleSheets.Add(shared);
			var own = Resources.Load<StyleSheet>("FerrySimInput/InputCalibration");
			if (own != null) styleSheets.Add(own);
			Add(Text("DEVELOPER WHEELHOUSE  /  INPUT SETUP", "cal-eyebrow"));
			Add(Text("Calibrate your controls", "cal-title"));
			Add(Text("Twin levers: −1 astern · 0 neutral · +1 ahead     Helm: −1 port · +1 starboard", "cal-subtitle"));
			var body = Box(this, "cal-body");
			var sidebarScroll = new ScrollView(ScrollViewMode.Vertical); sidebarScroll.AddToClassList("cal-sidebar"); body.Add(sidebarScroll);
			var sidebar = sidebarScroll.contentContainer;
			sidebar.Add(Text("CONNECTED DEVICES", "cal-eyebrow"));
			_devices = new ScrollView(ScrollViewMode.Vertical); _devices.AddToClassList("cal-devices"); sidebar.Add(_devices);
			sidebar.Add(Text("Move one axis at a time. Select the physical axis below, then capture its full travel and neutral detent.", "cal-note"));
			sidebar.Add(Text("The simulator owns gear detents, clutch timing and shift interlocks. These settings only normalize your hardware.", "cal-note"));
			var main = new ScrollView(ScrollViewMode.Vertical) { name = "cal-main" };
			main.AddToClassList("cal-main"); body.Add(main);
			_deviceName = Text("Select a device", "cal-device-title"); main.Add(_deviceName);
			_profileName = Text("", "cal-eyebrow"); main.Add(_profileName);
			_identityNote = Text("", "cal-note"); main.Add(_identityNote);
			var row = Box(main, "cal-row");
			_target = new EnumField("Simulator control", ProfileTarget.PortLever) { name = "target" }; row.Add(_target);
			_axis = new DropdownField("Physical axis", new List<string> { "(none)" }, 0) { name = "axis" }; row.Add(_axis);
			row = Box(main, "cal-row");
			_inverted = new Toggle("Invert output") { name = "invert" }; row.Add(_inverted);
			_relative = new Toggle("Relative / encoder") { name = "relative" }; row.Add(_relative);
			_relativeStep = new FloatField("Order per pulse") { name = "relative-step", value = 0.1f }; row.Add(_relativeStep);
			_band = new FloatField("Neutral band ± raw units") { name = "neutral-band", value = 0.05f }; main.Add(_band);
			main.Add(Text("Absolute: astern / port end → neutral / centre → ahead / starboard end. Relative: negative pulse → no motion → positive pulse.", "cal-note"));
			var steps = Box(main, "cal-steps");
			foreach (var label in StepLabels)
			{
				var step = Text(label, "cal-step"); steps.Add(step); _steps.Add(step);
			}
			row = Box(main, "cal-row cal-buttons");
			AddButton(row, "Start calibration", "begin", () => BeginCalibration((ProfileTarget)_target.value, _axis.value, _inverted.value, _relative.value));
			_captureButton = AddButton(row, "Capture point", "capture", CapturePoint);
			_applyButton = AddButton(row, "Apply captured axis", "apply", () => StageCalibration(_band.value));
			AddButton(row, "Cancel capture", "cancel", CancelCalibration);
			row = Box(main, "cal-row cal-buttons");
			AddButton(row, "Use default range", "default-range", StageDefault);
			AddButton(row, "Remove assignment", "remove-axis", RemoveAssignment);
			AddButton(row, "Apply inversion / mode", "apply-options", ApplyOptions);
			main.Add(Text("LIVE RAW AXES", "cal-eyebrow"));
			_axisValues = Box(main, "cal-live");
			sidebar.Add(Text("DRAFT OUTPUT / UNSMOOTHED", "cal-eyebrow"));
			var outputs = Box(sidebar, "cal-outputs");
			foreach (var target in new[] { ProfileTarget.PortLever, ProfileTarget.StarboardLever, ProfileTarget.Helm })
			{
				var output = Box(outputs, "cal-output");
				output.Add(Text(target == ProfileTarget.PortLever ? "PORT LEVER" : target == ProfileTarget.StarboardLever ? "STARBOARD LEVER" : "HELM", "cal-eyebrow"));
				var label = Text("+0.000", "cal-number"); output.Add(label); _outputs.Add(target, label);
				var bar = new ProgressBar { lowValue = -1f, highValue = 1f, value = 0f, title = "" }; output.Add(bar); _bars.Add(target, bar);
			}
			sidebar.Add(Text("Saved locally: " + _store.Path, "cal-path"));
			row = Box(main, "cal-row cal-buttons");
			_saveButton = AddButton(row, "Save device", "save", () => SaveDevice());
			_saveButton.AddToClassList("cal-primary");
			AddButton(row, "Reset this device", "reset-device", ResetDevice);
			AddButton(row, "Use throttle quadrant profile", "use-quadrant", UseQuadrantProfile);
			_status = Text(string.IsNullOrEmpty(store.LastError) ? "Choose a device. Defaults work before calibration." : store.LastError, "cal-status"); main.Add(_status);
			_target.RegisterValueChangedCallback(_ => { CancelCalibration(); LoadOptions(); });
			_axis.RegisterValueChangedCallback(_ => CancelCalibration());
			InputSystem.onDeviceChange += OnDeviceChange;
			InputSystem.onEvent += OnInputEvent;
			RefreshDevices();
			Refresh();
		}

		public void SelectDevice(InputDevice device)
		{
			_device = device;
			_capture = null; _working = null; _relativePreview.Clear();
			_draft = device == null ? null : _store.Find(DeviceIdentity.Key(device)) ?? DeviceProfileDefaults.Create(device);
			_live.Clear(); _controls.Clear(); _axisValues.Clear();
			_displayedOutputs.Clear();
			_deviceName.text = device?.displayName ?? "Select a device";
			_profileName.text = _draft == null ? "" : ProfileLabel(_draft.deviceClass);
			_identityNote.text = device == null ? "" : string.IsNullOrEmpty(device.description.serial)
				? "No serial reported: identical units share this profile regardless of reconnect order. Separate profiles require distinct device serials."
				: "Serial " + device.description.serial + " · calibration follows this device on reconnect";
			var axes = device == null ? new List<AxisControl>() : DeviceProfileDefaults.Axes(device);
			_axis.choices = axes.Select(DeviceProfileDefaults.RelativePath).ToList();
			if (_axis.choices.Count == 0) _axis.choices.Add("(none)");
			_axis.SetValueWithoutNotify(_axis.choices[0]);
			foreach (var axis in axes)
			{
				var path = DeviceProfileDefaults.RelativePath(axis);
				var prefix = path + "   ";
				var label = Text(prefix + "+0.0000", "cal-axis");
				_live.Add(new LiveAxis { control = axis, label = label, prefix = prefix });
				_controls.Add(path, axis); _axisValues.Add(label);
			}
			if (axes.Count == 0) _axisValues.Add(Text("Uses the existing Helm action map: Q/A · E/D · W/S · ←/→ for keyboard; sticks, triggers and shoulders for gamepad.", "cal-note"));
			LoadOptions(); Refresh();
		}

		public void BeginCalibration(ProfileTarget target, string path, bool inverted = false, bool relative = false)
		{
			if (!Connected || !_controls.TryGetValue(path, out _workingControl))
			{ SetStatus("Select a connected device and a physical axis first."); return; }
			_target.SetValueWithoutNotify(target); _axis.SetValueWithoutNotify(path);
			_inverted.SetValueWithoutNotify(inverted); _relative.SetValueWithoutNotify(relative);
			_capture = new AxisCalibrationCapture(); _pulseSample = 0f;
			_working = new AxisAssignment { path = path, target = target, inverted = inverted, relative = relative, relativeStep = _relativeStep.value };
			SetStatus(relative ? "Send a negative pulse, then click Capture point." : "Move fully astern (helm: fully to port), then click Capture point.");
			Refresh();
		}

		public void CapturePoint()
		{
			if (!Connected || _capture == null || _working == null) return;
			var control = _workingControl;
			if (control == null) return;
			var captured = _working.relative ? (_capture.Count == 1 ? 0f : _pulseSample) : control.ReadUnprocessedValue();
			if (_capture.Count < 3) _capturedValues[_capture.Count] = captured;
			_capture.Capture(captured);
			_pulseSample = 0f;
			SetStatus(_capture.Count == 1 ? "Leave the axis in its neutral detent / centre, then capture." : _capture.Count == 2
				? "Move fully ahead (helm: fully to starboard), then capture." : "All three points captured. Set the neutral band, then Apply captured axis.");
			Refresh();
		}

		public bool StageCalibration(float band)
		{
			if (!Connected || _capture == null || !_capture.TryFinish(band, out var calibration))
			{ SetStatus("Capture three distinct points with neutral between the ends. The band must fit inside both ends."); return false; }
			_working.calibration = calibration;
			if (!_working.IsValid) { SetStatus("Check the axis path and relative pulse amount (greater than 0, at most 1)."); return false; }
			ReplaceAxis(_working);
			_capture = null; _working = null;
			SetStatus("Draft ready. Check the outputs across full travel, then Save device.");
			Refresh(); return true;
		}

		public void CancelCalibration()
		{
			_capture = null; _working = null;
			SetStatus("Capture cancelled. Saved settings are unchanged."); Refresh();
		}

		public bool SaveDevice()
		{
			if (!Connected || _capture != null || _draft == null || !_draft.IsValid)
			{ SetStatus("Finish or cancel the capture before saving a connected device."); return false; }
			if (!_store.SaveProfile(_draft)) { SetStatus(_store.LastError); return false; }
			SetStatus(string.IsNullOrEmpty(_store.LastError) ? "Saved. This device is ready for the next session." : _store.LastError);
			Refresh(); return true;
		}

		public float DraftOutput(ProfileTarget target)
		{
			if (!Connected || _draft == null) return 0f;
			AxisAssignment assignment = null;
			foreach (var candidate in _draft.axes)
			{
				if (candidate.target != target && (target == ProfileTarget.Helm || candidate.target != ProfileTarget.BothLevers)) continue;
				assignment = candidate; break;
			}
			var control = assignment != null && _controls.TryGetValue(assignment.path, out var found) ? found : null;
			if (control == null) return 0f;
			if (assignment.relative) return _relativePreview.TryGetValue(assignment.target, out var order) ? order : 0f;
			return assignment.calibration.Apply(control.ReadUnprocessedValue(), assignment.inverted);
		}

		public void Refresh()
		{
			var connected = Connected;
			var count = CaptureCount;
			if (_displayedCapture != _capture || _displayedCount != count || _displayedConnected != connected)
			{
				_displayedCapture = _capture; _displayedCount = count; _displayedConnected = connected;
				_target.SetEnabled(_capture == null); _axis.SetEnabled(_capture == null);
				_inverted.SetEnabled(_capture == null); _relative.SetEnabled(_capture == null); _relativeStep.SetEnabled(_capture == null);
				_captureButton.SetEnabled(connected && _capture != null && count < 3);
				_applyButton.SetEnabled(connected && _capture != null && count == 3);
				_saveButton.SetEnabled(connected && _capture == null);
				for (var i = 0; i < _steps.Count; i++)
				{
					_steps[i].text = StepLabels[i] + (_capture != null && count > i ? "  " + Format(_capturedValues[i], 3) : "");
					_steps[i].EnableInClassList("cal-step-active", _capture != null && count == i);
					_steps[i].EnableInClassList("cal-step-done", _capture != null && count > i);
				}
			}
			foreach (var axis in _live)
			{
				if (!connected) break;
				var value = axis.control.ReadUnprocessedValue();
				if (value.Equals(axis.displayed)) continue;
				axis.displayed = value; axis.label.text = axis.prefix + Format(value, 4);
			}
			foreach (var pair in _outputs)
			{
				var value = connected ? DraftOutput(pair.Key) : float.NaN;
				if (_displayedOutputs.TryGetValue(pair.Key, out var previous) && previous.Equals(value)) continue;
				_displayedOutputs[pair.Key] = value;
				pair.Value.text = connected ? Format(value, 3) : "—";
				_bars[pair.Key].value = connected ? value : 0f;
			}
		}

		public void UseQuadrantProfile()
		{
			if (!Connected) return;
			var profile = DeviceProfileDefaults.Create(_device, useQuadrant: true);
			if (profile.deviceClass != DeviceProfileClass.ThrottleQuadrant)
			{ SetStatus("This layout has no pair of slider or Z/Rz axes. Assign individual axes instead."); return; }
			_draft = profile; _capture = null; _working = null; _relativePreview.Clear();
			_profileName.text = ProfileLabel(_draft.deviceClass); LoadOptions();
			SetStatus("Throttle quadrant staged by your choice. Check each engine axis, then Save device."); Refresh();
		}

		private bool Connected => _device != null && _device.added && _device.enabled;
		private void StageDefault()
		{
			if (!Connected || _axis.value == "(none)") return;
			var axis = new AxisAssignment { path = _axis.value, target = (ProfileTarget)_target.value, inverted = _inverted.value,
				relative = _relative.value, relativeStep = _relativeStep.value };
			if (!axis.IsValid) { SetStatus("Invalid axis options."); return; }
			ReplaceAxis(axis); _capture = null; _working = null;
			SetStatus("Default −1 / 0 / +1 range staged. Check outputs, then Save device."); Refresh();
		}
		private void ApplyOptions()
		{
			if (!Connected || _draft == null || _capture != null) return;
			var axis = _draft.axes.Find(item => item.target == (ProfileTarget)_target.value);
			if (axis == null) { SetStatus("Capture or assign this control first."); return; }
			var copy = JsonUtility.FromJson<AxisAssignment>(JsonUtility.ToJson(axis));
			copy.inverted = _inverted.value; copy.relative = _relative.value; copy.relativeStep = _relativeStep.value;
			if (!copy.IsValid) { SetStatus("Relative pulse amount must be greater than 0 and at most 1."); return; }
			ReplaceAxis(copy); SetStatus("Axis options staged. Save device to apply."); Refresh();
		}
		private void ReplaceAxis(AxisAssignment axis)
		{
			_draft.axes.RemoveAll(item => item.target == axis.target || item.path == axis.path
				|| (axis.target == ProfileTarget.BothLevers && item.target != ProfileTarget.Helm)
				|| (axis.target != ProfileTarget.Helm && item.target == ProfileTarget.BothLevers));
			_draft.axes.Add(axis); _relativePreview.Remove(axis.target);
		}
		private void RemoveAssignment()
		{
			if (_draft == null) return;
			_draft.axes.RemoveAll(axis => axis.target == (ProfileTarget)_target.value);
			CancelCalibration(); SetStatus("Assignment removed from draft. Save device to apply.");
		}
		private void ResetDevice()
		{
			if (!Connected) return;
			var identity = DeviceIdentity.Key(_device);
			if (!_store.ResetAndSave(identity)) { SetStatus(_store.LastError); return; }
			SelectDevice(_device); SetStatus("Saved settings reset for this device. Class defaults restored.");
		}
		private void LoadOptions()
		{
			var assignment = _draft?.axes.Find(axis => axis.target == (ProfileTarget)_target.value);
			if (assignment == null) return;
			if (_axis.choices.Contains(assignment.path)) _axis.SetValueWithoutNotify(assignment.path);
			_inverted.SetValueWithoutNotify(assignment.inverted); _relative.SetValueWithoutNotify(assignment.relative);
			_relativeStep.SetValueWithoutNotify(assignment.relativeStep); _band.SetValueWithoutNotify(assignment.calibration.neutralBand);
		}
		private void RefreshDevices()
		{
			_devices.Clear();
			foreach (var device in InputSystem.devices)
			{
				var captured = device;
				var button = AddButton(_devices, device.displayName + "\n" + ProfileLabel(DeviceProfileDefaults.Create(device).deviceClass), "device-" + device.deviceId, () => SelectDevice(captured));
				button.AddToClassList("cal-device");
			}
			if (_device != null && !_device.added)
			{
				SelectDevice(null);
				_deviceName.text = "Device disconnected"; SetStatus("Device disconnected. Unsaved capture discarded; reconnect and select it again.");
			}
		}
		private void OnInputEvent(InputEventPtr inputEvent, InputDevice device)
		{
			if (!Connected || device != _device || (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>())) return;
			if (_capture != null && _working?.relative == true && _capture.Count != 1)
			{
				var axis = _workingControl;
				if (axis != null && axis.ReadUnprocessedValueFromEvent(inputEvent, out var pulse) && pulse != 0f) _pulseSample = pulse;
			}
			foreach (var assignment in _draft.axes)
			{
				if (!assignment.relative) continue;
				_controls.TryGetValue(assignment.path, out var axis);
				if (axis == null || !axis.ReadUnprocessedValueFromEvent(inputEvent, out var raw)) continue;
				_relativePreview.TryGetValue(assignment.target, out var order);
				_relativePreview[assignment.target] = Mathf.Clamp(order + assignment.calibration.Apply(raw, assignment.inverted) * assignment.relativeStep, -1f, 1f);
			}
		}

		private void OnDeviceChange(InputDevice device, InputDeviceChange change) { RefreshDevices(); Refresh(); }
		private void SetStatus(string text) { if (_status != null) _status.text = text; }
		private static string ProfileLabel(DeviceProfileClass kind)
		{
			switch (kind)
			{
				case DeviceProfileClass.Keyboard: return "Keyboard";
				case DeviceProfileClass.Gamepad: return "Gamepad";
				case DeviceProfileClass.ThrottleQuadrant: return "Throttle quadrant";
				case DeviceProfileClass.WheelOrKnob: return "Wheel or knob";
				default: return "Generic joystick";
			}
		}

		private static string Format(float value, int digits) => value.ToString(digits == 3 ? FormatThree : FormatFour, CultureInfo.InvariantCulture);
		private static VisualElement Box(VisualElement parent, string classes)
		{
			var result = new VisualElement(); foreach (var cls in classes.Split(' ')) result.AddToClassList(cls); parent.Add(result); return result;
		}
		private static Label Text(string text, string cls) { var label = new Label(text); label.AddToClassList(cls); return label; }
		private static Button AddButton(VisualElement parent, string text, string name, Action action)
		{ var button = new Button(action) { text = text, name = name }; parent.Add(button); return button; }
		public void Dispose()
		{
			InputSystem.onDeviceChange -= OnDeviceChange;
			InputSystem.onEvent -= OnInputEvent;
		}
	}
}
