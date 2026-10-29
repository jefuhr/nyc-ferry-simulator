# Resident Harbor Chart Pack

Offline chart and radar semantics, independent of Unity and render residency. **Not for navigation.** This is not collision geometry, a depth environment, or a replacement for the runtime pack. ENC depths stay in their chart datum, **MLLW**, while known building roofs explicitly use **NAVD88**. Nothing converts between these datums.

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_chart build
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_chart verify
PYTHONPATH=data-pipeline uv run --project data-pipeline pytest data-pipeline/tests/harbor_chart -q
```

`--harbor` selects the adopted input root; `--out` selects a chart output directory. A build uses the heavy lock externally (unlike the existing internally locked harbor/environment/traffic builders):

```sh
lockf -k "$HOME/FerrySim-cache/heavy.lock" env PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_chart build
```

No fetcher is invoked. Missing or changed cache pins fail before publication. GDAL reads the pinned ZIPs through `/vsizip/`, applying their S-57 updates without extracting or writing in the raw cache. The runtime exporter verifies its source chain first. Its pinned harbor `index.json` supplies the exact cell set; the cached, hash-verified NOAA catalogue supplies editions and revision/publication dates. Each cell also records DSID edition/update/issue dates, usage band, compilation scale, `DSPM_SDAT`, `DSPM_VDAT` and names from `enc.DATUMS`. A feature `VERDAT` override and its cell's sounding datum must both match the declared MLLW datum; metre depth units are mandatory.

## Geometry and Overlap

All horizontal coordinates are **[north, east] metres** in the runtime pack's exact shared WGS84/ECEF tangent plane. The manifest copies its origin and frame text and records the exact runtime DIGEST. These geometry objects are not WGS84 GeoJSON. Rings, holes, multiparts, lines and points survive; line features never receive invented widths.

Like runtime footprints, features are merged by LNAM, canonicalized and clipped to the active runtime coverage. Chart source selection additionally partitions `M_COVR CATCOV=1` coverage: higher usage band wins, then smaller compilation-scale denominator, then lexical cell name. Features clip to that owned region before same-LNAM fragments union. There is no positive-area duplicate coverage at cell overlaps; coincident boundary lines and points can touch. Same-ID conflicting attributes fail. The current 20 cells are all band 5 / 1:12,000, so lexical ownership only resolves their equal-detail overlaps. This extends runtime's same-ID tile-fragment merge; runtime itself does not implement usage-band masking.

A multipoint SOUNDG becomes individually indexed points. Its stable `source_id` retains the original LNAM followed by its original longitude/latitude at seven decimal places, so reordering source multipoints does not alter identity. Each point retains its own Z depth, including negative drying heights. Other ENC source IDs are unmodified LNAMs.

Coordinates are rounded to **0.01 m**, at most **0.005 m per axis** (0.007072 m planar), after transformation and clipping. Millimetre source/intersection preparation precedes this explicit final quantisation. Invalid rings caused by rounding are repaired with `make_valid`, preserving their feature identity and any collapsed line/point evidence. Land for radar is topology-preserving simplified at **1 m**; building footprints at **1 m**, after union of skyline tile pieces. Skyline inputs already carry 0.35 m source simplification. Chart land, depth areas, contours, channels and hazards receive no display simplification.

## Layers and Compact Layout

The eight fixed payload names are `land.json`, `depth_areas.json`, `contours.json`, `soundings.json`, `hazards.json`, `channels.json`, `radar_obstructions.json` and `references.json`. `manifest.json` contains schema/version, counts, hashes, byte sizes, sources, datum and licence evidence, tolerances, NOAA's user agreement and the navigation notice. DIGEST is SHA-256 over `manifest.json` followed by those eight files in that exact order, each framed as UTF-8 filename, NUL, raw bytes, NUL. Every file is deterministic UTF-8 JSON with sorted object keys, compact separators and one final LF.

Each layer has `format: ferrysim-chart-layer/1`, a `fields` array, `features` as column tuples, `attributes` and `grid`. Geometry-layer fields are:

```text
[id, source_id, feature_class, attribute_index, geometry_ne_m, cells]
```

`geometry_ne_m` is `[type, coordinates]`, or `["GeometryCollection", geometries]` where children are ordinary NE geometry objects. `cells` lists contributing ENC cell names and is null for buildings. `attribute_index` references a per-layer table of shared attribute objects. Bounds are derived from geometry, not duplicated in each tuple. IDs sort ordinally and remain stable for identical source bytes.

References use `[id, runtime_layer, runtime_id, bounds_ne_m]`. They carry aids, physical bridges and shoreline-footprint IDs, with **no copied geometry**. Resolve draw geometry against the exact linked runtime pack; bounds support indexed culling only. Bridge bounds union its source spans. The references intentionally exclude structure/barge footprint duplicates: those remain available directly from runtime.

The grid uses **250 m** squares and keys `northCell,eastCell`, with `floor(coordinate/250)` for negative coordinates too. Each closed feature bounding box is assigned to every touched cell, including its maximum edge. Lists contain sorted feature indices. This gives near-field chart/radar queries small candidate sets without multiplying the ~73,000 building records; a larger 500 m grid would make dense waterfront candidate lists roughly four times as broad. Readers deduplicate candidates and test exact geometry; references use bounding-box tests. The complete expected index is reconstructed and checked at load.

## Attributes and Building Heights

Retained ENC attributes, when present:

`DRVAL1`, `DRVAL2`, `VALDCO`, `VALSOU`, `WATLEV`, `CATOBS`, `CATWRK`, `CATROC`, `CATREA`, `CATNAV`, `CATTRK`, `CATTSS`, `CATFWD`, `CATDRG`, `ORIENT`, `TRAFIC`, `STATUS`, `OBJNAM`, `VERDAT`, `QUASOU`, `TECSOU`, `EXPSOU`, `SOUACC`, `RESTRN`, `NATSUR`, `NATQUA`, `CONRAD`, `CONVIS`, `CATCOA`, `CONDTN`, `NATCON`, `HEIGHT`, `ELEVAT`, `VERACC`, `VERLEN`, `COLOUR`.

Source `HEIGHT`, `ELEVAT` and `VERLEN` remain unconverted in their cell's recorded vertical datum (MHW here); they never become NAVD88 building roofs. Depth-related records additionally retain `depth_datum: MLLW`; soundings have `depth_m`. Unused S-57 transport/portrayal/source-description fields are dropped: `RCID`, `PRIM`, `GRUP`, `OBJL`, `RVER`, `AGEN`, `FIDN`, `FIDS`, `LNAM_REFS`, `FFPT_RIND`, `NOBJNM`, `INFORM`, `NINFOM`, `NTXTDS`, `TXTDSC`, `SCAMAX`, `SCAMIN`, `RECDAT`, `RECIND`, `SORDAT`, `SORIND`, `DATEND`, `DATSTA`, `PEREND`, `PERSTA`, `PICREP`, `PRODCT`, `hypcat`. `LNAM` is stored as source identity instead. Any other field outside the explicit allowlist is not exported; raw bytes remain pinned by cell SHA-256. See the report's observed dropped-column inventory for this cell set.

Buildings come only from the verified skyline semantic sidecars, never GLBs. All selected skyline buildings remain present, unioned by source ID across tile seams. Only the simplified footprint, source identity and nullable `roof_navd88_m` reach the radar layer. A roof is known only when both source height and NAVD88 ground are published: NYC `heightroof-feet` plus `nyc-groundelev-feet`. Levels/default height estimates and terrain-ground estimates become null, including NJ OSM heights whose absolute ground was estimated. Unknown heights never inherit the skyline's rendering fallback. The manifest retains skyline selection policy, sidecar hashes, the source-index hash and its licence/ODbL notices; it does not imply complete inland-building coverage beyond that selected inventory.

Publication derives and verifies all files in a temporary sibling before copying recognized output files. It rejects unowned output contents and a pack above **25,000,000 bytes**. This is offline publication, not an atomic concurrent-player directory swap. Processed outputs remain ignored by git; copy `data/processed/harbor/chart/` with its linked runtime pack for downstream work.
