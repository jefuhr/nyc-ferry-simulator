# Harbor Skyline Data

`ferrydata.buildings` writes the independent LoD1 skyline tileset required by decision 0004. It uses the existing `world3d` GLB writer, tile frames, ECEF transforms, bounding-volume helpers and per-point geoid owner rule. There is no Unity implementation, collision geometry, credential, new dependency or night material here.

From the repository root:

```sh
lockf -k "$HOME/FerrySim-cache/heavy.lock" env PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.buildings fetch
lockf -k "$HOME/FerrySim-cache/heavy.lock" env PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.buildings build
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.buildings validate --report artifacts/skyline-data/validation.json
uv run --project data-pipeline pytest data-pipeline/tests
```

`--harbor`, `--cache` and `--out` override the harbor input, raw cache and output directories. `--report` writes build metrics or validation JSON. `validate --compare <second-output>` also checks the full byte digest against a separately built output, and reports explicitly when determinism was not checked. The default output is `data/processed/harbor/3dtiles-buildings/`, outside git. Harbor inputs must already exist (`ferrydata build --harbor`); this module never rebuilds or changes terrain. The default raw cache is `$HOME/FerrySim-cache/data/raw/buildings/`, respecting `FERRYSIM_CACHE`.

## Sources and Redistribution

### New York City

The official NYC Open Data dataset is **Building Footprints / BUILDING, `5zhs-2jue`**, provided by the City of New York Office of Technology and Innovation (OTI, formerly DoITT).

- Dataset: <https://data.cityofnewyork.us/City-Government/BUILDING/5zhs-2jue>
- API: <https://data.cityofnewyork.us/resource/5zhs-2jue.json>
- Schema/metadata: <https://data.cityofnewyork.us/api/views/5zhs-2jue.json>
- Official data dictionary: <https://github.com/CityOfNewYork/nyc-geo-metadata/blob/main/Metadata/Metadata_BuildingFootprints.md>
- NYC Open Data policy: <https://www.nyc.gov/assets/doitt/downloads/pdf/nyc_open_data_tsm.pdf>, section 2.3.2
- Portal public-policy terms: <https://a860-gpp.nyc.gov/downloads/nv935438b?locale=en>, section 6
- General NYC terms: <https://www.nyc.gov/main/terms-of-use>

The City's Open Data policy makes published datasets a public resource without licensing requirements; portal and NYC terms apply, including the disclaimer of completeness, accuracy and fitness for use. This is **not labelled CC0 or an independently inferred Creative Commons licence**. The source dictionary warns that ground-elevation datum accuracy varies; modern/photo-derived observations use NAVD88, while older observations are not uniformly certified. Treat the massing as visual context, never surveyed navigation or collision data.

Player credits should identify the source and modification:

> Building footprints: City of New York, Office of Technology and Innovation, NYC Open Data, dataset 5zhs-2jue. Simplified, clipped and extruded for this simulator; source data may be incomplete or inaccurate. https://data.cityofnewyork.us/City-Government/BUILDING/5zhs-2jue

`height_roof` is **height above ground in feet**, not a roof elevation. `ground_elevation` is elevation in feet. The import multiplies both by exactly 0.3048, then adds building height to ground elevation. Only completed-building and garage feature codes (2100 and 5110) are included; tanks, canopies, skybridges, placeholders, auxiliary structures and under-construction outlines are counted as excluded, rather than drawn as buildings. OTI's unique `doitt_id` provides identity; BIN and BBL are not unique building keys. Source names, addresses and parcel identifiers are not exported into the semantic sidecar.

### New Jersey

The NJ building source is **OpenStreetMap**. After bounded Overpass failures left incomplete coverage, the coordinator explicitly approved one uniform [Geofabrik New Jersey extract](https://download.geofabrik.de/north-america/us/new-jersey.html): <https://download.geofabrik.de/north-america/us/new-jersey-latest.osm.pbf>, checked against its adjacent `.md5` file. The original Overpass responses are now cross-check inputs only; they are never mixed into the generated NJ geometry.

The database licence remains **Open Database License 1.0 (ODbL)**: <https://www.openstreetmap.org/copyright> and <https://opendatacommons.org/licenses/odbl/1-0/>. Players must display **© OpenStreetMap contributors**, link to the copyright page and identify ODbL availability. Distribution of the derived NJ footprint/attribute database carries applicable ODbL share-alike and data-offer obligations. Retain and offer those data and their source/processing information under ODbL. Suggested processing credit: “New Jersey building data © OpenStreetMap contributors (ODbL 1.0), extract processed by Geofabrik.” Final player credits/data offering are coordinator release work.

The existing pyogrio/GDAL **3.12.4** OSM driver reads the PBF `multipolygons` layer inside the harbor read bbox. Closed ways use `osm_way_id`; relations use `osm_id`. The same PBF supplies the `ISO3166-2=US-NJ`, `admin_level=4` boundary, so the building filter and state mask come from one snapshot. A building belongs to NJ when its original representative point is covered by that boundary. `building=no` is excluded. Heights/levels/use are decoded from `other_tags`; JSON/dictionary and HSTORE representations are supported. No new package is required. GDAL temporary indexes use `artifacts/skyline-data/gdal-tmp/`, with a 128 MB in-memory database threshold.

GDAL assembles multipolygon relations and suppresses consumed member ways. A literal OSM fixture verifies relation identity, roof/level tags and a whole building crossing a narrow bbox, guarding against geometry truncation. The public Geofabrik extract omits contributor user names, user IDs and changeset IDs. Only building attributes needed by this package reach the sidecars; contact/POI/user tags do not.

Heights use valid `height` tags (metres, or explicit ft/feet), otherwise `building:levels × 3 m`, otherwise a **9 m estimated default**. Compound or ambiguous units fall back explicitly rather than being guessed. Valid heights are 1–650 m. Levels and use/building type, where known, are retained. Ground elevation is sampled from the existing harbor merged NAVD88 terrain at the original footprint's representative point using the terrain owner rule (or a deterministic point in its clipped active footprint if the original point lies beyond the terrain halo); the sampled north/east point is retained; missing/nonsane ground is excluded and counted. The 3 m storey and 9 m defaults are engineering estimates, not source measurements. Missing NYC roof heights use the same labelled fallback policy.

## Fetch Integrity and Etiquette

Every download goes through `ferrydata.fetcher.fetch_file` and `ferrydata.manifest.Manifest`, recording URL, retrieval UTC, byte count and SHA-256 in the raw cache's `buildings.json`. The PBF also records the actual HTTP `Last-Modified` header and its verified MD5. Exactly one PBF response is downloaded on a cache miss; download/checksum/driver failures stop the route without Overpass fallback. Valid cache hits make no network request. `fetch --force` explicitly accepts refreshed upstream bytes.

NYC requests are sequential with a descriptive project User-Agent, at least 1 s between newly downloaded pages, bounded retries and a global cap. The historical Overpass path used 1 km NJ land subchunks, `[out:json][timeout:180]`, at most three shared attempts across the original instance and the approved mirror, 30–120 s backoff plus jitter, and a 240-request cap. It stopped with 28/180 subchunks covered; no further Overpass requests are made by the final `fetch` CLI.

The final raw inventory uses the uniform PBF for every required NJ land subchunk. The 28 cached Overpass subchunks are compared separately for building counts and common-ID heights, with each original endpoint/osm_base/SHA-256 retained. Comparison counts use original footprints with positive area overlap of each exact 1 km core, under the same bulk NJ boundary; snapshot and query-selection differences remain visible. Legacy partial inventories are still buildable, but their `coverage.complete=false` and missing IDs remain explicit in both index and validation. A geometry pass never upgrades coverage.

`source-manifest.json` and `source-inventory.json` preserve the reviewed wave 3a pin/coverage snapshot in this package. An absent default-cache manifest is seeded from the packaged pins; an existing cache manifest remains authoritative. Mutable public APIs may no longer reproduce old response bytes, in which case the pin fails loudly and the original raw-cache backup is needed.

NYC pages contain at most 5,000 rows, ordered by `doitt_id`, with polygon-intersection filters for each tile's read envelope. Cross-envelope duplicates are deduplicated by stable source ID; disagreements fail rather than arbitrarily choosing a row. NYC and NJ remain separate dated sources; NJ now uses one coherent PBF snapshot. Repeatability comes from pinned bytes, not a claim that live public services offer an atomic cross-source snapshot.

## Geometry, Seams and Heights

- Footprints reach the shared north/east tangent plane through `ferrydata.geodesy`, never flat longitude scaling. Simplification is 0.35 m, preserving topology, before clipping. Land, tile and protected-feature intersections are then snapped to 1 mm for stable small geometry; components of area ≤0.01 m² are omitted. Water/footprint validation allows 2 mm to account for that snap and float32 storage.
- The chart `land_areas.geojson` is the land mask. Landing structures, aids and bridges receive an **estimated 5 m visual exclusion envelope**. These are render exclusions, not navigation clearances or new collision rules. Masking may remove real over-water structures and source/chart shoreline disagreements; the output reports omitted source buildings.
- Each building has one stable source ID. Its footprint is clipped into nonoverlapping tile pieces, with stable `source-id@tile:part` IDs. A seam-touching footprint contributes zero area to its neighbour, so it does not make a second piece. A crossing building is drawn once in parts, not copied whole into both tiles. Interior clipping walls can coincide on the zero-area seam, without duplicate footprint area.
- Every piece retains the original building's common ground and roof elevations across seams. Every vertex independently receives its owning terrain tile's geoid offset (`world3d.heights.Sources.geoid_at`), then goes through ECEF into `world3d.frames.TileFrame`. NAVD88 zero is never hard-coded as −32 m. Adjacent tiles share the same geoid ownership at seam points, including the terrain fallback to active grown squares.
- The mesh is a closed prism including its base, concave roof and courtyard walls. Unreferenced cap vertices are removed, including duplicate coordinates where a valid courtyard touches an outer ring. Constrained triangulation preserves holes. Roof/base normals are separate from shared wall-corner normals. glTF coordinates are east/up/south, matching world3d's y-up conversion and tile ENU transforms.

## Explicit Water-View Selection

`build --policy A|B|C` records its exact selection in the output index and reports omissions by source. **B is the coordinator-approved default**. A and C remain explicit comparison/rebuild choices, and the unchanged 100/25 MB guards apply to every option.

| Policy | Rule |
|---|---|
| A | Every eligible cached building |
| B | Every building whose original footprint is within 600 m of harbor shoreline, plus inland buildings at least 25 m high above ground |
| C | Within 400 m, plus inland buildings at least 40 m high above ground |

Distances use actual ENC **COALNE** lines in the shared world metre frame, not land polygon boundaries that include artificial tile-clipping edges. Selection and estimated-ground sampling use the repaired original footprint, before rendering simplification or tile/land/exclusion fragmentation, so a single building cannot change selection across a seam. A separate 0.35 m simplified geometry supplies the rendered prisms. Roof height means the source's height **above ground**, not ground plus height. The omitted-building policy is explicitly labelled **“Inland low-rise omitted: not visible from the water”**; it is a water-view heuristic, not a measured occlusion guarantee. Unknown heights retain their documented fallback and may therefore be omitted inland.

The coordinator also requested offline option D (B plus bounded coarse cluster massing). Its price and previews are comparison artifacts, not an implemented or selected production LOD. The conservative candidate uses 15 m centroid cells and 10 m roof bands, merging to a maximum roof only when the full group diameter is at most 20 m and ground range at most 10 m; other groups retain exact prisms. A proposed 24 m geometric error gives a reference fine switch around 1.4 km at 1080 rows / 60° / 16 pixels. Fine and coarse payloads are priced separately because coarse data increases packaged size; the price is not runtime performance acceptance.

## Output and Streaming Policy

`tileset.json` references one external tileset per populated terrain tile. Each has the terrain tile's exact transform and up to sixteen roughly 1 km chunks, assigned by each footprint piece's representative point. Chunk bounds contain the actual complete pieces, including pieces crossing a chunk edge. Empty active terrain tiles remain in the index with zero counts/bytes.

The skyline is **one exact LoD1 level**, not a fabricated coarse approximation: contentless root/tile nodes have the terrain writer's traversal errors (100,000/10,000 m); chunk leaves have geometric error zero. Additive refinement is used only on contentless parents. This streams independently by actual chunk bounds without duplicating a coarse skyline payload. A far-field simplification and its measured error can be added later if the combined player benchmark calls for it; this Python work does not certify decision 0004's process-memory, frame-time or start-time budgets.

Each populated tile contains:

- `tileset.json` and `c<row>_<column>.glb` files written with the existing world3d GLB writer;
- `attributes.json.gz`, a deterministic gzip JSON semantic sidecar, **not a file Cesium loads**. It retains ID, source, height, ground, roof, levels, use, estimate basis, footprint in world east/north metres, and vertex/index ranges into each mesh. `build.read_attributes(folder)` reads it for a later night-windows pass. Gzip carries no timestamp or filename.

`index.json` inventories every other output file's size/SHA-256 (self-hashing the index is intentionally excluded), raw response provenance, harbor input hashes, tile counts, byte sizes, triangle counts and parameters. Total on-disk size, including the index, is enforced below 100,000,000 bytes; per tile below 25,000,000 bytes; per chunk at most 250,000 triangles. A build beyond these caps fails and retains the previous complete output: ask the coordinator before changing a cap. These are packaging guards, not measured renderer budgets.

A build writes a recognized sibling `.building` staging directory and replaces the output only after successful generation. It refuses to replace an unrecognized directory. Rebuild determinism includes geometry, semantic sidecars, hierarchy and the index; compare using `ferrydata.world3d.build.digest`.

## Validation and Acceptance Boundary

The validator checks input and output hashes, exact active-tile and root-reference inventories, strict GLB decoding, leaf/parent errors, tile transforms, mesh boxes and regions, footprint validity and tile/land/exclusion coverage, stable part IDs, sane heights, semantic mesh ranges, outward winding, finite nondegenerate geometry, per-vertex footprint placement and NAVD88/geoid agreement, building/triangle counts and size budgets. Small-fixture tests cover seams, region-edge centres, water clipping, concave/touching courtyards, height fallback, GDAL bbox transport and relation identity, MD5/header capture, raw corruption, HTTP-200 partial errors, request limits, modified transforms, source-count/policy drift, safe replacement and two-build byte equality.

Full live-build counts, sizes, digests, rendered previews, exact checks and unresolved limits are recorded in `artifacts/skyline-data/report.md`. No Unity, Cesium player, night-window, combined-cache, frame-time, Windows or manual navigation acceptance is claimed by this package.

## Review Fix

Wave 3a review finding 2 exposed an ordering error: render simplification could erase a small shoreward projection before selection measured it. The build now retains the repaired original geometry through policy selection and ground sampling, then simplifies only the separate render geometry. Policy B remains 600 m from shoreline or at least 25 m above ground. A build-level regression covers a footprint at 599.9 m whose simplified rendering lies at 600.1 m, with both published and sampled ground; it must remain selected while its rendered projection is simplified away. Cached rebuild digests, exact changed source IDs and size/count deltas are in the report's Review Fix section.
