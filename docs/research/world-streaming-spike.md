# World streaming spike: Unity tiles or local Cesium 3D Tiles for the whole harbor

M3 wave 1, task `world-streaming-spike`, on `main` at 3545e5b. Measured on 2026-10-25 on the Mac mini (Apple M1, 16 GB) with a development player and in the editor. The spike's code is throwaway (`unity/Assets/FerrySim/Spikes/`, `tools/spikes/world-streaming/`); this report is the product. Paths below are relative to `artifacts/world-streaming-spike/` unless they start with `unity/`, `tools/` or `data-pipeline/`.

> **Where the data and code are.** On `main` this report and the Python half of the tooling (`tools/spikes/world-streaming/`) are kept. The measurements (`runs/`, `analysis/`, `player/`) are not in git: they stay in the spike's worktree under `artifacts/world-streaming-spike/`. The Unity half (`unity/Assets/FerrySim/Spikes/`) and the run drivers are on branch `jefuhr/world-streaming-spike`. Approach B was adopted in [decision 0004](../decisions/0004-harbor-world-local-3d-tiles.md).

## Decision

**Recommendation: approach B.** Stream the harbor's terrain, and later its skyline, as local 3D Tiles that the data pipeline writes. Cesium for Unity loads them from the player's StreamingAssets under the scene's existing georeference. Keep everything the simulation depends on out of the streamed tiles: the core's contact segments and depth grid load whole, and the radar and camera get a view-independent harbor representation from the pipeline.

Why, from the measurements (player, 5x the service speed, the whole 31 km path, quiet machine):

- **Streaming does not separate them on this Mac.** The worst frame within two frames of a streaming event was 8.8 ms for B and 10.7 ms for A's 4 km ring. Of the 34 frames over 33 ms in the four quiet runs, 32 fell on a once-a-minute stall of the machine itself, and the no-tile control flight has the same stalls. The other two are a single long frame about 15 s after launch.
- **For the same view of the harbor, B uses less memory and starts faster.** Showing the whole harbor out to the fog, B averaged 696 MB of process memory while moving (804 MB by the end of the path, still growing as its tile cache filled). A with every tile resident held 1,371 MB. The bench alone, with no tiles, is 426 MB. B's first full view came 1.0 s after the first frame, and A's with every tile resident 3.8 s after it. A's 4 km ring matches B on memory (746 MB) only by drawing nothing beyond 4 km, while the fog leaves the skyline visible for about 25 km.
- **B's tiles are the pipeline's own output.** Python wrote all 36 tiles in 28 s, byte-identical on a rerun, as plain files that a player build copies. A needs a 2-minute editor import into 631 MB of text-serialised Unity assets, whose prefabs and scenes come out different on every import. Every data change then needs a player rebuild.
- **B is the mechanism the project already has.** Cesium is the scene's georeference today. The same `Cesium3DTileset` path later carries the skyline and, if approved, photoreal tiles (decision 0003). In the editor B behaves as it does in the player, while A's tile loads stalled the editor's main thread for up to 123 ms.

What A does better, and how the design keeps it: A's residency does not depend on where the camera looks. A 360-ray sweep therefore found geometry in 288 to 360 directions against A's colliders, and only 93 to 309 against B's. The design gives the radar its own view-independent geometry instead of either approach's render colliders (findings 2 and 3 below).

**Risks:**
- `file://` loading in the Windows player is untested.
- Cesium for Unity is a native plugin, so its upgrades need a gate.
- Its selection is per view, so nothing that must be reproducible may read render tiles.
- Its default 512 MB tile cache lets memory keep growing unless it is capped.

The design sketch below says how each is contained.

**What this needs from the user:** nothing to buy, install or approve now. There are no new packages, Cesium for Unity 1.26.0 is already in the project, and nothing uses a token or paid tiles. In wave 2, the streaming test needs one run on the Windows PC.

## At a glance

Player, 5x the T-class service speed (64.3 m/s), the whole path, each run 8 minutes, taken one after another between 06:30 and 07:03 UTC after the other wave 1 workers had finished ("quiet" runs). The earlier shared-machine runs agree (tables below).

| | A: ring 4 km | A: all resident | B: Cesium | Control: no tiles |
|---|---|---|---|---|
| Frame time median / p95 / p99 | 1.22 / 7.29 / 8.30 ms | 1.64 / 7.21 / 8.41 ms | 2.00 / 6.89 / 8.40 ms | 1.05 / 6.59 / 8.21 ms |
| Worst frame | 79.7 ms | 85.9 ms | 94.5 ms | 77.4 ms |
| Frames over 33 ms (at the minute stall / other) | 7 (7 / 0) | 11 (10 / 1) | 8 (7 / 1) | 8 (8 / 0) |
| Worst frame within 2 frames of a streaming event | 10.7 ms | no streaming while moving | 8.8 ms | – |
| Main-thread time, median | 0.79 ms | 1.39 ms | 1.71 ms | 0.53 ms |
| GPU time, median | 2.05 ms | 2.01 ms | 2.19 ms | 1.95 ms |
| Triangles drawn, median | 214,000 | 342,000 | 354,000 | 22,000 (water) |
| Draw batches, median | 46 | 130 | 138 | 5 |
| Process memory while moving, mean | 746 MB | 1,371 MB | 696 MB | 426 MB |
| Process memory, highest after start-up / at the end | 780 / 733 MB | 1,388 / 1,388 MB | 804 / 804 MB (rising) | 447 / 430 MB |
| Launch to first frame | 3.6 s | 3.5 s | 3.5 s | 3.5 s |
| First frame to first full view | 0.87 s (8 tiles) | 3.76 s (36 tiles) | 1.03 s | – |
| Moving time with the view not fully loaded (longest stretch) | 0.4% (0.24 s; tiles at the ring's 4 km edge in flight) | 0% | 1.0% (0.21 s) | – |
| Horizon | 4 km | the fog, about 25 km | the fog, about 25 km | – |
| 360-ray sweep hits at the 9 points | 288 to 360 | 300 to 360 | 93 to 309 | 0 |
| Tiles made by | the editor, 118.5 s for 36, plus a player rebuild | the editor, 118.5 s for 36, plus a player rebuild | Python, 27.7 s for 36, copied as files | – |

## Production design sketch for wave 2 (`harbor-world`)

### Shape

```
ferrydata (pipeline, Python)                          Unity player
────────────────────────────                          ────────────────────────────────────────────────
harbor tiles v1 rasters (merged NAVD88, per tile)     CesiumGeoreference (shared origin, as today)
  └─ harbor 3D Tiles writer  ──────────────┐            ├─ Cesium3DTileset "Harbor terrain"  ◄── StreamingAssets/harbor/terrain/tileset.json
     per tile: tileset.json, c<r>_<c>_l0/l1.glb        │     (file:// URL built at run time, no token, physics meshes off)
     root tileset.json (active tiles only)             │     opaque material: terrain shader (depth colour, lit, fog)
     index entry: files + SHA-256                      ├─ Cesium3DTileset "Skyline" (wave 2 skyline, same mechanism)
ferrydata/buildings (skyline task)                     ├─ World (WorldFrame, globe anchor pinned to the tangent plane)
  └─ the same writer, one more tileset ────────────────┤     ├─ WaterSurface: one curved mesh over the harbor, from a harbor datum
geoid per tile, landings, structures, aids             │     ├─ landings, barges, bridges, aids: global objects, curvature-correct
  └─ harbor datum, landing faces, segments ────────────┤     └─ vessel, wheelhouse mount (as today)
radar/camera raster (wave 3 input)  ───────────────────┤  Camera: CesiumGlobeAnchor + CesiumOriginShift (100 m), as today
environment pack (A1): currents, levels, depth grid ───┴─► core: IEnvironment (depth grid, whole harbor), IWorldGeometry (segments, whole harbor)
```

### Decisions inside the design

1. **Tiles are pipeline outputs, not Unity assets.** `ferrydata` writes the glTF tiles and tilesets from harbor tiles v1, deterministically (the spike's writer regenerated 1,189 files byte for byte), and records every file's SHA-256 in `index.json`. Nothing is imported into `Assets/`, and generated tiles stay out of git (plan item B5).
2. **Curvature is in the tiles.** Vertices are written in each tile's ENU frame through ECEF, and the tile's 3D Tiles `transform` pins that frame to the globe. Retire `Slice/ImportedTerrain`'s run-time correction for harbor content: nothing needs moving at run time.
3. **Chunking and LOD.** Keep the spike's structure to start: 16 chunks of 1 km per 4 km tile, L1 at 48 m refining to L0 at 12 m around 2 km, and add an L2 per 4 km tile (about 200 m) if far-field triangle counts matter once the real skyline is in. Tile boundaries follow harbor tiles v1; the 250 m overlap band is for data, not for render meshes (render chunks meet exactly on the grid lines, so neighbours share edge vertices and never overlap).
4. **Packaging.** The player build copies the harbor tilesets into StreamingAssets with their hashes in `build-info.json`, the way `PlayerPayload` already packages data files. That is a change to `Build/` (wave 2's `unity-environment` owns it). The URL is built at run time from `Application.streamingAssetsPath` and escaped (the spike loaded from a path with spaces in it, inside the `.app`). Data-only updates then need no Unity rebuild. Expect about 9 MB per active tile uncompressed (about 220 MB for 25 tiles). `KHR_mesh_quantization`, which the writer can produce with numpy alone and cesium-native supports, should roughly halve that (not tested in the spike).
5. **What does not stream.** The core's contact segments and depth grid load whole at scenario start (finding 2). The water surface is one global mesh from a harbor datum (geoid offsets from the tiles' `geoid_offset.json` corners, in place of the slice-only `SliceDatum`), dense near the camera's working area and coarser out to the fog. Landings, barges (with `FloatOnWater`), bridges and aids are global objects placed through `WorldFrame` with `SurfaceUp`, as the greybox builder does now: about 24 landings and a few hundred aids, small enough to keep resident.
6. **Raycasts.** Turn `createPhysicsMeshes` off on the render tilesets. The radar (wave 3) and camera clamping read a view-independent harbor representation from the pipeline (finding 3). Unity colliders stay for the vessel's own needs; contact physics stays in the core.
7. **Budgets to test against** (from the measurements, with headroom): process memory under 1.2 GB for the harbor with the stand-in skyline; no frame with a streaming event over 20 ms at 1x on this Mac; first full view within 3 s of a scenario start anywhere on the path; Cesium's tile cache capped explicitly (`maximumCachedBytes`, 256 MB to start) so memory is predictable.

### Order of work for `harbor-world`

1. **Pipeline writer and validation** (Python, first, because everything else consumes it): the harbor 3D Tiles writer from harbor tiles v1 rasters (the spike's `glb.py`, `tiles.py` and `generate.py` are a working starting point), with `validate --harbor` checks for content inside bounding volumes, neighbours meeting at shared edges (under 1 cm in ECEF), vertex density, determinism (build twice, same hashes) and size.
2. **Harbor scene in Unity:** the existing georeference, world frame and camera rig plus the terrain tileset loaded from StreamingAssets (editor: from `data/processed/`), with a terrain shader. The water on a harbor datum. Landings and structures from the harbor data.
3. **Packaging:** the build step that copies and hashes the tilesets into players; extend the release smoke run to check the tiles load in the shipped `.app`.
4. **Streaming end-to-end test:** drive Wall St to St. George, Astoria and Soundview without a load screen (plan B2's "done when"), asserting the budgets above, no Cesium load failures, and Cesium's load progress back at 100% within a few seconds of every turn; keep screenshots and the CSVs as the artifact.
5. **Windows:** run the same test on the Windows player on the user's PC early, because `file://` URLs with drive letters are the one packaging path the spike could not exercise.
6. **Skyline** (wave 2 `skyline`): a second tileset from `ferrydata/buildings` through the same writer, with its own LOD policy and night windows in its shader.

### Risks and how to contain them

| Risk | Containment |
|---|---|
| `file://` loading on Windows (drive letters, backslashes) is untested | Test on the user's PC in wave 2 before building on it; build the URL with `System.Uri` as the spike does |
| Cesium for Unity is a native plugin; an upgrade could change selection, threading or memory | Pin 1.26.0 (as now) and make the streaming E2E test the upgrade gate |
| Cesium's selection is per view: what is loaded depends on where the camera looks | Never use render tiles for simulation, radar or replays (findings 2 and 3) |
| cesium-native's memory sits outside Unity's managed and native counters, and the default 512 MB cache let B's footprint rise all the way along the path | Watch the process footprint ("System Used Memory") in the E2E test; cap `maximumCachedBytes` (re-reading a local tile is cheap) |
| The stand-in skyline is lighter than New York's: the real footprints can be several times denser in Manhattan and Brooklyn tiles | B's cost follows the view, not the total; recheck the budgets once the skyline tileset exists |
| Teleporting to a scenario start loads a whole new view at once | Measured: first full view about 1 s after the first frame from a cold start; the E2E test should cover a jump between distant landings |

## Findings that hold for either approach

### 1. Harbor geometry must carry Earth curvature; the slice importer's flat mapping does not scale

The slice importer places a vertex at Unity (east, NAVD88 + geoid, north) in the world tangent plane, ignoring how far the ellipsoid has dropped below that plane. The water (`WaterSurface`) is curved correctly, so the terrain stands too high by the drop. The slice works around it at startup: `Slice/ImportedTerrain` moves every terrain and structure vertex by up to 4.0 m (about 0.55 s). Across the harbor the same mapping would put NAVD88 zero this far above the water:

| Place | Distance from the world origin | Flat-mapping error |
|---|---|---|
| Wall St/Pier 11 | 0.8 km | 0.05 m |
| Slice north-east corner | 7.2 km | 4.1 m |
| St. George | 7.8 km | 4.8 m |
| Astoria | 10.4 km | 8.5 m |
| Soundview | 18.2 km | 26.0 m |
| Ferry Point Park | 19.0 km | 28.4 m |
| Grid's far corner (16, 16 km) | 22.6 km | 40.2 m |

So harbor tiles must be generated with curvature built in, not corrected at run time. The slice's correction, scaled to the harbor's 18 times as many vertices, would cost roughly 10 s at start-up, or the same spread over every tile load (an estimate). The spike does it the way wave 2 should: each tile's geometry is in its own east-north-up frame, through ECEF from each vertex's latitude, longitude and ellipsoidal height. Inside a 4 km tile that frame differs from a flat one by about 0.6 m at the corners, and the tile frame is pinned to the globe exactly, by a `CesiumGlobeAnchor` (A) or the 3D Tiles `transform` (B). The A and B screenshots at the same pose line up pixel for pixel, which checks both placements against each other.

### 2. What the core needs is not streamed

Contact physics stays in the core, and so does depth: `IWorldGeometry` (contact segments for landings, piers and bulkheads) and `IEnvironment.SampleDepth` (the environment pack's depth grid). Both are deterministic inputs to a deterministic simulation, so neither may depend on which render tiles happen to be loaded. They are small enough to load whole for the harbor at scenario start: a few thousand segments, and a depth grid of a few to a few tens of MB depending on its resolution. That is what makes "the core's contact and depth data is continuous across tiles" true by construction. Render tiles are visuals (and, at most, a camera's collision target).

### 3. Raycast targets for the radar should not be the render tiles

The 360-ray sweeps show the difference between the two kinds of residency ("Raycasts and colliders" below). Approach A's ring holds colliders all around the camera, so the sweep hits whatever is within the ring in every direction. Cesium selects tiles for what the main camera sees: behind the camera there is little or nothing loaded, so the same sweep from the same point finds a fraction of the shore, and what it finds changes with where the camera looks and what has finished loading. A radar picture must not depend on the camera, and replays must reproduce it. Whichever approach renders the harbor, wave 3's radar should raycast against view-independent geometry from the pipeline (a harbor height raster or extruded shoreline, structure and building footprints, a few MB for the whole harbor, loaded whole), not against streamed render tiles. The chase and wheelhouse cameras only need geometry near them, which is in view in either approach.

### 4. Floating origin

`CesiumOriginShift` (100 m, as the slice uses) shifted the origin about every 8 s at ferry speed and every 1.6 s at 5x. Each shift moves every globe anchor (A) or the tileset (B), and every collider under them. That costs 0.2 to 0.8 ms in the frame where it happens and never caused a hitch ("Floating origin" below). Keep the shift for the harbor, as the slice does.

Static batching must stay off for tiles. Statically batched geometry is baked in world space and would not follow the shift.

### 5. Keep collider cooking off the main thread

The project does not prebake collision meshes (`bakeCollisionMeshes: 0` in Player Settings), so a `MeshCollider` that is enabled in a loaded scene is cooked by PhysX on the main thread. Run `a4-x5-main` measured that against the job-baked default (`Physics.BakeMesh` in a job, then enable): at most 1.9 ms per tile on the main thread, because each 1 km chunk's collider mesh is small. It grows with mesh size, so wave 2 should keep any streamed colliders job-baked.

Cesium cooks its physics meshes with the same call (`Helpers.BakeMeshFromId`, invoked from its native tile preparation; the spike did not check which thread that runs on). Prebaking at build time only helps colliders that ship inside built scenes.

## Results

Every number here comes from `analysis/tables.md`. That file has every table per run, and `tools/spikes/world-streaming/analyze.py` regenerates it from each run's CSVs in `runs/<run>/`.

**Frame statistics** cover the moving phase only. They leave out the frames the bench flags for its own probes: each screenshot and raycast sweep, plus the three frames after it.

**Where each run was taken.** Runs marked "quiet" came after the other wave 1 workers had finished. The rest ran beside them and the coordinator's checks (`runs/<run>/machine.txt` and the machine-load table in `analysis/tables.md`). Load averages before and after the runs were 4 to 12 on the shared machine and 4 to 20 in the quiet window. The user's own apps (Chrome, qBittorrent, SonoBus, the Docker VM, the animated wallpaper, Spotlight, and in the quiet window Phone, FaceTime and photo analysis) ran throughout, and about 23 GB of swap stayed in use.

The A-versus-B comparison holds in both settings. Absolute numbers are best read from the quiet runs.

### Frame time, pooled by configuration

| Configuration | Where | Speed | Runs | Frames | Median ms | p95 ms | p99 ms | Worst ms | > 33 ms | > 50 ms |
|---|---|---|---|---|---|---|---|---|---|---|
| A ring 4 km | player | x1 (3 legs) | 3 | 1,098,723 | 1.24 | 7.23 | 8.29 | 112.1 | 48 | 17 |
| B Cesium | player | x1 (3 legs) | 3 | 804,494 | 1.99 | 6.92 | 8.36 | 109.2 | 45 | 23 |
| A ring 4 km | player | x5 | 1 | 206,230 | 1.30 | 7.42 | 8.40 | 68.8 | 6 | 2 |
| A all resident | player | x5 | 1 | 156,544 | 2.15 | 6.89 | 7.79 | 137.4 | 9 | 4 |
| B Cesium | player | x5 | 1 | 144,969 | 2.47 | 6.81 | 8.40 | 93.1 | 4 | 3 |
| A ring 4 km, main-thread colliders | player | x5 | 1 | 214,602 | 1.25 | 7.30 | 8.30 | 95.9 | 4 | 2 |
| A ring 4 km | player, quiet | x5 | 1 | 221,673 | 1.22 | 7.29 | 8.30 | 79.7 | 7 | 5 |
| A all resident | player, quiet | x5 | 1 | 168,972 | 1.64 | 7.21 | 8.41 | 85.9 | 11 | 5 |
| B Cesium | player, quiet | x5 | 1 | 154,198 | 2.00 | 6.89 | 8.40 | 94.5 | 8 | 6 |
| No tiles (control) | player, quiet | x5 | 1 | 252,519 | 1.05 | 6.59 | 8.21 | 77.4 | 8 | 6 |
| A ring 4 km | editor | x5 | 1 | 428,606 | 1.04 | 1.80 | 2.31 | 123.2 | 18 | 7 |
| B Cesium | editor | x5 | 1 | 202,732 | 2.33 | 3.28 | 4.00 | 61.9 | 7 | 3 |

- **There is a lot of headroom.** Every configuration draws a frame in 1 to 2.5 ms at the median on this Mac (uncapped, vSync off, 1080p, a development build), with p99 around 8 ms. A 60 Hz frame is 16.7 ms.
- **The player's p95 and p99 of 7 to 8 ms are the window, not the content.** They are the same with no tiles at all, and the batchmode editor, which renders off-screen, has a p95 of 1.8 to 3.3 ms. They come from the macOS window's presentation pacing.
- **A's 4 km ring is the cheapest because it draws the least:** about 214,000 triangles, against 342,000 for A with everything resident and 354,000 for B, since it has nothing beyond 4 km.
- **With the same view of the harbor, A (all resident) and B cost about the same.** B's main thread does about 0.3 ms more per frame (Cesium's tile selection, and 138 draw batches against 130, because Cesium gives every tile its own material instance).
- **Speed changes little.** The 1x legs and the 5x runs have the same distributions: streaming at five times the service speed made neither approach worse.

### Streaming events against the rest

For each run this table splits the moving frames in two. The first group is every frame that carries a streaming event, plus the two frames after it. For A an event is a tile's scene arriving and being placed, its colliders being enabled, a tile being destroyed, or `Resources.UnloadUnusedAssets`. For B it is Cesium creating tile objects. The second group is all other frames. This is the table that answers "does streaming hitch".

| Run | Configuration | Frames near an event | Their p99 ms | Their worst ms | Other frames' worst ms |
|---|---|---|---|---|---|
| a4-x1-up / down / bay | A ring 4 km | 75 / 82 / 64 | 20.3 / 12.4 / 10.5 | 26.1 / 13.1 / 13.9 | 112.1 / 97.7 / 84.5 |
| b-x1-up / down / bay | B Cesium | 190 / 165 / 117 | 8.4 / 8.1 / 7.7 | 9.2 / 8.5 / 7.9 | 93.0 / 108.4 / 109.2 |
| a4-x5 / a4-x5-quiet | A ring 4 km | 213 / 209 | 12.1 / 9.4 | 17.6 / 10.7 | 68.8 / 79.7 |
| a4-x5-main | A ring 4 km, main-thread colliders | 154 | 9.9 | 17.2 | 95.9 |
| b-x5 / b-x5-quiet | B Cesium | 407 / 385 | 8.7 / 8.3 | 9.7 / 8.8 | 93.1 / 94.5 |
| editor-a4-x5 | A ring 4 km, editor | 211 | 38.3 | 55.5 | 123.2 |
| editor-b-x5 | B Cesium, editor | 382 | 5.5 | 7.1 | 61.9 |

- **B:** in every player run, the frames with Cesium tile creation stayed under 10 ms. Cesium's main-thread share of a tile is small, and it loads on its own threads.
- **A, loading:** loading was cheap in a player. Moving a tile under the georeference and pinning it cost under 2.1 ms. Enabling its job-baked colliders cost under 2.4 ms. An asynchronous scene load took 116 to 206 ms of wall time at the median, spread over many frames.
- **A, unloading:** the longest A frames near events were its unloads. The worst, 26 ms, was a 9.5 ms `Destroy` of a tile followed by `Resources.UnloadUnusedAssets` (7 to 37 ms of wall time, because it scans every loaded object). A production A would unload each tile's own bundle (`AssetBundle.Unload(true)`) instead of running the global scan.
- **Collider cooking on the main thread (`a4-x5-main`) cost at most 1.9 ms per tile here,** because each L0 chunk collider is small (about 12,000 vertices). It would grow with denser chunk meshes; baking in a job removes it entirely.

| Approach A run | Loads | Load wall p50 / max ms | Place max ms | Collider bake wall p50 / max ms | Collider enable max ms | Destroy max ms | UnloadUnusedAssets p50 / max ms |
|---|---|---|---|---|---|---|---|
| a4-x5-quiet | 27 | 116 / 331 | 1.46 | 26 / 145 | 1.20 | 2.18 | 7.1 / 15.9 |
| aall-x5-quiet (all at start) | 36 | 188 / 285 | 1.36 | 35 / 74 | 0.49 | – | – |
| a4-x5-main | 27 | 126 / 457 | 2.04 | – (main thread) | 1.88 | 2.08 | 11.2 / 20.0 |
| a4-x1-up | 16 | 151 / 280 | 1.31 | 32 / 150 | 2.32 | 9.53 | 11.7 / 35.3 |

### The once-a-minute stall

Of the 175 frames over 33 ms in all 16 runs, 156 (89%) fell on one stall a minute, at the same second of each minute within a run. It appears in both approaches. It appears in the all-resident A run, which loads nothing while it moves, and in the control flight, which has no tiles at all. Mapped to the wall clock (to within the second or two it takes to launch the player), it is the same second of the minute in every run.

Of the other 19:
- 12 are the editor A run's own tile loads (see "Editor against player").
- 6 are a single long frame 15.3 to 15.5 s after launch, in six runs of both approaches.
- 1 is a frame with no event near it (`a4-x5-main`, 146 s).

During the stalled frames the bench's own CPU and GPU work was normal, so the time went outside it: the macOS compositor or another process. The cause on this machine was not identified. It is not the streamer, and a hitch budget for wave 2's test should be set on a machine without it.

### Memory

| Run | Configuration | Process memory while moving, mean MB | Highest after start-up MB | At the end MB | Native allocated, peak MB | Mesh memory, peak MB | GPU estimate (Gfx Used), peak MB |
|---|---|---|---|---|---|---|---|
| none-x5-quiet | no tiles (control) | 426 | 447 | 430 | 60 | 1 | 173 |
| a4-x5-quiet | A ring 4 km | 746 | 780 | 733 | 256 | 201 | 293 |
| aall-x5-quiet | A all resident | 1,371 | 1,388 | 1,388 | 636 | 590 | 401 |
| b-x5-quiet | B Cesium | 696 | 804 | 804 | 226 | 161 | 173 |
| b-x1-up / down / bay | B Cesium, one leg each | 621 / 568 / 524 | 686 / 627 / 574 | 685 / 626 / 574 | 170 / 163 / 116 | 104 / 95 / 48 | 142 / 138 / 197 |
| a4-x1-up / down / bay | A ring 4 km, one leg each | 727 / 697 / 728 | 778 / 728 / 772 | 764 / 711 / 721 | 255 / 239 / 250 | 201 / 185 / 194 | 272 / 192 / 194 |

- **Process memory** is Unity's "System Used Memory", the process footprint. It is the one counter that includes cesium-native's own allocations. The `ps` resident size in `rss.csv` is lower on this machine, because macOS compresses and swaps pages under its memory pressure. Every run starts with a few-hundred-MB transient while the scene loads, the control included, so the table leaves out the first 10 s.
- **A all resident** holds every tile's L0 and L1 meshes plus a CPU copy of every L0 mesh for its colliders: 590 MB of mesh memory. About 280 MB of that is the CPU copy (36 tiles x 7.8 MB of L0 vertex and index data). Without colliders it would be roughly 1.1 GB of process memory (an estimate, not measured). Over the control, its tiles cost about 945 MB.
- **B grows as it goes.** It keeps what the view needs plus a tile cache (`maximumCachedBytes`, 512 MB by default): 571 to 805 MB over the 5x path in the shared run, 530 to 804 MB in the quiet one, and still rising at the end. The tiles behind the camera stay in the cache as inactive objects (the "loading" column in `frames.csv` counts them: 675 inactive against 12 active by St. George). A long session would keep growing toward the cap, so the design sets an explicit, smaller cap. Over the control, B's tiles cost about 270 MB on average and 375 MB at the end of the path.
- **A's 4 km ring** stays flat between about 700 and 780 MB, at the price of the far field.
- **Managed memory** was 13 to 16 MB in every player run. The recorder streams its rows to disk on a worker thread, so it does not count here.
- **The GPU estimates** (`GetAllocatedMemoryForGraphicsDriver` and the "Gfx Used Memory" counter agree) are 140 to 400 MB, and vary by 100 MB between runs of the same configuration. On Apple silicon this memory is shared with the CPU and is part of the process footprint.

### Cold start and the first full view

| | A ring 4 km | A all resident | B Cesium | Control |
|---|---|---|---|---|
| Launch to first frame, quiet runs | 3.61 s | 3.49 s | 3.52 s | 3.51 s |
| First frame to the first full view, quiet runs | 0.87 s | 3.76 s | 1.03 s | 0 s |
| First frame to the first full view, the three 1x legs (each starts cold) | 0.96 / 0.87 / 0.92 s | – | 1.01 / 0.75 / 0.56 s | – |

- From process launch to the first frame takes about 3.5 to 4.4 s in every configuration: Unity's start-up plus the bench scene. The approaches differ only after that.
- "Full view" means every tile within the ring placed with its colliders (A), or Cesium's load progress at 100% for 10 frames (B).
- These are warm-cache numbers, because the files had been read recently. A first launch after a reboot will be slower for both.
- While moving, B's view was incomplete for 1% of the time at 5x and 0.2 to 0.3% at 1x, never for more than 0.21 s at a stretch in the quiet run. Its longest gap, in the shared 5x run, was the first 2.1 s after leaving Pier 11, when the view first swings up the river. The 180-degree turn at Hell Gate refilled in 0.22 s.

### Disk, import, generation and packaging

| | A: Unity tiles | B: 3D Tiles |
|---|---|---|
| Made by | the editor (`SpikeTileImporter`), from the same glTF files: 118.5 s for 36 tiles on a clean import (meshes 92 s, prefabs 3 s, scenes 20 s), 29.4 s re-importing in place, plus about 25 s of editor start-up | Python (`generate.py`): 27.7 s for 36 tiles, no Unity |
| In the project | 631 MB of text-serialised assets, 17.5 MB per tile (the project forces text serialisation, so mesh data is stored as hex) | nothing; the files live in gitignored output folders |
| In the player | 309 MB of player data, 8.5 MB per tile in `sharedassets*.assets`; every data change needs a player rebuild (first build 117 s, incremental 29 s) | 313 MB of files in StreamingAssets, 8.7 MB per tile (7.8 MB L0, 0.9 MB L1), copied and never processed by Unity |
| Reproducible | re-importing in place gave identical bytes for all 1,152 mesh assets and all 1,261 `.meta` files, but different bytes for all 36 prefabs and all 36 scenes (new object IDs each time); a clean import recreates the `.meta` files too, so it would also change every GUID (not run twice) | byte-identical on a rerun (1,189 of 1,189 files) |
| Native code in the player | `UnityPlayer.dylib` 128 MB | the same, plus `libCesiumForUnityNative.dylib` 67 MB (already in today's players) |

The sizes are uncompressed. Quantising positions and normals (`KHR_mesh_quantization`, which numpy alone can write and cesium-native supports; not tested here) should about halve B's files. Unity's mesh compression would do the same for A.

### Raycasts and colliders

The same horizontal sweep, one ray per degree from the eye out to 8 km, at the nine named points of the quiet runs:

| Point | B hits | A ring 4 km hits | A all resident hits | Nearest hit, B / A all (m) | 360 rays, B / A ring / A all (ms) |
|---|---|---|---|---|---|
| pier11 | 153 | 347 | 359 | 258 / 258 | 0.8 / 1.2 / 1.7 |
| brooklyn-bridge | 259 | 346 | 357 | 202 / 202 | 1.0 / 0.8 / 2.6 |
| williamsburg-bridge | 245 | 359 | 359 | 233 / 233 | 1.1 / 2.6 / 2.4 |
| east-34th | 259 | 360 | 360 | 324 / 324 | 1.2 / 2.8 / 2.3 |
| roosevelt-island | 309 | 360 | 360 | 163 / 162 | 0.9 / 1.8 / 1.7 |
| hell-gate | 149 | 360 | 360 | 178 / 178 | 0.7 / 3.3 / 1.7 |
| governors-island | 175 | 340 | 354 | 333 / 333 | 0.9 / 2.9 / 4.0 |
| upper-bay | 93 | 325 | 327 | 2,553 / 1,293 | 0.6 / 6.3 / 6.2 |
| st-george | 103 | 288 | 300 | 613 / 613 | 0.6 / 3.5 / 3.0 |

- **The nearest hits agree to the metre between A and B** wherever B has the geometry, which is a second check that both place the tiles identically.
- **A answers in nearly every direction.** B answers only where its view-driven selection has active tiles: 26 to 86% of the directions on this path. The share changes with the view.
- **Behind the camera, B's tiles are cached but inactive**, and inactive colliders do not answer raycasts. In the Upper Bay, B's nearest hit is 2.6 km away because the nearer shore is behind the camera.
- **Cost:** a sweep costs 0.8 to 6 ms against A's colliders, and under 1.2 ms against B's because there is less to hit. Under the shared machine's load the same A sweeps took up to 20 ms, so a radar must not raycast PhysX every frame either.

### Floating origin

`CesiumOriginShift` at 100 m, as the slice uses it, shifted the origin 307 times in each 5x run and 112 times on each East River leg at 1x. In A's 4 km ring, frames with a shift are as fast as the rest. With hundreds of tile objects (B) or every tile resident (A all), a shift adds about 0.2 to 0.8 ms to that frame's median: moving the tileset or the tile anchors, with their colliders, under the new origin. The p95 is unchanged. Without shifting, Unity coordinates would reach 20 km at the harbor's edge, where float32 steps are about 2 mm. That is tolerable for rendering, but the slice's bridge and cameras assume small coordinates, so the harbor should keep the shift.

### Editor against player

| | A ring 4 km, player (`a4-x5`) | A ring 4 km, editor (`editor-a4-x5`) | B, player (`b-x5`) | B, editor (`editor-b-x5`) |
|---|---|---|---|---|
| Median / p95 / p99 ms | 1.30 / 7.42 / 8.40 | 1.04 / 1.80 / 2.31 | 2.47 / 6.81 / 8.40 | 2.33 / 3.28 / 4.00 |
| Worst frame near a streaming event | 17.6 ms | 55.5 ms | 9.7 ms | 7.1 ms |
| Frames over 33 ms (at the minute stall / other) | 6 (6 / 0) | 18 (6 / 12) | 4 (3 / 1) | 7 (7 / 0) |
| First frame to the first full view | 0.98 s | 1.46 s | 0.94 s | 0.96 s |
| Process memory, highest after start-up | 784 MB | 1,960 MB (the editor) | 805 MB | 1,949 MB (the editor) |

- **The batchmode editor renders off-screen.** It draws into a 1920 x 1080 target and has no window to present to, which is why its p95 and p99 are lower than the player's.
- **A behaves worse in the editor than in a player.** `EditorSceneManager.LoadSceneAsyncInPlayMode` does part of every load on the main thread. The two longest editor frames (123 and 120 ms) are the frames that started a tile load, and 12 of its 18 frames over 33 ms are not the machine's minute stall. In a player, starting a load costs nothing measurable. So with A, play-mode iteration in the editor would hitch where the shipped player does not.
- **B behaves the same in the editor as in the player.** Cesium reads the same files through the same code, and its frames with tile creation stayed under 7.1 ms.
- **GPU time is missing in the editor.** The `FrameTimingManager` GPU time is not available in the batchmode editor (it reads 0). The main-thread medians agree with the player's: 0.78 against 0.88 ms for A, 1.91 against 1.95 ms for B.

### Screenshots

The bench saves a 1080p screenshot at nine named points of every run (`runs/<run>/shots/<run>-<point>.png`). In `analysis/shots-b-x5-quiet-<point>.png` the horizon band of the same point is stacked for B, A's ring, A all resident and the no-tile control. `analysis/shots-b-x5-<point>.png` does the same for the shared-machine runs.

- `analysis/shots-b-x5-quiet-east-34th.png` and `...-roosevelt-island.png` look up the East River. B and A all resident show the far field toward Astoria and the Bronx; A's ring stops at 4 km.
- `analysis/shots-b-x5-quiet-upper-bay.png` and `...-st-george.png` cross the Upper Bay toward Staten Island.
- `analysis/shots-b-x5-quiet-pier11.png` is the start, looking up the river past the bridges' sites (the bridges themselves are not part of this workload).

Synthetic land would be tinted lavender, and none appears. The grey boxes are the stand-in buildings, and the cyan fringes at the shore are intertidal seabed showing through very shallow water. Each run also has a timeline (`analysis/timeline-<run>.png`) of frame time along the path, with its tile events under it.

## How it was measured

### Machine and conditions

- Mac mini (Macmini9,1), Apple M1, 8 cores, 16 GB, macOS 27.0.1, Metal. Unity 6000.3.27f1, URP 17.3.0, Cesium for Unity 1.26.0.
- Development player (`BuildOptions.Development`, Apple silicon only), built by `SpikePlayerBuilder` with an explicit scene list (the bench scene plus 36 tile scenes) into `player/`. The build settings were not touched. The player is one build for both approaches: B ignores the tile scenes.
- Windowed 1920 x 1080 on the 1080p display, vSync off, no frame-rate cap. Quality level Ultra, with `lodBias` set to 1 at run time so the LOD distances are the ones designed below.
- Editor comparison: the same bench in editor play mode, batchmode with graphics, rendering into a 1920 x 1080 target (the batchmode editor's own screen is 640 x 480).
- **The machine was shared.**
  - Other wave 1 workers ran beside the first 12 runs, and the coordinator's checks ran throughout. When the spike started, the load average was 10 to 60 and 25 GB of swap was in use.
  - The user's own apps (Chrome, qBittorrent, SonoBus, a Docker VM, the animated wallpaper, Spotlight) ran throughout.
  - Every run held the heavy lock and the Unity lock for its own length only, so no other Unity process or heavy data step overlapped it. A and B runs were interleaved so both saw the same conditions.
  - Each run's `machine.txt` records the lock times, load average, swap and top processes before and after; from the second 1x leg on, also `memory_pressure` and `vm_stat`.
- **A quiet window for the headline runs.** After harbor-tiles and core-environment had finished, the headline 5x runs were repeated, interleaved (marked "quiet"), between 06:30 and 07:03 UTC. The coordinator's remaining checks took the heavy lock, so none of them overlapped a run. Load averages were 4 to 20 around those runs, with 35 to 44% of memory free.
- **A no-tile control.** The same flight with water, sky and the bench but no tiles (`none-x5-quiet`: a negative ring loads nothing) gives the machine's own baseline for frame time and memory.
- **The A-versus-B comparison is the robust part.** Absolute numbers are pessimistic for a dedicated machine.

### Workload

| | Per 4 km tile (mean of 36) | Whole grid (36 tiles) |
|---|---|---|
| Chunks | 16 of 1 km | 576 |
| L0 (11.9 m terrain, 85 x 85 vertices per chunk, plus every building) | 219,000 vertices, 277,000 triangles | 10.0 million triangles |
| L1 (47.6 m terrain, 22 x 22 per chunk, plus buildings 30 m and taller) | 25,000 vertices, 23,000 triangles | 0.82 million triangles |
| Stand-in buildings (extruded boxes, 20 vertices and 10 triangles each) | 5,171 (2,444 to 6,000) | 186,140 |
| glTF on disk (L0 + L1) | 8.7 MB (7.8 + 0.9) | 313 MB |

- **Grid:** harbor tiles v1, 4 km squares in the world frame, i (north) and j (east) from -2 to 3. All 36 candidate tiles were built, not only the roughly 25 active ones, so the totals are an upper bound.
- **Terrain is real everywhere.** Inside the slice it is the pipeline's merged NAVD88 raster (`data/processed/lower-east-river/depth_merged_navd88.tif`, the same raster `World/Generated/LowerEastRiver` was imported from, there at every fourth cell). Elsewhere it is the four cached CUDEM 1/9 arc-second tiles, read at their 4x overview (about 10 x 14 m). The harbor-tiles worker had already cached the two northern CUDEM tiles, so the synthetic fill the brief allowed was not needed; the generator keeps it for holes (none occurred) and tints it lavender. Both sources are resampled to the same chunk grids, so every tile has the same vertex density, and the terrain varies naturally from tile to tile.
- **Buildings are synthetic stand-ins:** boxes on land above NAVD88 +1.5 m, 2,000 + 4,000 x land fraction per tile, seeded per tile, heights log-normal around 14 m and up to 380 m near five downtowns. They exist to make the skyline's cost show up, not to look like New York.
- **One LOD policy for both approaches:** a chunk switches from L1 to L0 when the camera is within about 2 km of it. In A that is each chunk's `LODGroup` transition (screen-relative height 0.346 for a 1 km chunk, 60 degree view). In B it is the L1 node's geometric error, 34.2 m, which is 16 pixels of screen-space error at 2 km on a 1080-row, 60 degree view (Cesium's default maximum screen-space error).
- **Curvature is built in.** Every vertex is placed in its tile's own east-north-up frame through ECEF from its geodetic position and ellipsoidal height (NAVD88 + GEOID18 offset). A tile in A is pinned to that frame by a `CesiumGlobeAnchor`; in B the same frame is the tile's 3D Tiles `transform`. The two pipelines put the same geometry in the same pixels (compare any pair of screenshots).
- **Rendering is identical in both:** one hand-written URP shader (vertex colour, per-vertex Lambert from the sun plus the ambient probe, fog, no shadow casting), passed to Cesium as the tileset's opaque material. The world's water surface (`WaterSurface`, `Water.mat`), sun and sky are in both, from the existing World code.
- **Reproducible:** `tools/spikes/world-streaming/generate.py` regenerated all 1,189 files byte-identically on a second run.

### The two approaches as built

| | A: Unity tiles | B: local 3D Tiles in Cesium |
|---|---|---|
| Content | per tile: 32 mesh assets (16 chunks x L0, L1), a prefab with a two-level `LODGroup` per chunk, an additive scene holding the prefab | per tile: an external `tileset.json` and 32 `.glb` files; a root `tileset.json` with region volumes for the 36 tiles |
| Made by | `SpikeTileImporter` in the editor, from the same `.glb` files | `generate.py` (Python, numpy only) |
| Placement | the loader moves the tile root under the `CesiumGeoreference` and adds a `CesiumGlobeAnchor` with the tile's ENU-to-ECEF matrix | Cesium applies the tile's `transform` |
| Residency | a distance ring: load when the camera is within R of the tile's square, unload beyond R + 1 km; nearest first, at most 2 loads in flight; `Resources.UnloadUnusedAssets` once loading is quiet. R = 4 km ("A ring 4 km", about a 3 x 3 ring) or 40 km ("A all resident") | Cesium's screen-space-error selection for the main camera with its defaults: frustum and fog culling on, ancestors and siblings preloaded, 20 loads in flight, 512 MB tile cache |
| Colliders | a `MeshCollider` on every L0 chunk (terrain plus buildings), baked with `Physics.BakeMesh` in a job when the tile arrives, then enabled ("main": enabled at once, so PhysX cooks on the main thread, as the slice importer's colliders do today) | `createPhysicsMeshes` (a MeshCollider per loaded tile content) |
| Loading thread | `SceneManager.LoadSceneAsync` (additive); `backgroundLoadingPriority` High until the first ring, then BelowNormal | cesium-native worker threads; Unity objects created on the main thread |
| Floating origin | `CesiumOriginShift` on the camera, 100 m, as in the slice scene | the same |

### The camera path

Pier 11, up the East River under the Brooklyn, Manhattan and Williamsburg bridges and up the west channel of Roosevelt Island to Hell Gate (11.3 km), back to Pier 11 (11.3 km), then past Governors Island across the Upper Bay toward St. George (8.5 km): 31.1 km in all, mid-channel at a 6 m eye height, every 10 m of it deeper than 3.8 m. Ferry speed is the T-class service speed from `vessels/t-class.json`, 25 kn (12.86 m/s): 40 minutes for the path, so the 1x runs were split into its three legs (each starting cold) to keep each lock short. The 5x runs (64.3 m/s) fly the whole path in 8 minutes. Screenshots and a 360-ray horizontal raycast sweep are taken at nine named points; those frames are flagged and left out of the frame statistics.

### What is recorded

- Every frame: frame interval (`Time.unscaledDeltaTime`), `FrameTimingManager` CPU, main-thread, render-thread and GPU times, triangles, batches and SetPass calls, the camera's position along the path, resident and loading tiles, loads started and completed, unloads, Cesium tile objects created, origin shifts and GC collections.
- Every half second:
  - managed memory (`GetMonoUsedSizeLong`, heap);
  - native memory (`GetTotalAllocatedMemoryLong`, reserved);
  - GPU estimates (`GetAllocatedMemoryForGraphicsDriver`, the "Gfx Used Memory" counter);
  - "Mesh Memory";
  - "System Used Memory", the process footprint, which includes cesium-native's own allocations that the other counters miss.

  The tables leave out the first 10 s, where every run, the control included, has a start-up transient of a few hundred MB.
- Every second, from outside: the process's resident memory from `ps`.
- Every streaming event (load start, load done with its main-thread placement time, collider bake, unload, `UnloadUnusedAssets`, Cesium failures) with its frame number, so each long frame can be matched to what happened in it.
- Cold start: process launch to the first frame (from the process start time) and the first frame to the first full ring (A: every tile within R placed with its colliders; B: Cesium's load progress at 100% for 10 frames).

## Checks run

**Automated.** Every Unity command ran under the heavy lock, then the Unity lock.

| Check | Command | Result |
|---|---|---|
| Generator tests | `uv run --project data-pipeline pytest tools/spikes/world-streaming/tests` | 29 passed: grid and ownership, glTF container and accessors, winding and normals, bounding volumes, neighbour seams under 1 cm in ECEF, vertex density, variety, determinism, and the path on real water every 10 m |
| Generator determinism | `generate.py` run twice, manifests compared | 1,189 of 1,189 files byte-identical |
| Spike EditMode tests (in the suite below) | `FerrySim.Spikes.Tests.EditMode` | 8 passed: ring selection, camera path, glTF to Unity axes and winding, tile frame column order, and the generated tile frames against the core's `LocalTangentPlane` (under 1 mm) |
| Existing EditMode suite, with the Spikes folder in the project | `unity.sh editmode-suite-full -batchmode -nographics -runTests -testPlatform EditMode -testResults <abs>/unity/editmode.xml` | **627 of 627 passed** (`unity/editmode.xml`) |
| Existing PlayMode suite, with the Spikes folder in the project | `unity.sh playmode-suite -batchmode -runTests -testPlatform PlayMode -testResults <abs>/unity/playmode.xml` | **146 of 146 passed** (`unity/playmode.xml`) |
| Approach A re-import | `SpikeTileImporter.ImportFromCommandLine` in place, hashes compared with the clean import | meshes and metas identical; prefabs and scenes all changed (`unity-import/reproducibility.md`) |
| Measurement runs | `run_matrix.sh x5 x1 editor headline` | 16 runs, each exited 0 with no error in its `summary.json` |

**Notes on the suite runs:**
- **A first EditMode run failed 8 of 627,** all in `FerrySim.World.Import.Tests.WorldImportTests`, with `DirectoryNotFoundException` for `data/processed/lower-east-river/unity/depth.json` (`unity/editmode-incomplete-slice-data.xml`). The spike had built the slice outputs without the documented `ferrydata export-unity --slice lower-east-river` step, and those tests treat an incomplete slice folder as a failure by design. After that step the suite passed in full, as above.
- **One of those tests re-imports the slice into committed files.** `ImportTwicePreservesEveryGeneratedByteAndGuid` re-imports into `World/Generated/LowerEastRiver/`, and rewrote one line of the committed `import-manifest.json`: the recorded hash of `landing_faces.geojson`. Today's pipeline writes a different `landing_faces.geojson` from the one the committed import was made from, although every generated asset came out identical. The spike copied that folder before the run (`backup/`) and restored the file afterwards. The working tree now holds nothing outside the spike's own folders except `unity/ProjectSettings/URPProjectSettings.asset`, which Unity writes into every fresh checkout (the main checkout has it untracked too). The mismatch itself predates the spike, and the coordinator may want the slice import refreshed.

**Manual:**
- I looked at the screenshots: A and B line up pixel for pixel at the same pose.
- I looked at the figures for label collisions and legibility.

**Not verified:**
- The Windows player.
- A cold disk cache.
- A machine without the once-a-minute stall.
- `KHR_mesh_quantization`.
- Turning Cesium's physics meshes off.
- A capped `maximumCachedBytes`.

## Open questions for the coordinator

1. **Adopt B for wave 2's `harbor-world`?** That is the recommendation. A decision record would be `docs/decisions/` (yours).
2. **Refresh the slice import.** Today's pipeline writes a `landing_faces.geojson` whose hash differs from the one recorded in the committed `World/Generated/LowerEastRiver/import-manifest.json`. The generated assets themselves came out identical, but `WorldImportTests.ImportTwicePreservesEveryGeneratedByteAndGuid` rewrites that manifest line whenever it runs with fresh slice outputs.
3. **Unity writes `unity/ProjectSettings/URPProjectSettings.asset` into every fresh checkout.** It is untracked here and in the main checkout. Commit it or ignore it (coordinator-owned path; the spike left it untouched and unstaged).
4. **Keep or drop the spike's code.** It is throwaway and self-contained (its own asmdefs, nothing references it, and its EditMode tests skip cleanly without generated data), so it can merge as a record or be dropped. `SpikeEditorRunner` is an `[InitializeOnLoad]` hook that acts only when the spike's own command line set its flag.
5. **The Windows check needs the user's PC** (wave 2).

## Reproducing it

From the repository root (`tools/spikes/world-streaming/README.md` has the details):

```sh
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river --skip ais   # 126 s, reads the cache only
uv run --project data-pipeline python data-pipeline/ferrydata export-unity --slice lower-east-river        # needed by the World import tests
uv run --project data-pipeline pytest tools/spikes/world-streaming/tests
uv run --project data-pipeline python -I tools/spikes/world-streaming/generate.py                          # 28 s, 313 MB
S=tools/spikes/world-streaming
$S/unity.sh bench-scene -batchmode -nographics -executeMethod FerrySim.Spikes.EditorTools.SpikeBenchSceneBuilder.BuildFromCommandLine
$S/unity.sh import -batchmode -nographics -executeMethod FerrySim.Spikes.EditorTools.SpikeTileImporter.ImportFromCommandLine -spikeClean 1
$S/unity.sh player -batchmode -executeMethod FerrySim.Spikes.EditorTools.SpikePlayerBuilder.BuildFromCommandLine
$S/run_matrix.sh x5 x1 editor headline     # about 3 h; each run takes the heavy and Unity locks for its own length only
uv run --project data-pipeline python -I tools/spikes/world-streaming/analyze.py
```

After the measurements, approach A's generated Unity assets were moved out of the project to `unity-generated/`. The delivered `unity/Assets/FerrySim/Spikes/` folder therefore holds only code, tests, the bench scene and its material. The import command above recreates the assets.

## Files

| Path | What |
|---|---|
| `report.md` | this report |
| `analysis/tables.md`, `analysis/runs.csv`, `analysis/configurations.csv`, `analysis/hitches.csv` | every table, per run and pooled per configuration |
| `analysis/timeline-<run>.png` | frame time along the path with the tile events under it, per run |
| `analysis/cdf-*.png`, `analysis/memory-*.png` | frame-time distributions and process memory over time, per group of runs |
| `analysis/shots-*.png` | the same point from each configuration, stacked |
| `runs/<run>/frames.csv` | one row per frame: frame, CPU, main-thread, render-thread and GPU times, phase, flags, position, tile counts and events, triangles, batches |
| `runs/<run>/memory.csv` | every half second: managed, native, GPU and process memory, mesh memory and count, tiles |
| `runs/<run>/events.csv` | every streaming event with its frame and cost |
| `runs/<run>/rss.csv` | the process's resident memory and CPU from `ps`, every second |
| `runs/<run>/summary.json` | the run's configuration, machine, cold-start times and raycast sweeps |
| `runs/<run>/machine.txt` (editor runs: `logs/<run>.machine.txt`) | lock times, load average, swap, top processes, and `memory_pressure` and `vm_stat` before and after |
| `runs/<run>/shots/` | the nine screenshots |
| `runs-dev/` | the early checks (Cesium loading a local tileset in the editor; A's scenes loading), not used in the tables |
| `data/` | the generated workload: `camera_path.json` and `tiles3d/` (the tilesets, glTF files and `manifest.json` with every hash) |
| `unity-import/` | approach A's import reports with every generated file's hash, and `reproducibility.md` |
| `unity-generated/` | approach A's generated Unity assets, moved out of the project |
| `player/` | the development player and `spike-build-report.json` (per-file sizes) |
| `unity/` | the suites' XML results (and the first, incomplete-data EditMode run) |
| `logs/` | every Unity log and lock record, the generator's output, `matrix.txt` |
| `backup/` | the copy of `World/Generated/LowerEastRiver` used to undo the import test's side effect |
