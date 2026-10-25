# Scenario-day environment packs

This package writes `ferrysim-env/1` for the 28 AIS scenario days: the four Monday–Sunday weeks beginning 2024-10-07, 2025-01-13, 2025-04-14 and 2025-07-14. It has its own CLI and does not change the slice CLI or the calibration cache.

From the repository root:

```sh
# Generate the source merged raster from the existing verified raw cache, if absent.
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river --skip ais

PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --days 2025-07-14,2025-07-15
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment verify --region lower-east-river
```

`PYTHONPATH=data-pipeline` is needed because this repository's existing uv project has `package = false`. Alternatively, run from `data-pipeline/` with `uv run python -m ferrydata.environment ...`. No dependency or project configuration changes are required.

Outputs are gitignored under `data/processed/lower-east-river/env/`. The shared depth set lives in `depth/`, with its own `manifest.json`, grid files and `DIGEST`; each date has `days/<date>/manifest.json`, `DIGEST`, `currents.csv`, `water_levels.csv` and `wind.csv`. The build also writes `artifacts/environment-pack/report.md`, `build-summary.json` and `currents-tide.png`. A subset build replaces that day's files and refreshes the report for that invocation; other existing day folders remain intact. `verify` checks every existing day, without network access.

## Sources and provenance

All five harmonic current prediction files are checksum-verified against `data/manifests/coops-calibration.json`, then parsed read-only. If their time coverage is insufficient, the required day's 6-minute series is fetched to `coops-environment`. The API-default bin and its actual depth are recorded in day notes; Dimond Reef's 9.4 m bin remains a known surface-current weakness.

New NOAA CO-OPS metadata, NAVD88 observations, same-datum prediction gap fills and wind observations use the existing fetcher and `data/manifests/coops-environment.json`. Downloads are sequential and cached; raw no-data JSON is retained with its digest. Mismatched or untracked raw files fail before network access, and no force/refetch switch exists in this CLI. Water observations take precedence over predictions; prediction rows carry `source=predicted`. The raw prediction filename and SHA-256 are also recorded in day notes. Gauges lacking NAVD88 are omitted explicitly; NAVD88 is never replaced with a tidal datum.

The Battery remains the first water gauge. Wind uses exactly one station per day, in the coordinator-approved availability order: The Battery, Kings Point, Robbins Reef, Bergen Point West Reach. If no valid wind exists, the CSV contains only its header, the station list is empty, and a day note tells the core to report still air. Kings Point is on Long Island Sound and is more exposed than the East River; this spatial-proxy limitation appears in pack notes. Sensor height is unknown; gusts are retained but the v1 core uses mean wind only.

Times are UTC seconds relative to New York local midnight. Series include the 6-minute samples spanning -3600 to 90000 seconds. Missing source values are missing rows, not zero/NaN placeholders; long gaps are documented for the reader's nearest-value hold policy. From-directions for wind become toward-components; current directions already point toward the flow. Components and heights use SI units and the shared world north/east frame.

## Depth

World-frame centres are 5 m apart, south-to-north rows and west-to-east columns. Each centre passes through `geodesy.from_local` and a WGS84-to-raster CRS transform before strict bilinear sampling. The coordinator approved shrinking the original slice-covering lattice to its largest world-aligned rectangle wholly inside the merged raster's cell-centre sampling domain. There is no edge extrapolation; points outside this inset grid use the specified `outside_depth_m = 20` fallback.

Merged CUDEM values already use NAVD88; ENC MLLW chart depths use The Battery's published MLLW-minus-NAVD88 datum offset. The merged raster and its metadata are hashed in `sources`; no additional vertical transformation is applied here. The horizontal raster CRS is read from the file (currently EPSG:4269), not assumed to be WGS84.

Unknown water cells are filled only where `depth_chart_navd88.tif` has a valid bilinear value, as approved by the coordinator. The chart raster digest and fill counts are recorded in the depth manifest. Remaining unknown cells stay NaN; water-hole clusters, centroid positions and nearby charted construction classes are listed in the report. The present source raster has no interior nodata, so the chart fill count is zero.

## Integrity and repeatability

Each depth set and day directory has a `DIGEST` file containing exactly 64 lowercase SHA-256 hex characters followed by LF. Neither manifest contains a `digest` key. The day manifest's `depth_digest` equals the shared depth `DIGEST`.

Both digests hash raw bytes on disk, including the complete manifest without re-serialising JSON. For each file, append its UTF-8 filename, NUL, its bytes, NUL:

- Depth: `manifest.json`, then every grid file in the manifest's `grids` order.
- Day: `manifest.json`, `currents.csv`, `water_levels.csv`, `wind.csv`, then append the 64 ASCII characters of the depth digest and NUL (without its LF).

This covers all manifest fields, including `outside_depth_m`, `time_coverage_s`, station positions and provenance. Even a whitespace-only manifest edit changes its digest. Individual grid/CSV SHA-256 values remain in the manifests for file-specific diagnostics. CSV uses UTF-8, LF endings, Python round-trippable numbers, and station/time sorting. No build timestamps enter pack bytes.

`verify` recomputes the raw-byte digests, checks the exact `DIGEST` file syntax, verifies individual file SHA-256 values and the day-to-depth digest reference, and refuses mismatches with the affected path. It also checks raw-grid lengths, format/region/datum, epochs, station identities and finite positions, sorted unique rows, finite series values, source labels and endpoint coverage. It accepts an empty wind series only with an empty station list. Digests detect byte changes; they are not authenticity signatures.
