# 0004: The harbor world streams as local 3D Tiles

**Status:** accepted (2026-10-25)

## Context

M3 grows the world from the Lower East River slice to the whole harbor: 29 active 4 km tiles from Bay Ridge and St. George to Ferry Point Park, with a skyline. The slice is imported into Unity assets once and kept resident. Two ways to carry the harbor were measured in M3 wave 1 (`docs/research/world-streaming-spike.md`), on the project's Mac mini (M1, 16 GB), in a development player and in the editor:

- **A:** the pipeline's tiles imported as Unity assets and loaded as additive scenes, either in a ring around the camera or all resident;
- **B:** local 3D Tiles written by the pipeline and streamed by Cesium for Unity, which already provides the scene's georeference.

Measured on a quiet machine at 5x the service speed over a 31 km path:

| | A, 4 km ring | A, all resident | B |
|---|---|---|---|
| Worst frame next to a streaming event | 10.7 ms | (no streaming) | 8.8 ms |
| Process memory while moving, mean | 746 MB | 1,371 MB | 696 MB |
| First full view after the first frame | 0.87 s | 3.76 s | 1.03 s |
| Horizon | 4 km | the fog, about 25 km | the fog, about 25 km |
| Tiles made by | a 2-minute editor import plus a player rebuild | the same | Python in 28 s, copied as files |

Streaming events don't separate the two: nearly every frame over 33 ms falls on a once-a-minute machine stall that a flight with no tiles shows too. B matches A's memory with the whole harbor in view, starts faster, and its tiles are deterministic pipeline outputs, byte-identical on a rebuild. A's prefabs and scenes differ on every import.

## Decision

The harbor's terrain, and later its skyline, stream as **local 3D Tiles** that `ferrydata` writes and Cesium for Unity loads from the player's StreamingAssets.

- **Tiles are pipeline outputs, not Unity assets.**
  - They're written deterministically from harbor tiles v1, with every file's SHA-256 in the index.
  - They stay out of git, rebuilt from the pipeline (plan item B5), and the player build copies them into StreamingAssets with their hashes.
- **Curvature is in the tiles.**
  - Vertices are in each tile's east-north-up frame through ECEF, and the tile's 3D Tiles `transform` pins that frame to the globe.
  - No run-time vertex correction for harbor content.
- **The simulation never reads render tiles.**
  - The core's contact segments and depth grid load whole at scenario start.
  - Radar and camera raycasts use a view-independent harbor representation from the pipeline.
  - Physics meshes stay off on the render tilesets.
- **Cesium for Unity stays pinned at 1.26.0.**
  - A streaming end-to-end test gates any upgrade.
  - Its tile cache is capped explicitly.
- **No new packages and no tokens.** The glTF writer uses numpy, which the pipeline already locks. Photoreal tiles remain the opt-in of decision 0003.

## Consequences

- **Wave 2's `harbor-world`:**
  - builds the pipeline's 3D Tiles writer and its validation first, starting from the spike's Python tooling in `tools/spikes/world-streaming/`;
  - then the harbor scene, the packaging step and a streaming end-to-end test from Wall St to St. George, Astoria and Soundview with no load screen.
- **Budgets to test against:**
  - process memory under 1.2 GB with the stand-in skyline;
  - no frame with a streaming event over 20 ms at 1x;
  - the first full view within 3 s of a scenario start.

  They're rechecked once the real skyline tileset exists.
- **Windows:** `file://` loading in the Windows player is untested. It's checked on a real Windows PC early in wave 2, before more is built on it.
- **Floating origin:** stays as in the slice (`CesiumOriginShift`, 100 m), and static batching stays off for tiles.
- **Colliders:** any streamed collider is baked in a job, never cooked on the main thread.
