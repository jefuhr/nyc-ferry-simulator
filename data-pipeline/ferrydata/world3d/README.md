# Harbor 3D Tiles (`ferrydata.world3d`)

Writes the harbor's terrain and fixed chart structures as local 3D Tiles 1.1 for Cesium for Unity, as [decision 0004](../../../docs/decisions/0004-harbor-world-local-3d-tiles.md) sets out, from a harbor tiles v1 build. It has its own CLI and doesn't change `ferrydata`'s.

From the repository root:

```sh
uv run --project data-pipeline python data-pipeline/ferrydata build --harbor          # harbor tiles v1 (locks per tile itself)
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.world3d build
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.world3d validate
```

`build` reads `data/processed/harbor/` and writes `data/processed/harbor/3dtiles/` (gitignored, never committed: plan item B5). It takes about 20 s and 720 MiB on the Mac mini for the 29 active tiles. `validate` writes `artifacts/world3d/validation.md` and `validation.json` and exits 1 when any check fails. Its slice check needs the slice outputs (`ferrydata build --slice lower-east-river`, then `export-unity`). `--harbor`, `--out`, `--slice`, `--import-manifest` and `--report` point either command elsewhere, and `build --metrics <file>` records its time and peak memory.

## Layout

| Path | What |
|---|---|
| `tileset.json` | The root: one child per active tile, with a `region` around that tile's content. Its content is the tile's own tileset |
| `tiles/<id>/tileset.json` | The tile's `transform` and its tree: the whole tile at level 2 (`l2.glb`), refining to 16 chunks of 1 km at level 1 (`c<row>_<column>_l1.glb`, rows from the south, columns from the west), each refining to level 0 (`c<row>_<column>_l0.glb`) |
| `index.json` | Every other file's path, size and SHA-256; the SHA-256 of every source file read; and per tile its frame, sizes, vertex and triangle counts per level, and the structures drawn and left out |

Nothing in the output carries a time, so two builds from the same inputs are byte-identical. A build writes into `3dtiles.building/` and swaps it in, so a failed build leaves the previous output alone. It refuses to replace a folder that has no `harbor-3dtiles-v1` `index.json`.

There is no physics content. The core never reads render tiles: its contact segments and depth grid load whole from the pipeline.

## Frames and heights

- **Frames.** Each tile's content is in its own east-north-up frame. The origin is the tile's centre on the WGS84 ellipsoid (height 0), through `ferrydata.geodesy`. Every vertex goes through ECEF from its own latitude, longitude and ellipsoidal height, so the Earth's curvature is in the tiles, and nothing corrects vertices at run time. The tile's 3D Tiles `transform` (column-major: east, north, up, origin) pins that frame to the globe.
- **glTF axes.** glTF is y-up, so content is written as (x east, y up, z south). The 3D Tiles 1.1 y-up to z-up rotation turns it back into the tile's frame. Both are proper rotations, so triangles keep their winding.
- **Heights are WGS84 ellipsoidal metres.** A height is NAVD88 from the owning tile's merged raster (`depth_merged_navd88.tif`) plus that tile's geoid offset (the ellipsoidal height of NAVD88 zero). The offset is interpolated bilinearly in the world frame between the four corners of the tile's grown square in `geoid_offset.json`. Nothing hard-codes 32 m.
- **Sampling.** The raster is sampled strictly bilinearly at cell centres, the way `ferrydata.environment` samples the core's depth (a test keeps the two the same). A nodata neighbour gives nodata. Such a vertex keeps a place at NAVD88 0, but no triangle uses it. The current harbor rasters have no nodata inside any tile. The raster's CRS is read from the file, and WGS84 points reach it through pyproj; for NAD83 that is the null "NAD83 to WGS 84 (1)".

## Levels of detail

One lattice in the world frame: 336 cells per 4 km tile side (11.905 m), so every tile edge is a lattice line. Coarser levels take every 4th and every 16th point of it, so their vertices sit exactly on finer ones.

| Level | Node | Spacing | Vertices | Geometric error | Refines |
|---|---|---|---|---|---|
| 0 | 1 km chunk | 11.9 m | 85 x 85 | 0 (leaf) | – |
| 1 | 1 km chunk | 47.6 m | 22 x 22 | 34.21 m | to level 0 within about 2 km |
| 2 | 4 km tile | 190.5 m | 22 x 22 | 136.86 m | to level 1 within about 8 km |

The errors follow the world-streaming spike's policy. Level 1's is the error that is 16 pixels at 2 km on a 1,080-row, 60 degree view, Cesium's default maximum screen-space error. Level 2's is four times that, as its lattice is four times coarser. Nodes without content must always refine to reach their content: the root has 100,000 m and each tile's reference 10,000 m. Level 2 means the far field loads one file per tile, not sixteen.

## No cracks

- **One owner per point.** A lattice point's height comes from exactly one tile:
  - the tile whose half-open square holds it, so a tile owns its south and west edges and its south-west corner;
  - if that tile is inactive, the nearest active tile whose 250 m grown square holds it, the lowest (i, j) on a tie;
  - otherwise none.

  The harbor rasters agree in their overlaps to within the harbor validation's 0.05 m, but the geoid corners of two tiles don't give the same offset along their shared edge. The owner rule makes the two tiles' vertices the same anyway.
- **Computed once.** The lattice is computed once for the whole harbor, each point in one batch with its owner's other points. Each tile's frame coordinates are computed once per level, and chunks are cut from them.
- **What that gives.**
  - Inside a tile, neighbouring chunks share their edge vertices bit for bit in the float32 GLB data, normals and colours included.
  - Across tiles, each tile writes the same point in its own frame, so the float32 numbers differ, but the points coincide in ECEF. The validator measures 0.16 mm at worst: float32 rounding at 2 km from a tile's origin.
- **Skirts.** Cesium refines each chunk on its own, so a level-0 chunk can border a level-1 chunk, and a level-1 chunk can border a neighbouring tile's level 2. The finer edge's in-between vertices then stand above or below the coarser edge's straight segments. Every chunk and tile has a skirt for that: its edge vertices again, 30 m lower along the ellipsoid normal, facing out. The largest step measured in the current data is 20.7 m between levels 0 and 1, and 23.6 m between levels 1 and 2 (at tile edges). The validator checks every edge against the skirt depth.

## Vertex order in a GLB

One mesh, one triangle primitive: POSITION and NORMAL as float32, COLOR_0 as normalized uint8, uint16 indices (uint32 past 65535 vertices), one fully rough material, no extensions. The vertices come in this order, and the validator relies on it:

1. The terrain grid, rows from south to north, columns from west to east.
2. The skirt: the four edges counter-clockwise seen from above (south from west to east, east from south to north, north from east to west, west from north to south).
3. The walls, eight vertices each: the face to the left of the segment's direction (bottom start, bottom end, top start, top end), then the same four for the face to the right.

Triangles wind counter-clockwise seen from their front:
- terrain faces up;
- skirts face out of their chunk;
- a wall has a face to either side, so back-face culling hides none of them.

Normals are central differences of the lattice's ECEF positions at the level's own spacing, the same across tiles. Colours are World/Import's terrain ramp: shallow cyan to deep blue below NAVD88 0 (over 45 m), green to brown above it (over 30 m). Walls are the slice's structure grey.

## Structures

Levels 0 and 1 carry each tile's fixed chart structures from `shoreline_constructions.geojson`, extruded as World/Import extrudes the slice's: a wall from NAVD88 -60 m to +1 m along every line and ring. This is greybox, not surveyed structure heights.

**Split across seams.** Each segment is clipped to the tile's square and kept where the tile owns its midpoint (the same owner rule), then cut the same way into chunks. A structure across a seam is drawn once, in parts, and a segment lying exactly on a seam belongs to the tile that owns that edge.

**Left out:**
- floating landing barges: every structure whose LNAM any tile's `landing_structures.geojson` gives for a barge-kind segment (`berth-face`, `barge-side` or `barge-end`). That is 14 PONTONs in the current data. The greybox draws them on the tide;
- point objects (dolphins, piles), which have no extent;
- aids, bridges and buildings, which harbor-world, night-fog and skyline own.

**Other floating chart structures are still drawn fixed:** PONTON, FLODOC, HULKES, and SLCONS with WATLEV 7. `index.json` lists them per tile under `floating_drawn`, and `validation.md` lists them deduplicated.

**Piers beside barges.** Charted piers (SLCONS, WATLEV 3, always under water) run along parts of some barges' sides: 44% of 02266B95280E764B's outline and 40% of 02264AC31B5B764B's. They are fixed structures and are drawn, as World/Import draws the slice's.

## Bounding volumes

- Every chunk has a `box` in its tile's frame around its level 0 and level 1 content (skirts and walls included), padded by 1 cm. Both levels use it.
- Each tile's root `box` holds everything in the tile.
- The root's `region`s are the latitude, longitude and ellipsoidal height range of each tile's content, padded by about 1 m.

## Validation

`validate` fails loudly, naming the tile and the file, on any of these:

| Check | What fails it |
|---|---|
| `index` | A file missing, unlisted, or with a size or SHA-256 other than `index.json`'s; a source file changed since the build |
| `structure` | An active harbor tile missing from the root (or an inactive one present); a tile tree other than l2, 16 l1 and 16 l0 in row order; a missing content file; a file nothing uses |
| `glb` | A GLB the strict reader refuses: container, accessor ranges and counts, POSITION min/max, non-finite values, indices out of range or not whole triangles |
| `bounds` | Content, its own or a descendant's, outside a node's box (0.1 mm tolerance); a tile's vertices outside its region in the root; the root's region not holding its children's |
| `geometric-error` | A child whose error isn't below its parent's, or a leaf whose error isn't 0 |
| `frame` | A transform that isn't the tile's ENU frame (origin, axes, a proper rotation); a corner or centre vertex at level 0 more than 1 mm in ECEF from where `ferrydata.geodesy` puts its lattice point at the owner rule's ellipsoidal height |
| `cracks` | Neighbouring chunks inside a tile whose shared edge isn't bit-identical (positions, normals, colours); neighbouring tiles' shared edges or corners more than 1 mm apart in ECEF at any level; a step between levels deeper than the skirts |
| `floating` | A landing barge listed as drawn, or more than 90% of a barge's outline lying along fixed walls |
| `slice` | Terrain that disagrees with the slice's imported terrain (below) |
| `size` | Any tile over 25 MB (the report gives the total and every tile's size per level) |

**Agreement with the slice.** World/Import built the slice's terrain from `data/processed/lower-east-river/unity/depth.f32`, the slice's merged raster on the same 1/9 arc-second lattice the harbor rasters use.
- The check first requires that file's SHA-256 to be the one `World/Generated/LowerEastRiver/import-manifest.json` recorded, so it compares with the terrain actually imported.
- At every level-0 vertex over that grid, it compares the vertex's NAVD88 height (its ellipsoidal height less the owner's geoid offset) with the grid sampled the same way.
- **The operational-ais floor.** The slice deepens its berths where the fleet's AIS shows boats afloat (`ferrydata.operational_depth`), and the harbor tiles don't yet. So the check compares with the slice's terrain before the floor: the lowering the slice records in `depth_operational_lowered_m.tif`, on the same grid, is added back. The report gives the number of cells lowered. When the floor reaches the harbor tiles, compare after it on both sides instead.
- **Stated tolerance:** at least 99.9% of the vertices within 0.01 m, and none beyond 1.0 m.

The two merges differ in a few cells, where a chart correction or the slice's own edge differs from the harbor tile's. Today 221,104 vertices compare: 99.9986% agree to float rounding (median 0.001 mm), and the worst, three vertices on the slice's east edge, differs by 0.48 m.
