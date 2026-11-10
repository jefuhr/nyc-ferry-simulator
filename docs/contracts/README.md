# Shared contracts for v0.5

v0.5 is built by several people and agents working in parallel. These are the shapes they share. Each contract has one owning area, was committed before the work that depends on it started, and changes only by agreement with the release coordinator.

**Rules for every contract:**
- **Additive only.** New channels, actions, fields and element types may be appended. Nothing that exists changes meaning, name or position.
- **Names, not numbers, are the identity.** Files refer to channels, alarms, mounts and actions by their text names.
- **No maker, model, boat, operator or contributor names** in any file covered here.

| # | Contract | Where it lives | Owning area |
|---|---|---|---|
| C1 | Instrument source | `core/FerrySim.Core/Instruments/`, `unity/Assets/FerrySim/Sim/IInstrumentSource.cs` | Core instruments |
| C2 | Wheelhouse layout v2 | `art/blender/data/layout.schema.json` (`screens`, `controls`) | Wheelhouse model |
| C3 | Control map | [controls-v1.schema.json](controls-v1.schema.json) | Wheelhouse controls |
| C4 | Input actions and bindings | `unity/Assets/FerrySim/Input/Bindings/` | Input |
| C5 | Saved files | This page | Settings and HUD |
| C6 | Display skins | [display-skin-v1.schema.json](display-skin-v1.schema.json) | Displays |
| C7 | Jog input and recordings | `core/FerrySim.Core/Contracts/ControlInputs.cs`, this page | Core steering; simulation bridge |
| C8 | Landing survey | [landing-survey.schema.json](landing-survey.schema.json), `data/manifests/landing-survey.json` | Data pipeline |
| C9 | Reference manifest and request ids | [reference-manifest.schema.json](reference-manifest.schema.json), this page | Reference tools |

The JSON Schema files use only the keywords the repository's small evaluator supports (`tools/reference/reference.py`, `SCHEMA_KEYS`), so they can be checked without new dependencies.

## C1: Instrument source

One instrument model computes everything the instruments show. The console screens, the head-up display, the moving wheelhouse controls and the audio all read it, so they cannot disagree.

- **`Channel`** is the flat, append-only list of values. All are SI; `NaN` means not available. **`ChannelNames`** gives each a stable text name such as `engine.port.oil-pressure`. The two engines differ only in the side token, so a skin can write `engine.{side}.speed`.
- **`IInstrumentReadout`** returns channel values, the alarm list and the alarm event log.
- **`IInstrumentSource`** (Unity) exposes the readout of a running vessel plus acknowledge, silence and lamp test.

**The model is outside the state digest.** It reads the simulation and never writes to it. Acknowledge, silence and lamp test are viewer actions: they are not recorded and reset when the run changes.

**Alarm sequence:**

| Phase | Meaning | Lamp | Sounder |
|---|---|---|---|
| `Normal` | Condition absent, nothing waiting | Off | Off |
| `Unacknowledged` | Condition seen, not yet acknowledged | Flashing | On, unless silenced |
| `Acknowledged` | Acknowledged while the condition persists | Steady | Off |
| `RingBack` | Condition went away before it was acknowledged | Slow flash | Off |

**Lamp rhythm.** Every viewer lights a lamp with the one pure function `AlarmAnnunciator.LampLit(phase, lampTest, time)`, passing the alarm's `Phase`, the readout's `LampTest` and the simulation time of the state it is showing (`IVesselTelemetrySource.State.Time`). No viewer keeps its own oscillator, so all lamps flash in step and a paused run freezes the flash.

**Alarm events** are polled, not pushed. Each consumer keeps its own cursor between `FirstEventSequence` and `NextEventSequence`. The log is bounded; a consumer whose cursor has fallen behind `FirstEventSequence` resynchronises from `Phase` and `SounderOn`. The beep rhythm belongs to the sound clip. A repeat interval per alarm re-raises an acknowledged alarm on simulation time.

Engine curves and alarm definitions are data in `vessels/instruments/<class>.json`. They are separate from `vessels/<class>.json` on purpose: that file's hash is pinned by recordings and by the world import.

## C2: Wheelhouse layout v2

`schema_version: 2` adds two optional lists to a wheelhouse layout file. Version 1 files stay valid.

- **`screens`**: one entry per console screen mount, with `centre`, `size`, `tilt` and `focus_eye` (where the camera goes for the lean-in view), and an optional `yaw` that turns the screen about the vertical axis toward the helm. The sign convention for `yaw` is stated in `art/blender/data/README.md` and pinned by a test.
- **`controls`**: one entry per lever, jog lever, wheel, button, switch, lamp, needle or card, with `kind`, `pivot`, and where they apply `axis`, `travel`, `normal`, `lamp` and `colour`.

**Conventions:**
- Positions are `[x from the centreline, y above the wheelhouse floor, z from the forward window (negative aft)]` in metres, the same as the existing lever and screen values.
- Directions are unit vectors in the layout frame: x to starboard, y up, z forward.
- A control's rest pose is zero rotation. Positive travel is a positive rotation about `axis`. For an engine lever, positive travel moves the grip forward.
- In the prefab every control is a child transform at its pivot, named by its `id`. A screen mount's +z points into the screen, and its surface UV (0, 0) is the operator's lower left.
- All lamps share one mesh and one material. `lamp` is the lamp's index in that bank.

## C3: Control map

`controls-v1.json` ties each control mount to what moves it and to the input actions that operate it.

```json
{
	"version": 1,
	"controls": [
		{"mount": "PortLever", "channel": "lever.port", "range": [-1, 1], "actions": ["Helm/PortLeverStep", "Helm/PortLever"], "label": "Port engine"},
		{"mount": "PortNFU", "channel": "steering.jog", "range": [-1, 1], "actions": ["Helm/NfuJog"], "label": "Jog lever"},
		{"mount": "RudderNeedle", "channel": "rudder.port", "range": [-0.349, 0.349]},
		{"mount": "SteeringAlarmLamp1", "alarm": "steering.failure"}
	]
}
```

A channel value is mapped linearly from `range` onto the mount's `travel` and clamped. A control with no `channel` or `alarm` does not move; it can still carry `actions` for a popup.

## C4: Input actions and bindings

- **`ActionIds`** names the actions other assemblies refer to, as `Map/Action`.
- **`IBindingService`** is the one place bindings are changed, saved and looked up. The actions asset is instantiated more than once, so every instance is registered with the service and an override reaches all of them.
- **Resetting one action never changes another.** If the action's default control has since been taken by a different action, `TryReset` returns false and names that action, and nothing changes. `ResetAll` always works.
- **Analog axes** stay with the device profiles (`input-profiles-v1.json`) and are not part of this service.

**Maps and when they are live:**

| Context | Maps | Live when |
|---|---|---|
| Wheelhouse | Helm, Displays, Alarms, Camera | Any view except free-fly |
| Free-fly | FreeFly, Camera | The free-fly view; the Helm map is disabled |
| Global | Audio, Interface | Always |

Two bindings conflict only if their contexts can be live at the same time. The same rule is used by the binding tests and by the options menu.

**New default keys.** Every letter and digit is already bound, so new actions default to function keys and punctuation.

| Action | Default |
|---|---|
| `Helm/NfuJog` | F6 and F7; numpad 4 and 6; d-pad left and right |
| `Camera/ViewFreeFly`, `ViewChase`, `ViewWheelhouse` | F1, F2, F3 (unchanged) |
| `Camera/BowView` | B (unchanged) |
| `Camera/BowAutoToggle` | F4 (was Shift+B) |
| `Camera/FocusCycle` | F5 |
| `Camera/LookHold`, `LookDelta`, `Recentre`, `Scroll` | Right mouse button, mouse movement, middle mouse button, scroll wheel (unchanged) |
| `FreeFly/Move`, `Vertical`, `Fast`, `Slow` | W A S D, Q and E, Shift, Ctrl (unchanged) |
| `Alarms/Acknowledge`, `Silence`, `LampTest` | `/`, `\`, hold F9 |
| `Interface/Options`, `KeybindHints`, `HudEdit` | F10, hold backquote, F8 |

## C5: Saved files

All three sit in the application's persistent data folder beside `input-profiles-v1.json` and follow the same rules as `DeviceProfileStore`: a version number, an atomic write through a temporary file, and a load that is all or nothing. A file that fails to load is ignored and the defaults are used.

```json
{
	"version": 1,
	"display": {"palette": "auto", "distanceUnit": "feet", "depthAlarmMetres": 1.524, "vectorMinutes": 2.0, "predictor": "straight"},
	"hud": {"layout": "console", "keybindHints": "hold"},
	"console": {"screens": true},
	"audio": {"master": 1.0, "muted": false, "alarms": 1.0},
	"controls": {"lookSensitivity": 0.15}
}
```
`settings-v1.json`. `predictor` is `straight` or `curved`; `keybindHints` is `off`, `hold` or `always`; `hud.layout` names a preset (`console`, `classic`, `minimal`, `everything`) or a saved layout.

```json
{
	"version": 1,
	"layouts": [
		{"name": "mine", "basedOn": "classic", "widgets": [{"id": "compass", "visible": true, "anchor": "bottom-center", "x": 0, "y": -12, "scale": 1.0}]}
	]
}
```
`hud-layouts-v1.json`. `anchor` is one of nine positions (`top-left`, `top-center`, `top-right`, `middle-left`, `middle-center`, `middle-right`, `bottom-left`, `bottom-center`, `bottom-right`); `x` and `y` are offsets from it in 1280×720 reference pixels. A widget missing from a layout takes its position from `basedOn`.

```json
{
	"version": 1,
	"overrides": [{"action": "Helm/Horn", "scheme": "Keyboard", "slot": 0, "path": "<Keyboard>/slash"}]
}
```
`input-bindings-v1.json`. An override whose action or slot no longer exists is dropped with a warning; the rest still load.

**Widget ids** are stable once published: `classic-panel` (the whole v0.4 panel as one widget), `compass`, `heading`, `speed`, `depth`, `rudder`, `engine-port`, `engine-starboard`, `environment`, `clock`, `traffic-alarm`, and the console pop-outs `screen:radar`, `screen:plotter`, `screen:engine-port`, `screen:engine-starboard` and `screen:alarms`. A pop-out shows the texture the console supplies for that screen id through `IHudScreenSource`, or a neutral "no signal" face when there is none. An unknown widget id in a saved layout is dropped with a warning; the rest of the layout still loads.

## C6: Display skins

A skin lays out one console display as data: the elements, the channels they show, their ranges and the refresh rate. See [display-skin-v1.schema.json](display-skin-v1.schema.json).

- Skins in the repository are generic look-alikes. They may match a real unit's fields, positions, colours and scales; they carry no maker names, model names or logos.
- A skin is rejected whole, with a message naming the element, when it names an unknown channel, alarm, element type, unit, page, colour, group, format or action, gives a property that does not apply to its element type, places a rectangle outside the reference size, or gives a band outside its element's range.
- A user can load a private skin from `skins/<display>/<id>.json` in the persistent data folder. It is looked up before the shipped skin and is never shipped; a broken one is reported and the shipped skin is used.

**Element types:** `dial`, `bar`, `value`, `lamp`, `text`, `softkey`, `frame` and `list`. Appended since the first version, all optional:

| Addition | Applies to | Meaning |
|---|---|---|
| `list` element | | Every alarm the readout defines, grouped by alarm group, each with a lamp and its state in words. The rows come from the readout, so a vessel that gains alarms needs no skin change |
| `group` | `list` | Show only that alarm group's alarms |
| `format` | `value` | `number` (the default) or `gear`, which shows a shift-phase channel as a word |
| `action` | `softkey` | `acknowledge`, `silence` or `lamp-test` (held while pressed). The view raises an event; the host forwards it to the instrument source. A soft key has a `target` page or an `action`, never both |
| `bands` | `dial`, `bar` | Coloured ranges such as warning and red-line arcs, each `{range, colour}` in the display unit, drawn once with the static face |

## C7: Jog input and recordings

**Input.** `ControlInputs` has a fourth value, `Jog`, in [-1, 1]. The tiller (`Helm`) and the jog lever are both live at once on the real boats; while `Jog` is non-zero it drives the rudder at the steering rate and the tiller is ignored. What the rudder does on release is a vessel option whose default is to return to the tiller's order.

**What must stay bit-identical:**
1. The replay of every existing recording: same states, same state digest.
2. Any run whose jog demand is zero at every step, against the same run before this input existed.
3. The bytes written by any control source that is not an `INfuControlSource`: tests, journeys, benchmarks and the command-line tool.
4. The bytes of `vessels/*.json`.

**Control trace.** Two header rows are valid.

| Version | Header row | Written when |
|---|---|---|
| 1 | `time_s,port_lever,starboard_lever,helm` | The control source is not an `INfuControlSource` |
| 2 | `time_s,port_lever,starboard_lever,helm,nfu_jog` | The control source is an `INfuControlSource`, even if the jog lever is never touched |

- A reader accepts either. A row must have exactly as many cells as its header.
- The writer chooses once, when the file is opened. A version 1 recording that meets a non-zero jog demand fails loudly; it never drops the value.
- A build that predates version 2 refuses a version 2 file at the header row, which is the intended behaviour.

**Telemetry CSV.** The format stays `# ferrysim-csv: 1`. A run recorded from an `INfuControlSource` appends one trailing column, `nfu_jog`, after `helm`.
- The strict reader that protects recordings (`unity/Assets/FerrySim/Sim/TelemetryCsv.cs`) accepts the base columns, or the base columns plus that one, and nothing else.
- The debrief's reader (`unity/Assets/FerrySim/UI/Telemetry/TelemetryCsv.cs`) keeps reading by column name, as it always has: it tolerates reordered and extra columns, including `depth_alarm_threshold_m`, and understands `nfu_jog` when present.

## C8: Landing survey

`data/manifests/landing-survey.json` records where each landing really sits: the barge's centre and heading, each berth's face and landed heading, gangway hinges, pile positions, the canopy and the shore structure. See [landing-survey.schema.json](landing-survey.schema.json).

- **Frames.** Positions are `[latitude, longitude]` in WGS84 degrees. Bearings and headings are degrees true. Heights of fixed structure are metres above NAVD88; heights of floating structure are metres above the barge deck.
- **Every number has a source, method and confidence,** in the same shape as the layout files.
- **An absent stop changes nothing.** With an empty survey, every pipeline output is byte-identical to the output without the file.
- **A surveyed stop changes only that stop.** Its contact geometry is rebuilt from the surveyed pose, which deliberately changes replays and digests at that stop. A test pins the set of stops that changed.
- **Trust order** when sources disagree: crew landed headings and berth names; views of crew footage reconstructed in 3D; public aerial imagery (measuring only); the chart polygon; the derived rectangle.

## C9: Reference manifest and request ids

**Manifest.** `manifest.json` sits beside the approved reference sheets and links each sheet to its private source, the consent that covers it and the screening decision that released it. See [reference-manifest.schema.json](reference-manifest.schema.json). The manifest is private; only its shape is public.

**The gate.** A sheet may be catalogued, measured or processed only when all of these hold:
1. it has a manifest entry whose `sha256` matches the file;
2. every source's consent version equals the manifest's `consent_policy`, or its scope is `owner` or `measurement-only`;
3. no source is withdrawn;
4. the screening decision is `approved` or `approved-with-edits`.

**Derived files.** A file computed from sources, such as a geometry-only view of a 3D reconstruction, is screened like any other sheet. It appears twice in the manifest:
- once under `sheets`, with its own screening decision and with `sources` set to the source ids it was computed from, so the gate above checks consent and withdrawal on it directly;
- once under `derived`, with the same `path` and `sha256`, recording how it was made (`method`, `scale`, `pixels_per_metre`, `from`).

A file listed only under `derived` is refused.

**Derived files that live in the repository.** A finished audio clip is committed to the repository, so it cannot sit beside the sheets. Its `derived` entry (method `audio-prep`) names it as `repo:<path from the repository root>` with the clip's hash and the source ids it was cut from. It has no `sheets` entry and is never read through the gate; the entry exists so that withdrawing a source points at the clip that has to be rebuilt or removed. The two entries must agree: same path and hash, and `from` equal to the set of source ids in the `sheets` entry. Withdrawing any source withdraws the derived file and flags every value measured from it.

**Contributors are roles** (`owner`, `crew-a`, `builder`), never names or account ids.

**Request ids** name the items on the reference shot list, so an upload can say which request it answers and the request can be followed from wanted to used.

| Prefix | Subject |
|---|---|
| `EXT` | Boat exterior |
| `WH` | Wheelhouse geometry |
| `SCR` | Screens, gauges, alarms and buttons |
| `MOT` | Controls in motion |
| `LND` | Landings |
| `SND` | Sound |
| `HND` | Handling answers |

An id is the prefix, a hyphen and two digits, optionally followed by `@` and a scope: a class (`EXT-01@k-class`) or a GTFS stop id (`LND-03@87`). A request moves through `open`, `received`, `screened`, `used` and, if the footage cannot be used, `needs-reshoot`.
