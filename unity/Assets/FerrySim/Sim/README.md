# Sim

Owner: **unity-sim-bridge-controls**. The fixed-step bridge between Unity and the sim core: it steps an `ISimulation` at the true 100 Hz clock, draws the vessel, records every run and replays recordings. Units, frames and signs follow [docs/conventions.md](../../../../docs/conventions.md).

| Folder | Assembly | Contents |
|---|---|---|
| `Sim/` | `FerrySim.Unity.Sim` (namespace `FerrySim.Sim`) | Runner, driver, placement, scenario, recorder, replay, chase camera, debug readout, greybox |
| `Sim/Cesium/` | `FerrySim.Unity.Sim.Cesium` | `CesiumVesselPlacement`: puts the vessel on the Cesium globe |
| `Sim/Authoring/` | `FerrySim.Unity.Sim.Authoring` (editor only) | `BridgeDevSceneBuilder`: builds the dev scene from code |
| `Sim/Tests/EditMode/`, `Sim/Tests/PlayMode/` | `FerrySim.Unity.Sim.Tests.*` | Tests for this folder and `Input/` |

## The fixed-step bridge

`VesselSimulationRunner` (a MonoBehaviour) owns one simulation from `VesselDynamicsFactory`. It implements `IVesselTelemetrySource` for the UI and cameras.

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

Simulation time always comes from the step count, so frame rate cannot change the trajectory. A run at 30 fps and one at 144 fps give bit-identical states (tested).

Controls come from any `IControlInputSource`, chosen in this order:

1. the runner's `controlSource` field;
2. the first one on the runner's GameObject (normally `HelmInputController`);
3. a recorded `ControlTrace`.

Runner API: `Pause`, `Resume`, `TogglePause`, `ResetRun` (starts a new run from the scenario start, on a fresh simulation) and `EndRun`.

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
| `environment.calm` | `UniformEnvironment.Calm` (still water and air, 50 m deep); the fields below are then ignored |
| `environment.currentSpeedKnots`, `currentTowardDegrees` | Uniform current, direction it flows toward |
| `environment.windSpeedKnots`, `windFromDegrees` | Uniform wind, direction it blows from |
| `environment.depthMetres` | Uniform depth |

## Recording and replay

Every live run is recorded unless `recordRuns` is off:
- in the editor, to `artifacts/unity/runs/`;
- in a player, to `<persistentDataPath>/runs/`.

A new file pair starts on every reset.

| File | Content |
|---|---|
| `<stamp>-<vessel>-runNN.csv` | Telemetry in the CLI's schema (`core/dotnet/FerrySim.Cli/TelemetryCsv.cs`): the same `# ferrysim-csv: 1` header, columns and number formats, at `telemetrySampleHz` (default 10). Each row is the state at `time_s` and the controls that start applying then; the last row is the end of the run. |
| `<stamp>-<vessel>-runNN.inputs.csv` | Every control change, lossless, in the `ferrysim run --inputs` format, plus a closing row at the end time and trailing `#` lines: step count, `state_digest` (FNV-1a over every bit of every state) and the end state at full precision. |

`maneuver` is `unity-run`. The header also carries what a replay needs, at full precision: `vessel_file`, `initial.*`, `env.*`, `scenario_start_utc`, `start_latitude_deg`/`start_longitude_deg` and `world_geometry_digest` (the `WorldGeometryDigest` of the fixed structures, `none` for open water; `ReplayPlayer.Verify` and `InitializeReplay` refuse a replay against other structures). The header holds no wall-clock times and no absolute paths.

The CLI tools read Unity runs directly:

```
dotnet run --project core/dotnet/FerrySim.Cli -- summarize artifacts/unity/runs/<run>.csv
uv run tools/plot_maneuver.py artifacts/unity/runs/<run>.csv --out <run>.png
```

`ReplayPlayer.Verify(RecordedRun.Load(csv), parameters, factory)` re-drives a fresh simulation from the recorded trace. Two checks must pass:
- every telemetry row must come out identical;
- the state digest must match, because CSV rounding alone would hide a divergence in the last bits.

To watch a replay in a scene, set the runner's `replayRecording` (or call `InitializeReplay`). The runner then plays the run back, checks it row by row, pauses at the end and logs the result. A replay is not recorded again.

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

## Limitations

- **Player builds.** Vessel files are read from the repository in the editor; a player needs them under StreamingAssets.
- **Horn.** The horn is a state and an event only; there is no sound yet.
- **Keyboard helm.** The keyboard helm is hold-to-steer (±1). With the shipped follow-up steering, holding gives hard over.
- **Placement and the water surface.** `CesiumVesselPlacement` takes the water surface from `IWaterSurface`; the world's tide-aware implementation is wired in at integration.
