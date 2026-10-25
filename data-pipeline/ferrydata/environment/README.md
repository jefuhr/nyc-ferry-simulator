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

All five harmonic current prediction files are checksum-verified against `data/manifests/coops-calibration.json`, then parsed read-only. If their time coverage is insufficient, the required day's 6-minute series is fetched to `coops-environment`. The API-default bin and its actual depth are recorded in day notes. CO-OPS measures Dimond Reef's (`NYH1919`) bins up from the seabed (`depthType` S), so its default bin 9 at 9.4 m is its shallowest of three, about 5 m below the surface in 14 m of water, not 9.4 m down.

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

## Subordinate current stations

The slice's seven subordinate stations (`ACT3406`, `ACT3411`, `ACT3416`, `ACT3421`, `ACT3426`, `ACT3431`, `ACT3646`, as the slice's CO-OPS build lists them) become 6-minute velocity series on every day, by NOAA's own method. NOAA CO-OPS, *Current Predictions User Guide: Understanding NOAA Current Predictions* (v5), p. 3: "Predictions at subordinate stations are obtained by taking the predictions calculated at nearby reference stations and applying time and speed corrections. These include four corrections for time (slack before flood, max flood, slack before ebb, and max ebb) and two corrections for speed (max flood and max ebb)."

- The corrections are the station's MDAPI `currentpredictionoffsets` (`sbfTimeAdjMin`, `mfcTimeAdjMin`, `sbeTimeAdjMin`, `mecTimeAdjMin`, `mfcAmpAdj`, `mecAmpAdj`, `meanFloodDir`, `meanEbbDir`), with the reference station and bin they name (`NYH1924` bin 6 for the East River stations, `n03020` bin 7 for `ACT3646`).
- The reference's own MAX_SLACK events are shifted by their phase's time offset; maxima are scaled by their phase's ratio and slack stays zero. A slack takes the phase of the maximum that follows it.
- CO-OPS publishes only those events for a subordinate station: its 6-minute interval is for harmonic stations only, and its [FAQ](https://tidesandcurrents.noaa.gov/noaacurrents/faq.html) calls the curve between events "a mathematical interpolation between the known maximum and slack values". Here the curve keeps the reference's own shape: between two consecutive shifted events the reference's 6-minute prediction is read at the linearly corresponding time between its two events, times the flood ratio where it floods and the ebb ratio where it ebbs. Flood points toward `meanFloodDir`, ebb toward `meanEbbDir`.
- Raw inputs, all in `raw/coops-environment/` and `data/manifests/coops-environment.json`: `offsets_<station>.json`, `currents_<reference>_bin<n>_<week>.csv` (6-minute) and `events_<reference>_bin<n>_<week>.csv` (MAX_SLACK) for each scenario week with 12 hours either side, and `events_<station>_bin1_<week>.csv`, what CO-OPS publishes for the subordinate station itself, kept to check the series against.
- Check on all 28 days (`artifacts/current-field/subordinates.md`): every shifted event falls on the published minute, published speeds agree to the 0.1 cm/s the reference speeds are rounded to, and the 6-minute series crosses zero within about a minute of each published slack.

## Current field (approved 2026-10-25)

Between stations the core no longer blends by inverse distance across land. Each region gets a static field set, built once from the region's depth set, its ENC land and CUDEM, and its current stations' positions. Days refer to it, and the core reads every station's series through it.

### Method (`channel-harmonic/1`, `ferrydata/environment/field.py`)

On a 10 m world-frame lattice of water cells (`region_field.py`; the cell size is chosen by convergence at the landings, below):

1. **Water.** Cells whose centre is on ENC land (`LNDARE`) are solid; the largest 4-connected body of the rest is the water. Shoreline constructions (`SLCONS`) are not solid. In these cells every one charted as an area is always under water (`WATLEV` 3, mostly old pier remains: 248 areas) or covers and uncovers (`WATLEV` 4: 4 areas). Those above water are charted only as lines, the outlines of piers, wharves, seawalls and fenders. So a pier charted only by its outline is open water to the field.
2. **Flow pattern.** Potential flow through the water, phi fixed at the inflow edge (south: the Upper Bay) and the outflow edge (north: toward Hell Gate and up the Hudson), no flux through land. Its velocity gives each cell's flood-oriented channel axis and a pattern speed S. Cells next to land have their axis turned onto the shoreline's tangent.
3. **Factors.** Sheltering is min(1, S / S̄), with S̄ = S smoothed over 800 m through water: water at least as fast as its surroundings keeps the stations' speed, and slower water is slowed in proportion. Depth is min(1, (h / h̄)^(1/6)), with h̄ = depth smoothed over 200 m.
   - along = sheltering × depth, but at least the station reach: 1 on a station's 40 m disk, falling linearly to 0 at 120 m from it. So the field does not rescale a station's speed.
   - cross = along × shore taper × across reach. The shore taper is 0 in the cells next to land, rising to 1 at 60 m from the shoreline. The across reach is 1 on a station's disk, falling linearly to 0 at 500 m from the nearest station. So a station's direction off the channel axis stays near it, and between stations the flow runs along the axis.
4. **Weights.** Harmonic interpolation through the water only: Laplace's equation with each station a 40 m disk held at 1 for itself and 0 for the others, and no flux through land or the lattice edges. Along a channel it blends the stations on either side by distance along it, and it never reaches across land. Each cell keeps its six largest weights, renormalised to sum to 1.
5. **Stations.** Each station's frame is the cell axes around it blended by the sampling rule. A station inside the stored grid takes it from the stored float32 axes. So the field at a station is exactly its velocity, unless the station is closer to the shore than the taper: there the shore wins, and the taper cuts the part of its flow across the axis. In this region only four stations in the Roosevelt Island channels are that close (`ACT3406` is within about 15 m of the charted shore), and they are outside the stored grid.

The computation box (north −3500 to 8000 m, east −2600 to 6500 m) holds every slice current station with water around it, so leave-one-out works at all twelve. It also reaches the Upper Bay past Red Hook, so Buttermilk Channel and the main channel meet in the bay rather than at an edge. A pack stores only the cells over the depth set's footprint.

### Layout

```
data/processed/<region>/env/
  depth/                   unchanged
  field/
    manifest.json          format ferrysim-env-field/1
    <grid id>.f32          rows × cols × (4 + slots) float32, little-endian, row 0 south, row-major
    <grid id>.u8           rows × cols × slots station indices (255 = empty)
    DIGEST
  days/<date>/
    manifest.json          + "field_digest"; subordinate stations carry "derivation"
    ...
```

`field/manifest.json` has these keys:

- `format`, `region` and `method` (`channel-harmonic/1`).
- `stations`: per station, `id`, `north_m` and `east_m` (where its disk was solved), and `frame_north` and `frame_east`. A cell's slot value indexes this list.
- `grids`: `id`, `values_file` (`<id>.f32`), `stations_file` (`<id>.u8`), `origin_north_m`, `origin_east_m`, `cell_m`, `rows`, `cols`, `slots` (1 to 8), `values_sha256` and `stations_sha256`.
- `parameters`: the values below.
- `sources`: the depth set's DIGEST, the ENC catalogue and cells, and the CUDEM tiles, each with its SHA-256.
- `notes`.

A cell's values are: the axis's north and east components (a unit vector, flood-oriented), the along and cross factors in [0, 1], then one weight per slot. A dry cell has every slot empty and all values zero. Grids follow the depth grids' conventions: cell centres, rows growing north, and no overlap.

The day manifest's optional `field_digest` names the field. When it is present, the day's current stations must include every field station, each within 0.01 m of where the field solved it.

A subordinate current station has `kind: subordinate-prediction` and, from this builder, a `derivation` object. The format makes it optional, and refuses it on any other kind. Its keys:

- `method`: `noaa-subordinate`.
- `reference_station` and `reference_bin`.
- The four time offsets in minutes: `slack_before_flood_min`, `max_flood_min`, `slack_before_ebb_min` and `max_ebb_min`.
- `flood_ratio` and `ebb_ratio`.
- `flood_dir_deg` and `ebb_dir_deg`, in [0, 360).
- `sources`: `name` and `sha256` of the offsets, the reference's 6-minute series and its events.

### Digests

- **Field digest:** SHA-256 over the name `manifest.json`, NUL, its bytes and NUL. Then, for each grid in `grids` order, its values file and then its stations file, each as name, NUL, bytes and NUL.
- **Day digest:** without `field_digest`, the rule above exactly, so packs without a field keep their digests. With it, the field digest's 64 hex characters and a NUL follow the depth digest's.

Both readers refuse:

- unknown or missing keys, and a format, method or region other than these;
- any per-file SHA-256, DIGEST or `field_digest` mismatch;
- station ids that are empty, repeated, more than 254 or contain a comma or quote, and a frame shorter than 1e-3;
- grid ids that are not plain names, files not named `<id>.f32` and `<id>.u8`, grids under 2 × 2 cells, and grids that overlap;
- sizes that do not fit the grid shape, and slots outside 1 to 8;
- non-finite values;
- factors or weights outside [0, 1];
- a weight in an empty slot, or a slot naming no station;
- dry cells with values.

On a water cell, both readers also refuse an axis further than 1e-5 from unit length, or weights summing further than 1e-5 from 1. The tolerances allow for float32 storage, and are the same in `pack.py` and the C# reader (`CurrentFieldGrid`, `EnvironmentPackJson`).

### Sampling rule (part of the format)

The pipeline (`field.evaluate`) and the core (`ChannelCurrentField`) implement the same rule:

1. Each station's velocity v at the pack time is resolved on its frame f into an along part a = v·f / |f|² and an across part x = v·f⊥ / |f|², where f⊥ = (−f_east, f_north) is f turned 90° clockwise.
2. A water cell's velocity is along·A·n + cross·X·n⊥. A and X are the slot-weighted sums of the stations' a and x, n is the cell's axis, and along and cross are its factors.
3. A point's value blends the four cells around it bilinearly (the depth grids' cell lookup, with the last row and column of centres inside), over the water cells only. The sum is divided by the water cells' share of the bilinear weight, or by ½ when their share is smaller. So the flow keeps its full strength up to a shoreline halfway between a water centre and a land centre, and fades to zero over land.
4. Outside every grid, the value is the station blend (inverse distance squared over all the day's current stations, as `StationBlendCurrentField`).

**Golden vectors.** `core/dotnet/FerrySim.Core.Tests/Environment/python_field_pack.py` is an independent implementation. It writes a small pack with a field and samples it at 13 points:

- mid-channel;
- at station A, next to it, and at station B, where two corners have no across part;
- near a shore, with a water share of 0.65 and of 0.4 (either side of the ½ floor);
- on a land centre;
- either side of a cell boundary and on it;
- just past the grid's north edge, far outside it, and at a station outside it.

The C# reader, `ChannelCurrentField`, the pipeline's `pack.verify` and `field.evaluate` all reproduce its digests and samples within 1e-12 m/s (`CurrentFieldPackTests`, `test_environment_golden.py`).

### Why the shore renormalises

The task asks for no normal flow at the shoreline, not no flow, and the evidence supports renormalising:

- **Landing faces.** At every slice landing's primary face (midpoint, 5 m and 15 m out), the two rules give identical values at 20 m and at 10 m, because all four cells around those points are water.
- **Along a seawall.** The rules differ only within half a cell of the shoreline. On a transect at 1760 m north (40.716° N), out from Battery Park City's straight Hudson seawall in open flow, counting a land cell as zero halves the flow at the shoreline: 0.36 of the speed 100 m out against 0.72 half a cell away on the 10 m lattice, and 0.43 against 0.85 on 20 m. It fades over half a cell, 5 m on the 10 m lattice and 10 m on 20 m. That is a grid artifact, not a measured wall layer.
- **Renormalising.** It keeps the along-shore flow up to the shoreline: 0.72 of the speed 100 m out on the 10 m lattice, 0.85 on 20 m. The shore-tangent axes and the cross taper remove the normal part.

### Parameters and how each was chosen

| Parameter | Value | How it was chosen |
|---|---|---|
| `cell_m` | 10 | Convergence at the slice's primary landing faces, by the coordinator's rule: the coarsest lattice whose face values (sheltering, and the speed fraction at the face midpoint) are within about 0.05 of the next finer one. 20 m is not: it differs from 10 m by up to 0.31 (North Williamsburg), 0.15 (East 34th Street) and 0.08 (Pier 11). 10 m is: it differs from 5 m by at most 0.047 (Pier 11's sheltering) and by 0.033 or less elsewhere (`artifacts/current-field/convergence.md`). The field is 14.3 MB, within the 15 MB budget, and takes about 15 minutes to build. Leave-one-out and the sweep run on a 20 m lattice (`validation.LOO_CELL_M`), where each left-out weight solve takes under a minute |
| `computation_north_m`, `computation_east_m` | −3500 to 8000, −2600 to 6500 | Every slice current station with water around it, so leave-one-out works at all twelve. The box reaches the Upper Bay past Red Hook, so Buttermilk Channel and the main channel meet in the bay rather than at an edge |
| `inflow`, `outflow` | south, north | The pattern's open edges: the flood enters from the Upper Bay and leaves toward Hell Gate and up the Hudson. The axes point with the flood; their sign does not change the field |
| `conveyance_exponent` | 0 | Plain potential flow: the water's shape decides the pattern, and the bed does not. Leave-one-out is worse with depth-weighted flux (1: 0.398 m/s) and with Manning's 5/3 (0.411) than with 0 (0.393). And near berths the merged bed is too shallow (−2.4 to −2.8 m from 10 to 150 m off the Pier 11 face), which a conveyance-weighted pattern turns into dead water at the landing |
| `min_depth_m` | 1 | A floor for water whose bed reads less than 1 m below NAVD88, or is unknown, such as the Pier 11 face (+0.4 m) |
| `smoothing_m` | 800 | The neighbourhood the pattern speed is compared with. Chosen by leave-one-out, which improves with longer smoothing: pooled RMS vector error 0.400, 0.393 and 0.388 m/s at 400, 800 and 1600 m. Past 800 m the gain is only at `NYH1919` and `ACT3431`, while `ACT3426` and `NYH1922` lose, so 800 m takes most of it without fitting two stations. 400 m was the first proposal |
| `shelter_threshold` | 1 | No dead band: water at least as fast as its surroundings keeps the stations' speed, and slower water is slowed in proportion. Leave-one-out chose it over the first proposal, 0.85 (the 5th percentile of S / S̄ in open water): 0.393 against 0.405 m/s. It predicts the three stations in water slower than their surroundings better (`NYH1919`, `ACT3431`, `ACT3426`) and moves the others by at most 0.006 m/s. Above 1 it would slow water as fast as its surroundings: at 1.1 the RMS error is lower (0.389), but the field runs slower than the stations nearly everywhere (speed bias −0.129 against −0.104 m/s) |
| `depth_exponent` | 1/6 | The research's proposal, and a mild one. The task asks for the speed to vary across the channel with depth, which rules out 0. Leave-one-out barely sees it, because station speeds do not follow local depth (`ACT3411` is fast in 8.3 m, `ACT3431` slow in 18.4 m): 0.391 m/s with no depth factor, 0.393 at 1/6, 0.395 at 1/3 and 0.400 at Manning's 2/3. Capped at 1 and relative to the depth around, it only slows water shallower than its surroundings |
| `depth_smoothing_m` | 200 | The cross-channel scale the depth is compared with. Leave-one-out is flat: 0.3926, 0.3927 and 0.3934 m/s at 100, 200 and 400 m |
| `cross_taper_m` | 60 | The across part grows from none in the cells next to land to all of it 60 m from the shoreline. It never reaches the four stations inside the stored grid, which are 150 to 310 m from shore. Leave-one-out is flat: 0.3923 m/s with the taper at 20 m or 120 m, against 0.3927 at 60 m |
| `station_disk_m` | 40 | Covers the four cells around a station (up to 14 m away on the 10 m lattice, 28 m on 20 m), so the field is exact at it. The closest pair (`ACT3416` and `ACT3421`, 290 m apart) keeps the disks well apart |
| `station_reach_m` | 120 | Within it the along factor rises to 1, so the field does not rescale a station's speed; it fades over the 80 m beyond the disk. Leave-one-out cannot see it, because stations are at least 290 m apart: 41 m and 240 m score the same 0.3927 m/s |
| `across_reach_m` | 500 | How far a station's own direction off the channel axis carries. The approved rule kept it everywhere, tapered only at the shore. Leave-one-out showed that carrying one station's offset to the others hurts more than it helps: with the first proposed sheltering, pooled RMS error 0.422 m/s everywhere against 0.409 within 120 m to 500 m, and the mean 90th-percentile direction error 15.1° against 9.8°. Nine stations improve, two get slightly worse (`ACT3426`, `ACT3431`). It is flat from 120 m to 500 m and worse from 800 m. 500 m, the largest of the flat range, turns a station's direction back to the axis most gently: `NYH1921`'s flood, 20° off the axis at Corlears Hook, turns back over 460 m rather than 80 m. With the final sheltering: 0.3931 at 120 m, 0.3927 at 500 m, 0.3947 at 1000 m, 0.3972 at 2000 m and 0.4069 everywhere |
| `slots` | 6 | Truncation drops at most 0.76 % of a cell's weight (99th percentile 0.64 %) over the computation box; on the 20 m lattice, 4 slots dropped up to 5.7 % |
| `tolerance` | 1e-10 | The relative residual every solve converges to |

`python -m ferrydata.environment validate --region lower-east-river` writes the evidence to `artifacts/current-field/`:

- `subordinates.md`: the subordinate series against the published events;
- `loo.md` and `loo.json`: leave-one-out at every station against the inverse-distance baseline;
- `sensitivity.md`: each parameter moved one at a time;
- `landings.md`: each landing's speed as a fraction of the nearest open channel;
- plots of the errors, series and maps.

### Sizes (lower-east-river)

- **Field:** 14.26 MB (one 611 × 507 grid of 10 m cells: 12.39 MB of values and 1.86 MB of slots), once per region. The coordinator's budget is about 15 MB.
- **Day:** about 226 kB with all twelve current stations (`currents.csv` 171 kB).
- **All 28 days:** 147 files and 25,558,905 bytes in all, with the depth set and the field. Two builds are byte-identical.

### Limitations

- **One pattern for every tide.** The axes and the sheltering are one potential-flow solution, the same on the flood and the ebb. Real flow separates differently on each. At Corlears Hook, `NYH1921`'s flood runs 20° off the axis while its ebb follows it within 4°. The field keeps that within 500 m of the station and follows the axis beyond.
- **Slips and basins.** Shelter comes from potential flow, so it catches dead ends and pier shadows but not the eddies that stir them. Wallabout Bay is a dead end, so the field at the Brooklyn Navy Yard landing is close to zero. No near-shore measurements calibrate any of this; crew observations at the landings are the way to do that.
- **Piers charted as outlines.** They are open water to the field (step 1), so where such a pier is solid the field misses its shelter.
- **Inherited bed errors.** The depth factor reads the depth set, which is too shallow next to some berths (South Williamsburg, Pier 11's outer berth, the Brooklyn Navy Yard). The field is a function of the depth set, so a revised depth set needs only a rebuild.
