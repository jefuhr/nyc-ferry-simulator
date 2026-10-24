# Input

Owner: **unity-sim-bridge-controls**. Assembly `FerrySim.Unity.Input` (namespace `FerrySim.Controls`, not `FerrySim.Input`, which would shadow `UnityEngine.Input`). Devices produce normalized `ControlInputs` through the Input System package and nothing else. Detents, clutch timing, shift interlocks and steering behaviour belong to the core's control model. `Profiles/` belongs to **unity-input-profiles**.

## Pieces

| File | Role |
|---|---|
| `FerrySimControls.inputactions` | The "Helm" action map and default bindings |
| `HelmInputMapper` | Turns the map into lever and helm positions, plus button events |
| `ControlInputSmoother` | Device-independent noise smoothing, once per physics step |
| `HelmInputController` | MonoBehaviour: the runner's `IControlInputSource`; buttons pause and reset the run, cycle the camera and sound the horn |

## The Helm map

| Action | Type | Keyboard | Gamepad | Joystick (generic HID) |
|---|---|---|---|---|
| `PortLever` | axis, −1..1 | | left stick Y | |
| `StarboardLever` | axis, −1..1 | | right stick Y | |
| `BothLevers` | axis, −1..1 | | right trigger ahead, left trigger astern | stick Y |
| `Helm` | axis, −1..1 | ←/→ held | left/right shoulder | stick X, twist (`{Twist}`) |
| `PortLeverStep` | step | Q ahead, A astern | | |
| `StarboardLeverStep` | step | E ahead, D astern | | |
| `BothLeversStep` | step | W ahead, S astern | d-pad up/down | |
| `Horn` | button, held | H | south button | trigger |
| `Pause` | button | P | start | |
| `Reset` | button, hold 1 s | R | select | |
| `CameraCycle` | button | C | north button | |

Signs follow `ControlInputs`: +1 is full ahead, −1 full astern, and +1 helm is hard over to starboard. The Input System inverts HID Y axes, so stick forward reads +1.

## How positions are set

**Step actions** move a lever one notch per press: 5 notches each way by default (`leverNotches`), so 0.2 per press. They click through −1 … −0.2, 0, 0.2 … 1. Neutral is exactly 0, the ends are exactly ±1, and the levers clamp there. A lever left between notches by an analog device steps to the next notch in that direction.

**Absolute axes** set a lever or the helm to the device's position. When the device returns to rest, so does the control.

**Device priority:** the device that moved a control last wins. A gamepad lying idle does not override levers set from the keyboard.

**Run start:** when a run starts or is reset, stepped levers return to neutral and absolute axes are read again. A throttle quadrant left at half ahead stays at half ahead.

## Smoothing

`ControlInputSmoother` is a first-order lag (`smoothingTimeConstant`, default 0.05 s; 0 turns it off):
- it advances once per physics step, so it does not depend on the frame rate;
- it lands exactly on the input once within 1e-6, so a steady input comes out unchanged, bit for bit, from the first step (tested for every time constant);
- it resets at the start of each run.

The recorder writes the smoothed values the physics received, so a replay is exact.


## Device profiles and calibration

`HelmInputController` loads versioned per-device axis assignments and calibration from
`Application.persistentDataPath/input-profiles-v1.json`. Keyboard, gamepad and generic joystick
fallbacks retain the existing action map, processors, notch stepping and button semantics.
Recognizable quadrants get two independent lever axes; ambiguous X/Y/Z/Rz joysticks keep their
original stick/twist mapping until a quadrant profile is explicitly chosen. Wheels and knobs get a helm axis. Keyboard
lever steps remain available with a wheel or knob. Any available physical axis can be assigned
(including analog triggers) to `PortLever`, `StarboardLever`, `BothLevers` or `Helm`, inverted, or treated as relative pulses.

Open `Input/Profiles/Scenes/InputCalibrationDev.unity` and enter Play Mode for the mouse-operated
UI Toolkit screen. It lists devices and live unprocessed axis values, captures the astern/port end,
neutral/centre and ahead/starboard end, previews draft outputs, then saves a complete device profile.
Unfinished or cancelled captures never replace saved settings. See [Profiles/README.md](Profiles/README.md)
for the workflow, identity limitations and a real-hardware checklist.

`DeviceProfileRouter` reads assigned layout controls from Input System events and applies calibration
in the mapper **before** `ControlInputSmoother`. Only changed calibrated values take ownership;
noise inside a calibrated neutral band cannot steal keyboard orders. Relative axes accumulate order
and clamp at ±1. Devices without an assignment still work with defaults, and reconnecting hardware
is resolved against the same stable identity without restarting the mapper.

The existing `ferryNeutralDeadzone(min=0.05)` trigger/twist bindings are unchanged: exact zero inside
the band, unchanged magnitude outside it. A user-calibrated axis instead maps each side of its captured
neutral band continuously to the corresponding full-travel endpoint. Calibration does not implement
gear detents, clutch timing or shift interlocks; those remain in the simulation control model.
