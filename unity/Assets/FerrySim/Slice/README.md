# Slice

Owner: **unity-slice-integration**. The Lower East River slice as one drivable scene: the world (terrain, water, landings), the bridge's simulated T-class (or K-class) and the wheelhouse UI on its real telemetry, with landing contact against the real landing faces, the vessel on the world's water and, since M3, the tide, current, wind and depth of a real day from an environment pack. Units, frames and signs follow [docs/conventions.md](../../../../docs/conventions.md). Nothing in `Sim/`, `Input/`, `World/` or `UI/` is changed by this folder, apart from the coordinator-approved geometry digest (below).

| Folder | Assembly | Contents |
|---|---|---|
| `Slice/` | `FerrySim.Unity.Slice` (namespace `FerrySim.Slice`) | `SliceBootstrap`, landing geometry from data, fender parameters, the water height adapter and the tide (`Water/`), the imported terrain on the water's datum, the scenario catalog and launcher (`SliceScenarioCatalog`, `SliceScenarios`) |
| `Slice/Authoring/` | `FerrySim.Unity.Slice.Authoring` (editor only) | `SliceSceneBuilder`: builds the scene from code |
| `Slice/Data/` | (data) | `landing-contact-surfaces.json`: the landings' fender parameters |
| `Slice/Scenes/` | (generated) | `LowerEastRiverSlice.unity` and its scenario asset |
| `Slice/Player/` | (in `FerrySim.Unity.Slice`) | `PlayerBuildInfo`, the build's record in a player; `PlayerSmokeRun` with its options and report, the release smoke run |
| `Slice/Tests/EditMode/`, `Slice/Tests/PlayMode/` | `FerrySim.Unity.Slice.Tests.*` | Tests for this folder and the integration |

## The scene

`Scenes/LowerEastRiverSlice.unity` is the world scene (`World/Scenes/LowerEastRiver.unity`, opened by the builder and never saved back) plus:

- `Vessel` under the `CesiumGeoreference`: `VesselSimulationRunner` (T-class, recording every run), `HelmInputController` (keys in [Input/README.md](../Input/README.md)), `CesiumVesselPlacement` reading the world's water through `WorldWaterHeight`, and the bridge's greybox. The world's `WheelhouseMount` is moved aboard at the helm eye point, facing the bow and tilted 8° down: the panel covers the bottom 42% of the screen, and a level eye 6.9 m up would lose the water under it out to about 65 m ahead (28 m with the tilt). Head look (right mouse) still looks anywhere within the mount's limits.
- `Slice`: `SliceBootstrap` (also the scenario catalog and launcher) and `WorldWaterHeight`.
- `Wheelhouse UI`: `WheelhouseUI` bound to the runner.

The world's `CameraRig` follows the vessel: F2 chase, F3 wheelhouse (F1 free-fly), and the helm's camera button swaps chase and wheelhouse. The panel shows in the wheelhouse view and the HUD in the others.

The scenario (`Scenes/LowerEastRiverSliceScenario.asset`) starts the T-class at rest with its bow 40 m off the middle of South Williamsburg's primary landing face, the NW long river side (id 149111046, 25.6 m), heading straight in (125°), on a real day: the environment pack `lower-east-river/2025-07-20` from 18:48 EDT (`2025-07-20T22:48:00Z`). The builder computes the start from the landing data, so it follows regenerated data, and refuses to build a start less than 5 m clear of every face. `SliceScenarioTests` fails when the shipped scenario is not exactly what the builder derives from the imported data, so a data change without a rebuild is caught (clearance alone would not catch a stale pose).

**Why that day and time.** The landing is the scene's long-standing one; the day and time are chosen so that the first run a reviewer sees can do what the scene is for, a landing at South Williamsburg:
- **Enough water to land.** The time was first chosen because the pack's depth grid was shallow off this face (bed −1.06 m NAVD88 10 m off, −1.39 m 17 m off), so a T-class landing there grounded unless the tide stood above about +0.7 m. Since the operational-ais floor (data-pipeline/README.md, Operational depth floor), the grid off this face is the depth the fleet's own boats were seen afloat in: bed −3.19 m from 5 to 20 m off and −2.91 m 30 m off (see Known limits). A T-class (1.9 m `maxDraft`) landed there, midships about 13 m off, now grounds only below about −1.3 m of tide, lower than the water there on any of the 28 scenario days (−1.13 m at its lowest). At 18:48 the water stands about 0.78 m above NAVD88, under two hours after the day's high water (1.07 m at The Battery at 17:06).
- **Slack water.** The flood has just turned: the current at the start is about 0.13 m/s, setting down the river along the face and a little off it, so a run left alone drifts gently instead of being set onto the barge at a knot or two, as it is most of the flood. Earlier, with more water, the late flood sets a landing boat along the face onto the shoal to the north-east; later, the ebb and the falling tide do the same to the south-west.
- **Daylight and a real day.** Sunday 20 July 2025 is one of the 28 scenario days with all its data (three tide gauges, five current stations, wind from Kings Point, about 4.6 m/s); the sun is still well up at 18:48.

The E2E test `SouthWilliamsburg_AtTheScenesDayAndTime_LandsSoftWithoutGrounding` lands a T-class there at that day and time, from the catalog's 4 m start, and keeps at least 1.79 m under the keel (0.06 m before the floor). With the same scripted landing on the grid before the floor, start times from 18:36 to 19:06 landed, held and never grounded, and 18:48 held stillest; that sweep has not been repeated on the floored grid.

The existing calm checks keep their own explicit calm scenarios (`SliceScene.Approach`, and the E2E trips' `TripData.Start`).

**Picking another scenario.** `SliceBootstrap` is the hook point for a scenario picker. It implements `IScenarioCatalog` and `IScenarioLauncher` (`ScenarioCatalog` and `ScenarioLauncher` return the component itself, so a UI can reference it in a serialized field), through `SliceScenarios`, and `EnvironmentSource` is the runner's environment readout. The catalog offers the slice's landings, the pack days in the bootstrap's `environmentFolder` (`data/processed/lower-east-river/env`) and the vessels in its `vesselFiles` (`vessels/t-class.json`, `vessels/k-class.json`); the launcher starts a choice on the runner. Sim/README.md (The scenario catalog) has the rules. A UI binds to the interfaces only.

**The wheelhouse UI is wired to it.** In `Awake`, the bootstrap calls `WheelhouseUI.ConnectScenario(runner, this, this)`. The panel and the HUD then read the runner's water and air, and the scenario picker uses this catalog and launcher. The helm's new-run key (N, `HelmInputMapper.NewRunRequested`) opens the picker (`ToggleScenarioPicker`). While it is open, the run is paused and the helm is held (its action map is disabled), so browsing never moves the boat. Closing it, or starting a run from it, gives both back, but a run the captain had already paused stays paused. Escape or the picker's Close button closes it.

**Each run's vessel and day.** At every run start the greybox is rebuilt from the run's vessel file and the wheelhouse eye moves to that vessel's helm (`GreyboxDimensions`), so a K-class run is drawn and seen as a K-class. The sun follows the run's start time, and the drawn water its tide (Water height).

**Traffic.** When a run's scenario names a traffic day (`traffic`, Sim/README.md), the bootstrap's `TrafficView` (added and connected in `Awake`) draws that day's harbor traffic on the drawn water, in the world frame, from the core's replay (Traffic/README.md); runs without traffic draw none.

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
- **Which files.** The build copies the files that the built scenes, and the project assets they reference, name in serialized `*File` fields, and every file of the folders they name in `*Folder` fields. Today those are the scenario's `vessels/t-class.json`, the placement's `geoid_offset.json`, the catalog's `vessels/t-class.json` and `vessels/k-class.json`, and every file of `data/processed/lower-east-river/env`: the region's depth set and all 28 days, about 8.3 MB, including any file a later pipeline adds there.
- **The record.** Beside the files goes `build-info.json` (`PlayerBuildInfo`): the version, the commit, whether the tree was dirty, and each file's SHA-256. A player logs it at startup.
- **A missing file.** A player whose build left out the vessel file or the scenario's environment pack stops with an error naming it (`SliceBootstrap.PlayerError`), rather than with the runner's own exception.
- **Other data.** The landing data and fenders travel as TextAssets. Recordings go to the player's persistent data folder (`runs/`).

**Smoke run.** In a player, `-ferrysim-smoke <folder> [-ferrysim-smoke-seconds <s>]` starts `PlayerSmokeRun`. It:
1. waits for the scene's run to step
2. restarts it with both levers at −0.3, astern off the face the scenario starts at, for 10 simulated seconds unless told otherwise
3. captures the wheelhouse and chase views
4. ends the run and writes `smoke.json`

It quits with 0 when every check passed, 1 when one failed, and 2 when the switches are unusable. On a pack scenario (the scene's own) `smoke.json` also names the pack, its digest and the folder it was read from, which in a player must be inside StreamingAssets, and checks that the drawn water stood at the pack's level at the vessel. [docs/releasing.md](../../../../docs/releasing.md) lists the checks. `scripts/release.sh` runs it on the app installed from the disk image.

## Landing contact from data

`LandingGeometry` (Landings/README.md) turns the world importer's `World/Generated/LowerEastRiver/contacts.json` into `ContactSegment`s and a `SegmentListGeometry` at run time; `SliceBootstrap` hands it to `VesselSimulationRunner.WorldGeometry` before the first run.

- **Contract: contacts.json version 2** (data-pipeline/README.md, World/Import/README.md). The file holds each landing's whole structure, not only its berthing face. Every segment has a `kind`: `berth-face`, `barge-side` or `barge-end` for the landing's barge, its other berths and nearby pontoons (each berth with its `berth_id`), and `pier`, `bulkhead` or `shoreline` for the fixed structures within 150 m of the GTFS stop. The version 1 fields keep their meaning: `id`, `stop_id`, `stop_name`, `start_ne_m`, `end_ne_m`, `water_normal_ne`, `source`, `confidence`, `verification`, `water_side_verified_by_pipeline`, and the optional `water_side_crew_reported` and `primary_face`. `solid_side_evidence` records the charted solid-side proof. Other fields, such as `polyline_id`, `index` and `crew_evidence`, are ignored, and the number of segments does not matter. A version 1 file (no `kind`) still loads, with the version 1 gate.
- **Which segments are kept (the runtime gate).** A segment is kept for contact when `water_normal_ne` is within 1° of the left-hand normal of start to end (the core's contract: solid on the right, water on the left) and

  `water_side_verified_by_pipeline || water_side_crew_reported || (!primary_face && solid_side_evidence.verified)`

  That is, its water side is verified by the pipeline's chart test or reported by crew, or, for any segment but a primary face, its charted solid side is verified: it is an edge of a closed charted outline with charted water 0.5 m out on its left (Landings/README.md). A primary face is never kept on its solid side alone. A missing flag counts as false.
- **What was kept and left out.** `LandingGeometry.Excluded` lists every segment left out, with its reason, and each is also a warning at startup. `CrewReportedOnly` lists the segments kept on the crew's report alone, which the pipeline's chart check did not confirm, and `SolidSideOnly` those kept on their charted solid side alone. `Describe()` lists every kept segment with its kind, berth, basis (`pipeline`, `crew`, `pipeline+crew` or `solid-side`) and fender; the EditMode tests write it to `artifacts/unity/slice/landing-geometry-v2.txt`.
- **Ids** are the pipeline's, unchanged; the file order is kept.
- **Primary faces.** `primary_face: true` marks every subsegment of the face the pipeline selected for the stop. `LandingGeometry.PrimaryFace(stop)` returns them, or fails naming the file when they are missing, left out of contact or do not join. It is independent of the water-side flags: South Williamsburg's primary face is kept on the crew's report alone.
- **Malformed or missing input fails loudly**: `LandingDataException` names the file and every problem (not JSON, not a list, a missing field, an id that is not a whole number from 1 to 2³¹−1 or is repeated, a number past Int64 or double range, a point that is not two finite numbers, end points too far apart for a finite length, a water normal that is not a unit vector, a segment shorter than 1 mm, a flag that is not true or false). Integers are range-checked as read, never cast first, and the core's own segment check is a backstop that also reports through `LandingDataException`. The bootstrap then logs the error and disables the runner, and ends any run that is started anyway before its first step, so the vessel never runs through the landings by accident. A failure the loaders do not name (a bug in them) fails closed the same way, with the exception logged.

With the importer's current file, 446 segments are read and 445 kept: 21 berth faces, 35 barge sides, 44 barge ends, 117 pier, 187 bulkhead and 41 shoreline segments. Of the kept segments:
- 233 are kept on the pipeline's water-side check, and 3 on that and the crew's report;
- 208 are kept on their charted solid side alone (`SolidSideOnly`): 74 bulkhead, 69 pier, 26 barge-side, 20 barge-end and 15 shoreline segments, and 4 berth faces off the primary face;
- 1 is kept on the crew's report alone (`CrewReportedOnly`): 149111046, South Williamsburg's primary face.

One segment is left out (`Excluded`): 1424255117, a Corlears Hook bulkhead. Neither its water side nor its solid side is verified.

### Fender parameters

Every kept segment's `ContactSurface` comes from `Data/landing-contact-surfaces.json`, never from code, by its stop and kind: `LandingGeometry.Surfaces.For(stop, kind)`. That is the landing's own entry (under `landings`, keyed by GTFS `stop_id`; none yet) on that landing's barges and berths only, else the kind's entry under `kinds`, else `default`, so a landing's own fender never reaches the piers and bulkheads beside it. Every value is a parameter with `value`, `unit` (the contract's: `N/m2`, `N*s/m2`, `1`, `m`), `source` and `confidence`. Unknown properties and kinds are errors, as in vessel files, and a non-estimated value must cite a key in `sources`. Today every value is `estimated`; no landing has been surveyed.

| Kind | Stiffness, N/m per m | Damping, N·s/m per m | Friction | Travel, m |
|---|---|---|---|---|
| `berth-face`, `barge-side`, `barge-end` | 1.0e6 | 5.0e4 | 0.3 | 0.25 |
| `pier` | 2.0e6 | 7.07e4 | 0.4 | 0.15 |
| `bulkhead` | 1.0e7 | 1.58e5 | 0.5 | 0.05 |
| `shoreline` | 1.0e7 | 1.58e5 | 0.6 | 0.05 |

The barge kinds use the elastomeric barge fender of the core's contact report. A timber-piled pier is stiffer, with less travel. An unfendered bulkhead and a rip-rap or natural shoreline are nearly rigid. Damping scales with the square root of stiffness, so the damping ratio stays the barge fender's.

### Contact test landing

The contact tests use **South Williamsburg** (stop 8), a NOAA ENC barge outline with medium confidence in the middle of the slice, and land on its primary face: the NW long river side, 149111046. Crew report that boats land there (`south-williamsburg-face-orientation-2026-10-24`), while the pipeline's chart check fails on a dry charted structure near the face, so the face is used on the crew's report alone. The barge's SW end (216221522, 11.0 m, chart-verified) is used for contact too, and the landing tests require it untouched.

## Geometry identity in recordings

Coordinator-approved change in `Sim/`: `WorldGeometryDigest` (a 64-bit FNV-1a over every bit of every segment, in order) is written to each recording's header as `world_geometry_digest` (`none` for open water). `ReplayPlayer.Verify` returns a failed result, naming both digests, without replaying when the geometry differs, and `VesselSimulationRunner.InitializeReplay` throws. Recordings without the header replay as before. `LandingGeometry.Digest` is the same digest.

## Water height

`WorldWaterHeight` implements the bridge's `IWaterSurface` on the world's `WaterSurface`: the ellipsoidal height of the drawn surface, the slice's GEOID18 grid under the point plus the water level. `CesiumVesselPlacement.waterSurfaceSource` points at it, so the hull sits on the surface the camera sees (within 0.03 cm at Pier 11, South Williamsburg and Hunters Point in the PlayMode test; the slice-centre constant from `geoid_offset.json` would be 1 to 8 cm off).

**The level follows the run's tide** (`Water/TideFollower.cs`, made by the bootstrap). Just before the runner places the vessel, every frame and at every run start (`VesselSimulationRunner.BeforePlacement`), it sets `WaterSurface.WaterLevelNavd88` to the pack's water level at the vessel's reference point at the run's time (the runner's environment readout). The hull, which `WorldWaterHeight` places on the drawn level, the drawn water and the floating barges (`FloatOnWater`, in the same frame's LateUpdate) therefore agree. The drawn surface is one level for the whole slice: the level at the vessel. The pack's levels differ across the slice, because its gauges are blended by inverse distance squared and the northern landings take some of Kings Point's and Willets Point's tide: between the slice's landings by 0.19 m at the median and up to 0.41 m over the 28 days, but by under 2 cm within 200 m of any landing. So the barge the vessel is at floats at its own level, and one far away at the vessel's. Calm and uniform runs have no tide: the water returns to the level the scene was built with, NAVD88 0.0 m (`waterLevelNavd88: 0` in `World/Scenes/LowerEastRiver.unity`).

## Imported terrain on the water's datum

The bootstrap instantiates the importer's `lower-east-river.prefab` (terrain, chart structures, aids, and contact strips on the fixed structures) under the world frame. The importer draws no strip for a barge kind: its strips stand fixed at NAVD88 +0.18 m and would show above a floating deck at low water, while the greybox draws the barges' sides on the tide. The importer maps heights flat, Unity y = NAVD88 + the slice-centre geoid offset (its `import-manifest.json`), while the world's water follows the slice's geoid grid and Earth curvature (World/README.md). Placed as imported, the terrain stands 1.2 m proud of the drawn water 4 km out (3.3 m at the far tiles), so shoals show through the water as flat cyan patches. `ImportedTerrain` moves every vertex of the terrain and structure meshes (on copies) and every placed object by the difference between the two NAVD88 zeros, `WaterSurface.SurfaceUpAt(datum, n, e, 0) − navd88_zero_y_m`: −4.0 to −0.09 m across the slice, about 0.55 s at startup. It runs only when the manifest states that exact flat mapping, so an importer that adopts the world's mapping is not moved twice. An instance is corrected once: the `TerrainCorrection` component it leaves on the instance marks it, so a second `Apply` changes and copies nothing, and one with another datum or height throws. The fix belongs in the importer; this keeps the joined scene right until then.

## Tests

Run with the commands in `unity/README.md` (`-assemblyNames FerrySim.Unity.Slice.Tests.EditMode` or `.PlayMode`). PlayMode needs graphics.

| Test | Checks |
|---|---|
| `LandingGeometryTests`, `LandingSurfaceTableTests` | The loader and the fender file, every failure mode above (the Astra review's id 9223372036854775808 and ±1e308 end points among them), primary faces, and the importer's real file read independently |
| `SliceScenarioTests`, `LandingApproachTests` | The shipped scenario is the builder's scenario for the imported data and runs on its pack day at 18:48 local; the approach off a primary face, whole or split, and a bent face refused |
| `ScenarioCatalogTests` | The catalog on the real landings, packs and vessel files: landings in GTFS stop id order, the 28 days oldest first with their own offsets and wind, the T- and K-classes; a choice's start (the trips' bow-on 4 m start, the pack's midnight plus the minutes, on the pack); every landing's start, for both vessels, against each kept contact segment (none overlapping or within 1 m; the nearest of each kind in `artifacts/unity/slice/scenario-catalog/start-clearances.txt`); every refusal with its reason; the region from the folder |
| `TideFollowerTests` | The drawn water at the pack's level at the vessel before every placement, back to the base level for calm water, and still after the follower is disposed |
| `WorldGeometryDigestTests`, `GeometryIdentityTests` | The digest, and recordings and replays with the same and other faces (EditMode, through the runner) |
| `FaceClearanceTests`, `WorldWaterHeightTests`, `ImportedTerrainTests` | Scenario clearance, the water adapter, the terrain correction, applied once per instance |
| `PlayerSupportTests` | A player without the vessel file or the scenario's pack fails with a message naming it, StreamingAssets and docs/releasing.md |
| `PlayerSmokeOptionsTests`, `PlayerBuildInfoTests` | The smoke run's switches; the build record's round trip, and a missing, malformed or version-less record |
| `IntegratedNoDataTests` (a) | Before the first step and after the run ends every instrument, the HUD and the alarms show no data |
| `IntegratedContactLatchTests` (b) | A one-step touch of South Williamsburg's primary face between rendered frames is latched and shown (PEAK on the contact indicator) |
| `IntegratedRunResetTests` (c) | An acknowledged depth alarm and the contact latch reset on a new run |
| `LandingSmokeTests` (d) | Scripted landing at about 1 kn on the primary face (checked against the file's `primary_face` flags): force, compression within the fender's travel, at rest on the face, only that face touched; writes `artifacts/unity/slice/landing-smoke.md` |
| `WaterHeightTests` (e) | The hull on the drawn water within 1 cm at three positions in calm water (level 0.0) and at Pier 11 on a pack day's high water; the terrain on the water's NAVD88 frame; at the packs' lowest water (2025-01-14) no imported contact strip on a barge and none standing above a floating deck, and every floating barge, berth and pontoon of the greybox riding the water with its fenders on its deck |
| `IntegratedReplayTests` (f) | A landing recorded in the scene replays bit for bit, contact included, through `ReplayPlayer.Verify` and the runner |
| `SliceBootstrapTests` | Missing or malformed landing data stops the runner with the file and the reason, and a run started anyway takes no step |
| `StartClearanceTests` | A start overlapping the face or 0.5 m off it is ended before its first step, after a reset too, with the reason kept |
| `SliceSceneSmokeTests` | The scene as a user opens it runs without errors; one handler for the helm's camera button; the player check reads the scenario's vessel file |
| `SliceEvidenceTests` | Screenshots `artifacts/unity/slice-wheelhouse.png` and `slice-chase-landing-contact.png`, frame time and GC allocations in `artifacts/unity/slice-performance.json`, on the scene's earlier calm approach (40 m off South Williamsburg, calm, noon on 15 June 2026) so the evidence stays comparable |
| `ScenarioLauncherTests` | The scene opens on its pack day with the water and every floating landing at the tide, and exposes the hook points; launching ends the run (recording closed) and starts a new one; a scenario that cannot start leaves the run going; a refused start reports the bootstrap's refusal; a K-class run is drawn and seen from the K-class's greybox and helm, and back; no garbage per frame on a pack run, in open water and in contact (`artifacts/unity/slice/pack-garbage/pack-run-garbage.json`) |
| `ScenarioPickerWiringTests` | The wheelhouse UI on the real simulation: the panel, the HUD and the picker are bound to the runner and the bootstrap; the new-run key opens the picker, which pauses the run and holds the helm until it closes; a run the captain paused stays paused; starting from the picker runs the chosen landing, day, time and vessel |
| `PlayerSmokeRunTests` | The release smoke run on the scene (its pack day): passes on its 10 s astern drive; fails on a logged error, and on a vessel file read from the repository where a player must read StreamingAssets; quits with its code; writes `artifacts/unity/slice/player-smoke/` |

## Known limits

- **Real days, provisional fields.** The current is the pack's channel field (the station's prediction at a station, following the shore away from one; core/FerrySim.Core/Environment/README.md), or the station blend for a pack without one. The wind is uniform without gusts, and the drawn tide is one level, the vessel's, so a distant barge can be drawn up to 0.4 m off its own level (Water height).
- **Shallow water at some landings.** The CUDEM and ENC merge is too shallow right off some primary faces, which real boats land at in all tides. The operational-ais floor (data-pipeline/README.md, Operational depth floor) deepens the merged bed where the fleet's T- and K-class boats were seen afloat, and only for the levels they were seen at. Off South Williamsburg the pack's bed is now −3.12 m NAVD88 at the face and −3.19 m from 5 to 20 m off (−0.74 m and −1.39 m 17 m off before), so a T-class grounds there only below about −1.3 m of tide. Brooklyn Navy Yard's catalog start is now at −2.90 m (−1.55 m before), so a T-class grounds there below about −1.0 m. Off Pier 11, the crew-modelled southern pontoon, which T/K boats use least of the three ramped pontoons, keeps its flat −2.85 m berth (the ENC's charted 2.0 m below MLLW) 10 to 35 m off the face; the face itself is now −2.45 m (+0.07 m before), and a ridge at −1.6 to −1.9 m 45 to 60 m off is a charted always-submerged pier (02261F00A4701C82), which the floor leaves alone. DUMBO and Corlears Hook are deep (−9 to −11 m). validate's approach-depth check still fails at these three landings at the window's lowest water; the scene's scenario and the E2E landings work with the floored grid.
- **Whole structures, not every structure.** contacts.json version 2 holds each landing's whole barge and berths and the piers, bulkheads and shoreline within 150 m of its stop, so a hull sliding off a face meets the barge's end or side, or the pier beside it. Points (MORFAC dolphins and piles), open lines in water (gangways, pier lines with no charted solid side) and always-submerged structures are not contact. Nor is anything beyond 150 m of a landing, or a segment left out (one at Corlears Hook): the hull passes through those unopposed.
- **Garbage per frame.** The runner's stepping and placement allocate nothing (asserted, open water and in contact, calm and on a pack day with the tide and the environment readout), and neither does a UI refresh of a sample already on screen. While under way the UI allocates about 34 objects a frame formatting new readouts, and recording at 10 Hz about 5 a frame on average (measured, not asserted; `slice-performance.json`).
- **Aids** flash on Unity play time, not simulation time (`BuoyFlasher.SetSimulationTime` is not driven yet).
- **Refused starts are recorded.** The runner opens a run's recording before it raises `RunStarted`, so a start the bootstrap refuses leaves a recording with its header and start state and zero steps.
- **Editor only**, above.
