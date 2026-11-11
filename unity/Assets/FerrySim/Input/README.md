# Input

Owner: **unity-sim-bridge-controls**. Assembly `FerrySim.Unity.Input` (namespace `FerrySim.Controls`, not `FerrySim.Input`, which would shadow `UnityEngine.Input`). Devices produce normalized `ControlInputs` through the Input System package and nothing else. Detents, clutch timing, shift interlocks and steering behaviour belong to the core's control model. `Profiles/` belongs to **unity-input-profiles**.

## Pieces

| File | Role |
|---|---|
| `FerrySimControls.inputactions` | The Helm, Displays, Audio, Camera, FreeFly, Alarms and Interface maps and default bindings |
| `HelmInputMapper` | Turns the Helm map into lever and helm positions, plus button events |
| `DisplayInputMapper` | Turns the Displays map into events for the harbor's chartplotter and radar |
| `AudioInputMapper` | Turns the independent Audio map into a session-only mute event |
| `ControlInputSmoother` | Device-independent noise smoothing, once per physics step |
| `HelmInputController` | MonoBehaviour: the runner's `IControlInputSource`; buttons pause and reset the run, cycle the camera and sound the horn |

## The Helm map

| Action | Type | Keyboard | Gamepad | Joystick (generic HID) |
|---|---|---|---|---|
| `PortLever` | axis, −1..1 | | left stick Y | |
| `StarboardLever` | axis, −1..1 | | right stick Y | |
| `BothLevers` | axis, −1..1 | | right trigger ahead, left trigger astern | stick Y |
| `Helm` | axis, −1..1 | | | stick X, twist (`{Twist}`) |
| `HelmStep` | step and hold | ←/→ | left/right shoulder | |
| `Midships` | button | M | | |
| `PortLeverStep` | step | Q ahead, A astern | | |
| `StarboardLeverStep` | step | E ahead, D astern | | |
| `BothLeversStep` | step | W ahead, S astern | d-pad up/down | |
| `Horn` | button, held | H | south button | trigger |
| `Pause` | button | P | start | |
| `Reset` | button, hold 1 s | R | select | |
| `CameraCycle` | button | C | north button | |
| `NewRun` | button | N | | |

`NewRun` raises `HelmInputMapper.NewRunRequested`. In the slice scene, `SliceBootstrap` opens the wheelhouse UI's scenario picker on it. While the picker is open, the run is paused and the helm map is disabled, so no key or axis moves the boat; Escape or the picker's Close button closes it.

Signs follow `ControlInputs`: +1 is full ahead, −1 full astern, and +1 helm is hard over to starboard. The Input System inverts HID Y axes, so stick forward reads +1.

## The Displays map

Keyboard only, for the harbor wheelhouse's chartplotter and radar (`Harbor/Integration/README.md`, Displays). The composition root enables the map only in the wheelhouse view with the scenario picker and the debrief closed, and a display's keys act only while that display is visible. No key is shared with the Helm map, the bow view (B, F4), the camera (F1–F3) or the picker and debrief (Escape, Enter, Space, the arrows, Page Up/Down, Home, End), and no Shift combination is used: a plain key binding fires on Shift+key too, so Shift+arrows would also step the helm. `BindingConflicts` checks duplicate controls only in contexts that can run together. The same rule guards the defaults and live rebinding. Free-fly movement may share helm keys because the Helm map is disabled while flying. Picker navigation keys remain reserved.

| Action | Key | Action | Key |
|---|---|---|---|
| `ChartplotterToggle` show / hide | 1 | `RadarToggle` show / hide | 2 |
| `ChartZoomIn` / `ChartZoomOut` (0.25 … 8 NM) | = / − | `RadarRangeUp` / `RadarRangeDown` (0.125 … 6 nm) | ] / [ |
| `ChartOrientation` north-up / course-up | O | `RadarOrientation` north-up / head-up | Z |
| `ChartCentre` back to own ship | U | `RadarEblLeft` / `RadarEblRight` 1° | , / . |
| `ChartPanUp` / `ChartPanLeft` / `ChartPanDown` / `ChartPanRight` 40 px | I / J / K / L | `RadarVrmIn` / `RadarVrmOut` 2 % of range | ; / ' |
| `ChartPalette` day, dusk, night | F | `RadarPalette` day, dusk, night | X |
| `ChartNextTarget` / `ChartClearSelection` | T / Y | `RadarGainDown` / `RadarGainUp` 0.1 | 3 / 4 |
| `ChartCpaLimitDown` / `ChartCpaLimitUp` 0.05 NM (0.05 … 1 NM) | 7 / 8 | `RadarSeaDown` / `RadarSeaUp` 0.05 | 5 / 6 |
| `ChartTcpaLimitDown` / `ChartTcpaLimitUp` 1 min (1 … 30 min) | 9 / 0 | | |
| `ChartAlarmMute` mute / unmute the CPA alarm | G | | |

The CPA alarm keys (v0.3.1) are display settings on the chartplotter view: they are never recorded and never part of a run's digest, they are separate from the run's own `TrafficAlarmLimits`, they hold across runs in a session and reset to 0.1 NM and 6 min, unmuted, at launch. Muting hides only the alarm treatment (the banner and the row and target fill); the targets and their CPA/TCPA values stay. Like every display key they act only while the chartplotter is shown, and never while the scenario picker is open (typing digits or G into the landing search changes nothing on the plotter).

Palettes follow the scene's light: at night both displays hold their night palette, so F and X cycle back to it at once; by day a palette chosen with F or X holds until the next night resets it.

## The Audio map

`Mute` on **V** toggles the runtime audio bus (`Audio/README.md`). The rig enables its private Audio map independently of Helm and Displays, so the mute key remains available while the picker holds the helm, except while a text field has focus (typing a landing name must not change mute). It is a session setting, never a recorded input. V is free across all action maps and the camera, bow-view and picker reservations; the existing global key-map tests and audio tests guard against clashes. Player benchmarks pass `-ferrysim-audio-volume 0`, which leaves the audio graph running with zero master gain.

## How positions are set

**Step actions** move a lever one notch per press: 5 notches each way by default (`leverNotches`), so 0.2 per press. They click through −1 … −0.2, 0, 0.2 … 1. Neutral is exactly 0, the ends are exactly ±1, and the levers clamp there. A lever left between notches by an analog device steps to the next notch in that direction.

**Helm steps** order four detents each way: −20, −15, −10, −5, 0, 5, 10, 15, 20 degrees with the crew-reported T/K 20° hard-over. Each press moves to the next 0.25 normalized detent, releasing holds it, and M orders exact midships. Holding repeats after 0.4 s, then every 0.2 s (5 steps/s); a stalled render frame catches up by at most eight steps. These timing choices are UI estimates, not crew measurements. Run start and M cancel the repeat until a fresh press. The last moved absolute axis takes ownership and cancels held-button repeat.

**Absolute axes** set a lever or the helm to the device's position. When the device returns to rest, so does the control.

**Device priority:** the device that moved a control last wins. A gamepad lying idle does not override levers set from the keyboard.

**Run start:** when a run starts or is reset, stepped levers and helm return to neutral and absolute axes are read again. A throttle quadrant left at half ahead stays at half ahead.

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

## Bow view

In the wheelhouse only, B toggles a smooth bow-deck look and F4 toggles automatic approach tilt (on by default, remembered for the session). Right-mouse head look overrides automatic tilt until the next approach; middle mouse recentres. See [camera behavior](../World/Cameras/README.md) for geometry, thresholds and hysteresis. These camera choices are render-only and never recorded.

## Binding service (v0.5)

`InputBindingService.Instance` implements the frozen `IBindingService` contract. Register each private
runtime asset immediately after instantiation with `InputBindingService.Instance.Register(instance)`;
unregister before destroying it with `Unregister(instance)`. The helm and camera controllers register
their own copies. Display and audio composition roots register their copies during release integration.
`CreateInstance()` supplies a registered copy for new consumers; callers own disabling and destroying it.

Rows enumerate buttons and each composite part in asset order, with zero-based slots within a scheme.
Helm analog axes remain exclusively with device profiles. Mouse delta and scroll are read-only axes;
mouse buttons share the Keyboard scheme. `DisplayString` includes current overrides. The controller
caches generated key help and refreshes it on `BindingsChanged`.

### Player-facing binding labels

`Bindings/BindingLabels.cs` is the label table for every rebindable action and slot. Use sentence case
and plain words, with the control noun followed by its direction: "Port engine ahead", "Tiller to port",
"Jog lever to starboard". Spell out port and starboard, reuse the nouns in `controls-v1.json`, and name
both sides of a composite separately. Labels must be unique within a scheme; a second binding pair
uses " (alternate)" on each direction. Slots follow the asset's binding order within each scheme.
Add table entries with any new rebindable action or slot. An unknown row retains the generated label,
but the shipped-asset coverage test fails if any row lacks an explicit entry.

For group headings use `BindingLabels.GroupName(mapName)` (for example "Free-fly camera"). For a row's
key use `IBindingService.DisplayString(row.ActionId, row.Slot, row.Scheme)` instead of splitting the
existing two-argument combined string. The slot overload reads the binding directly, reflects overrides,
and returns empty for a missing slot; the combined-string overload retains its existing behaviour.

Interactive capture disables the registered live maps and holds their existing lever orders. It restores
previously enabled maps after the capture key and previously held buttons are released, so the capture
press cannot become a simulation command. Escape or `CancelRebind()` cancels once. Conflicts report
the holding action and apply nothing. Saving happens before publishing a change; an I/O failure leaves
live bindings intact and is exposed as `LastError` (direct writes throw; capture completes as cancelled).
`TryReset` refuses a single-action reset that would collide, names its holder and changes nothing.
`Reset` throws for that case; reset the conflicting action first or reset all.

`BindingOverrideStore` writes C5 `input-bindings-v1.json` in the persistent data directory. Rejected loads
restore defaults without rewriting the file; obsolete actions or slots are dropped with warnings. Loaded
conflicting documents are rejected whole. Writes replace a sibling temporary file atomically.

Live rebinding and saved overrides share semantic control validation: the registered device layout must
belong to the declared scheme (Mouse joins Keyboard), and the path must resolve to button controls.
Resolution uses layout metadata, including inheritance, aliases, usages and nested controls; no device
needs to be connected or created. A base-layout path conflicts with a derived-layout path when both
resolve to the same control on that derived layout, subject to the existing live-context rule.
An invalid replacement path is dropped with a warning naming its action, scheme, slot and path; other
valid overrides still load and the dropped entry leaves its default intact. Structurally corrupt documents,
unsupported versions, duplicate valid entries and conflicting final bindings still reject the whole file.

A saved override whose device layout is not registered is kept unchanged, with a warning that it cannot
be checked until its device is connected. Its path is inert while that layout is unknown and survives
unrelated saves. A later load validates it once the layout is registered. Live `TryApply` still refuses
unknown layouts. Connecting the device after loading can make the kept binding go live unchecked;
there is no automatic validation on device arrival. A live edit validates its chosen control as usual.

The Camera map stays live across views. `CameraRig` and `FreeFlyCameraController` set the service's
free-fly context, suspending Helm and restoring its previous enabled state when leaving. A helm map
explicitly disabled by the picker stays disabled. Movement buttons held while flying must be released
and pressed again before they can step a lever after returning. FreeFly actions are enabled only by the fly controller.
F4 replaces the ambiguous automatic-bow Shift chord; F5 is focus cycle, F6/F7 and numpad 4/6 (or d-pad
left/right) are jog. Alarms use slash, backslash and held F9. Interface uses F10, held backquote and F8.

`Bindings/Resources/FerrySimInput/BindingDefaults.json` is the player-loadable snapshot of
`FerrySimControls.inputactions`, allowing cameras to start before any helm exists. After editing the asset,
copy its exact JSON to that resource; an equality test prevents drift. No UnityEditor asset lookup runs
in players. `Camera/Scroll` preserves chase zoom and free-fly speed scaling.
