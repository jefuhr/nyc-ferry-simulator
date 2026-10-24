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
