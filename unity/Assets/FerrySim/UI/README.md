# UI

Owner: **wheelhouse-ui**. Assembly `FerrySim.Unity.UI` (namespace `FerrySim.UI`): the wheelhouse instrument panel, the HUD for external camera views, and the debrief screen. Built with UI Toolkit only (no uGUI, no TextMeshPro); everything is drawn with `Painter2D` and plain labels.

The PlayMode tests write screenshots to `artifacts/unity/ui-*.png` (not committed): the panel at three moments of a crash stop and in a turn, by day and night, the alarm and contact states, a one-row contact latched between frames, no data (no source, and a run that has ended, with the HUD), 720p and 1440p, the HUD (also over a white backdrop), the dev toolbar and the debrief.

## Run it

- **Dev scene:** open `Scenes/UIDev.unity` and press Play. A `FakeTelemetrySource` plays the committed crash-stop sample in a loop; the toolbar (top right) switches day/night, ft/m, the alarm threshold, the panel and HUD, playback, the next CSV (the sample, then every CSV in `artifacts/maneuvers/`) and the debrief.
- **Rebuild the dev scene** (deterministic: the same bytes every time):
  `lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -nographics -quit -projectPath unity -executeMethod FerrySim.UI.Editor.UIDevSceneBuilder.Build -logFile "$PWD/artifacts/unity/scene-build.log"`, or the menu *FerrySim > UI > Rebuild UIDev Scene*.
- **Tests:** `UI/Tests/EditMode` and `UI/Tests/PlayMode` run with the project's tests (`unity/README.md`, `scripts/check.sh --only unity`). PlayMode needs graphics and writes the screenshots.

## Using it from other systems

Add `WheelhouseUI` to a GameObject. It creates its own `UIDocument` and panel settings and refreshes every frame from an `IVesselTelemetrySource`: the serialized one, else `WheelhouseUI.Source` set from code, else the first one in the scene (the bridge in preference to a fake).

| Member | For |
|---|---|
| `View.Palette`, `View.TogglePalette()` | Day and night palettes |
| `View.DistanceUnit`, `View.ToggleDistanceUnit()` | Feet or metres for depth, alarm threshold and track |
| `View.DepthAlarmThresholdMetres`, `View.StepDepthAlarmThreshold(±1)` | Shallow alarm threshold (steps of 1 ft or 0.5 m) |
| `View.AcknowledgeAlarms()` | Stops the alarm flashing (clicking the depth block does the same) |
| `View.PanelVisible`, `View.HudVisible` | Wheelhouse view vs external camera views |
| `View.Debrief.Show(recording, parameters)`, `View.Debrief.TryLoad(path)`, `View.DebriefVisible` | Debrief of an in-memory recording or a CSV |

Key and controller bindings for these belong to the input profiles; the dev toolbar is only for the dev scene.

The UI relies on these parts of `IVesselTelemetrySource` (contract version 2):

- **`HasTelemetry`**: while it is false every readout on the panel and the HUD shows `---`, no alarm is shown and the status bar says NO DATA, whatever `State` and `Telemetry` still hold. A scene source that has been destroyed counts as no source. An unset `ScenarioStartUtc` (`default`) is never formatted: the clock shows `---`.
- **`RunId`**: when it or the source itself changes, the panel forgets the previous run: the depth alarm and its acknowledgement, and the contact episode. A dropout within one run (`HasTelemetry` false for a while) keeps them.
- **`Contact`**: the contact indicator is driven by the latch, which counts every physics step with contact, never by `Telemetry.ContactForce` sampled at frame time. A run's latest step is `round(100 × State.Time) − 1` (time is `SimulationClock.TimeAtStep(stepCount)`), which is how the indicator tells a touch still going on from one that ended between frames. `Telemetry.ContactForce` is only checked for being finite: a non-finite reading shows `---`, not NONE.

## What a captain sees first, and why

The panel is ordered by what goes wrong first in close-quarters handling of a twin-screw boat, and by where things are on the boat.

1. **Is each engine actually in gear?** The gear badges (NEUTRAL, AHEAD, ASTERN, SHIFTING) are the boldest text in each engine column, directly under that engine. Believing you have drive while the clutch is still shifting is the classic landing mistake, so the three states differ by fill, outline and word, not just color: AHEAD and ASTERN are filled, SHIFTING is filled amber (no reliable drive yet), NEUTRAL is only outlined. None of them flash; shifting is normal.
2. **Command against response, per engine.** Each engine has one column: lever command (what I asked for), gear (what the clutch is doing), engine rpm (needle and digits) and shaft direction and rpm (what the propeller is doing). The delay between them is what a learner has to feel (3.2 s from AHEAD to ASTERN in the recorded crash stop), and keeping it in one column shows it without hunting. Port is on the left and starboard on the right; the levers sit toward the middle like the twin control head.
3. **Where am I going, and how fast?** Heading and speed over ground are the largest numerals on the panel. Over-ground speed is primary because it decides a landing; speed through water sits beside it, so the difference shows the current. The compass card turns under a fixed lubber line, and a dashed course-over-ground vector shows set and drift against the heading.
4. **The rudders.** One horizontal scale: port side on the left, starboard on the right (red and green by day), the port rudder's pointer above and the starboard rudder's below, each with its own readout. Hard-over marks come from the vessel file.
5. **Depth and contact stay quiet until they matter.** Depth under keel is a calm readout until it drops below the alarm threshold; then it is the only flashing thing on the screen (1 Hz, well under the 3 flashes per second photosensitivity limit) until acknowledged, then steady until the depth recovers past a hysteresis band. Every new run starts with a fresh alarm, so an acknowledgement never carries over. Contact is not an alarm, because touching the fenders at a landing is normal: it lights steady orange with the force while the latest physics step touches, then shows the episode's peak, outlined, for 3 s of simulated time after the last touching step. It comes from the source's per-step latch, so a touch that lasts one 10 ms step between two rendered frames is still shown.
6. **Clock, vessel and elapsed time** are the smallest, in the top strip: for logs and the debrief, not for driving. The clock is New York local time from the scenario's UTC start; when CSV playback has no recorded start it says ASSUMED START beside the clock.

## Honest instruments

- Needles and numbers show the latest sample, with no smoothing or animation. The fake source holds the most recent CSV row, like the fixed-step bridge, instead of interpolating.
- Every number is rounded to the resolution it claims (0.1 kn, 1°, 1 rpm, 1% of lever travel, 0.1 below 100 ft or m and whole units above). Anything missing or meaningless reads `---`: no data, a non-finite value (including an unknown contact reading), or a course over ground below 0.2 kn.
- Settings that are still guesses say so where they are shown: the 5 ft alarm default reads `ALARM BELOW 5.0 ft · PLACEHOLDER` on the panel, and the dev toolbar and the debrief call it a placeholder default and unit, pending crew input.
- Labels say exactly what is measured: heading and course are true (°T), SOG is over ground, STW is through water (negative going astern), depth is under the keel.
- Dial markings come only from the vessel file: a redline from rated rpm and hard-over marks appear only when those values exist (otherwise generic scales without them), and the lever notches show the control model's clutch-in position. These are estimated values today; see `vessels/*.json` for provenance.
- Throttle is not shown: the CSV does not carry it, so the fake source reports NaN rather than inventing a number.

## Day and night

- Colors live in C# (`Display/InstrumentPalette.cs`); the style sheet only lays things out, and elements carry role classes (`fs-c-text`, `fs-bg-face` and so on) that the palette fills in. `PaletteTests` measures the rules below.
- **Day** is a dark console with bright numerals, like real consoles that keep glare off the windows: primary numerals at least 7:1 contrast (WCAG AAA), captions and units at least 4.5:1 (AA), graphics at least 3:1. Every day text color is tested against every background it is drawn on, and a PlayMode test checks every visible label as rendered. The HUD and the dev toolbar float over the 3D view, so their backing is opaque: a translucent one measured 3.16:1 over the test backdrop and worse over bright sky.
- **Night** is dim red to amber only: no blue, green or white light (red ≥ green ≥ blue, green at most 0.65 of red), luminance capped, numerals still at least 3:1. Port and starboard lose red and green at night and are told apart by position and their P/S labels. Buttons and fields take palette colors too, so nothing shows neutral grey light.
- Nothing relies on color alone: gear by fill and word, plot series by solid and dashed lines, debrief events by shape (diamond gear shift, circle contact, triangle depth alarm).

## Layout and scaling

The UI is designed at 1280×720 reference pixels and `PanelSettings` scale it with the screen (matching width and height equally), so 2560×1440 is exactly 2×. A PlayMode test renders both and checks that every instrument sits at the same panel-space position and inside the panel. The panel is the bottom 300 reference pixels (42% of a 16:9 screen); the HUD sits at the bottom centre, lifted above the panel if both are shown.

## Debrief

Loads a ferrysim telemetry CSV (CLI or Unity run) or takes a `TelemetryRecording` from memory. It plots the track (north up, equal scale on both axes, the boat drawn to length at the cursor), speed (SOG, STW), heading (HDG, COG), controls (levers and helm as % of travel, drawn as steps because they hold until the next sample) and rudder angles against time. Gear shifts are one event from leaving a steady gear to reaching the next, with the shift time; contacts are episodes (touches within 3 s are one landing); depth alarms run the panel's own alarm logic over the recording. When the recording carries the threshold in force at each sample (the optional `depth_alarm_threshold_m` column) the marks are the alarms the learner had; otherwise they are recomputed at the current threshold, and a note under EVENTS says they are retrospective, using the current threshold. Scrub by dragging on any plot or the timeline, clicking the track, the arrow keys (Shift for 10 samples), or the event list.

## Telemetry files

- `Telemetry/TelemetryCsv.cs` reads format version 1 by column name (extra columns are ignored) and converts kn, deg and rpm back to SI. Malformed files are errors naming the line and column, never silent zeros.
- The `# scenario_start_utc:` header the Unity run recorder writes (ISO 8601, for example `2026-06-01T13:00:00Z`) sets the playback clock; a malformed one is an error. CLI files have none, so `FakeTelemetrySource` falls back to its inspector setting and the panel marks the clock ASSUMED START.
- The optional `depth_alarm_threshold_m` column (metres under the keel, one per row) is the alarm threshold in force at each sample. Nothing writes it yet; the bridge recorder could. The CLI and `tools/plot_maneuver.py` skip it like any extra column.
- `Telemetry/TelemetryCsvWriter.cs` writes the same format digit for digit as the CLI's `CsvRecorder`, plus the threshold column when a recording has it; the committed sample round-trips byte for byte. A Unity run can collect `TelemetryRecordingBuilder.Add(state, telemetry, controls)` at 10 Hz and save it with the writer.
- `FakeTelemetrySource` plays a recording through the contract: no telemetry before the first row or after the end, a new `RunId` for every load, loop and seek backwards, and a contact latch that counts every row with contact up to the one shown.
- `Samples/t-crash-stop.csv` is unmodified CLI output (`ferrysim maneuver crash-stop --vessel vessels/t-class.json`), used by the dev scene and the tests.
- Development tools resolve paths and `vessels/<id>.json` against the repository root (`UIRepoPaths`), so they work in the editor, not in a player build.

## Folders

| Folder | Contents |
|---|---|
| `Display/` | Units and readout formatting, gear-state logic, depth alarm, contact monitor, New York time, dial scales, palettes |
| `Telemetry/` | CSV reader and writer, recordings, `FakeTelemetrySource`, vessel file loading |
| `Instruments/` | Vector-drawn instruments: tachometer, lever, rudder, compass, gear badge |
| `Panel/` | The panel, HUD, root view, panel settings and the `WheelhouseUI` host |
| `Debrief/` | Plot scaling, event detection, plots, track, timeline and the debrief screen |
| `Resources/FerrySimUI/` | Style sheet (layout only) and the runtime theme |
| `Editor/` | `UIDevSceneBuilder` (editor-only assembly) |
| `Scenes/`, `Samples/` | Dev scene and the sample CSV |
| `Tests/EditMode`, `Tests/PlayMode` | Logic tests; rendered panel, HUD, debrief and dev-scene tests with screenshots |

## Placeholders to confirm

- Depth reads in **feet** by default and the shallow alarm defaults to **5 ft under the keel**, both until crew say what they use. The panel, the dev toolbar and the debrief mark the default as a placeholder.
- There is no hard-contact alarm: what counts as a hard landing is a question for crew.
- The fonts are UI Toolkit's default runtime font; its digits are not strictly fixed-width, so readouts are right-aligned in fixed boxes.

## Questions for crew about the real gauges

Asked within the limits of `docs/reference-capture-kit.md` (permission first, nothing security-sensitive).

1. Where are the tachometers: one above each control head, together on the console, overhead, or on an engine monitoring screen (which make)? Analog, digital or both?
2. What does the tach scale read (0 to 2500, 0 to 3000, ×100?) and is there a red band? From what rpm?
3. How do you see the gear state: lights on the control head (which colors, do they blink while shifting?), a screen, or only by feel and sound? Is there any sign that a shift is still in progress?
4. Is shaft speed or shaft direction shown anywhere, or only engine rpm?
5. The rudder angle indicator: one pointer or one per rudder? Overhead or on the console, a dial or a bar? What range does the scale show?
6. Heading: gyro or magnetic, card or digital, true or magnetic? Where do you read heading most: compass, chartplotter or radar?
7. Speed: GPS speed over ground, a log (speed through water), or both? Which do you use on approach?
8. The depth sounder: feet, metres or fathoms; below the transducer, below the keel or below the surface? What shallow alarm do you set, and how does it alert and get silenced?
9. Is there a rate-of-turn indicator, and do you use it?
10. At night: red lighting, dimmed screens (which palette), anything you switch off?
11. Coming alongside a landing, what do you look at first and second, and how much of the time are your eyes outside?
12. Is fender load or contact shown anywhere, or felt? What separates a normal landing from a hard one?
13. Which clock do you use in the wheelhouse?
14. How do the K-class gauges and their layout differ from the T-class?
