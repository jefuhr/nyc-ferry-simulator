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
- `build` processes from the cache only (no network) into `data/processed/<slice>/` and `data/processed/ais/`. A build selecting all of `gtfs,enc,cudem,coops` also refreshes the landing faces and merged depth. Partial builds do not refresh these dependent products; `--skip ais` can refresh them without rebuilding AIS.
- `validate` writes `data/processed/<slice>/validation_report.md` (and `landing_approaches.png`) and exits non-zero if any check fails.

Raw downloads live outside the repo in `$HOME/FerrySim-cache/data/raw/<source>/` (override the base with `FERRYSIM_CACHE`). Processed outputs are gitignored. The manifests in `data/manifests/<source>.json` are committed and hold, for each file: URL, retrieval timestamp (UTC), sha256, size, plus the source's licence or terms of use and notes.

Fetch policy: a cached file that matches its manifest entry is never downloaded again. A cached file that does not match, or a fresh download whose sha256 differs from the manifest (the upstream changed), is an error until you pass `--force`. Floating catalogues such as the CO-OPS station list are exempt.

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
| `routes.geojson` | GTFS route shapes clipped to the slice | NYC Ferry GTFS |
| `schedule_summary.json` | routes, trips, ordered stops per route and direction, `in_slice` flags | NYC Ferry GTFS |
| `depth_merged_navd88.tif` + `.json` | CUDEM grid with conservative chart-supported corrections near structures; metadata pins datum station and source manifests | CUDEM + NOAA ENC + CO-OPS |
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
3. Depth at each landing approach at the lowest water level of the window is at least K-class max draft + 0.5 m (1.6 + 0.5 = 2.1 m; the draft is read from `vessels/k-class.json`).
4. More than 0 aids, with parsed light characteristics.
5. The Brooklyn, Manhattan and Williamsburg bridges are present with clearances.
6. Current series cover the window with no gap over 1 h (the gap includes the distance from each window edge to the nearest sample).
7. The geoid offset is within -35 to -29 m.
8. `geodesy.py` matches the C# reference vectors.

### Landing faces and charted depth

A GTFS stop is a passenger point, not a surveyed docking face. `landing_faces.geojson` has one feature per stop: the GeoJSON geometry is the barge polygon, and `properties.face_geometry` is the berthing line in WGS84. `face_local_coordinates` holds the same line as `[north_m, east_m]`; the outline also carries the standard `local_coordinates`. `face_heading_deg_true` is the **outward normal**, clockwise from true north, not the line's tangent. The midpoint and unit `outward_normal_ne` are explicit.

- `source: enc-feature` uses a nearby PONTON/FLODOC polygon, or another floating polygon with WATLEV=7. MORFAC and shoreline categories are retained for inspection; mooring buoys, dolphins, ramps and ordinary piers are not silently reclassified as floating barges. Contiguous near-collinear ENC segments are joined into a long berthing side, retaining their source vertices. Its normal points away from the nearest fixed ENC LNDARE shoreline to the barge itself. Multiple nearby polygon candidates are marked ambiguous with low confidence; At the reviewed multi-berth terminal Wall St/Pier 11 (stop 87), candidate priority uses cached AIS slow-report support (SOG < 0.5 kn, within 150 m of GTFS then within 10 m of the polygon), requiring a valid water side. At stop 87, a topology-supported candidate with more AIS support wins over a less-used directly charted candidate. AIS includes other operators and antenna offsets; every assignment remains provisional. Per-candidate counts, route distances and side checks are exported in `candidate_evidence`.
- `source: derived-from-gtfs` means no matching floating polygon exists. A 35 ft × 90 ft (10.668 × 27.432 m) rectangle is estimated parallel to the nearest fixed ENC LNDARE shoreline, with an 85 ft (25.908 m) horizontal gangway to its landward edge, retaining a farther-offshore GTFS offset when present. These sizes come from [ASCE, June 2021](https://www.asce.org/publications-and-news/civil-engineering-source/civil-engineering-magazine/article/2021/06/floating-ferry-landings-help-solve-nyc-transit-travails), which gives a 70–100 ft gangway range. The horizontal placement and orientation are estimates, not surveyed coordinates.
- Every derived face has low confidence and `needs-crew-verification`. ENC outlines have medium confidence unless matching is ambiguous; their assigned berthing side also needs crew verification. No face is presented as operationally confirmed.

Water-side validation independently checks the line 0.5 m outside the face and a probe 12 m along its outward normal against ENC depth areas and land polygons, clears the full face swept from 0.5 m to 12 m against LNDARE and surface/unknown construction, and requires positive separation from the fixed shoreline along the normal. The small offset avoids testing the pontoon's own excluded footprint and coordinate rounding at shared edges. It does not relax the 50 m GTFS distance or 2.1 m approach-depth requirements. Where a charted floating outline has a 12 m point missing DEPARE and a face at least 8 m long, a separately labelled fallback requires an 8 m wide, 30 m outward corridor with no intersection with LNDARE or any other surface/unknown shoreline construction (including moorings). WATLEV=3 means always under water: those features are excluded from horizontal blocking and listed individually as depth hazards, with id, CATSLC, source date and corridor overlap. All other WATLEV values and missing values remain blocking. The corridor must have its entire far edge in DEPARE. The primary 12 m check remains first. Faces shorter than 8 m cannot use this fixed-width fallback; direct evidence can still support a short side. Fallback faces stay low confidence unless supported by both chart evidence and a face-orientation crew note, and unresolved details retain `needs-crew-verification`; the report records the method and distance to the first fully covered corridor transect. This topology proof does not establish depth. Other missing coverage remains unverified.

`properties.contact_geometry` is a separate connected, open WGS84 LineString for contact, with `contact_local_coordinates` in `[north, east]`. Source outline vertices are retained and the polyline runs clockwise: solid on the right, water on the left. Adjacent segments share exactly the same vertex, supporting the core's 1 mm joint handoff; open ends release contact. Landward and structure-occluded sides are omitted. Estimated barge ends use the published ASCE dimensions and carry `contact_geometry_estimated: true` and ordinal `contact_confidence: low`; the main derived face remains low confidence. A new validation check rejects disconnected geometry, segments shorter than 1 mm, unsupported edges and reversed orientation. The primary face stays separate for the existing World greybox builder.

The 30 m × 30 m approach box starts at the berthing line midpoint and extends along its outward normal. Its shallowest valid **merged** elevation must provide K-class max draft + 0.5 m depth at the window's lowest water level, with at least 50% valid cells. CUDEM-only results use the identical face and box and remain in the validation report. `landing_approaches.png` shows the geometry for review. Validation also writes one image per stop plus `diagnosis.json` and `diagnosis.md` under `artifacts/landing-faces/`, with exact worst-cell, source-bound and route/AIS evidence. Every remaining depth failure has a per-landing explanation; uncertain placement versus bathymetry is kept unresolved rather than called a survey result.

For the merged raster, a 60 m buffer is measured in the world tangent plane around shoreline constructions and landing lines. Outside it, and within ENC land, CUDEM is unchanged. Inside it, use the shallowest overlapping DEPARE/DRGARE DRVAL1 bound per cell. A SOUNDG measurement replaces that broad area bound only in its containing cell; the shallowest sounding wins if multiple soundings share a cell. Soundings are never interpolated across the buffer. Charted water replaces CUDEM only when it is deeper; nodata stays nodata. Corrections never deepen past chart support, including float32 rounding. CUDEM already deeper than the chart is retained and flagged rather than made shallower. No positive chart depth is invented for a 0–2 m DEPARE band.

CO-OPS station datums are checksum-pinned during `fetch --only coops` and recorded in `coops_stations.json` and merged metadata. MLLW zero in NAVD88 is `MLLW(STND) − NAVD88(STND)`; seabed elevation is that offset minus charted depth. For this slice, The Battery **8518750**, epoch **1983–2001**, gives **−0.846 m**. This is a constant local datum offset, not a VDatum surface transformation or a water-level observation. Water levels remain observed NAVD88 values.

Known limits: CUDEM can smear structure elevations into water; ENC conservative depth bands can still be too shallow to establish 2.1 m at the window's lowest tide. North Williamsburg and Wall St chart geometry can omit depth areas around floating structures, and multiple-berth GTFS points cannot uniquely identify a barge. Those uncertainties and failing checks stay visible; crew/survey evidence is needed to resolve them.

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
ferrydata/        geodesy, slices, manifest, fetcher, gtfs, enc, lights, cudem, depth, approach, landings, navdepth, coops, geoid, ais, validate, cli
tests/            pytest suite (geodesy reference vectors, GTFS fixture, light parsing, current semantics, bbox clipping,
                  depth-box sampler on a synthetic raster, manifest checksum verification, and more)
pyproject.toml    uv project, Python 3.12, only the approved packages
```

`uv run --project data-pipeline pytest` runs offline except for one test that needs the real GEOID18 grid in the cache; it is skipped, with a reason, when the grid is absent.

Code style: tabs, SI units inside the pipeline (knots only appear in the AIS `sog_kn` column and the roster thresholds, next to `sog_mps`).

### Crew landing evidence

`data/manifests/crew-landing-notes.json` carries dated statements with source `crew-reported: project crew reviewer` and no personal names. The pipeline reads the notes and embeds their SHA256 and exact statement in each affected landing. North Williamsburg uses the NW long side toward the opposite Manhattan ENC shoreline (identified from Manhattan stop 114), with all four side bearings reported. Its independent chart corridor check retains the submerged-pier disclosure. Pier 11 retains one southern pontoon as an explicit single-face simplification; which pontoons have ramps remains unverified. Multi-berth pipeline, importer, World greybox and Slice support is follow-up work. Geometry `confidence` remains the ordinal `high` / `medium` / `low` scale. Optional `crew_evidence` carries the tracked `note_id` and `kind` (`face-orientation` at stops 19 and 8, `berth-use-policy` at stop 87). Stop 19 has medium confidence from independent chart fallback plus crew orientation; the stop 87 policy leaves its ambiguous chart/AIS geometry low confidence; `needs-crew-verification` remains for unresolved structure/depth or ramp mapping. Chart verification and crew orientation are separate booleans and neither edits depths.

Each `contact_segment_evidence` entry matches one zero-based segment index of `contact_geometry`. Only the primary face’s own subdivisions share its chart proof and directional crew flag. Every other side is independently checked with the direct test or eligible corridor fallback; unsupported sides have `water_side_verified_by_pipeline: false` and no crew flag. Validation reports the retained indices and connected open runs after the runtime chart-OR-crew filter and fails if any primary subdivision is lost.

`outline_id` identifies the physical source outline (`enc:<ENC id>` or `derived-barge:<stop id>:<fixed-shore id>`). The Unity importer derives segment IDs from this identity, stop id and canonical clockwise edge ordinal, independently of primary/end role or inclusion flags. This is the coordinator-approved pre-merge ID migration; no hand-maintained ID manifest is used.

South Williamsburg (Broadway, stop 8) uses the crew-reported NW long river side, selected from the existing ENC outline using `face_outward_compass: NW`. The compass statement changes the primary role while retaining the chart-selected contact outline and physical-edge IDs. Its NW primary has crew support and medium ordinal geometry confidence, but independent chart verification stays false: dry MORFAC `022646E95F81764B` intersects the unchanged 0.5–12 m sweep. The SW end retains its independent chart evidence and no crew flag. The validation report and stop image disclose all readable MORFAC attributes, position, distance and signed along-face offset. Crew orientation outranks the chart check for berth orientation; the structure role, clearance and actual berth depth remain unverified. The approach is re-sampled at the unchanged 2.10 m requirement.
