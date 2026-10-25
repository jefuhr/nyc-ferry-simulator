# Harbor traffic packs

Build from the 28 cached MarineCadastre daily files, without downloads:

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic build --all
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic build --days 2025-07-14
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic verify
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic report
```

The existing pipeline is not installed (`package = false`); `PYTHONPATH` makes the module available from the repository root. From `data-pipeline/`, use `uv run python -m ferrydata.traffic` instead.

Each day's worker runs under `lockf -k "$HOME/FerrySim-cache/heavy.lock"`, reads selected columns in bounded Arrow batches, rejects national rows outside the harbor before pandas conversion, and validates the input file against the existing committed download manifest. It reads adjacent UTC files sequentially, never entire national files into memory. The optional internal `_sources/` cache holds checksummed spatial extracts keyed by the complete raw-source SHA-256. Raw sources are still validated before every use; corrupted extracts are regenerated. These intermediate files are excluded from the pack size and inventory. Metrics are measured in a fresh subprocess per scenario day and live outside the deterministic pack.

## Time and coverage

The epoch is America/New_York local midnight expressed in UTC. A pack selects reports in the inclusive interval `[-3600, 90000]` seconds, combining the named UTC day and the following cached UTC day. `time_coverage` records the requested interval, covered source interval and missing intervals with reasons. The last Sunday of each cached week lacks its following Monday; that tail is explicitly unavailable, with no new download or synthetic reports. Coverage describes source availability, not observation cadence or continuous vessel presence.

The manifest identifies both source files by filename, URL, byte size and SHA-256, plus the read-only identity audit's SHA-256. MarineCadastre's `csv2` reports are already downsampled to about one per minute. Source counts and spatial drops cover both whole national input files, then `outside_time_window` counts harbor rows outside the padded window; the remaining reasons count selected harbor reports. These mutually exclusive counts sum exactly to source reports minus kept reports.

## Cleaning and classes

Spatial limits are inclusive. Nonfinite or out-of-world latitude/longitude is impossible; valid coordinates outside `AIS_HARBOR_BBOX` are outside the bbox. Invalid MMSIs and timestamps are counted separately. Sort stably by MMSI and time, keep the first source row for duplicate `(MMSI, time)` keys, then screen against the previous kept report. Reject distance greater than `(max(valid endpoint SOG, 0) + 10 kn) * elapsed_seconds + 30 m`. Missing SOG contributes zero only to this screen and remains NaN in the output. Split kept reports at gaps strictly greater than 300 s, numbering segments from zero per vessel.

Heading and COG are valid only from 0 inclusive to 360 exclusive degrees; invalid values, including heading 511 and COG 360, become NaN. SOG outside 0–102.2 kn becomes NaN. Convert valid values to SI at import. Never synthesize heading, COG or reports. `moving` is true when any kept report is farther than 50 m from the componentwise median NED position. Stationary vessels remain in the pack.

| AIS ship type | Class |
|---|---|
| 30 | fishing |
| 31, 32, 52 | tug-tow |
| 35 | military |
| 36 | sailing |
| 37 | pleasure |
| 50 | pilot |
| 51 | search-rescue |
| 55 | law-enforcement |
| 60–69 | ferry-passenger |
| 70–79 | cargo |
| 80–89 | tanker |
| All other codes, zero, missing, non-integer | other |

Names, type and dimensions are the modes among kept reports; ties resolve to the smallest numeric or lexicographic value. Missing or nonpositive dimensions remain blank. The passenger class follows the published format's name and includes all passenger codes, without claiming each passenger vessel is a ferry. `ferry_candidate` is strictly membership in the calibration identity audit's T or K subsets.

## Binary and verification

`reports.json` publishes the little-endian 44-byte layout, field types and offsets. Reports are sorted by ascending MMSI-derived vessel index and time. The digest hashes `vessels.csv`, `reports.bin`, `reports.json` in that order, each as name, NUL, file bytes, NUL, then the epoch UTC string and NUL. No build timestamp or performance metric enters the pack.

`verify` requires all 28 scenario packs by default and checks file inventory, byte sizes, SHA-256, digest, record layout, report/roster counts, drop accounting, vessel indices, time order, segments and roster time bounds. `--days` permits a focused verification. Replay/interpolation is outside this package.

The report plots segment-separated tracks on cached NOAA ENC land polygons. Its Pier 11 table uses the closest observed report within ±60 s of 08:30 local and selects positions within 2 km of the cached GTFS stop; it does not interpolate missing observations.
