# ferrydata

Turns free public data into validated, simulator-ready outputs for the first vertical slice, the Lower East River (`lower-east-river`), plus an AIS extract of the real NYC Ferry boats for calibration.

Slice bounding box: latitude 40.695 to 40.750, longitude -74.020 to -73.960 (Wall St/Pier 11, DUMBO, the Brooklyn, Manhattan and Williamsburg bridges, up to East 34th St). Stop names and ids always come from GTFS; nothing about landings is hard-coded.

## Running it

The project is a uv project (Python 3.12) that is not installed as a package, so run the CLI as a script path. Every command works from the repo root:

```
uv run --project data-pipeline python data-pipeline/ferrydata fetch    --slice lower-east-river
uv run --project data-pipeline python data-pipeline/ferrydata build    --slice lower-east-river
uv run --project data-pipeline python data-pipeline/ferrydata validate --slice lower-east-river
uv run --project data-pipeline pytest
```

(`python -m ferrydata` also works when the working directory is `data-pipeline/`.)

- `fetch` downloads to the cache and is idempotent and checksum-verified. `--only gtfs,enc,cudem,coops,geoid,ais` and `--skip ...` select sources; `--force` re-downloads and accepts changed upstream data (the manifest is updated).
- `build` processes from the cache only (no network) into `data/processed/<slice>/` and `data/processed/ais/`. A build selecting all of `gtfs,enc,cudem,coops` also refreshes the landing faces and merged depth. Partial builds do not refresh these dependent products; `--skip ais` can refresh them without rebuilding AIS. The merged depth's operational floor (Operational depth floor, below) reads the AIS extract and the environment packs' pinned gauge files, so it needs one full build first and stops, naming the file, when either is missing; it never downloads.
- `validate` writes `data/processed/<slice>/validation_report.md` (and `landing_approaches.png`) and exits non-zero if any check fails.

Raw downloads live outside the repo in `$HOME/FerrySim-cache/data/raw/<source>/` (override the base with `FERRYSIM_CACHE`). Processed outputs are gitignored. The manifests in `data/manifests/<source>.json` are committed and hold, for each file: URL, retrieval timestamp (UTC), sha256, size, plus the source's licence or terms of use and notes.

Fetch policy: a cached file that matches its manifest entry is never downloaded again. A cached file that does not match, or a fresh download whose sha256 differs from the manifest (the upstream changed), is an error until you pass `--force`. Floating catalogues such as the CO-OPS station list are exempt.

## Harbor tiles v1

```
uv run --project data-pipeline python data-pipeline/ferrydata fetch --harbor
uv run --project data-pipeline python data-pipeline/ferrydata build --harbor
uv run --project data-pipeline python data-pipeline/ferrydata build --harbor --tiles tile_0_0,tile_1_0
uv run --project data-pipeline python data-pipeline/ferrydata validate --harbor
```

The grid has 36 candidates: north/east indices -2 through 3, 4000 m squares in the shared world tangent plane. `tile_<i>_<j>` owns north `[4000*i, 4000*(i+1))` and east `[4000*j, 4000*(j+1))`; outputs use a 250 m margin. Vector clipping follows the local square transformed through `geodesy.from_local`, rather than its larger lon/lat read envelope. Tiles are active if an ENC DEPARE intersects their square or they own a ferry stop. GTFS ferry routes (`route_type=4`) identify the 24 harbor landings; Rockaway (88) and the feed's bus shuttle stops are explicitly listed as excluded in the index.

Harbor downloads append records in `data/manifests/<source>-harbor.json` and share the existing raw source directories. Reads verify the original plus the additive manifest, rejecting conflicting pins. Original manifests and existing raw files stay unchanged: the slice's merged-depth metadata hashes those original manifests, so appending harbor entries directly to them would change the slice's output bytes. Harbor fetch never refreshes existing pinned files or catalogues and does not support `--force`.

Outputs live in `data/processed/harbor/tiles/<id>/`, with every candidate and output SHA-256 in `index.json`. `raw_sources.json` records each tile's raw dependencies; validation verifies their hashes against the original/additive manifests. `build_metrics.json` measures elapsed time and peak RSS in an isolated subprocess for each tile. Tile processing runs serially under `$HOME/FerrySim-cache/heavy.lock`, including CUDEM mosaics. A subset build preserves the previous index entries for other tiles; validation still checks every active tile and fails on unbuilt ones.

Depth rasters share one exact 1/9 arc-second lattice anchored at 0° latitude/longitude, using nearest-neighbour sampling to remove small origin/resolution rounding differences between CUDEM source headers. The conservative merge reads a further 100 m support halo so its 60 m shoreline buffer agrees at tile boundaries. It never fills missing CUDEM cells or extrapolates individual soundings; missing cells whose centres lie in charted DEPARE water fail validation. The Battery MLLW/NAVD88 offset is a disclosed constant across the harbor, not a spatial VDatum transformation. CO-OPS outputs list current prediction and water-level station metadata within each grown square; harbor v1 does not fetch additional prediction time series. Harbor v1 excluded landing faces, contact geometry and the Unity export; wave 2 adds the first two (below), and the harbor's Unity import is wave 2b.

Since wave 2, each tile build ends with `landings.build_tile(tile)`, the same landing code as the slice: every active tile gets `landing_faces.geojson` and `landing_structures.geojson` (empty collections where it owns no stop), indexed and hashed in `index.json` like every other output. Every other tile output stays byte-identical. A crew note's reference stop outside the tile (North Williamsburg's Stuyvesant Cove) comes from the cached GTFS inventory, and LNDARE points and lines (islets, narrow land) are listed beside a landing, never used as a shoreline anchor. The slice's eleven landings build the same faces and contact ids in their harbor tiles as in the slice.

Validation also writes `depth_chart_audit.json`: per-tile counts, worst discrepancy and location for merged depth more than 5 m below a DEPARE maximum or more than 2 m shallower than its minimum, compared in MLLW. Cells are counted once in each category; on overlapping polygons every finite bound is tested, which also exposes chart-range conflicts. These are `INFO` findings for wave 2, with no effect on pass/fail gates or merged elevations.

## Coordinates

Every output carries WGS84 lon/lat and the world-local `north_m`/`east_m` of `docs/conventions.md`: the WGS84 ECEF tangent plane through 40.7000, -74.0150, height 0 (`ferrydata/geodesy.py`, tested against the vectors in `core/dotnet/FerrySim.Core.Tests/Contracts/LocalTangentPlaneTests.cs` to 1e-3 m; `validate` re-parses that C# file so the check cannot drift).

- GeoJSON geometry is WGS84 `[lon, lat]`. Each feature also has a `local_coordinates` member that mirrors the geometry's nesting with `[north_m, east_m]` pairs, and `lon`, `lat`, `north_m`, `east_m` properties for a representative point.
- CSV and Parquet tracks have `lat`, `lon`, `north_m`, `east_m` columns.
- The raster `depth_navd88.tif` is geographic (EPSG:4269, treated as WGS84); `depth_navd88.json` records its corners in lon/lat and north/east.

## Outputs of `data/processed/lower-east-river/`

| File | Content | Source |
|---|---|---|
| `landings.geojson` | GTFS stops in the slice, with id, name, code and serving routes | NYC Ferry GTFS |
| `landing_faces.geojson` | Barge outline per stop, berthing line, outward heading, coordinates, provenance, confidence and crew-verification flag | NOAA ENC or explicit GTFS/ASCE estimate |
| `landing_structures.geojson` | Whole landing structures as contact polylines: the barge's every side and end, every berth of the stop, nearby pontoons, piers, bulkheads and shoreline within 150 m, each segment with kind, ids, both water-side proofs and provenance; charted objects left out of contact | NOAA ENC, GTFS, cached AIS, crew notes |
| `unity/contacts.json` | contacts.json version 2 for the Unity importer (`export-unity`) | Derived |
| `routes.geojson` | GTFS route shapes clipped to the slice | NYC Ferry GTFS |
| `schedule_summary.json` | routes, trips, ordered stops per route and direction, `in_slice` flags | NYC Ferry GTFS |
| `depth_merged_navd88.tif` + `.json` | CUDEM grid with conservative chart-supported corrections near structures, then the operational-ais floor where the fleet's T- and K-class boats were seen afloat; metadata pins datum station, source manifests and the floor's provenance (`operational_ais`) | CUDEM + NOAA ENC + CO-OPS + AIS |
| `depth_operational_floor_navd88.tif` | The operational floor per cell: the deepest required bed over the T/K reports covering it, NAVD88 m, NaN where none applies | AIS + CO-OPS gauges |
| `depth_operational_lowered_m.tif` | Metres the floor lowered each merged cell (float64, exact): 0 where unchanged, NaN where the bed is nodata | Derived |
| `depth_operational_reports.csv` | Every AIS report the floor used, with its landing, day, pack time, simulated level, draft, margin, required bed and footprint radius | AIS + CO-OPS gauges |
| `depth_operational_floor.json` + `.md` | The floor's parameters, sources and gauges, and per landing: reports by class and day, cells lowered, largest and mean lowering, and the approach box before and after | Derived |
| `depth_chart_navd88.tif` | Conservative chart elevation support on the same grid, with nodata where uncharted | NOAA ENC + CO-OPS |
| `depth_disagreement_gt1m.tif` | Cell flag: 1 where chart/CUDEM elevation difference exceeds 1 m within the allowed buffer, including retained deeper CUDEM | Derived |
| `landing_depth_diff.csv` | Per-face approach minima for CUDEM, chart and merged data; coverage, disagreement counts, station and verdicts | Derived |
| `land_areas.geojson`, `soundings.geojson` | Fixed ENC land polygons and individual soundings below MLLW | NOAA ENC |
| `depth_navd88.tif` + `.json` | seabed and land elevation, NAVD88 metres, positive up; metadata has resolution, datum and coverage gaps | NOAA CUDEM 1/9 arc-second |
| `aids.geojson` | buoys, beacons, lights with parsed characteristics (rhythm, colour, period, range, height, sector, sequence) | NOAA ENC |
| `channels.geojson` | fairways, dredged areas, navigation lines | NOAA ENC |
| `shoreline_constructions.geojson` | SLCONS (piers, wharves, bulkheads, fenders), PONTON, FLODOC, MORFAC, COALNE, HULKES | NOAA ENC |
| `bridges.geojson` | bridges with vertical and horizontal clearance and the clearance datum | NOAA ENC |
| `depth_areas.geojson` | charted conservative depth ranges (DRVAL1 is used for the merge) | NOAA ENC |
| `currents/<station>.csv` | `time_utc, speed_mps, direction_toward_deg, phase, north_mps, east_mps, velocity_major_mps, depth_m, bin` | NOAA CO-OPS |
| `water_levels/<station>.csv` | `time_utc, water_level_navd88_m, quality` | NOAA CO-OPS |
| `coops_stations.json` | every CO-OPS station used, with lon/lat and north/east | NOAA CO-OPS |
| `geoid_offset.json` | ellipsoidal height of NAVD88 zero (GEOID18) at the slice centre and corners | NOAA NGS via PROJ |
| `validation_report.md`, `landing_approaches.png` | output of `validate` | |

And `data/processed/ais/candidate_roster.csv` and `nycferry_tracks.parquet` (see below).

### Semantics worth knowing

- **Current direction is where the water flows toward** (degrees true), as NOAA publishes it, and matches the convention in `docs/conventions.md`. CO-OPS gives a signed major-axis velocity (cm/s): positive on the flood (toward `meanFloodDir`), negative on the ebb (toward `meanEbbDir`). We output the magnitude in m/s and the matching "toward" direction, never flipped by 180 degrees. The default (shallowest) bin of each station is used. Subordinate stations only publish max/slack events, so they are written as `<station>_events.csv`.
- **Time is UTC** everywhere in the outputs. The reference window is 2026-06-14 to 2026-06-16 (three full UTC days).
- **Water levels** are observed 6-minute values in NAVD88 metres at The Battery (8518750) and any nearby gauge that has the datum. Gauges without NAVD88 are skipped and listed under `skipped_water_level_stations` in `data/manifests/coops.json`.
- **Elevation / depth:** depth at a point and time is water level(t) minus seabed elevation, both NAVD88. The original CUDEM and merged seabeds are both retained.
- **Bridge clearances** are metres above the ENC vertical datum (MHW for these cells, recorded per feature as `clearance_datum`). They are not NAVD88 heights. Soundings in `depth_areas.geojson` and `channels.geojson` are metres below MLLW.
- **Geoid:** `geoid_offset.json` gives the ellipsoidal height of NAVD88 zero from the real GEOID18 grid (`us_noaa_g2018u0.tif`), fetched explicitly into the cache and used by pyproj with network access off. NAD83(2011) and WGS84 ellipsoidal heights are treated as equal.

## Validation

`validate` implements the checks of the brief; thresholds are not relaxed. A failing check is reported with its numbers, not hidden.

1. Required outputs exist.
2. Every GTFS landing has exactly one landing face within 50 m and on the water side of the fixed shoreline (distances and water-side results are reported).
3. The whole landing structures are well formed: every polyline joined and solid on the right, each landing's exposed run and back closing its barge, kinds, ids and provenance on every segment, the crew label rule, and no listed object exported as contact.
4. Depth at each landing approach at the lowest water level of the window is at least K-class max draft + 0.5 m (1.6 + 0.5 = 2.1 m; the draft is read from `vessels/k-class.json`), on merged or on charted data. Each box names its basis, the strongest evidence that passes it: `merged` (the CUDEM and chart merge alone), `charted`, then `operational-ais` (the merged bed with the operational floor), or `neither`.
5. More than 0 aids, with parsed light characteristics.
6. The Brooklyn, Manhattan and Williamsburg bridges are present with clearances.
7. Current series cover the window with no gap over 1 h (the gap includes the distance from each window edge to the nearest sample).
8. The geoid offset is within -35 to -29 m.
9. `geodesy.py` matches the C# reference vectors.

### Harbor bridge clearances

Harbor validation merges bridge pieces from every tile by ENC `LNAM`, then groups features whose geometries touch or are within 2 m in the shared local metre frame. Grouping is transitive and crosses tile seams. One harbor-level gate checks each GTFS ferry route (`route_type=4`) crossing a physical bridge: at least one span intersected by that route must have finite `vertical_clearance_m` (`VERCLR`, or `VERCCL` for an opening bridge). A channel span with clearance covers a route that also crosses an uncharted side span; a route crossing only the uncharted side span fails. Bus-shuttle shapes do not enter this gate. Failures name the bridge names, ENC ids and routes. Per-tile ENC bridge equality checks and the slice's bridge check remain unchanged.

`bridge_clearance_audit.json` records all physical bridge groups, their names, ids, categories and tiles, every span without vertical clearance and whether a ferry route crosses it, and each route's crossed spans carrying clearance. Missing-clearance spans are report-only `INFO` findings, including bridges that no ferry route crosses. The audit is hashed in the index's root `outputs`, beside `depth_chart_audit.json`; neither audit changes clearances or elevations. The validation report includes one bridge-audit `INFO` row per tile. Run harbor validation under `lockf -k "$HOME/FerrySim-cache/heavy.lock"` to coordinate with other memory-heavy work.

### Landing faces and charted depth

A GTFS stop is a passenger point, not a surveyed docking face. `landing_faces.geojson` has one feature per stop: the GeoJSON geometry is the barge polygon, and `properties.face_geometry` is the berthing line in WGS84. `face_local_coordinates` holds the same line as `[north_m, east_m]`; the outline also carries the standard `local_coordinates`. `face_heading_deg_true` is the **outward normal**, clockwise from true north, not the line's tangent. The midpoint and unit `outward_normal_ne` are explicit.

- `source: enc-feature` uses a nearby PONTON/FLODOC polygon, or another floating polygon with WATLEV=7. MORFAC and shoreline categories are retained for inspection; mooring buoys, dolphins, ramps and ordinary piers are not silently reclassified as floating barges. Contiguous near-collinear ENC segments are joined into a long berthing side, retaining their source vertices. Its normal points away from the nearest fixed ENC LNDARE shoreline to the barge itself. Multiple nearby polygon candidates are marked ambiguous with low confidence; At the reviewed multi-berth terminal Wall St/Pier 11 (stop 87), candidate priority uses cached AIS slow-report support (SOG < 0.5 kn, within 150 m of GTFS then within 10 m of the polygon), requiring a valid water side. At stop 87, a topology-supported candidate with more AIS support wins over a less-used directly charted candidate. AIS includes other operators and antenna offsets; every assignment remains provisional. Per-candidate counts, route distances and side checks are exported in `candidate_evidence`.
- `source: derived-from-gtfs` means no matching floating polygon exists. A 35 ft × 90 ft (10.668 × 27.432 m) rectangle is estimated parallel to the nearest fixed ENC LNDARE shoreline, with an 85 ft (25.908 m) horizontal gangway to its landward edge, retaining a farther-offshore GTFS offset when present. These sizes come from [ASCE, June 2021](https://www.asce.org/publications-and-news/civil-engineering-source/civil-engineering-magazine/article/2021/06/floating-ferry-landings-help-solve-nyc-transit-travails), which gives a 70–100 ft gangway range. The horizontal placement and orientation are estimates, not surveyed coordinates.
- Every derived face has low confidence and `needs-crew-verification`. ENC outlines have medium confidence unless matching is ambiguous; their assigned berthing side also needs crew verification. No face is presented as operationally confirmed.

Water-side validation independently checks the line 0.5 m outside the face and a probe 12 m along its outward normal against ENC depth areas and land polygons, clears the full face swept from 0.5 m to 12 m against LNDARE and surface/unknown construction, and requires positive separation from the fixed shoreline along the normal. The small offset avoids testing the pontoon's own excluded footprint and coordinate rounding at shared edges. It does not relax the 50 m GTFS distance or 2.1 m approach-depth requirements. Where a charted floating outline has a 12 m point missing DEPARE and a face at least 8 m long, a separately labelled fallback requires an 8 m wide, 30 m outward corridor with no intersection with LNDARE or any other surface/unknown shoreline construction (including moorings). WATLEV=3 means always under water: those features are excluded from horizontal blocking and listed individually as depth hazards, with id, CATSLC, source date and corridor overlap. All other WATLEV values and missing values remain blocking. The corridor must have its entire far edge in DEPARE. The primary 12 m check remains first. Faces shorter than 8 m cannot use this fixed-width fallback; direct evidence can still support a short side. Fallback faces stay low confidence unless supported by both chart evidence and a face-orientation crew note, and unresolved details retain `needs-crew-verification`; the report records the method and distance to the first fully covered corridor transect. This topology proof does not establish depth. Other missing coverage remains unverified.

`properties.contact_geometry` is a separate connected, open WGS84 LineString for contact, with `contact_local_coordinates` in `[north, east]`. Source outline vertices are retained and the polyline runs clockwise: solid on the right, water on the left. Adjacent segments share exactly the same vertex, supporting the core's 1 mm joint handoff; open ends release contact. Landward and structure-occluded sides are omitted. Estimated barge ends use the published ASCE dimensions and carry `contact_geometry_estimated: true` and ordinal `contact_confidence: low`; the main derived face remains low confidence. A new validation check rejects disconnected geometry, segments shorter than 1 mm, unsupported edges and reversed orientation. The primary face stays separate for the existing World greybox builder. `contact_geometry` stays the exposed run: the rest of the barge, the other berths and the structures around the landing are in `landing_structures.geojson`.

For every selectable berth, the 30 m × 30 m approach box starts at its berthing line midpoint and extends along its outward normal. Its shallowest valid **merged** elevation must provide K-class max draft + 0.5 m depth at the window's lowest water level, with at least 50% valid cells. CUDEM-only results use the identical face and box and remain in the validation report. `landing_approaches.png` shows the geometry for review. Validation also writes one image per stop plus `diagnosis.json` and `diagnosis.md` under `artifacts/landing-faces/`, with exact worst-cell, source-bound and route/AIS evidence. Every remaining depth failure has a per-landing explanation; uncertain placement versus bathymetry is kept unresolved rather than called a survey result.

For the merged raster, a 60 m buffer is measured in the world tangent plane around shoreline constructions and landing lines. Outside it, and within ENC land, CUDEM is unchanged. Inside it, use the shallowest overlapping DEPARE/DRGARE DRVAL1 bound per cell. A SOUNDG measurement replaces that broad area bound only in its containing cell; the shallowest sounding wins if multiple soundings share a cell. Soundings are never interpolated across the buffer. Charted water replaces CUDEM only when it is deeper; nodata stays nodata. Corrections never deepen past chart support, including float32 rounding. CUDEM already deeper than the chart is retained and flagged rather than made shallower. No positive chart depth is invented for a 0–2 m DEPARE band.

CO-OPS station datums are checksum-pinned during `fetch --only coops` and recorded in `coops_stations.json` and merged metadata. MLLW zero in NAVD88 is `MLLW(STND) − NAVD88(STND)`; seabed elevation is that offset minus charted depth. For this slice, The Battery **8518750**, epoch **1983–2001**, gives **−0.846 m**. This is a constant local datum offset, not a VDatum surface transformation or a water-level observation. Water levels remain observed NAVD88 values.

Known limits: CUDEM can smear structure elevations into water; ENC conservative depth bands can still be too shallow to establish 2.1 m at the window's lowest tide. The operational-ais floor (below) deepens the merged bed only where the fleet's boats were seen afloat, and only for the levels they were seen at. North Williamsburg and Wall St chart geometry can omit depth areas around floating structures, and multiple-berth GTFS points cannot uniquely identify a barge. Those uncertainties and failing checks stay visible; crew/survey evidence is needed to resolve them.

## AIS extract (`data/processed/ais/`)

`candidate_roster.csv` and `nycferry_tracks.parquet` come from MarineCadastre's daily AIS files (`csv2` product: Nationwide AIS filtered to one report per minute, public domain CC0, acknowledging the USCG Navigation Center). Four Monday-to-Sunday weeks, one per season: 2024-10-07, 2025-01-13, 2025-04-14 and 2025-07-14 (28 daily files, about 7 GB, in the cache).

Method:

1. Read each day, keep reports inside the harbor box (lat 40.50 to 40.90, lon -74.10 to -73.75) from passenger vessels (AIS ship type 60 to 69) between 20 and 36 m (a loose first cut).
2. Per MMSI, take the most common reported length and beam; keep vessels whose length is 25 to 31 m. All their harbor-box reports are kept.
3. Landings are every GTFS stop inside the harbor box (not only the slice). A visit is a run of reports within 75 m of a stop at SOG <= 2 kn; runs more than 10 minutes apart count as separate visits.
4. A vessel joins the candidate roster with at least 20 visits in total across at least 2 different landings.

Roster columns: `mmsi, name, length_m, beam_m, vessel_type, visits_total, landings_visited, first_seen, last_seen, days_seen, report_count, visits_by_landing` (JSON keyed by stop name) and one `visits_<stop_id>` column per landing. Tracks: `mmsi, time_utc, lat, lon, north_m, east_m, sog_kn, sog_mps, cog_deg, heading_deg`; AIS "not available" sentinels (SOG 102.3, COG 360, heading 511) become NaN.

Result of the build recorded in this branch: 73 passenger vessels of 25 to 31 m were seen in the harbor box; 45 meet the roster rule and 611,075 reports are kept. There is a clean split: 32 vessels visit 14 to 24 different landings (the names are consistent with NYC Ferry's fleet), while 13 visit only 2 to 6 landings and match other operators that share Wall St/Pier 11, East 34th St and similar piers (for example NY Waterway boats). The pipeline does not apply a landings cutoff beyond 2; treat `landings_visited` as the first discriminator when cross-checking against the official roster.

Limitations (this roster is AIS-derived and meant to be cross-checked against the official fleet roster):

- AIS has no operator field. Other 25 to 31 m passenger vessels that stop at the same landings (other ferry operators, water taxis, charter and sightseeing boats that dock at shared piers) can pass the filter. Shared landings such as Wall St/Pier 11 make this more likely.
- NYC Ferry boats that were not in the harbor box or were not broadcasting in the four sampled weeks are missed, as are boats that report a wrong length or ship type. Boats on the Rockaway side are outside the longitude range.
- Length and beam are what the crew entered into the transponder, not surveyed particulars. A modal value is used, so a persistent entry error stays.
- Reports are one per minute, so very short stops (under a minute or two) can be missed, and a dwell is a coarse proxy for a landing call. The 75 m radius, 2 kn and 10 minute thresholds are judgement calls, not calibrated.
- Reports with a speed over 100 kn or an invalid position are not removed beyond the sentinel handling above; the source product is already cleaned by NOAA.

## Data sources and terms

| Source | Terms |
|---|---|
| NYC Ferry GTFS | NYC Ferry Developer Terms: a revocable, non-exclusive, non-transferable licence to use the feed to test, design and support products and services; NYC Ferry retains all rights. This is not an open licence: ask NYC Ferry before any public release. |
| NOAA CUDEM 1/9 arc-second (NCEI, DOI 10.25921/ds9v-ky35) | Public domain (US Government work). |
| NOAA ENC (Office of Coast Survey) | May be downloaded, used and redistributed without restriction (NOAA ENC User Agreement). Not for navigation. |
| NOAA CO-OPS | Public domain; cite NOAA CO-OPS. |
| GEOID18 (NOAA NGS, via the PROJ grid CDN) | Public domain. |
| MarineCadastre / USCG AIS | CC0 1.0. |

The full wording and notes are in `data/manifests/*.json`.

## Layout and tests

```
ferrydata/        geodesy, slices, manifest, fetcher, gtfs, enc, lights, cudem, depth, approach, landings, navdepth, operational_depth, coops, geoid, ais, validate, cli
tests/            pytest suite (geodesy reference vectors, GTFS fixture, light parsing, current semantics, bbox clipping,
                  depth-box sampler on a synthetic raster, manifest checksum verification, and more)
pyproject.toml    uv project, Python 3.12, only the approved packages
```

`uv run --project data-pipeline pytest` runs offline except for one test that needs the real GEOID18 grid in the cache; it is skipped, with a reason, when the grid is absent.

Code style: tabs, SI units inside the pipeline (knots only appear in the AIS `sog_kn` column and the roster thresholds, next to `sog_mps`).

### Crew landing evidence

`data/manifests/crew-landing-notes.json` carries dated statements with source `crew-reported: project crew reviewer` and no personal names. The pipeline reads the notes and embeds their SHA256 and exact statement in each affected landing. North Williamsburg uses the NW long side toward the opposite Manhattan ENC shoreline (identified from Manhattan stop 114), with all four side bearings reported. Its independent chart corridor check retains the submerged-pier disclosure. The 2026-10-25 Pier 11 crew note supersedes the 2026-10-24 single-face policy, which remains in the manifest as history. Crew reports ramps and berth use at the outer (east), north and south pontoons; the outer pontoon is the default. This establishes neither ramp side, depth nor clearance. Each face remains the pipeline `select_face` result, with no crew water-side flag. Every charted berth of a multi-berth stop is now its own contact structure (Whole landing structures, below); the current crew note supplies the default primary identity as data. Geometry `confidence` remains the ordinal `high` / `medium` / `low` scale. Optional `crew_evidence` carries the tracked `note_id` and `kind` (`face-orientation` at stops 19 and 8, `berth-use-policy` at stop 87). Stop 19 has medium confidence from independent chart fallback plus crew orientation; the stop 87 policy leaves its ambiguous chart/AIS geometry low confidence; `needs-crew-verification` remains for unresolved structure/depth or ramp mapping. Chart verification and crew orientation are separate booleans and neither edits depths.

Each `contact_segment_evidence` entry matches one zero-based segment index of `contact_geometry`. Only the primary face’s own subdivisions share its chart proof and directional crew flag. Every other side is independently checked with the direct test or eligible corridor fallback; unsupported sides have `water_side_verified_by_pipeline: false` and no crew flag. Validation reports the retained indices and connected open runs after the runtime chart-OR-crew filter and fails if any primary subdivision is lost.

`outline_id` identifies the physical source outline (`enc:<ENC id>` or `derived-barge:<stop id>:<fixed-shore id>`). The Unity importer derives segment IDs from this identity, stop id and canonical clockwise edge ordinal, independently of primary/end role or inclusion flags. This is the coordinator-approved pre-merge ID migration; no hand-maintained ID manifest is used.

South Williamsburg (Broadway, stop 8) uses the crew-reported NW long river side, selected from the existing ENC outline using `face_outward_compass: NW`. The compass statement changes the primary role while retaining the chart-selected contact outline and physical-edge IDs. Its NW primary has crew support and medium ordinal geometry confidence, but independent chart verification stays false: dry MORFAC `022646E95F81764B` intersects the unchanged 0.5–12 m sweep. The SW end retains its independent chart evidence and no crew flag. The validation report and stop image disclose all readable MORFAC attributes, position, distance and signed along-face offset. Crew orientation outranks the chart check for berth orientation; the structure role, clearance and actual berth depth remain unverified. The approach is re-sampled at the unchanged 2.10 m requirement.

### Crew verification label

`verification` on exported structure segments and the landing barge names the applicable evidence: `crew-reported landing side` for the own barges at South and North Williamsburg, `crew-reported berth` for Pier 11's three named ramped pontoons, and `needs-crew-verification` elsewhere. These labels verify neither the outline, depths nor clearances. The lower-level `contact_segment_evidence` retains its face-specific `crew-reported` label only where a face-orientation note supplies `water_side_crew_reported`; berth-use evidence never sets that flag. Validation checks structure labels against the current notes, including which particular pontoon the note lists.

## Whole landing structures

A ferry lands on, scrapes and stops against the whole structure, not one face. `landing_structures.geojson` holds every contact polyline of every landing, solid on the right and water on the left, one feature per polyline (WGS84 `LineString`, closed rings repeat their first vertex), with `local_coordinates`. Per stop, in this order:

| `polyline_id` | `role` | What |
|---|---|---|
| `landing-<stop>` | `landing` | The existing exposed run, `contact_geometry`: same vertices, indices, ids and primary flags |
| `landing-<stop>-back` | `landing` | The rest of the landing barge, from the run's end back to its start: together they close the outline |
| `berth-<stop>-<LNAM>` | `berth` | Every other charted floating outline the stop's candidate evaluation considered (PONTON, FLODOC or WATLEV 7 within 50 m of the GTFS stop), whole, with its own berthing face from the same face selection |
| `floating-<stop>-<LNAM>` | `floating-structure` | Any other floating outline (PONTON, FLODOC, HULKES, WATLEV 7) within the radius |
| `fixed-<stop>-<n>` | `fixed-structure` | Runs of the fixed charted boundary within the radius: LNDARE merged with dry SLCONS areas, so a pier drawn on land has no internal wall |

**Radius.** 150 m from the GTFS stop, the radius of the AIS slow-report search: it covers the final approach and the departure (about six T-class lengths), every berth of a multi-berth stop and the piers beside it. An edge is included when any part of it lies within the radius; whole floating outlines are included. Edges on the output clip boundary (the slice box or the tile's grown square) are cuts, never walls.

**Kinds.** `berth-face` (the subsegments of a berth's selected face, the primary face included), `barge-side` and `barge-end` (other edges of a floating outline, along or across the long axis of its minimum rotated rectangle, so a slightly bent long side stays a side), and for fixed edges the charted construction lying on the edge: `pier` (CATSLC 4, 5, 14, 16), `bulkhead` (6, 7, 10, 15, 18) or `shoreline` (any other category, uncategorised SLCONS, COALNE or bare LNDARE). Each fixed edge records the LNAMs that classified it.

**Berths.** `berth_id` is the outline identity (`enc:<LNAM>` or the derived barge's `derived-barge:<stop>:<shore>`); `berth_rank` orders a stop's berths by the same cached AIS slow reports within 10 m of each outline (then GTFS distance, then LNAM). At Wall St/Pier 11 the three charted pontoons within 50 m are berths ranked 02261FA117EFB198, 0226111D16AAB198, 0226015B0512B198; the outer/east 02261FA117EFB198 is now primary; the fourth, 02260848030BB198, lies beyond 50 m and is a floating structure. AIS includes other operators and is not a berth assignment.

**Two water-side proofs on every segment.**
- `water_side_verified_by_pipeline` is the existing per-side chart test, unchanged: DEPARE 0.5 m outside and 12 m out, the 0.5-12 m sweep clear of LNDARE and surface structures, a normal away from the anchor (the fixed shore for an outline, the edge itself for a fixed boundary), or the 8 x 30 m corridor for a charted floating outline. It was designed to choose a berthing side, so it fails wherever another structure is within 12 m.
- `solid_side_evidence` (coordinator-approved for collision geometry): the edge belongs to a closed charted outline whose interior is on its right (checked just inside and outside the edge, `winding_check`), and the point 0.5 m out on its left is charted water with no LNDARE and no other structure's polygon (`probe_in_charted_water`, `probe_in_lndare`, `probe_in_other_structure`). Charted water here is a depth area (DEPARE or DRGARE with a non-negative lower bound) or ENC UNSARE, unsurveyed water of unknown depth, read from the cached cells; `probe_water_layer` and `probe_water_lnam` record which. It records `layer`, `lnam`, `method` (`enc-closed-outline`, or `derived-outline` for the ASCE estimate), `probe_ne_m` and `probe_lonlat`. It proves which side is solid, never depth, approach room or a berth: approach depth and the merged depth ignore UNSARE.

Runtime contact (Unity's `LandingGeometry`, `landings.runtime_contact`) retains chart-verified or crew-water-side-supported segments, and non-primary segments with verified solid-side geometry. A selectable berth can also be primary on the distinct basis `solid-side + crew berth use (water side unsurveyed: UNSARE)`: its current, non-superseded crew-use note lists the berth, its solid-side geometry is verified, and its 0.5 m outward midpoint probe is UNSARE with no land or other structure. Slow T/K reports projecting onto the whole face span within 15 m must number at least one outward and at least as many outward as inward. Validation independently reloads the current note and manifest digest, recomputes the probe against the cached ENC and recounts AIS; a failing guard is a FAIL, never a reason to rotate the face. The existing 12 m and corridor results remain unverified where they failed. Neither AIS nor crew berth use becomes water-side verification. UNSARE never establishes crew verification. Only a current note applying to the particular structure changes its label: `crew-reported landing side` for the North and South Williamsburg landing barges, `crew-reported berth` for Pier 11’s three ramped pontoons, and `needs-crew-verification` for other structures. These labels verify neither the outline nor depth, and the validation report counts them per landing by kind so a captain can confirm them.

**Left out of contact.** Point objects (MORFAC dolphins and piles, LNDARE islets) have no charted extent, open lines in water (SLCONS or PONTON lines off the solid boundary, such as gangways) have no charted solid side, and always-submerged (WATLEV 3) structures are depth hazards. None is invented into contact geometry: each is listed in the collection's `left_out` with stop, LNAM, layer, CATSLC, WATLEV, nearest point (WGS84 and world metres), distance and reason. South Williamsburg's dolphin 022646E95F81764B is one of them.

**Ids.** Every segment carries `id` (FNV-1a over UTF-16 code units, masked to a positive Int32, as Unity's importer hashes) and the `id_key` it hashes. Floating outlines use the importer's physical-edge identity, `<stop>:<outline>:edge:<ordinal>` on the outline clockwise from its smallest WGS84 vertex, so the existing exposed runs keep their contacts.json ids. Fixed edges use `<layer>:<LNAM>:<lon,lat>:<lon,lat>` with sorted end points, independent of ring start and clipping. A new key that collides is rehashed with `+` suffixes in file order; an existing run's id never moves (the build fails instead). A physical edge within the radius of two stops fails the build: with stops at least 684 m apart it does not happen yet, and a larger radius would need an ownership rule.

## contacts.json version 2

**The harbor's file.** `export-unity --harbor` writes `data/processed/harbor/unity/contacts.json` in the same schema: all 24 harbor landings, assembled from the 29 tiles' landing files, each segment once even where tiles overlap (`export_unity.run_harbor`). The core loads it whole at a scenario's start. The slice's file is unchanged by it.

`export-unity` writes `unity/contacts.json` from `landing_structures.geojson`; World/Import recomputes `start_ne_m`, `end_ne_m` and `water_normal_ne` through the core's `LocalTangentPlane` from the WGS84 end points and writes `World/Generated/LowerEastRiver/contacts.json`. It is still a bare JSON array with one record per segment, every version 1 field first and unchanged in meaning, so a reader that knows only version 1 still loads it: the Slice loader's version 1 gate keeps the chart- or crew-verified segments and ignores the new fields. Records are ordered by `stop_id`, then by polyline as above, then by `index`; the existing exposed runs keep their ids, end points, indices and flags.

| Field | Version | Meaning |
|---|---|---|
| `id` | 1 | Stable segment id, 1 to 2^31-1, unique in the file |
| `stop_id`, `stop_name` | 1 | The GTFS landing the structure belongs to |
| `start_ne_m`, `end_ne_m` | 1 | World [north, east] metres; solid on the right walking start to end |
| `water_normal_ne` | 1 | Unit left-hand normal, into the water |
| `source` | 1 | `enc-feature` or `derived-from-gtfs` |
| `confidence` | 1 | Ordinal geometry confidence: `high`, `medium`, `low` |
| `verification` | 1 | Evidence-scoped structure label: `crew-reported landing side`, `crew-reported berth`, or `needs-crew-verification`; legacy primary-face records may say `crew-reported` |
| `polyline_id`, `index` | 1 | The polyline and the segment's place in it; consecutive segments share end points exactly |
| `water_side_verified_by_pipeline` | 1 | The existing per-side chart test |
| `water_side_evidence` | 1 | That test's method and details |
| `primary_face` | 1, optional | Every subsegment of the stop's selected landing face, and nothing else |
| `water_side_crew_reported`, `crew_evidence` | 1, optional | Crew support on the primary face; berth-use policy on the selected pontoon |
| `geometry_estimated` | 1, optional | Derived barge sides with estimated placement and size |
| `kind` | 2 | `berth-face`, `barge-side`, `barge-end`, `pier`, `bulkhead` or `shoreline` |
| `role` | 2 | `landing`, `berth`, `floating-structure` or `fixed-structure` |
| `berth_id`, `berth_rank` | 2, optional | The berth a barge segment belongs to and its AIS rank at the stop |
| `berth_face`, `berth_name`, `berth_order` | 2, optional | Selectable face, display name and consecutive selector order (default 0); distinct from AIS rank |
| `berth_face_segment_ids` | 2, optional | Complete start-to-end ordered face segment IDs, repeated on each subdivision |
| `berth_use_crew_note`, `berth_use_evidence` | 2, optional | Current crew-use note ID, embedded exact note and manifest SHA256; never a crew water-side claim |
| `berth_side_evidence` | 2, optional | Slow T/K report counts outward/inward within the whole face span and 15 m, plus the wrong-side verdict |
| `berth_corridor_structures` | 2, optional | Unchanged corridor blockers, LNAM/class/position, distance off face and along-face offset; role and clearance unverified |
| `structure_id` | 2, optional | The floating outline's identity |
| `layer`, `lnam` | 2 | The ENC object carrying the edge (`derived-from-gtfs` and null for the estimated barge) |
| `solid_side_evidence` | 2 | The solid-side proof above |
| `start_lonlat`, `end_lonlat` | 2 | WGS84 [lon, lat] end points, as written in `landing_structures.geojson` |

### Approach depth on charted data

Each approach box is also sampled on `depth_chart_navd88.tif`, the merge's own chart support: the shallowest DEPARE/DRGARE DRVAL1 per cell and a SOUNDG measurement in its own cell, converted from MLLW to NAVD88 through The Battery's datum offset, with the same box, cell rule, 2.10 m requirement and 50% coverage. A box passes when the merged or the charted column passes: charted depth can settle a failure only CUDEM shows, and never undoes a merged pass. Every box that fails on either column gets a verdict explained from the chart: the governing area or sounding, its band, its NAVD88 elevation and depth at the lowest water, the rest of the box's bands, and soundings in the box or within 30 m. The slice's three CUDEM-only failures (Brooklyn Navy Yard, South Williamsburg, Wall St/Pier 11) also fail on charted data: each box contains a 0-2 m MLLW band, whose lower bound gives -0.076 m at the window's lowest water, and Pier 11's and South Williamsburg's 2-5 m bands give only 1.924 m. The threshold is unchanged. The operational floor (below) deepens two of the three boxes but settles none.

### Operational depth floor (operational-ais)

Beside these berths the chart cannot settle the depth: its depth areas there are 0-2 m bands, the water next to the piers is unsurveyed (UNSARE), and CUDEM smears the structures into the water. The fleet's own boats berth there every day, and a boat afloat bounds the bed under it. `operational_depth.py` turns that into a floor on the merged bed (method `operational-ais`):

- **Which reports.** Every report in `data/processed/ais/nycferry_tracks.parquet` of a T- or K-class identity (the calibration identity audit, `data-pipeline/calibration/identity-audit.csv`: class `T`, or `K-...`; the traffic packs' ferry candidates), inside the depth grid, within 200 m (`RADIUS_M`) of any selectable berth-face midpoint at a slice landing (a report belongs to its nearest berth face), SOG at most 2.5 m/s (berthed and manoeuvring; faster reports squat), on the 28 scenario days as New York local days. A report counts once, under the first test it fails, in `depth_operational_floor.json`'s `dropped_reports`.
- **Level L.** The simulation's water level at the report's position and time, as the environment packs and the core's `DataEnvironment` compute it: the gauges a day's pack carries (the slice's NAVD88 gauges in its order, read from the same pinned `coops-environment` files by the same code), each sampled at the pack time by `SeriesTime` (linear across gaps up to 1800 s, the nearer sample across longer gaps, held at the ends and the coverage edges), then blended by inverse distance squared. It is not one gauge: The Battery and Kings Point differ by up to about 0.7 m.
- **Required bed.** L - D - M, with D the class's `particulars.maxDraft` (`vessels/t-class.json` 1.9 m, `vessels/k-class.json` 1.6 m; 1.9 m for an unknown class) and M = 0.3 m, a keel margin that is an estimate.
- **Footprint.** Every water cell whose centre lies within half the class's length overall of the report (T 13.00 m, K 14.80 m), because AIS antenna offsets are unknown. Distances are in the world tangent plane.
- **Floor and new bed.** A cell's floor is the deepest required bed over every report whose footprint covers it. The new bed is min(merged bed, floor) on water cells only: no bed is raised, ENC LNDARE and dry SLCONS areas (the fixed solid the landing structures and validate use) are never touched, and nodata is never filled. Cells under a floating barge are water. The float32 bed is rounded toward deeper water, so every lowered cell keeps the full margin; `depth_operational_lowered_m.tif` is float64, so the bed before the floor is exactly the merged bed plus the lowering.
- **Charted hazards keep their bed** (coordinator, 2026-10-25). Every cell touched by an always-submerged structure (WATLEV 3 in `shoreline_constructions.geojson`, mostly old pier remains) or an ENC obstruction or wreck area (OBSTRN and WRECKS polygons, read from the pinned cells) is left out of the floor. A footprint is a disc around an antenna whose place on the hull is unknown, so a boat beside a remnant would otherwise deepen the remnant itself, and a simulator that deepens a real hazard teaches crossing it. `depth_operational_floor.json`'s `hazards_left_out` lists every hazard with the cells it touches, how many a footprint reached and the lowering they would have had.
- **Determinism.** Pinned inputs (the AIS and `coops-environment` manifests, the identity audit and the vessel files, all hashed in `operational_ais.sources`), sorted iteration, and the same bytes from two builds.

`depth_merged_navd88.tif`'s tags and `depth_merged_navd88.json`'s `operational_ais` record the method, the parameters, the reports by class, the cells lowered, the largest and mean lowering, and the SHA-256 of the floor, lowering and report files. The environment packs' depth set lists the floor and lowering rasters among its sources, with the same parameters and counts in their notes. `floor_grid` and `apply_floor` work on any north-up lon/lat grid, a harbor tile too; only the slice applies the floor.

`validate`'s approach-depth table adds the merged minimum before the floor (from the merged bed and the exact lowering, checked never to raise a bed or fill nodata) and the basis column. A box the floor cannot settle says why: its minimum before and after, how many of its cells a report's footprint reaches, and the lowest level any covering report saw. The 2.10 m requirement is unchanged.

Before selectable berths, the recorded slice result (57,150 reports, T 53,372 and K 3,778; 5,161 cells lowered, largest 3.80 m; 113 charted hazards left out, whose 730 reached cells would have been lowered by up to 4.13 m): the floor deepened two of the three failing boxes but settled none, at the window's lowest water (-0.922 m NAVD88). The core's `DataEnvironment.SampleWaterLevel` on the built packs reproduces every report's level bit for bit.

| Landing | Box minimum before | After | Why it stays short of 2.10 m |
|---|---|---|---|
| South Williamsburg (8) | -0.08 m | 1.92 m | 100 of 103 cells pass; the three at the box's far edge, 27 to 30 m out, are covered only by reports at levels of -0.73 m or higher (-0.59 m for the worst of them) |
| Brooklyn Navy Yard (120) | 0.47 m | 0.66 m | Every cell is covered, but no T/K report covering the box saw a level below -0.75 m, so a T-class report's floor gives at most 2.2 - (0.922 - 0.75) = 2.03 m there; the worst cell, a far corner, is covered only by two reports at high water |
| Wall St/Pier 11 (87) | -0.08 m | -0.08 m | T/K boats rarely used the then-modelled southern pontoon (104 slow reports within 10 m of it on 18 days, against 1,151 on all 28 days at the pontoon AIS ranks first); 4 of the box's 100 cells lie beyond every footprint, and the reports covering the rest saw levels no lower than -0.76 m |

With all selectable berth faces as anchors, 57,215 reports are included (T 53,425 and K 3,790; +65); 5,161 cells are lowered, with the same 3.80 m maximum. The final merged raster has zero changed cell values; the separate floor changes in 660 cells. At the same lowest water and unchanged 2.10 m requirement, Pier 11 east passes at 2.30 m (2.09 m before the floor, operational-ais basis), north fails at 0.88 m (0.38 m before), and south remains -0.08 m. The north box retains the always-submerged pier `0226323C93821C82`; hazards are not deepened. Other stops' box results are unchanged. Every berth has its own appendix diagnosis and figure, using the exported face and source outline; the current crew note chooses the default, while historical AIS ranking remains evidence.

The floor is evidence that a boat floated there at that level, not a survey: it says nothing about tides lower than the fleet was seen at, nor about water it never entered. Crew or survey evidence is still what settles a berth.

### Harbor landing checks

`validate --harbor` adds, per landing a tile owns: a face within 50 m on the water side (the slice's independent recomputation, crew notes included), approach depth on merged or charted data against The Battery's lowest observed water in the window (read from the pinned raw CO-OPS file), and the whole structure present (the barge closed, every charted candidate a berth, kinds and provenance, no listed object as contact). Charted objects left out of contact are an `INFO` row per landing. Across tiles, each landing appears only in its owning tile and every contact id is unique. The wave-1 harbor checks are unchanged.

### Selectable berths

`landing_faces.geojson` carries `properties.berths`, each with a stable id, name, order, selected face and outward normal. Every single-berth stop is named `Main berth`. Pier 11 is ordered `Outer pontoon (east)` (`enc:02261FA117EFB198`), `North pontoon` (`enc:0226111D16AAB198`), `South pontoon` (`enc:0226015B0512B198`). This order comes from the current crew note and default, not identifiers in code. The fourth pontoon `02260848030BB198` remains contact structure but is not a selectable berth. The slice and harbor contacts exports use identical optional version-2 fields; `berth_id` is never invented on a fixed structure. A legacy single fixed face has catalog id `stop:<stop_id>`.

The berth-use note does not raise geometry confidence or change `verification`: all three Pier 11 faces retain `needs-crew-verification`. The World separately labels each one's berth use as crew-reported and keeps the face/depth/clearance caveat. The dry MORFAC points in the east/north corridors are disclosed as points, not invented collision polygons. Captain review must settle their role and clearance.
