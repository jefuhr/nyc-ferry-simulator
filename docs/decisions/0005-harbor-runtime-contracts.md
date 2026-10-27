# 0005: Harbor runtime contracts

**Status:** accepted (2026-10-26)

## Context

Wave 3a delivered the harbor's data products. The harbor runtime export (`data-pipeline/ferrydata/harbor_runtime`) gives aids, physical bridge groups, closed landing barges, a canonical geoid lattice and query footprints. The skyline pipeline (`data-pipeline/ferrydata/buildings`) gives a second local 3D Tiles set. The readiness spikes measured the open questions in a development player on the Mac mini and Romeo, with identical results on both:

- **S1 (starts).** 36,400 start rows: 26 berths, both hulls, 28 days. The three crew-question faces stay excluded. On unfloored harbor depth, some real landings refuse much of the day.
- **S2 (loading).** A verified, immutable depth set shared across days cuts the second day's load from 1,940 ms to 279 ms, and the managed peak from 185 MB to 116 MB.
- **S3 (water).** A 250 m datum lattice with gauge-weighted nodes keeps anchors within 0.59 mm and the surface within 0.64 mm of the exact sampler. Updates allocate nothing.

Decision 0004 fixed how the harbor streams. This record fixes the interfaces that wave 3b's parallel tasks build against, so the harbor runtime, scene, navigation world, traffic rendering and picker can be written at the same time.

## Decision

### Scenes

- **`Harbor.unity` is a new scene,** built by an editor builder from checked-in configuration and the runtime manifests.
- **It holds one runner and one UI, input and camera rig,** plus the georeference, water, atmosphere, the terrain tileset and a skyline tileset slot.
- **The slice keeps `SliceBootstrap`** and stays a regression fixture.
- **The harbor's composition root reuses** the slice's contact parser, approach and clearance logic and runtime services through explicit dependencies. It does not copy physics or recording.
- **Becoming the player's default scene** is a separate packaging step, after acceptance.

### Resident data and the atomic start

The core keeps one validating loader. Two additions make days cheap to switch:

- **`VerifiedDepth EnvironmentPackJson.LoadVerifiedDepth(string depthFolder)`** checks every file's SHA-256, the schema and the region. It returns an immutable depth set keyed by region and computed digest.
  - `VerifiedDepth` can only be made by this loader, never from a caller-supplied digest.
  - It is safe to share across days, the physics environment and the observer.
- **`EnvironmentPack EnvironmentPackJson.Load(string dayFolder, VerifiedDepth depth)`** checks the day as today's loader does (manifest, files, aggregate digest, epoch, coverage, stations), plus that the day's depth digest equals `depth`'s. It reuses the arrays without copying them.

The Unity side prepares a whole start before touching the current run:

- **What gets prepared:** contacts, the day, traffic when enabled, and the harbor runtime pack.
- **On failure:** the current run keeps going, and the exact missing or mismatched path is reported.
- **On success:** it binds them atomically, creates the core, places the vessel and initializes the view.
- **What stays resident:** at most one verified depth set, the active day bundle and one candidate. The 28 traffic days are never cached together.
- **Observer and physics** keep separate `DataEnvironment` instances, because their counters and time caches are mutable. Only immutable pack data is shared.

Timing for read, hash, decode, validation and the whole bundle is exposed separately, for the budget matrix.

### Water

- **`IWaterSurface.EllipsoidalHeight(north, east, time)` is unchanged.** The harbor implementation returns the canonical datum plus `RunEnvironment.WaterLevel(north, east, time)`.
  - Physics samples it at fixed-step time; moving visuals at the runner's interpolated render time.
- **The canonical datum is the runtime export's 250 m float64 lattice.** It has one owner rule and shared edge nodes, and is never extrapolated outside its active-cell mask.
- **Every floating object samples its own local tide:** the own vessel, each traffic hull and each landing barge.
  - Fixed piers and bridges do not move.
  - Rigid barges ride at their anchor.
- **The visual water lives in the Unity adapter** and never supplies depth. Each node is baked once, with its local base position, up vector and normalized gauge weights. Each frame blends the gauges into caller-owned vertex arrays with no allocation.
- **Precision:**
  - meshes are built relative to a local anchor after a double-precision subtraction;
  - ECEF is never cast to float;
  - near-field vessel and barge alignment stays within 1 cm, and the sampled water surface within 2 cm.

### The harbor runtime pack (`ferrysim-harbor-runtime/1`)

The format is fixed as wave 3a exported it, DIGEST framing included. A core reader, `HarborRuntimePack.Load(string folder)`, verifies every byte before parsing, as the environment and traffic packs do. Decisions on the exporter's open questions:

- **Datum:** the 250 m lattice, as above.
- **Bridges:** physical groups from the clearance audit's transitive grouping, with stable `bridge:` IDs. Each span keeps its own MHW clearance. A null underside or vertical bound stays null: the renderer marks such a span's tower or deck as estimated, and nothing infers a height.
- **Piers:** only charted SLCONS categories 4 and 5 that intersect a bridge footprint. Membership is geometric association only.
- **Query footprints:** for camera, radar and chartplotter queries only. `collision_authority` is false: they never become contacts or colliders. Collision scope stays the contact set.
- **Barges:**
  - one rigid mesh per `structure_id`, anchored locally, with the greybox barge's freeboard and depth, labelled estimated;
  - they ride their own local tide;
  - berth eligibility comes from the contacts, never from the outline.
- **Boundary aids:** the two source aids outside active coverage (`0226022294660032`, `02260222946E0032`) stay in the data but are neither drawn nor given a datum. Nothing extrapolates the lattice.
- **Publication:** players carry the pack offline, built before packaging. No live directory swap is needed.

### Scenarios and the picker

The harbor catalog implements the existing `IScenarioCatalog`, `IScenarioStartDepthSource` and `IScenarioLauncher`.

- **Landings:** all 24 in stable stop ID order, the stop name searchable.
  - A landing or berth that can't start stays listed.
  - `TryCreate` refuses it with a specific reason, for example "East 90th St: the landing side is waiting for a crew answer".
- **Notes:** `ScenarioLanding.Note` and `ScenarioBerth.Note` (new, optional, never null) carry evidence and crew restrictions for the picker.
- **Days:** `ScenarioDay.CurrentMethod` (new, optional) names the current model: "station blend" for harbor packs, "channel field" for the slice.
- **Aground starts are refused** (negative UKC), with a warning below 0.3 m, exactly as the slice does.

### Coverage and day end

- **When the vessel leaves covered water,** that is, when the environment readout's `DepthMisses` rises during a run, the harbor runner stops stepping and reports `RunHold.OutsideCoveredWater` through `IRunHoldSource` (new).
- **When the day's data runs out** (`TimeHolds` rises), it reports `RunHold.BeyondDayData` the same way.
- **The HUD explains the hold;** a new start resumes. Nothing continues on fallback data.
- **The slice runner** keeps its current behaviour.

### Recordings

- **The telemetry header gains `vessel_sha256`:** the SHA-256 of the vessel file's bytes the run used. Vessel files are kept in their canonical form (`tools/crew_intake` formatter), so this is the crew-intake fingerprint too.
- **Replay refuses a mismatch before the first row,** naming both hashes. Recordings without the key replay as before.

### Ownership in wave 3b

Exclusive paths, so the tasks can run in parallel. New folders get their own assemblies; a reference added to an existing asmdef needs the coordinator.

| Task | Owns |
|---|---|
| harbor-runtime | `core/FerrySim.Core/World/**` (new: `HarborRuntimePack` and its query index), `core/FerrySim.Core/Serialization/EnvironmentPackJson.cs`, `core/FerrySim.Core/Environment/DepthSet.cs`, their core tests; `unity/Assets/FerrySim/Sim/EnvironmentPacks.cs`, `RunEnvironment.cs`, `VesselSimulationRunner.cs`, `RunRecorder.cs`, `ReplayPlayer.cs` and their Sim tests; new `unity/Assets/FerrySim/World/HarborWater/**` and `World/Georeference/HarborDatum.cs` |
| harbor-scene | new `unity/Assets/FerrySim/Harbor/Runtime/**`, `Harbor/Editor/**`, `Harbor/Scenes/**`, `Harbor/Data/**`, `Harbor/Tests/Scene/**`; new `World/HarborTiles/**` and `World/Skyline/**` |
| harbor-navigation | new `unity/Assets/FerrySim/World/HarborNavigation/**` and its tests; `World/Atmosphere/Atmosphere.cs`, `AidLights.cs` and their registration tests |
| harbor-traffic | `unity/Assets/FerrySim/Traffic/**` and its tests |
| harbor-picker | new `unity/Assets/FerrySim/Harbor/Scenarios/**` and `Harbor/Tests/Scenarios/**`; `unity/Assets/FerrySim/UI/Scenario/**` and the picker's tests |
| harbor-landings | as dispatched: the harbor operational depth floor (candidate, approval needed), the start assessment, and the three crew faces |
| traffic-candidates | `data-pipeline/ferrydata/traffic/**` |

Coordinator only: `core/FerrySim.Core/Contracts/**`, `unity/Assets/FerrySim/Sim/I*.cs`, `WaterSurface.cs`, every existing asmdef and csproj, `scripts/**`, `docs/decisions/**`, project and build settings, and packaging (`unity/Assets/FerrySim/Build/**`).

## Consequences

- Parallel tasks build against fakes of these interfaces, and the coordinator integrates.
- Any change to these signatures or this schema goes back to the coordinator and a new record.
- The slice's tests keep passing unchanged. The harbor gets its own fixtures, not rebaselined slice outputs.
- Still open, for later records:
  - the three crew faces;
  - the harbor depth floor, waiting on the harbor-landings audit and the user's approval;
  - the harbor channel field;
  - extending collision scope to the harbor shoreline and bridge piers;
  - B4 route missions.
