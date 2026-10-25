# UI

Owner: **wheelhouse-ui** (wave 2a: **scenario-panel-ui**). Assembly `FerrySim.Unity.UI` (namespace `FerrySim.UI`): the wheelhouse instrument panel, the HUD for external camera views, the scenario picker, and the debrief screen. Built with UI Toolkit only (no uGUI, no TextMeshPro); everything is drawn with `Painter2D` and plain labels.

The PlayMode tests write screenshots to `artifacts/unity/ui-*.png` (not committed): the panel at three moments of a crash stop and in a turn, by day and night, the alarm and contact states, a one-row contact latched between frames, no data (no source, and a run that has ended, with the HUD), 720p and 1440p, the HUD (also over a white backdrop), the dev toolbar and the debrief. For the water and the air and the picker: `ui-env-{day,night}-{720p,1440p}.png` (panel and HUD with a pack day's readouts), `ui-env-warnings-*` (outside the day's data, off the depth grid, a day without wind), `ui-env-nodata-day.png`, `ui-picker-{panel,hud}-{day,night}-{720p,1440p}.png` and `ui-picker-refusal-{day,night}.png`.

## Run it

- **Dev scene:** open `Scenes/UIDev.unity` and press Play. A `FakeTelemetrySource` plays the committed crash-stop sample in a loop; the toolbar (top right) switches day/night, ft/m, the alarm threshold, the panel and HUD, playback, the next CSV (the sample, then every CSV in `artifacts/maneuvers/`) and the debrief. **New run** opens the scenario picker on a `FakeScenarioHost` (the slice's landings, the 28 scenario days, the T- and K-class); starting a run restarts the sample at the chosen time and gives a `FakeEnvironmentSource` the chosen day. Its water and air are SYNTHETIC (a semidiurnal tide, a reversing East River stream, a steady south-westerly), not data, and the toolbar says so. The fake marks 2025-01-15 as a day without wind (in the packs built so far every day has wind) and refuses the K-class (the recording is a T-class), so both show. **Off grid** makes the fake count depth misses, to show the warning.
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
| `ConnectScenario(environment, catalog, launcher)` | The water and the air, and what the picker offers and starts (below) |
| `ShowScenarioPicker(true/false)`, `View.PickerVisible`, `View.TogglePicker()` | The scenario picker over the 3D view |
| `View.PickerVisibilityChanged` | Raised with true when the picker opens and false when it closes |

Key and controller bindings for these belong to the input profiles; the dev toolbar is only for the dev scene.

## Wiring the water, the air and the scenario picker

The panel's top strip and the HUD's second row read an `IEnvironmentReadoutSource`; the picker reads an `IScenarioCatalog` and starts runs through an `IScenarioLauncher` (all three in `Sim/`, owned by the coordinator; unity-environment implements them and exposes them on `SliceBootstrap`). A scene connects them in one of two ways:

1. **From code (preferred for the slice).** Once the simulation side has built them, for example in `SliceBootstrap.Awake`:

   ```csharp
   wheelhouseUI.ConnectScenario(environmentReadouts, scenarioCatalog, scenarioLauncher);
   ```

   Any argument may be null: missing readouts read `---`, and a picker without a catalog or launcher shows why it cannot start, with Start disabled. What `ConnectScenario` sets is never replaced by a scene search, and it can be called before or after `WheelhouseUI` is enabled. To open the picker, call `wheelhouseUI.ShowScenarioPicker()` from a key binding or a menu (it reads the catalog again each time it opens).
2. **Serialized references.** `WheelhouseUI` has `environmentSource`, `scenarioCatalog` and `scenarioLauncher` fields; each takes a component implementing the interface (the launcher may be left empty when the catalog component is also the launcher). Fields left empty are filled at enable time with the first such component in the scene, preferring anything to the UI's fakes, as the telemetry source is found today.

The picker's keys (Tab, Up, Down, Page Up, Page Down, Home, End, Enter, Escape) are ones the Helm map does not use (`Input/README.md`), and its fields swallow the UI's directional navigation, which the Input System's default UI actions also raise for W, A, S and D. So the picker never steers the boat and the lever keys never change the picker. The Input System still reads the helm keys while the picker is open, though, so a host that wants the boat to wait can pause the run or hold the helm on `View.PickerVisibilityChanged`. The slice scene does both: `SliceBootstrap` connects the runner and itself, opens the picker on the helm's new-run key (N), and pauses the run and holds the helm while the picker is open (Slice/README.md).

The UI relies on these parts of `IEnvironmentReadoutSource` and the catalog contract:

- **`HasEnvironment`**: while it is false, or there is no source, every water and air readout shows `---`, no coverage warning shows, and the clock is New York time from the telemetry, as it was before (no date). A scene source that has been destroyed counts as no source.
- **`Kind` and `LocalUtcOffset`**: a pack run's clock is local time from the offset, labelled EDT (-4 h) or EST (-5 h), with the date beside it. Calm and uniform runs carry no offset, so their clock is UTC, labelled UTC, with the UTC date, and the strip says CALM or UNIFORM.
- **`WaterLevelNavd88`, `WaterLevelRate`**: NaN means the run has no tide (calm and uniform), shown as `---` with no trend.
- **`WindMissing`**: the wind reads NO WIND DATA, never the still air the simulation uses on such a day.
- **`TimeHolds`, `DepthMisses`**: per-run counts. Any count above zero shows its warning (OUTSIDE DAY'S DATA beside the clock on the panel and on the HUD, OFF DEPTH GRID in the depth block and on the HUD): filled while the count is rising, and for 1 s of simulated time after it last rose, then outlined (it happened earlier in this run). The warnings start over when the telemetry source, its `RunId` or the environment source changes, and are hidden but not forgotten while `HasEnvironment` is false.
- **`IScenarioCatalog`**: the lists are shown in the order given, never re-sorted. `ScenarioDay.Date` is yyyy-MM-dd; a date that is not still appears, in an OTHER column. Start calls `TryCreate`, then `IScenarioLauncher.TryStart` with the settings `TryCreate` made; a refusal shows inline as its reason (or "no reason was given" when the reason is empty), and an exception from either is logged and shown as one sentence. After a successful start the picker closes.

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
6. **The water and the air** sit in the top strip, between the vessel and the clock: the tide (height above NAVD88 in the depth unit, with an arrow and RISING, FALLING or STEADY), the current's set (where it flows toward, °T) and drift (kn), and the wind (where it comes from, °T, and kn). They change over minutes, not seconds, and set up an approach rather than steer it, so they are read the way an overhead wind and tide display is: a glance before and during a landing, not a needle to watch. Each group has a fixed width and its values sit in fixed right-aligned boxes, so nothing shifts as they change. When the run's clock has gone outside the day's data, OUTSIDE DAY'S DATA stands beside the clock, in place of the date, because that is what it qualifies (the strip has no room for both); when the depth comes from the pack's stand-in for water outside its depth grids, OFF DEPTH GRID appears in the depth block itself, next to the number it qualifies. Both are caution-filled while it is happening and outlined once it has stopped, like a finished contact, and neither flashes.
7. **Clock, date, vessel and elapsed time** are the smallest, in the top strip: for logs and the debrief, not for driving. A pack run's clock is local time on the scenario day with its date (MON 14 JUL 2025: the weekday matters on a ferry route); calm and uniform runs read UTC, labelled. Without an environment source the clock is New York local time from the scenario's UTC start, as before; when CSV playback has no recorded start it says ASSUMED START beside the clock.

The HUD carries the same water and air under its main readouts, in two fixed rows (the tide and the current, then the wind) with the clock beside them, and both coverage warnings on a line of their own when they show, so an external view loses none of it.

## The scenario picker

A card over the 3D view, clear of the panel: LANDING (the catalog's landings, in its order), DAY (a calendar with one column per Monday-to-Sunday week, so weekday and weekend read at a glance; a day without wind data carries `*`, explained underneath), START TIME (local, 24 h, 5-minute steps, 00:00 to 23:55, with the day's EDT or EST beside it; it does not wrap past midnight) and VESSEL. A line under the fields says the whole choice in words; Start sits beside it, filled like an engaged gear because it is the one action. A refusal appears under Start in caution color, as the catalog's or launcher's own sentence, and the picker stays open; changing any field clears it. 09:00 is the first time offered, as the existing scenarios start; after that the picker starts from the session's last choice (`ScenarioPicker.SessionChoice`, memory only, nothing written to disk).

## Honest instruments

- Needles and numbers show the latest sample, with no smoothing or animation. The fake source holds the most recent CSV row, like the fixed-step bridge, instead of interpolating.
- Every number is rounded to the resolution it claims (0.1 kn, 1°, 1 rpm, 1% of lever travel, 0.1 below 100 ft or m and whole units above). Anything missing or meaningless reads `---`: no data, a non-finite value (including an unknown contact reading), or a course over ground below 0.2 kn.
- Settings that are still guesses say so where they are shown: the 5 ft alarm default reads `ALARM BELOW 5.0 ft · PLACEHOLDER` on the panel, and the dev toolbar and the debrief call it a placeholder default and unit, pending crew input.
- Labels say exactly what is measured: heading and course are true (°T), SOG is over ground, STW is through water (negative going astern), depth is under the keel.
- Dial markings come only from the vessel file: a redline from rated rpm and hard-over marks appear only when those values exist (otherwise generic scales without them), and the lever notches show the control model's clutch-in position. These are estimated values today; see `vessels/*.json` for provenance.
- Throttle is not shown: the CSV does not carry it, so the fake source reports NaN rather than inventing a number.
- The water and the air are rounded the same way: tide to 0.1 ft or m, set and wind direction to 1°, drift to 0.1 kn, wind to whole knots (the station data does not support tenths). A set is `---` when the drift reads 0.0 kn and a wind direction when the wind reads 0 kn: slack water and calm air have no direction worth showing. A day without wind data reads NO WIND DATA, not the still air the simulation uses then. A run without tide (calm, uniform) reads `---` for the tide, never 0.0.
- The tide reads STEADY while it changes by less than 3 cm (0.1 ft) an hour, about ten minutes either side of high and low water, so a near-zero rate never flickers between RISING and FALLING.

## Day and night

- Colors live in C# (`Display/InstrumentPalette.cs`); the style sheet only lays things out, and elements carry role classes (`fs-c-text`, `fs-bg-face` and so on) that the palette fills in. `PaletteTests` measures the rules below.
- **Day** is a dark console with bright numerals, like real consoles that keep glare off the windows: primary numerals at least 7:1 contrast (WCAG AAA), captions and units at least 4.5:1 (AA), graphics at least 3:1. Every day text color is tested against every background it is drawn on, and a PlayMode test checks every visible label as rendered. The HUD and the dev toolbar float over the 3D view, so their backing is opaque: a translucent one measured 3.16:1 over the test backdrop and worse over bright sky.
- **Night** is dim red to amber only: no blue, green or white light (red ≥ green ≥ blue, green at most 0.65 of red), luminance capped, numerals still at least 3:1. Port and starboard lose red and green at night and are told apart by position and their P/S labels. Buttons and fields take palette colors too, so nothing shows neutral grey light.
- Nothing relies on color alone: gear by fill and word, plot series by solid and dashed lines, debrief events by shape (diamond gear shift, circle contact, triangle depth alarm), the tide's trend by arrow and word, coverage warnings by fill and outline as well as their words, the picker's selection by fill, and days without wind by a mark.
- The picker, the readouts of the water and the air and the coverage warnings use the existing roles only (`PaletteTests.Day_PickerAndEnvironmentPairsMeetWcagAA` names each pairing), its buttons and scroll bars included, so nothing shows the theme's grey light at night. A focused field has a ring in the text color.

## Layout and scaling

The UI is designed at 1280×720 reference pixels and `PanelSettings` scale it with the screen (matching width and height equally), so 2560×1440 is exactly 2×. A PlayMode test renders both and checks that every instrument sits at the same panel-space position and inside the panel. The panel is the bottom 300 reference pixels (42% of a 16:9 screen); the HUD sits at the bottom centre, lifted above the panel if both are shown. The console has no spare height at 300 px (the compass fills its row), so the water and the air went into the top strip rather than taking height from the 3D view. The picker sits over the 3D view, below the dev toolbar and clear of the panel, or of the HUD in an external view; tests check both at both resolutions. When the panel and the HUD are shown together, the HUD is lifted into the 3D view and the open picker covers it.

## Allocation

The panel and the HUD allocate nothing per frame under way, the water and the air included, except the clock string once per displayed second and the date string once per displayed day (at local midnight). The HUD shows the panel's clock string rather than building its own. `RefreshAllocationTests` (EditMode) and `MovingRefreshAllocationTests` (PlayMode, which writes `artifacts/health/moving-refresh*.txt`) measure it.

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
| `Display/` | Units and readout formatting (also for the water and the air), gear-state logic, depth alarm, contact and coverage monitors, New York time, dial scales, palettes, `EnvironmentDisplay` |
| `Telemetry/` | CSV reader and writer, recordings, `FakeTelemetrySource`, vessel file loading |
| `Instruments/` | Vector-drawn instruments: tachometer, lever, rudder, compass, gear badge, tide trend arrow |
| `Panel/` | The panel, HUD, the water and air row, root view, panel settings and the `WheelhouseUI` host |
| `Scenario/` | The scenario picker and its fields; `FakeScenarioCatalog`, `FakeScenarioHost` and `FakeEnvironmentSource` for the dev scene and tests |
| `Debrief/` | Plot scaling, event detection, plots, track, timeline and the debrief screen |
| `Resources/FerrySimUI/` | Style sheet (layout only) and the runtime theme |
| `Editor/` | `UIDevSceneBuilder` (editor-only assembly) |
| `Scenes/`, `Samples/` | Dev scene and the sample CSV |
| `Tests/EditMode`, `Tests/PlayMode` | Logic tests; rendered panel, HUD, debrief and dev-scene tests with screenshots |

## Placeholders to confirm

- Depth reads in **feet** by default and the shallow alarm defaults to **5 ft under the keel**, both until crew say what they use. The panel, the dev toolbar and the debrief mark the default as a placeholder.
- There is no hard-contact alarm: what counts as a hard landing is a question for crew.
- The fonts are UI Toolkit's default runtime font; its digits are not strictly fixed-width, so readouts are right-aligned in fixed boxes.
- The tide reads above **NAVD88**, the datum the packs and the depths use; tide tables and charts use MLLW (below).
- The STEADY band (3 cm an hour), whole-knot wind, and the coverage warnings' 1 s hold are display choices pending crew input.
- Calm and uniform runs read their clock in UTC because they carry no local offset (the contract's choice); a captain may prefer New York time there too.
- 09:00 local is the picker's first start time.

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
15. Tide height: above MLLW (chart datum, as in the tide tables) or NAVD88? MLLW sits 0.85 m below NAVD88 at the Battery but 1.28 m below at Kings Point, so showing it would need each gauge's MLLW offset in the pack. Feet, as for depth?
16. Is rising or falling enough, or would the time to the next high or low water (or slack) help more on approach?
17. Current: is set and drift in degrees and knots how you think of it, or "ebb, 2 knots" against the channel? Would an arrow on the compass card, relative to the bow, read faster?
18. Wind: true direction it comes from, or apparent wind relative to the bow, as a wind instrument shows it? Gusts?
19. Which date and time would you want to see for a run: local time only, or also the tide station's time?
20. In the scenario picker: which landings, days and times would you want first, and would you pick by tide (for example "strong ebb") rather than by date?
