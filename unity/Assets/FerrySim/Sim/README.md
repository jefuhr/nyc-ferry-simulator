# Sim

Owner: **unity-sim-bridge-controls**. The fixed-step bridge between Unity and the sim core: it steps an `ISimulation` at the true 100 Hz clock, draws the vessel, records every run and replays recordings. Units, frames and signs follow [docs/conventions.md](../../../../docs/conventions.md).

| Folder | Assembly | Contents |
|---|---|---|
| `Sim/` | `FerrySim.Unity.Sim` (namespace `FerrySim.Sim`) | Runner, driver, placement, scenario, environment packs and the environment readout, recorder, replay, the scenario picker's contracts, chase camera, debug readout, greybox |
| `Sim/Cesium/` | `FerrySim.Unity.Sim.Cesium` | `CesiumVesselPlacement`: puts the vessel on the Cesium globe |
| `Sim/Authoring/` | `FerrySim.Unity.Sim.Authoring` (editor only) | `BridgeDevSceneBuilder`: builds the dev scene from code |
| `Sim/Tests/EditMode/`, `Sim/Tests/PlayMode/` | `FerrySim.Unity.Sim.Tests.*` | Tests for this folder and `Input/` |

## The fixed-step bridge

`VesselSimulationRunner` (a MonoBehaviour) owns one simulation from `VesselDynamicsFactory`. It implements `IVesselTelemetrySource` and `IEnvironmentReadoutSource` for the UI and cameras.

Each frame:

1. **Frame time.** `Update` hands the frame time to `FixedStepDriver`:
   - normally `Time.unscaledDeltaTime × Time.timeScale`, because Unity's `maximumDeltaTime` clamp would hide most of a long hitch;
   - `Time.deltaTime` when a capture frame rate is set.
2. **Stepping.** The driver adds it to an accumulator and runs every whole `SimulationClock.FixedTimeStep` (10 ms) step now due.
   - Each step asks the control source for that step's `ControlInputs`.
   - The remainder carries over to the next frame.
   - Unity's `FixedUpdate` is not used.
3. **Guard.** A frame may run at most `maxStepsPerFrame` steps (default 25, a quarter of a second).
   - A slower frame drops the excess time instead of catching up later.
   - The drop is counted and logged (first drop at once, then at most every 5 s).
4. **Drawing.** The vessel is drawn between the last two physics states:
   - the interpolation factor is the leftover fraction of a step;
   - heading takes the short way round.
   - `BeforePlacement` is raised just before the placement, every frame (also while paused) and at every run start, so whatever the placement reads that follows the run, such as the slice's drawn tide, is current.

Simulation time always comes from the step count, so frame rate cannot change the trajectory. A run at 30 fps and one at 144 fps give bit-identical states (tested).

Controls come from any `IControlInputSource`, chosen in this order:

1. the runner's `controlSource` field;
2. the first one on the runner's GameObject (normally `HelmInputController`);
3. a recorded `ControlTrace`.

Runner API: `Pause`, `Resume`, `TogglePause`, `ResetRun` (starts a new run from the scenario start, on a fresh simulation) and `EndRun`. `Initialize` checks the scenario and loads its vessel file and environment before it ends the current run, so a scenario that cannot start throws and leaves the current run going.

Fixed structures reach the core through the runner's `WorldGeometry` (an `IWorldGeometry`, open water by default), passed to the four-argument `ISimulationFactory.Create` at each run start. Recordings name its type in a `world_geometry` header, and `ReplayPlayer.Verify` takes the same geometry.

## Placement

The runner uses the first `IVesselPlacement` on its GameObject. Without one it uses `TangentPlanePlacement`.

**`TangentPlanePlacement`** (the conventions' mapping):
- world (north, east, down) becomes Unity (x = east, y = −down, z = north), and heading is a yaw about +y;
- bow-up pitch and starboard-down roll are negative Unity rotations about +x and +z;
- positions stay double until the difference from a render origin is taken, and `recenterRenderOriginOnStart` puts that origin at the start point;
- the parent frame is east-up-north at the shared origin, so a CesiumGeoreference at `WorldOrigin` without origin shifting also works;
- Earth curvature is ignored.

**`CesiumVesselPlacement`** (in `FerrySim.Unity.Sim.Cesium`) drives a `CesiumGlobeAnchor`:
- the vessel sits at `LocalTangentPlane.World.ToGeodetic(north, east)`, at the water surface's ellipsoidal height minus heave;
- it is oriented in the local east-up-north frame there;
- curvature is therefore right (about a metre 3.6 km out), and the vessel stays put when the georeference origin moves (`CesiumOriginShift`).

Heights come from `IWaterSurface.EllipsoidalHeight(north, east, time)`: metres above WGS84, the geoid offset plus the NAVD88 water level. The world task owns the real surface. Until it is wired in, `ConstantWaterSurface` is used, with no value ever assumed:
- the offset is read from `data/processed/lower-east-river/geoid_offset.json` (slice centre), regenerated with `uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river`;
- otherwise it comes from the serialized field.

## Scenarios

A `ScenarioAsset` (Create > FerrySim > Scenario), or JSON with the same fields through the runner's `scenarioJsonFile`. `Scenarios/east-river-ebb-crosswind.json` is an example; its current and wind are illustrative, not data.

| Field | Meaning |
|---|---|
| `vesselFile` | Vessel parameter file, relative to the repository root (StreamingAssets first in a player) |
| `startLatitude`, `startLongitude` | WGS84 degrees of the reference point; converted through `LocalTangentPlane.World` |
| `startHeadingDegrees` | True heading |
| `startSpeedKnots` | Speed through the water along the heading (the CLI's convention); 0 starts at rest |
| `scenarioStartUtc` | ISO 8601; a time without an offset is UTC |
| `environment.pack` | An environment pack's day as region/date, for example `lower-east-river/2025-07-14`: the run takes that real day's tide, current, wind and depth. When set, the fields below are ignored |
| `environment.calm` | `UniformEnvironment.Calm` (still water and air, 50 m deep); the fields below are then ignored |
| `environment.currentSpeedKnots`, `currentTowardDegrees` | Uniform current, direction it flows toward |
| `environment.windSpeedKnots`, `windFromDegrees` | Uniform wind, direction it blows from |
| `environment.depthMetres` | Uniform depth |
| `traffic.day` | A traffic pack's day, yyyy-MM-dd, which must be the environment pack's: the run replays that day's harbor traffic (Traffic, below). Empty: no traffic |
| `traffic.leaveOut`, `traffic.leaveOutMmsi` | Which vessel to leave out of the traffic: `off`, `mmsi` (the vessel with `leaveOutMmsi`) or `auto` (the learner's own ferry) |

`startSpeedKnots` 0 starts at rest in the water: with a current, the hull starts moving over the ground with the water, as the CLI's runs do.

## Environment packs

A scenario with `environment.pack` runs on one real day (format `ferrysim-env/1`, `core/FerrySim.Core/Environment/README.md`): the core's `DataEnvironment` from the scenario start, with that day's tide, current, wind and depth.

- **The current** is the pack's channel field (`ChannelCurrentField`, method `channel-harmonic/1`, from the region's `env/field/` set) when the day names one, as the slice's days do: at a current station it is the station's prediction, and away from one it follows the shore and carries no water through land, so in a sheltered slip it runs weak and along the face. Outside the field's grids, and for a day without a field, it is the blend of the day's stations (`StationBlendCurrentField`). The hull feels it along its length, not only at its reference point.

- **Where packs are.** `EnvironmentPacks` finds a pack by name in `<root>/<region>/env/days/<date>`, its depth set in `<root>/<region>/env/depth`. The default roots are StreamingAssets' `data/processed`, where every player build copies the slice's packs, then the repository's. The runner's `EnvironmentPacks` property replaces them (tests do).
- **Checks.** `ScenarioSettings.Validate` refuses, naming the scenario: a name that is not region/date (lower-case region of letters, digits and hyphens, a real date), a pack that is not found (naming every place looked), and a start outside the day's coverage (naming the window). It reads only the day's manifest. Loading (`CreateEnvironment`, at every `Initialize`) verifies every byte through `EnvironmentPackJson` and refuses a changed file, naming it.
- **Time.** Simulation time t reads the pack at t + (scenario start − the pack's local-midnight epoch). Local time comes from the epoch, never from a time-zone database: the offset is the date's midnight as UTC minus the epoch, −4 h in daylight time and −5 h in standard time.
- **Each run** gets a fresh `DataEnvironment` (`RunEnvironment.StartRun`), so its time holds and depth misses count that run only. Everything that only looks (the start state, the readout, the drawn tide) reads a second environment over the same pack, so looking never changes the counts; both give the same bits.
- **Depth under the keel** comes from the core: water level minus bed, minus `maxDraft`. The depth alarm runs on it.
- **Calm and uniform** environments and simulation behavior are unchanged. New recordings add the vessel digest described below.

### Harbor preparation and coverage holds

`VesselSimulationRunner.InitializeHarbor` is the separate harbor entry point. It takes scenario settings, a runtime-pack folder and an explicit contact parser (`Func<string, string, IWorldGeometry>`). The parser receives the verified `contacts.json` text and its exact source path. An optional `validateStart` callback receives the prepared bundle, vessel parameters and initial state so the composition root can apply the existing approach, depth and clearance gates before the current run ends.

Preparation verifies the environment day, the runtime export and their depth-digest linkage, parses contacts, loads enabled traffic, snapshots the vessel file bytes and constructs the candidate simulation. Any preparation or gate failure leaves the current run and its recording intact. Only after preparation succeeds are the bindings replaced and `RunStarted` raised. The composition root binds its water and scene views on that event, before the harbor's first placement. Slice starts continue through `Initialize` with their existing event order.

One `EnvironmentPacks` instance retains at most one `VerifiedDepth`, created by `EnvironmentPackJson.LoadVerifiedDepth`. Day loads reuse that immutable instance and its grids without copying or rereading their arrays; every day hash, schema and depth-linkage check still runs. The loader cannot accept a caller-supplied digest as verification. There is no all-days cache: only the active bundle and a candidate are resident. Observer and physics `DataEnvironment` instances remain separate, sharing only immutable pack data.

`HarborLoadTimings` exposes the depth and day read/hash/decode/validation/total milliseconds plus runtime-pack, contact-parser, traffic and whole-preparation milliseconds. Depth timing is zero for a reused snapshot. Validation is the remaining synchronous schema and construction work after measured reads, hashes and decoding; it includes loop and allocation overhead. These are measured load costs, not frame-time or GPU measurements.

The runner implements `IRunHoldSource`. For harbor runs, a rise in physics `DepthMisses` latches `OutsideCoveredWater`; a rise in `TimeHolds` latches `BeyondDayData`. The driver checks its step limit between steps, stopping the same frame after the first step that reports lost coverage. `Resume` cannot clear a coverage hold; a new start or reset creates a fresh run. An ended run reports `None`. Observer-only water sampling cannot trigger a hold. Slice runs retain their previous coverage behavior.

Harbor moving visuals and placement use `RenderTime`, interpolated between the same two physics states as `RenderPose`. `HarborWaterService` implements `IWaterSurface` as the canonical lattice datum plus the run's local tide; its baked visual meshes never provide depth. See [HarborWater](../World/HarborWater/README.md).

### The environment readout

`IEnvironmentReadoutSource` on the runner: `HasEnvironment` follows `HasTelemetry`, and `Environment` is the water and the air at the reference point at the most recent step (at the start before the first), worked out once per step, allocation-free:

| Field | Pack run | Calm or uniform run |
|---|---|---|
| `Kind`, `PackName`, `PackDigest` | `Pack`, region/date, the day digest | `Calm` or `Uniform`, empty |
| `LocalUtcOffset` | From the pack's epoch | Zero (show UTC) |
| `WaterLevelNavd88` | The pack's level at the vessel, m | NaN |
| `WaterLevelRate` | The level's change over the minute centred on the step, m/s, positive rising | NaN |
| `Current`, `Wind` | The pack's at the reference point (the current from its channel field), m/s toward | The uniform values |
| `WindMissing` | The day has no wind station (still air) | False |
| `TimeHolds`, `DepthMisses` | The run's own counts (`DataEnvironment.Coverage`) | Zero |

## The scenario catalog

`IScenarioCatalog` and `IScenarioLauncher` connect the picker to `SliceBootstrap`, through `SliceScenarioCatalog` and `SliceScenarios`. `IScenarioStartDepthSource` is an optional preview source implemented by the same bootstrap.

- **Landings** have a usable default face, ordered by numeric GTFS stop id, then other ids by ordinal text. A missing, excluded or bent default face excludes the stop with its reason.
- **Berths** follow the exported `berth_order`: default first. Pier 11 offers Outer pontoon (east), North pontoon, South pontoon, in that fixed order. Other stops offer one `Main berth`. A barge id is `enc:<LNAM>` or its exported derived identity; a legacy fixed face uses `stop:<stop_id>`. Null or empty `ScenarioChoice.BerthId` means default. An unknown id is refused; it never silently selects another berth. Multi-berth scenario names include the selected berth name.
- **Days** are readable environment-pack manifests, oldest first, with the pack epoch's local UTC offset and missing-wind flag. `HasTraffic` requires that day's traffic pack in the traffic roots, StreamingAssets first and the repository second. Players without traffic payloads report false.
- **Vessels** follow the configured order: `vessels/t-class.json`, then `vessels/k-class.json`. An unreadable vessel or one without a contact outline is excluded.
- **Start pose** is at rest, bow 4 m off the selected face midpoint, heading straight in against its normal (`LandingApproach.BowOn`). Every face subdivision must be retained, joined end to start within 1 mm and straight within 1 degree. A pose less than 1 m clear of any contact face is refused.
- **Start time** is the pack's local-midnight epoch plus 0–1439 minutes. A start outside pack coverage is refused. The environment is the selected day's pack.
- **Traffic and visibility** copy `Traffic`, `LeaveOut`, `LeaveOutMmsi` and `VisibilityMetres` into `ScenarioSettings`. Missing traffic, an invalid leave-out or invalid visibility is refused through the existing scenario validation. The picker offers Automatic (default) or Off leave-out; the contract also supports MMSI.
- **Start depth** samples the pack's water level minus bilinear bed under the contact hull at the chosen start pose and time. It takes the minimum over a 0.25 m world lattice, covered depth-grid nodes and outline edges spaced at most 0.25 m. This is a sampled minimum, not a surveyed continuous minimum. Loaded days are cached. Unknown bed returns no numerical readout. The preview compares water depth with maximum draft; starts aground are permitted, with a non-blocking picker warning below 0.3 m under the keel.
- **Launching** validates the scenario before ending an existing run, then starts it with a new `RunId`. Refusals remain inline. A bootstrap clearance refusal ends the new run before its first step.

## Recording and replay

Every live run is recorded unless `recordRuns` is off:
- in the editor, to `artifacts/unity/runs/`;
- in a player, to `<persistentDataPath>/runs/`.

A new file pair starts on every reset.

| File | Content |
|---|---|
| `<stamp>-<vessel>-runNN.csv` | Telemetry in the CLI's schema (`core/dotnet/FerrySim.Cli/TelemetryCsv.cs`): the same `# ferrysim-csv: 1` header, columns and number formats, at `telemetrySampleHz` (default 10). Each row is the state at `time_s` and the controls that start applying then; the last row is the end of the run. |
| `<stamp>-<vessel>-runNN.inputs.csv` | Every control change, lossless, in the `ferrysim run --inputs` format, plus a closing row at the end time and trailing `#` lines: step count, `state_digest` (FNV-1a over every bit of every state) and the end state at full precision. |
| `<stamp>-<vessel>-runNN.traffic.csv` | Only for a run with traffic: its CPA alarms, collisions and the vessels it started alongside, one row per event, and the encounter digest (Traffic, below). |

`maneuver` is `unity-run`. The header also carries what a replay needs, at full precision: `vessel_file`, `initial.*`, the environment, `scenario_start_utc` (to the tick), `start_latitude_deg`/`start_longitude_deg` and `world_geometry_digest` (the `WorldGeometryDigest` of the fixed structures, `none` for open water; `ReplayPlayer.Verify` and `InitializeReplay` refuse a replay against other structures). The header holds no wall-clock times and no absolute paths.

New recordings also carry `vessel_sha256`, the SHA-256 of the exact vessel file bytes captured when the run was initialized, including whitespace and encoding. Parsing and hashing use the same byte snapshot, and resets retain that snapshot's digest. `ReplayPlayer.Verify` and `InitializeReplay` compare the current file's digest before the first replay row; a mismatch names both hashes and leaves an existing runner active. Older recordings without the key keep their previous replay behavior.

`TrafficStepMilliseconds` measures the last actual `TrafficMonitor.Update`, including event dispatch. `TrafficStepP95Milliseconds` is the nearest-rank p95 of the latest 256 steps in the current run. A fixed pair of arrays maintains the rolling window without allocations. The metric excludes visual traffic updates, water, the rest of physics and timing-window bookkeeping; it is zero before a traffic update or when traffic is off.

The environment in the header:

| Run | Keys |
|---|---|
| Calm or uniform | `environment` = the CLI's `--env` spec (`calm`, or `current=...,wind=...,depth=...`), and `env.current_north_mps`, `env.current_east_mps`, `env.wind_north_mps`, `env.wind_east_mps`, `env.depth_m`: exactly as before packs |
| Pack | `environment` = `pack`, then `environment_pack` (region/date), `environment_digest` (the day's `DIGEST`, which covers every byte of the day and its depth set), `environment_epoch_utc` and `environment_wind_station` (as the CLI writes them); no `env.*` |

A pack run replays against the pack its header names, found where scenarios find packs, or against a pack given to `ReplayPlayer.Verify(..., pack:)` or `InitializeReplay(run, vessel, pack)`. A pack whose day digest is not the recorded one, or a missing pack, is refused before anything is replayed, naming both: `Verify` returns a failed result, and `InitializeReplay` throws and leaves the current run going. A calm or uniform recording given a pack is refused the same way. Recordings made before packs have no pack keys and replay as before.

The CLI tools read Unity runs directly:

```
dotnet run --project core/dotnet/FerrySim.Cli -- summarize artifacts/unity/runs/<run>.csv
uv run tools/plot_maneuver.py artifacts/unity/runs/<run>.csv --out <run>.png
```

`ReplayPlayer.Verify(RecordedRun.Load(csv), parameters, factory)` re-drives a fresh simulation from the recorded trace. Two checks must pass:
- every telemetry row must come out identical;
- the state digest must match, because CSV rounding alone would hide a divergence in the last bits.

To watch a replay in a scene, set the runner's `replayRecording` (or call `InitializeReplay`). The runner then plays the run back, checks it row by row, pauses at the end and logs the result. A replay is not recorded again.

## Traffic

A scenario with `traffic.day` replays that real day's harbor traffic from a traffic pack (format `ferrysim-traffic/1`, `core/FerrySim.Core/Traffic/README.md`) on the run's clock: simulation time t reads the pack at t + (scenario start − the pack's local-midnight epoch), as the environment does. A day's tide, current, wind and traffic are one real day, so the traffic day must be the environment pack's.

- **Where packs are.** `TrafficPacks` finds a day in `<root>/harbor/traffic/<date>`. The default roots are StreamingAssets' `data/processed`, then the repository's (`ScenarioTraffic.DefaultPacks`); the runner's `TrafficPacks` property replaces them (tests do).
- **Checks.** `ScenarioSettings.Validate` refuses, naming the scenario: a day that is not yyyy-MM-dd, traffic on calm or uniform water or on another day than the environment pack's, and a leave-out that is not `off`, `mmsi` or `auto` (or an MMSI out of range); `Validate(packs, trafficPacks)`, which the runner calls, also refuses a day whose pack is not found, naming every place looked. Loading (`CreateTraffic`, at every `Initialize`) verifies every byte through `TrafficPackJson` and refuses a changed file, naming it, and an MMSI to leave out that the pack does not have. A scenario that cannot start leaves the current run going.
- **Leaving out the learner's ferry.** `auto` leaves out the ferry candidate (the fleet's T and K boats) nearest the start at the start time, within 150 m, by its position then or its report nearest in time within 120 s (`OwnFerryFinder`); none when no candidate is that near. The recording names the MMSI, and a replay leaves out the same one.
- **Every step.** The runner's `TrafficMonitor` (a fresh one per run) looks at the state after each physics step: every vessel present gets its CPA and TCPA (positions and velocities over the ground), the CPA alarm (a vessel closing to 100 m or less within 120 s, estimates for crew review), and the learner's contact outline against its AIS-sized rectangle. Alarms and collisions starting and ending are events. Traffic never pushes the learner's vessel: a collision is an event only. Allocation-free per step.
- **The readout.** The runner is also an `ITrafficReadoutSource` (core): `TrafficEnabled` (the run has traffic), `HasTraffic` (the readout is the latest step's) and `TrafficAlarm` (the alarm state and its target: the nearest vessel in collision, else under alarm, else within a nautical mile). The wheelhouse panel reads it.
- **Drawing** is the slice's `TrafficView` (Traffic/README.md); the runner only exposes `Traffic` (the scenario's `TrafficRun`: pack, replay, time base, the vessel left out) and `TrafficMonitor`.

**Recording a run with traffic.** The telemetry header carries, after the world geometry:

| Key | Value |
|---|---|
| `traffic` | `pack` |
| `traffic_pack` | The day, yyyy-MM-dd |
| `traffic_digest` | The pack's digest (`manifest.json`'s `digest`, which covers every byte of its three files and its epoch) |
| `traffic_epoch_utc` | The pack's local-midnight epoch |
| `traffic_leave_out` | `off`, `mmsi` or `auto` |
| `traffic_removed_mmsi` | The MMSI left out, or `none` |
| `traffic_events` | The event log's file name (after `param.inputs`) |

**The encounter data goes in a sidecar**, `<run>.traffic.csv` beside the telemetry and the control trace, not in columns: the telemetry CSV keeps the CLI's schema exactly (so `ferrysim summarize` and `tools/plot_maneuver.py` read traffic runs unchanged), events are kept at the exact physics step they happened (a 10 Hz column would lose a one-step collision), and runs without traffic write exactly the two files and the header they wrote before. The log (core Traffic/README.md, The event log) has the pack, the leave-out and the alarm limits (the alongside margin among them) in its header, one row per event at full precision (step, time, kind, MMSI, the episode's steps, range, bearing, CPA, TCPA, relative speed, the episode's least range and CPA, the vessel's class, hull size and name), then the snapshot of the episodes still open when the run ended (an alarm still up keeps its least CPA so far, written as `alarm-open`, never as an end), and closing lines with the event count, the open-episode count, the step count and the monitor's encounter digest. The debrief reads it (UI/README.md).

**Replaying a run with traffic.** `ReplayPlayer.Verify(..., traffic:, trafficPacks:)` and `InitializeReplay(run, vessel, pack, traffic)` replay against the given traffic pack, or the day the header names, found where scenarios find packs, leaving out the recorded MMSI with the recorded alarm limits and alongside margin. A pack whose digest is not the recorded one, a missing pack, or a pack given to a run without traffic is refused before anything is replayed, naming both: `Verify` returns a failed result, and `InitializeReplay` throws and leaves the current run going. During the replay every event the monitor produces must equal the recorded row character for character, as many as recorded, its open-episode snapshot at the end must equal the recorded one (when the recording has one), and the encounter digest must be the same (`ReplayResult.Traffic`); `ReplayResult.Matches` includes it. A run without traffic has no traffic keys and replays as before.

## BridgeDev

`Scenes/BridgeDev.unity` contains:
- the runner and the wheelhouse controls on `Vessel`;
- a greybox catamaran built from the loaded vessel file: two demihulls of `hull.demihullBeam` at `hull.demihullSeparation`, keel to deck, plus a deck and a cabin sized from the windage areas;
- `DevWater`, a grid plane that follows the vessel;
- a `ChaseCamera` with chase, bridge and overhead views;
- the UI Toolkit `SimDebugReadout`.

The scenario is a T-class at rest off the lower Manhattan piers, heading 035°.

The scene and its assets are generated. Rebuild them rather than editing by hand:

```
lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -nographics -projectPath "$PWD/unity" \
  -executeMethod FerrySim.Sim.Authoring.BridgeDevSceneBuilder.BuildFromCommandLine -logFile "$PWD/artifacts/unity/build-bridgedev.log"
```

Keys are listed in [Input/README.md](../Input/README.md).

## Tests

Run with the commands in `unity/README.md`. The trajectory test writes `artifacts/unity/bridge/trajectory-report.md`. That report also compares against a CLI run of the same trace when this CSV exists:

```
dotnet run --project core/dotnet/FerrySim.Cli -- run --vessel vessels/t-class.json \
  --inputs unity/Assets/FerrySim/Sim/Tests/Fixtures/ahead-then-hard-over.csv \
  --out artifacts/unity/bridge/cli-ahead-then-hard-over.csv --sample-hz 100
```

The BridgeDev test writes `artifacts/unity/bridge-dev.png`.

The pack tests run on small synthetic packs (`Tests/EditMode/SyntheticPacks.cs`: a uniform current, a water level rising at a fixed rate, a steady wind and a flat bed, written to a temporary folder by the core's `EnvironmentPackWriter`), so every sample can be checked by hand:

| Test | Checks |
|---|---|
| `PackScenarioTests` | Pack names that are not region/date, an unknown pack and a start outside the day refused with the pack, the places looked and the window; calm and uniform fields ignored on a pack; the day read at the start's offset from the epoch; StreamingAssets' copy before the repository's; days oldest first with their offsets and wind, a bad folder left out with its reason; a changed byte refused; JSON round trip; calm and uniform unchanged |
| `PackRunTests` | The runner on a pack: depth under the keel, the readout's level, rate, current, wind, offset and counts at the last step, counts per run, no data before the first step or after the end, calm and uniform reporting no tide, a scenario that cannot start leaving the run going, `BeforePlacement` before every placement, and no garbage per step |
| `PackRecordingTests` | The pack keys in the header and no `env.*`; calm and uniform headers byte-identical to the uniform builder's; bit-for-bit replay; another day's pack, a changed pack and a missing pack refused naming both digests (through `Verify` and the runner, which keeps its run); a calm recording given a pack refused |
| `VesselGreyboxTests` | The T-class and K-class greyboxes are their files' sizes, and a K-class run rebuilds the greybox |

## Limitations

- **Player builds.** Vessel files and environment packs are read from the repository in the editor; a player reads them from StreamingAssets, where every player build copies the files the scenes name (Build/README.md).
- **Wind** is the pack's first wind station's mean wind, the same everywhere; gusts are not applied (the core's v1 rule).
- **Horn.** The horn is a state and an event only; there is no sound yet.
- **Keyboard helm.** The keyboard helm is hold-to-steer (±1). With the shipped follow-up steering, holding gives hard over.
- **Placement and the water surface.** `CesiumVesselPlacement` takes the water surface from `IWaterSurface`. In the slice that is `WorldWaterHeight` on the drawn water, which follows the run's tide (Slice/README.md, Water height).
