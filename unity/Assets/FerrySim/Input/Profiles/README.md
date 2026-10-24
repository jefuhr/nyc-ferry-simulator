# Device Profiles

Owner: **unity-input-profiles**. Runtime code lives in `FerrySim.Unity.Input`, namespace
`FerrySim.Controls.Profiles`. No vendor SDK or additional package is used.

## Profiles and Defaults

| Device class | Default controls | Fallback |
|---|---|---|
| Keyboard | Q/A port lever, E/D starboard lever, W/S both levers, ←/→ helm | Existing `Helm` action map, including notch stepping and buttons |
| Gamepad | Left/right stick Y for port/starboard, triggers for both, shoulders for helm | Existing `Helm` bindings and processors, unchanged |
| Throttle quadrant | Slider pair or explicit quadrant signature for independent port/starboard levers; X for helm when present | Generic HID joystick layout and original `Helm` map if no recognizable pair is exposed |
| Wheel or knob | X/steering/wheel or dial for helm; relative delta axes accumulate helm order | Keyboard lever steps remain available; assign other axes explicitly |
| Generic joystick | Y for both levers, X/twist for helm | Existing `Helm` map, including twist neutral deadzone |

These are control-path heuristics, not claims about a particular hardware model. An unusual layout
can expose different names or ranges. The screen lists every scalar axis from the Input System layout,
including `stick/x`, `stick/y`, analog triggers, `slider`, `slider1`, `z`, `rz` and delta children where available.
Digital buttons and stick/d-pad direction buttons are excluded. Unsupported persisted paths are
reported in `LastError` and at controller startup; they never silently become working assignments.
An ambiguous X/Y/Z/Rz joystick keeps Y-to-both-levers and X/twist-to-helm semantics. Z/Rz alone
is not a quadrant signature. Choose **Use throttle quadrant profile** to stage those axes as independent
engines deliberately, then calibrate and save; reset restores conservative automatic defaults.
The Input System's built-in HID descriptor support supplies generic joystick/multi-axis layouts.
A device unsupported by the platform's Input System backend cannot be made usable by a profile alone.

For wheels, pedals are not automatically assumed to be centred twin throttle levers. Use keyboard
lever steps or deliberately assign/calibrate another axis. An assigned target or reassigned physical axis replaces only overlapping analog `Helm` bindings
for that device; other bindings, buttons and step actions remain available. Different devices can drive different
controls, and the last moved control wins in Input System event order. An idle calibrated detent does
not override a keyboard order. Disconnecting removes its routes; reconnecting restores its profile.

## Calibration Screen

Open `Assets/FerrySim/Input/Profiles/Scenes/InputCalibrationDev.unity` and enter Play Mode.
Rebuild it with **FerrySim → Input → Rebuild Calibration Dev Scene** if needed. The scene is deliberately
not added to the project's build settings. `CalibrationScreen` can also be placed beside a
`HelmInputController`; it shares that controller's profile store, so saved settings apply immediately. A changed calibration re-evaluates its held axis immediately;
unchanged axes keep the existing last-moved control priority. The controller owns one store for its
component lifetime, so disabling and re-enabling it preserves the store used by an already-open view.
Removing/reassigning a route releases only targets that route still owns to their current action-map
fallback or neutral; newer keyboard and other-device owners are preserved.

1. Select the device, simulator control and physical axis. Watch the live raw axis values while moving
   only that axis. Choose `BothLevers` to drive both levers together, or assign each lever separately.
2. Click **Start calibration**. Move to full astern (helm: full port) and click **Capture point**.
3. Put the lever in its neutral detent (helm: centre) and capture again. Set **Neutral band ± raw units**
   wide enough to include the small values observed while resting or gently moving within the detent.
4. Move to full ahead (helm: full starboard) and capture the third point. Click **Apply captured axis**.
   The neutral point must lie between the endpoints, and the band must not reach either end.
5. Sweep the axis while watching **Draft calibrated output**. Both ends must be exactly ±1 and the
   neutral band exactly 0. **Invert output** reverses the final sign; click **Apply inversion / mode**
   after changing it. Capturing physical astern/neutral/ahead also handles axes whose raw values decrease.
6. Click **Save device**. Repeat for the other lever and helm, saving each complete draft. Changes are
   not used by the simulation until saved. Switching device, closing the screen, unplugging or cancelling
   a capture discards unfinished work; the previous saved profile stays intact.

**Use default range** assigns an axis with −1/0/+1 endpoints and a 0.05 neutral band without a capture.
**Remove assignment** changes the draft; save to apply. **Reset this device** removes its saved profile
and restores class defaults without affecting other identities.

For a true relative encoder, enable **Relative / encoder**. The three capture points are a negative pulse,
no movement (zero), and a positive pulse. Pulses are latched until the mouse can capture them. Set
**Order per pulse** (default 0.1 normalized order for a full-scale pulse), then apply and save. Repeated
pulses accumulate and clamp at ±1; no-motion/reset-to-zero events do not return the helm to centre.
The layout must expose signed relative deltas, not an absolute wrapping angle. A wrapping absolute knob
should be configured as absolute unless a suitable relative Input System layout is supplied.

The dark opaque console, bezel colours, typography and compact controls follow
`UI/Resources/FerrySimUI/Wheelhouse.uss`; that stylesheet and its theme are reused without changing `UI/`.
The device list rebuilds only on Input System device-change notifications. Control references, static
labels, format strings and displayed values are cached; warmed unchanged refreshes allocate nothing
in application code (including mid-capture). Changed numeric values still format new strings.
The readout is dimensionless and before smoothing. Detents, clutch timing and gear interlocks remain
in the core control model.

## Mapping and Persistence

For increasing raw travel, let `n` be neutral and `b` the neutral half-band. The output is 0 throughout
`[n−b, n+b]`, ramps linearly from −1 at the astern end to 0 at `n−b`, and from 0 at `n+b` to +1 at the ahead
end. Values beyond the endpoints clamp. Decreasing raw travel works symmetrically. Inversion happens
after calibration. Segment arithmetic uses double precision with explicit endpoint handling before
a neutral rounding allowance bounded by the remaining calibrated travel. No calibration parameters are physical vessel-model parameters.

The file is `Application.persistentDataPath/input-profiles-v1.json`. On this project's macOS player/editor
configuration that is normally under `~/Library/Application Support/NYC Ferry Simulator/NYC Ferry Simulator/`;
the screen shows the actual runtime path. Schema version 1 stores a list of device identities, device
classes, device-relative control paths, inversion/relative options and calibration points/bands. Saving
writes a temporary file beside it and replaces the old file after writing. Unknown versions, malformed
JSON, invalid calibration or duplicate identities are reported in `LastError` (and at controller startup
in the Unity console) and ignored as a whole, with defaults retained. Loading never overwrites a rejected
file. UI saves and resets publish changes only after persistence succeeds. A failed write reports
the error and leaves the previous live profile and target ownership untouched.

Identity uses the exact interface, manufacturer, product and serial strings, with length-prefixed fields
to avoid delimiter collisions. The transient Input System device ID and USB port are not persisted.
Two otherwise identical devices with different serials have independent settings. Devices that report
no serial cannot be reliably distinguished from an identical model: they share one profile, and the screen
shows that limitation. A firmware change to identity strings requires recalibration.

## Real-Hardware Manual Check

No physical hardware was available during implementation; automated tests use simulated Input System
devices and a generated generic HID descriptor. Run this check with the actual devices before relying
on a profile:

1. Connect the quadrant/wheel/knob, open the calibration dev scene and enter Play Mode. Verify the device
   appears and only the expected raw axes move. Confirm the displayed serial when one is available.
2. Calibrate port and starboard independently. At each astern end the corresponding draft output must
   read **−1.000**; in the entire detent band **0.000**; at each ahead end **+1.000**. Moving one lever
   must leave the other lever unchanged. Toggle inversion, apply it and verify both signs reverse.
3. Calibrate the helm: full port **−1.000**, centre band **0.000**, full starboard **+1.000**. For an
   encoder, repeated positive pulses must advance the order and hold it between pulses; negative pulses
   reduce it. Check it clamps at both ends and that pulse gain feels usable.
4. Save, stop Play Mode, restart and reconnect the devices. Verify assignments, inversion, bands and
   outputs persist. With two same-model units with different serials, give them different assignments
   and confirm they remain separate after reconnecting.
5. Set a lever with Q/A or W/S, then leave the hardware at its neutral detent. Detent noise must not
   take over; moving the assigned axis outside the band must take over. Check keyboard notch stepping,
   gamepad triggers and buttons still behave as documented in the parent README.
6. Begin a new capture, stop after two points, close/reopen the screen and verify the old saved profile
   still works. Unplug mid-capture and verify save is unavailable. Reset one device and confirm other
   saved devices are unaffected.
