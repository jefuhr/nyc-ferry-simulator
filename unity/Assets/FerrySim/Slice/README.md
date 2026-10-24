# Slice

Owner: **unity-slice-integration**. The Lower East River slice as one drivable scene: the world (terrain, water, landings), the bridge's simulated T-class and the wheelhouse UI on its real telemetry, with landing contact against the real landing faces and the vessel on the world's water. Units, frames and signs follow [docs/conventions.md](../../../../docs/conventions.md). Nothing in `Sim/`, `Input/`, `World/` or `UI/` is changed by this folder, apart from the coordinator-approved geometry digest (below).

| Folder | Assembly | Contents |
|---|---|---|
| `Slice/` | `FerrySim.Unity.Slice` (namespace `FerrySim.Slice`) | `SliceBootstrap`, landing geometry from data, fender parameters, the water height adapter, the imported terrain on the water's datum |
| `Slice/Authoring/` | `FerrySim.Unity.Slice.Authoring` (editor only) | `SliceSceneBuilder`: builds the scene from code |
| `Slice/Data/` | (data) | `landing-contact-surfaces.json`: the landings' fender parameters |
| `Slice/Scenes/` | (generated) | `LowerEastRiverSlice.unity` and its scenario asset |
| `Slice/Player/` | (in `FerrySim.Unity.Slice`) | `PlayerBuildInfo`, the build's record in a player; `PlayerSmokeRun` with its options and report, the release smoke run |
| `Slice/Tests/EditMode/`, `Slice/Tests/PlayMode/` | `FerrySim.Unity.Slice.Tests.*` | Tests for this folder and the integration |

## The scene

`Scenes/LowerEastRiverSlice.unity` is the world scene (`World/Scenes/LowerEastRiver.unity`, opened by the builder and never saved back) plus:

- `Vessel` under the `CesiumGeoreference`: `VesselSimulationRunner` (T-class, recording every run), `HelmInputController` (keys in [Input/README.md](../Input/README.md)), `CesiumVesselPlacement` reading the world's water through `WorldWaterHeight`, and the bridge's greybox. The world's `WheelhouseMount` is moved aboard at the helm eye point, facing the bow and tilted 8° down: the panel covers the bottom 42% of the screen, and a level eye 6.9 m up would lose the water under it out to about 65 m ahead (28 m with the tilt). Head look (right mouse) still looks anywhere within the mount's limits.
- `Slice`: `SliceBootstrap` and `WorldWaterHeight`.
- `Wheelhouse UI`: `WheelhouseUI` bound to the runner.

The world's `CameraRig` follows the vessel: F2 chase, F3 wheelhouse (F1 free-fly), and the helm's camera button swaps chase and wheelhouse. The panel shows in the wheelhouse view and the HUD in the others.

The scenario (`Scenes/LowerEastRiverSliceScenario.asset`) starts the T-class at rest with its bow 40 m off the middle of South Williamsburg's primary landing face, the NW long river side (id 149111046, 25.6 m), heading straight in (125°), at noon EDT on 15 June 2026 (`2026-06-15T16:00:00Z`), in calm water (50 m deep, no current or wind). The builder computes the start from the landing data, so it follows regenerated data, and refuses to build a start less than 5 m clear of every face. `SliceScenarioTests` fails when the shipped scenario is not exactly what the builder derives from the imported data, so a data change without a rebuild is caught (clearance alone would not catch a stale pose).

**Which face the scenario approaches.** The stop's primary landing face, as the pipeline selected it: every segment of the stop that `contacts.json` flags `primary_face: true`, in file order (`LandingGeometry.PrimaryFace`). The flagged segments must all be used for contact and join end to start within 1 mm; a face split into several subsegments is approached as one, off the middle of the line from its first start to its last end, and must be straight within 1°. Anything else stops the build with the file and the reason. The builder never takes the stop's first usable segment instead: at South Williamsburg that is the barge's SW end (216221522), chart-verified but not where boats land. The contact tests aim at the same face through the same call.

**Every live run start is checked.** The bootstrap checks each run (on `Initialize` and on every `ResetRun`) starts at least 1 m clear of every face (`FaceClearance`). If not, it ends the run before its first physics step, logs an error naming the face and keeps the reason in `SliceBootstrap.StartRefusal`: the core only takes contact from a face the hull crosses from its water side, so a hull placed overlapping a face is never pushed by it. A refused run has no step and no live telemetry (the panel shows no data); the next clear start runs as usual.

Rebuild after the world, the import or the landing data change (the same inputs give the same bytes; the world's object ids are kept and new ones are hashed from names):

```
lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -nographics -projectPath "$PWD/unity" \
  -executeMethod FerrySim.Slice.Authoring.SliceSceneBuilder.BuildFromCommandLine -logFile "$PWD/artifacts/unity/slice-build-scene.log"
```

The builder also appends the scene, enabled, to the build settings if it is missing (coordinator-approved; existing entries untouched).

## Players

The slice runs in a player as it does in the editor. A player has no repository, so every standalone player build copies the files the slice reads at run time into the player's StreamingAssets, at their repository paths, where `SimPaths.Resolve` looks first (`Build/Editor/`, `FerrySim.Build.PlayerPayload`).
- **Which files.** The build copies the files that the built scenes, and the project assets they reference, name in serialized `*File` fields. Today those are the scenario's `vessels/t-class.json` and the placement's `geoid_offset.json`.
- **The record.** Beside the files goes `build-info.json` (`PlayerBuildInfo`): the version, the commit, whether the tree was dirty, and each file's SHA-256. A player logs it at startup.
- **A missing file.** A player whose build left out the vessel file stops with an error naming it (`SliceBootstrap.PlayerError`), rather than with the runner's own file-not-found exception.
- **Other data.** The landing data and fenders travel as TextAssets. Recordings go to the player's persistent data folder (`runs/`).

**Smoke run.** In a player, `-ferrysim-smoke <folder> [-ferrysim-smoke-seconds <s>]` starts `PlayerSmokeRun`. It:
1. waits for the scene's run to step
2. restarts it with both levers at −0.3, astern off the face the scenario starts at, for 10 simulated seconds unless told otherwise
3. captures the wheelhouse and chase views
4. ends the run and writes `smoke.json`

It quits with 0 when every check passed, 1 when one failed, and 2 when the switches are unusable. [docs/releasing.md](../../../../docs/releasing.md) lists the checks. `scripts/release.sh` runs it on the app installed from the disk image.

## Landing faces from data

`LandingGeometry` turns the world importer's `World/Generated/LowerEastRiver/contacts.json` into `ContactSegment`s and a `SegmentListGeometry` at run time; `SliceBootstrap` hands it to `VesselSimulationRunner.WorldGeometry` before the first run.

- **Contract.** A JSON list of segments with `id`, `stop_id`, `stop_name`, `start_ne_m`, `end_ne_m`, `water_normal_ne`, `source`, `confidence`, `verification`, `water_side_verified_by_pipeline` and the optional `water_side_crew_reported` and `primary_face` (World/Import/README.md). Unknown fields, such as `polyline_id`, `index`, `water_side_evidence` and `crew_evidence`, are ignored, and the number of segments does not matter.
- **Which segments are used.** A segment is used when `water_side_verified_by_pipeline` or `water_side_crew_reported` is true (either may be absent, which counts as false) and `water_normal_ne` is within 1° of the left-hand normal of start to end (the core's contract: solid on the right, water on the left). Every other segment is left out and listed with its reason, as a warning at startup and in `LandingGeometry.Describe()`. Segments used on the crew's report alone are listed apart (`CrewReportedOnly`), because the pipeline's chart check did not confirm them.
- **Ids** are the pipeline's, unchanged; the file order is kept.
- **Primary faces.** `primary_face: true` marks every subsegment of the face the pipeline selected for the stop; `LandingGeometry.PrimaryFace(stop)` returns them, or fails naming the file when they are missing, left out of contact or do not join. It is independent of the water-side flags: South Williamsburg's primary face is used on the crew's report alone.
- **Malformed or missing input fails loudly**: `LandingDataException` names the file and every problem (not JSON, not a list, a missing field, an id that is not a whole number from 1 to 2³¹−1 or is repeated, a number past Int64 or double range, a point that is not two finite numbers, end points too far apart for a finite length, a water normal that is not a unit vector, a segment shorter than 1 mm, a flag that is not true or false). Integers are range-checked as read, never cast first, and the core's own segment check is a backstop that also reports through `LandingDataException`. The bootstrap then logs the error and disables the runner, and ends any run that is started anyway before its first step, so the vessel never runs through the landings by accident. A failure the loaders do not name (a bug in them) fails closed the same way, with the exception logged.

With the importer's current file: 40 segments, 37 used, 3 left out (one end side each at Battery Park City/Vesey St., North Williamsburg and Wall St/Pier 11, water side not verified); one used on the crew's report alone (149111046, South Williamsburg's primary face). `artifacts/unity/slice/landing-geometry.txt` lists them after the EditMode tests.

### Fender parameters

Each landing's `ContactSurface` comes from `Data/landing-contact-surfaces.json`, never from code: a `default` entry and optional per-landing entries keyed by GTFS `stop_id` (none yet). Every value is a parameter with `value`, `unit` (the contract's: `N/m2`, `N*s/m2`, `1`, `m`), `source` and `confidence`; unknown properties are errors, as in vessel files, and a non-estimated value must cite a key in `sources`. Today every value is `estimated`: the elastomeric barge fender the core's contact report uses, stiffness 1.0e6 N/m per m, damping 5e4 N·s/m per m, friction 0.3, travel 0.25 m. No landing has been surveyed.

### Contact test landing

The contact tests use **South Williamsburg** (stop 8), a NOAA ENC barge outline with medium confidence in the middle of the slice, and land on its primary face: the NW long river side, 149111046. Crew report that boats land there (`south-williamsburg-face-orientation-2026-10-24`), while the pipeline's chart check fails on a dry charted structure near the face, so the face is used on the crew's report alone. The barge's SW end (216221522, 11.0 m, chart-verified) is used for contact too, and the landing tests require it untouched.

## Geometry identity in recordings

Coordinator-approved change in `Sim/`: `WorldGeometryDigest` (a 64-bit FNV-1a over every bit of every segment, in order) is written to each recording's header as `world_geometry_digest` (`none` for open water). `ReplayPlayer.Verify` returns a failed result, naming both digests, without replaying when the geometry differs, and `VesselSimulationRunner.InitializeReplay` throws. Recordings without the header replay as before. `LandingGeometry.Digest` is the same digest.

## Water height

`WorldWaterHeight` implements the bridge's `IWaterSurface` on the world's `WaterSurface`: the ellipsoidal height of the drawn surface, the slice's GEOID18 grid under the point plus the water level. `CesiumVesselPlacement.waterSurfaceSource` points at it, so the hull sits on the surface the camera sees (within 0.03 cm at Pier 11, South Williamsburg and Hunters Point in the PlayMode test; the slice-centre constant from `geoid_offset.json` would be 1 to 8 cm off).

**The level is constant: NAVD88 0.0 m**, the world scene's `WaterSurface` setting (`waterLevelNavd88: 0` in `World/Scenes/LowerEastRiver.unity`). It does not follow the tide. The pipeline's CO-OPS data in `data/processed/lower-east-river/water_levels/` are observed levels (The Battery 8518750 and Kings Point 8516945, 14 to 16 June 2026, 6-minute samples), not predictions; at the scenario start The Battery read −0.093 m NAVD88, falling about 0.33 m an hour. A tide driver would set `WaterSurface.WaterLevelNavd88` from such a series; the placement would follow without change, because the adapter reads the drawn surface's level.

## Imported terrain on the water's datum

The bootstrap instantiates the importer's `lower-east-river.prefab` (terrain, chart structures, aids, landing strips) under the world frame. The importer maps heights flat, Unity y = NAVD88 + the slice-centre geoid offset (its `import-manifest.json`), while the world's water follows the slice's geoid grid and Earth curvature (World/README.md). Placed as imported, the terrain stands 1.2 m proud of the drawn water 4 km out (3.3 m at the far tiles), so shoals show through the water as flat cyan patches. `ImportedTerrain` moves every vertex of the terrain and structure meshes (on copies) and every placed object by the difference between the two NAVD88 zeros, `WaterSurface.SurfaceUpAt(datum, n, e, 0) − navd88_zero_y_m`: −4.0 to −0.09 m across the slice, about 0.55 s at startup. It runs only when the manifest states that exact flat mapping, so an importer that adopts the world's mapping is not moved twice. An instance is corrected once: the `TerrainCorrection` component it leaves on the instance marks it, so a second `Apply` changes and copies nothing, and one with another datum or height throws. The fix belongs in the importer; this keeps the joined scene right until then.

## Tests

Run with the commands in `unity/README.md` (`-assemblyNames FerrySim.Unity.Slice.Tests.EditMode` or `.PlayMode`). PlayMode needs graphics.

| Test | Checks |
|---|---|
| `LandingGeometryTests`, `LandingSurfaceTableTests` | The loader and the fender file, every failure mode above (the Astra review's id 9223372036854775808 and ±1e308 end points among them), primary faces, and the importer's real file read independently |
| `SliceScenarioTests`, `LandingApproachTests` | The shipped scenario is the builder's scenario for the imported data; the approach off a primary face, whole or split, and a bent face refused |
| `WorldGeometryDigestTests`, `GeometryIdentityTests` | The digest, and recordings and replays with the same and other faces (EditMode, through the runner) |
| `FaceClearanceTests`, `WorldWaterHeightTests`, `ImportedTerrainTests` | Scenario clearance, the water adapter, the terrain correction, applied once per instance |
| `PlayerSupportTests` | A player without the vessel file fails with a message naming the file, StreamingAssets and docs/releasing.md |
| `PlayerSmokeOptionsTests`, `PlayerBuildInfoTests` | The smoke run's switches; the build record's round trip, and a missing, malformed or version-less record |
| `IntegratedNoDataTests` (a) | Before the first step and after the run ends every instrument, the HUD and the alarms show no data |
| `IntegratedContactLatchTests` (b) | A one-step touch of South Williamsburg's primary face between rendered frames is latched and shown (PEAK on the contact indicator) |
| `IntegratedRunResetTests` (c) | An acknowledged depth alarm and the contact latch reset on a new run |
| `LandingSmokeTests` (d) | Scripted landing at about 1 kn on the primary face (checked against the file's `primary_face` flags): force, compression within the fender's travel, at rest on the face, only that face touched; writes `artifacts/unity/slice/landing-smoke.md` |
| `WaterHeightTests` (e) | The hull on the drawn water within 1 cm at three positions; the terrain on the water's NAVD88 frame |
| `IntegratedReplayTests` (f) | A landing recorded in the scene replays bit for bit, contact included, through `ReplayPlayer.Verify` and the runner |
| `SliceBootstrapTests` | Missing or malformed landing data stops the runner with the file and the reason, and a run started anyway takes no step |
| `StartClearanceTests` | A start overlapping the face or 0.5 m off it is ended before its first step, after a reset too, with the reason kept |
| `SliceSceneSmokeTests` | The scene as a user opens it runs without errors; one handler for the helm's camera button; the player check reads the scenario's vessel file |
| `SliceEvidenceTests` | Screenshots `artifacts/unity/slice-wheelhouse.png` and `slice-chase-landing-contact.png`, frame time and GC allocations in `artifacts/unity/slice-performance.json` |
| `PlayerSmokeRunTests` | The release smoke run on the scene: passes on its 10 s astern drive; fails on a logged error, and on a vessel file read from the repository where a player must read StreamingAssets; quits with its code; writes `artifacts/unity/slice/player-smoke/` |

## Known limits

- **Environment.** The scenario's environment is uniform (calm, 50 m deep), so depth under the keel reads 158 ft at the landing; real depths, currents and wind are later work.
- **Landing faces only.** contacts.json holds each landing's berthing face, not the barge's other sides, so a hull sliding past a face's open end passes into the barge's footprint unopposed (the core's own open-end limitation).
- **Garbage per frame.** The runner's stepping and placement allocate nothing (asserted, open water and in contact), and neither does a UI refresh of a sample already on screen. While under way the UI allocates about 34 objects a frame formatting new readouts, and recording at 10 Hz about 5 a frame on average (measured, not asserted; `slice-performance.json`).
- **Aids** flash on Unity play time, not simulation time (`BuoyFlasher.SetSimulationTime` is not driven yet).
- **Tide**: constant level, above.
- **Refused starts are recorded.** The runner opens a run's recording before it raises `RunStarted`, so a start the bootstrap refuses leaves a recording with its header and start state and zero steps.
- **Editor only**, above.
