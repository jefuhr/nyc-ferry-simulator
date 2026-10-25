# New York Harbor Current-Field Research (`environment-currents.md`)

* **Research date:** `2026-10-24` (updated `2026-10-25` for M3 Wave 1 review fixes)
* **Target scenario days (28 days):** `2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`
* **Harbor bounding box:** `lat 40.60°N to 40.83°N`, `lon -74.12°E to -73.80°E`
* **Local sample artifacts:** `artifacts/env-research/`

---

## Executive Summary & Architecture Recommendation

To drive realistic vessel hydrodynamics across New York Harbor for the 28 scenario days—especially in the constricted tidal straits of the **East River**, **Roosevelt Island (West and East Channels)**, **Hell Gate**, and **Kill Van Kull**—we evaluated five candidate current-field sources using primary-source metadata and empirical fetches on `2025-07-14` and across all four scenario weeks:

1. **NOAA NYOFS** (New York and New Jersey Operational Forecast System, `s3://noaa-nos-ofs-pds/nyofs/`)
2. **NOAA STOFS-3D-Atlantic** (3D Surge and Tide Operational Forecast System – Atlantic, `s3://noaa-nos-stofs3d-pds/STOFS-3D-Atl/`)
3. **NOAA CO-OPS Harmonic + Subordinate Current Station Network** (`37` stations / `63` depth bins in the harbor bounding box via CO-OPS MDAPI and Data API)
4. **MARACOOS / IOOS HF Radar** (`realtime_maracoos_1km_totals_qartod` via Rutgers ERDDAP)
5. **Stevens Institute NYHOPS** (New York Harbor Observing and Prediction System)

### Primary Recommendation for v1 (Wave 2 Implementation)

**Use a CO-OPS Harmonic + Subordinate Channel-Constrained Current Field (`35` active prediction stations + `2` weak-and-variable stations inside the harbor box, optionally blended with NYOFS `stations.nowcast.nc` subtidal residuals), pre-evaluated at 6-minute intervals onto the harbor navigable-water mesh/grid.**

* **Why this is the primary choice for v1:**
  1. **100% 28-day availability with tiny raw fetch size (<5 MB total):** CO-OPS harmonic (`H`) and subordinate (`S`) predictions are deterministic and available for all `28/28` scenario days via lightweight JSON queries (`data-pipeline/ferrydata/coops.py` already fetches the 6 Lower East River harmonic stations). By contrast, **NYOFS 2D/3D spatial field files (`fields.nowcast.nc`) are missing from AWS S3 for `2024-10-01` through `2024-10-19`**, leaving **0/7 days available for Scenario Week 1 (`2024-10-07..13`)**, while **STOFS-3D-Atlantic raw 2D/3D field files require downloading `2.98 GB/day` to `6.43 GB/day` (`83–180 GB` across 28 days)** because they package the entire Atlantic Ocean mesh (`2,926,236` nodes) in each NetCDF file.
  2. **Resolution in Hell Gate and around Roosevelt Island:** CO-OPS provides **5 dedicated subordinate stations around Roosevelt Island** (`ACT3406`, `ACT3416`, `ACT3421` in the West Channel; `ACT3411`, `ACT3426` in the East Channel) referenced to `NYH1924` (Hell Gate bin 6), plus `NYH1924` (3 depth bins in Hell Gate), `NYH1926` (Harlem River south end), and `ACT3386` (Astoria). This station network explicitly resolves both channels around Roosevelt Island and their asymmetric flood/ebb speed multipliers (e.g., `ACT3406` West Channel north has a `1.12×` ebb multiplier relative to Hell Gate bin 6, reaching `2.83 m/s` / `5.51 kt` peak ebb across the 28 days). In contrast, **NYOFS's fine grid (`nyofs_fg`) has `0` cells in the East River** (it covers only Newark Bay, Kill Van Kull, and western Upper Bay), and **NYOFS's coarse grid (`nyofs`) collapses the entire East River at Roosevelt Island into a single 1-cell-wide column (`j=58`, `585–690 m` cell spacing)**, failing to split the West and East Channels and underpredicting Hell Gate peak ebb speeds by `~35%` (`1.44 m/s` vs `2.29 m/s` on `2025-07-14`).
  3. **Channel-constrained geometry & determinism:** Combining the 35 CO-OPS stations with CUDEM bathymetry and ENC shoreline polygons (from Wave 1) via channel-aligned interpolation (with a **proposed, to be calibrated** $(h/h_{\text{ref}})^{1/6}$ lateral depth-scaling factor and $L_{\text{wall}} = 15\text{–}25\text{ m}$ shore-decay length scale; see §1.3) produces a deterministic, smooth current field (`~1.0–2.5 MB/day` packed for the entire harbor) that enforces no-normal-flow along ENC shorelines. Evaluating this field on a `10–30 m` grid is a **modelling choice** to match the CUDEM/ENC shoreline geometry, **not validated hydrodynamic resolution**.

### Fallback Recommendation (and Gridded Hydrodynamic Upgrade Path)

1. **Immediate Gridded Fallback (if NYOFS gridded NetCDFs are used directly):** Use **NYOFS `nyofs` + `nyofs_fg` `fields.nowcast.nc` (`~35.1 MB/day`) for the 21 scenario days in 2025 (`2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`)**, and fall back to **NYOFS `stations.nowcast.nc` (`~1.56 MB/day`, available all `28/28` days) + CO-OPS harmonic/subordinate stations** for the missing October 2024 week (`2024-10-07..13`) and inside the Roosevelt Island / Hell Gate corridor.
2. **High-Resolution Gridded Upgrade (STOFS-3D-Atlantic clipped mesh):** **STOFS-3D-Atlantic (`v2.1`)** is available on AWS S3 for **all `28/28` scenario days** and has **3,763 wet mesh nodes inside the harbor bounding box** with **`80–110 m` node spacing** in the East River, Hell Gate (`137` wet nodes, median `81.4 m`), and Roosevelt Island (`75` wet nodes, median `99.3 m`, resolving both West and East Channels). If the pipeline runs a one-time offline clipper (under `heavy.lock`) that extracts only the `3,763` harbor wet nodes from `stofs_3d_atl.t12z.fields.out2d_n*.nc` (or `field2d_n*.nc`), the resulting harbor-clipped dataset is only **`~1.1 MB/day`** (`~31 MB` total for all 28 days).

---

## 1. Evaluation of Candidate Current-Field Sources

### 1.1 NOAA NYOFS (`s3://noaa-nos-ofs-pds/nyofs/`)

* **Model Architecture & Physics:**
  * NYOFS is a 3D **barotropic** (constant water density; no temperature or salinity evolution) hydrodynamic model based on the Princeton Ocean Model (POM), developed by NOAA/NOS/CSDL and operated by CO-OPS (**Confidence:** High; [NOAA Technical Report NOS CS 12](https://tidesandcurrents.noaa.gov/ofs/publications/CS_Techrpt_012_Dev_NYOFS_2001.pdf), [NOAA Technical Report NOS CO-OPS 037](https://tidesandcurrents.noaa.gov/ofs/publications/CO-OPS_Techrpt_037_NYOFS_2002.pdf), accessed `2026-10-24`).
  * NYOFS runs two one-way nested curvilinear orthogonal grids (**Confidence:** High; verified by inspecting `nyofs.t05z.20250714.fields.nowcast.nc` and `nyofs_fg.t05z.20250714.fields.nowcast.nc` from `s3://noaa-nos-ofs-pds/`, accessed `2026-10-24`):
    1. **Coarse Grid (`nyofs`):** `nx = 134, ny = 73` (`9,782` total cells, `4,461` wet cells), covering `lat 39.5060°N to 41.2797°N`, `lon -74.2654°E to -71.9087°E` (New York Bight, NY/NJ Harbor, Hudson River to Poughkeepsie, and Long Island Sound to Milford/Port Jefferson).
    2. **Fine Grid (`nyofs_fg`):** `nx = 40, ny = 126` (`5,040` total cells, `1,784` wet cells), covering **only** `lat 40.6310°N to 40.6822°N`, `lon -74.1981°E to -74.0243°E` (**Newark Bay, Kill Van Kull, Arthur Kill north end, and western Upper Bay**). It contains **`0` cells** in the East River, Hudson River, Lower Bay, or eastern Upper Bay.
  * *Note on NYOFS2:* NOAA CO-OPS lists an experimental FVCOM-based upgrade ("NYOFS2") on its development page ([CO-OPS OFS Models Page](https://tidesandcurrents.noaa.gov/models.html), accessed `2026-10-24`), but no `nyofs2` prefix exists in `s3://noaa-nos-ofs-pds/` as of `2026-10-24`.

* **S3 URL Patterns & November 2024 Directory Change:**
  * Bucket: `s3://noaa-nos-ofs-pds` (`https://noaa-nos-ofs-pds.s3.amazonaws.com/`, anonymous public read, **Confidence:** High, accessed `2026-10-24`).
  * **Legacy monthly prefix (through December 2024):**
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYYMM/nyofs.t{CC}z.{YYYYMMDD}.fields.nowcast.nc`
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYYMM/nyofs.t{CC}z.{YYYYMMDD}.stations.nowcast.nc`
  * **Current daily prefix (from `2024-11-19` onward, including all 2025 scenario days):**
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYY/MM/DD/nyofs.t{CC}z.{YYYYMMDD}.fields.nowcast.nc`
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYY/MM/DD/nyofs_fg.t{CC}z.{YYYYMMDD}.fields.nowcast.nc`
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYY/MM/DD/nyofs.t{CC}z.{YYYYMMDD}.stations.nowcast.nc`
    * `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYY/MM/DD/nyofs_fg.t{CC}z.{YYYYMMDD}.stations.nowcast.nc`
    * Vertical datum conversion grid: `https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/YYYY/MM/DD/nyofs_vdatums.nc` (`197,104 B`, containing `navd88_msl`, `mllw_msl`, `mhw_msl`, `mlw_msl`, `mtll_msl` on the `134×73` grid).
  * Cycles (`{CC}`): `05`, `11`, `17`, `23` UTC (`4` cycles/day). Each `fields.nowcast.nc` file contains `6` hourly snapshots (`nt = 6`), so concatenating all 4 cycles yields `24` hourly snapshots per UTC day (`00:00` to `23:00` UTC).

* **28-Day Scenario Availability Audit (Critical Gap in Week 1):**
  * We audited every file in `s3://noaa-nos-ofs-pds/nyofs/netcdf/` across all 28 scenario days (**Confidence:** High; verified via S3 ListObjectsV2 on `2026-10-24`):
    * **`2024-10-07..13` (7 days):** `fields.nowcast.nc` and `fields.forecast.nc` (for both `nyofs` and `nyofs_fg`) are **completely missing from the S3 bucket for `2024-10-01` through `2024-10-19`** (the monthly prefix `nyofs/netcdf/202410/` only contains `fields.*` starting at `nyofs.t17z.20241019`). However, **`nyofs.t*z.20241007..13.stations.nowcast.nc` and `nyofs_fg.t*z.20241007..13.stations.nowcast.nc` ARE present for all 4 cycles of all 7 days** (`28/28` files each).
    * **`2025-01-13..19` (7 days):** `100%` complete (`28/28` cycles of `nyofs` and `nyofs_fg` `fields.nowcast.nc` and `stations.nowcast.nc`).
    * **`2025-04-14..20` (7 days):** `100%` complete (`28/28` cycles of `nyofs` and `nyofs_fg` `fields.nowcast.nc` and `stations.nowcast.nc`).
    * **`2025-07-14..20` (7 days):** `100%` complete (`28/28` cycles of `nyofs` and `nyofs_fg` `fields.nowcast.nc` and `stations.nowcast.nc`).
  * We also checked the CO-OPS OPeNDAP/THREDDS server (`https://opendap.co-ops.nos.noaa.gov/thredds/catalog/NOAA/NYOFS/MODELS/catalog.xml`, accessed `2026-10-24`): it retains only `2026` months (`01` through `10`), and NCEI's legacy THREDDS archive (`https://www.ncei.noaa.gov/thredds/model/model.html`) returns `HTTP 404`. Thus, the October 2024 `fields.nowcast.nc` gap cannot be filled from NOAA public HTTP/S3 archives.

* **Variables, Vertical Coordinates, and File Sizes:**
  * **Vertical coordinate (`sigma`):** `7` terrain-following $\sigma$ levels: `[0.0, -0.1666667, -0.3333333, -0.5, -0.6666667, -0.8333333, -1.0]` (`k = 0` is surface $\sigma = 0$; `k = 6` is bottom $\sigma = -1.0$).
  * **Variables in `fields.nowcast.nc`:**
    * `u(time, sigma, ny, nx)` and `v(time, sigma, ny, nx)` in `m/s` (`missing_value = -99999.0`). **Crucial rotation note:** In `fields.nowcast.nc`, `u` and `v` are aligned with the local curvilinear grid axes $(\xi, \eta)$, NOT true East/North! They must be rotated by `angle(ny, nx)` (`grid_orientation_angle` in degrees counterclockwise from true East):
      $$u_{\text{east}} = u \cos(\text{angle}) - v \sin(\text{angle}), \qquad v_{\text{north}} = u \sin(\text{angle}) + v \cos(\text{angle})$$
    * `zeta(time, ny, nx)` (`water_surface_elevation` in `m` relative to model datum `MSL`), `depth(ny, nx)` (`bathymetry` in `m`), `mask(ny, nx)` (`land_binary_mask`: `1.0` = water, `0.0` = land), `air_u(time, ny, nx)` and `air_v(time, ny, nx)` (`10 m` wind velocity in `m/s`), `Pair(time, ny, nx)` (`surface_air_pressure` in `mbar`).
  * **Variables in `stations.nowcast.nc`:**
    * `61` time steps per 6-hour cycle (`6-minute` resolution, `360 s` step) at `45` coarse-grid stations (`nyofs`) and `20` fine-grid stations (`nyofs_fg`). Here `u(time, nstation, sigma)` and `v(time, nstation, sigma)` are already rotated into true `eastward_sea_water_velocity` and `northward_sea_water_velocity` in `m/s`.
  * **Raw size per day (all 4 cycles combined):**
    * `nyofs.*.fields.nowcast.nc`: `4 × 5.79 MB = 23.18 MB/day`
    * `nyofs_fg.*.fields.nowcast.nc`: `4 × 2.99 MB = 11.95 MB/day`
    * `nyofs.*.stations.nowcast.nc` + `nyofs_fg.*.stations.nowcast.nc`: `4 × (0.270 + 0.122) MB = 1.57 MB/day`
    * **Total raw NYOFS size:** `35.12 MB/day` (fields) + `1.57 MB/day` (stations).

---

### 1.2 NOAA STOFS-3D-Atlantic (`s3://noaa-nos-stofs3d-pds/STOFS-3D-Atl/`)

* **Model Architecture & Versions Across the 28 Scenario Days:**
  * STOFS-3D-Atlantic is a 3D **baroclinic** (full temperature/salinity density-driven + tidal + atmospheric + river forcing) coupled operational forecast system based on **SCHISM** (Semi-implicit Cross-scale Hydroscience Integrated System Model) on an unstructured triangular/quadrangular mesh, forced by HRRR (`3 km`) + GFS (`0.25°`) winds, NWM (`745` rivers), and HYCOM open boundaries (**Confidence:** High; [STOFS-3D-Atlantic S3 README](https://noaa-nos-stofs3d-pds.s3.amazonaws.com/README_Atl.html), accessed `2026-10-24`).
  * **Version history verified from `README_Atl.html`:**
    * **`v2.1` (operational `2024-05-14` through `2026-08-16`):** Covers **all 28 scenario days** (`2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`). Uses `2,926,236` horizontal mesh nodes (`nSCHISM_hgrid_node`), `5,654,157` elements (`nSCHISM_hgrid_face`), and up to `49` vertical layers (`nSCHISM_vgrid_layers`).
    * **`v3.1` (operational `2026-08-17` to present):** Expanded to `3,052,121` nodes and `5,872,610` elements.

* **S3 URL Pattern & 28-Day Availability:**
  * Bucket: `s3://noaa-nos-stofs3d-pds` (`https://noaa-nos-stofs3d-pds.s3.amazonaws.com/`, anonymous public read, **Confidence:** High, accessed `2026-10-24`).
  * Daily directory: `https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl.{YYYYMMDD}/`
  * Run cycle: `1` cycle/day (`t12z`), containing a `24-hour` nowcast (`n001_012` = hours `01..12`, covering `13:00` to `00:00` UTC of the previous/current day; `n013_024` = hours `13..24`, covering `01:00` to `12:00` UTC of `{YYYYMMDD}`) plus a `96-hour` forecast (`f001_012` .. `f085_096`).
  * **28-Day Availability:** Verified via S3 prefix listing (`list-type=2`) on `2026-10-24` that **all `28/28` scenario days** exist and contain complete nowcast 2D and 3D field files (`artifacts/env-research/stofs3d_v2.1_harbor_summary.json`).

* **Mesh Resolution in NY Harbor (`v2.1`, Verified Empirically from `2025-07-14` NetCDF):**
  * By extracting the horizontal mesh coordinates `(x, y)` and bathymetric `depth` from `stofs_3d_atl.20250714/stofs_3d_atl.t12z.fields.cwl.maxele.nc` (`artifacts/env-research/stofs3d_v2.1_harbor_summary.json`), we measured the wet-node counts (`depth > 0`) and nearest-neighbor node spacing across New York Harbor:
    * **Full Harbor Box (`40.60..40.83, -74.12..-73.80`):** `8,312` total nodes, **`3,763` wet nodes** (`depth > 0`; remaining `4,549` nodes are overland flood-plain nodes with negative depth).
    * **Upper Bay (`40.60..40.705, -74.08..-74.00`):** `422` wet nodes; spacing `66.8–457.5 m` (**median `206.1 m`**).
    * **Kill Van Kull (`40.635..40.655, -74.16..-74.06`):** `251` wet nodes; spacing `56.6–164.3 m` (**median `80.2 m`**).
    * **Lower East River (`40.70..40.75, -74.01..-73.95`):** `253` wet nodes; spacing `59.4–223.7 m` (**median `108.7 m`**).
    * **Roosevelt Island (`40.75..40.775, -73.965..-73.935`):** `75` wet nodes; spacing `75.2–194.8 m` (**median `99.3 m`**), with `32` wet nodes in the West Channel and `36` wet nodes in the East Channel (plus `7` at the north/south island tips).
    * **Hell Gate (`40.772..40.788, -73.945..-73.915`):** `137` wet nodes; spacing `52.5–176.9 m` (**median `81.4 m`**).
    * **Harlem River mouth (`40.78..40.81, -73.945..-73.925`):** `227` wet nodes; spacing `25.1–128.4 m` (**median `52.1 m`**).

* **Variables, Vertical Datum, and File Sizes per Day:**
  * **Vertical datum:** `xGEOID20B` (note: in `stofs_3d_atl.t12z.ncast.station.profile.nc`, `zeta` carries attribute `datum = "xGEOID20B"`).
  * **Daily nowcast files (`n001_012` + `n013_024` = 24 hourly steps/day):**
    * `stofs_3d_atl.t12z.fields.out2d_n001_012.nc` (`1.49 GB`) + `n013_024.nc` (`1.49 GB`) = **`2.98 GB/day`** (contains `elevation`, `windSpeedX`, `windSpeedY`, `airPressure`, `depthAverageVelX`, `depthAverageVelY` in `v3.1`).
    * `stofs_3d_atl.t12z.fields.field2d_n001_012.nc` (`3.21 GB`) + `n013_024.nc` (`3.21 GB`) = **`6.43 GB/day`** (contains `depth_average_vel_u`, `depth_average_vel_v`, `surface_vel_u`, `surface_vel_v`, `bottom_vel_u`, `bottom_vel_v`, `water_level`, `wind_u`, `wind_v`).
    * `stofs_3d_atl.t12z.fields.horizontalVelX_n001_012.nc` (`1.28 GB`) + `n013_024.nc` (`1.28 GB`) + `horizontalVelY` (`2.56 GB`) = **`5.12 GB/day`** (full 49-layer 3D currents).
    * `stofs_3d_atl.t12z.ncast.station.profile.nc` = **`2.59 MB/day`** (`6-minute` 49-layer `u, v, temp, salt, zeta, wind_u, wind_v` profiles at `291` stations, including `8` stations in NY Harbor: `NYH1927, NYH1919, NYH1918, NYH1915, NYH1914, n05010, n03020` and `8518750`).
  * **Size after clipping to NY Harbor (`3,763` wet nodes):** Storing `24` hourly steps of `(u_surf, v_surf, u_bar, v_bar, zeta, wind_u, wind_v)` in `float16` across `3,763` wet nodes (plus static mesh connectivity) requires only **`~1.1 MB/day`**.

---

### 1.3 Station-Based Current Fields (NOAA CO-OPS Harmonic + Subordinate Network)

* **Station Coverage in the Harbor Bounding Box (`lat 40.60..40.83, lon -74.12..-73.80`):**
  * Querying `https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets,harcon&units=metric` (`artifacts/env-research/coops_curr_offsets.json`, accessed `2026-10-24`) returns **37 unique stations (`63` depth-bin records)** inside the harbor bounding box (all documented in `docs/research/environment-stations.csv`):
    * **15 Harmonic (`H`) stations (`41` depth bins):** `n03020` (The Narrows, 3 bins), `n05010` (Gowanus Flats LBB 32, 3 bins), `NYH1914` (Constable Hook Approach, 3 bins), `NYH1915` (Robbins Reef Light, 3 bins), `NYH1917` (Gowanus Bay Entrance, 2 bins), `NYH1918` (Red Hook Channel, 3 bins), `NYH1919` (Dimond Reef, 3 bins), `NYH1920` (Brooklyn Bridge, 3 bins), `NYH1921` (Corlears Hook, 3 bins), `NYH1922` (Newtown Creek, 3 bins), `NYH1924` (Hell Gate, 3 bins), `NYH1926` (Harlem River south end, 1 bin), `NYH1927` (Hudson River Entrance, 3 bins), `NYH1928` (Hudson River Pier 92, 3 bins), `NYH1934` (Canarsie Jamaica Bay, 2 bins).
    * **20 Subordinate (`S`) stations (`20` bins):**
      * **Referenced to `NYH1924` (Hell Gate, bin 6 at 4.57 m depth):** `15` stations covering the entire East River from E 31st St through Roosevelt Island (West & East Channels), Hell Gate, Astoria, Rikers Island, Hunts Point, Throgs Neck/Whitestone, and the Harlem River (`ACT3431`, `ACT3426`, `ACT3421`, `ACT3416`, `ACT3411`, `ACT3406`, `ACT3386`, `ACT3381`, `ACT3376`, `ACT3366`, `ACT3356`, `ACT3346`, `ACT3481`, `ACT3486`, `ACT3491`).
      * **Referenced to `n03020` (The Narrows, bin 7 at 3.93 m depth):** `4` stations in Upper Bay and Newark Bay tributaries (`ACT3621` Bay Ridge, `ACT3646` Statue of Liberty east of, `ACT3971` Hackensack River, `ACT3976` Passaic River).
      * **Referenced to `HUR0611` (George Washington Bridge, bin 16 at 3.96 m depth, at `40.8511°N, -73.9522°E`):** `1` station in the Upper Hudson (`ACT3656` Grant's Tomb).
    * **2 Weak-and-Variable (`W`) stations (`2` bins):** `ACT3361` (Flushing Creek entrance) and `ACT3371` (Bronx River, 1 mile north of Hunts Pt.), where tidal currents are officially classified as too weak and variable (`<0.15 m/s`) to predict.

* **How Harmonic + Subordinate Stations Are Turned Into a Continuous Harbor Current Field:**
  1. **Step 1 — Continuous 6-Minute Reference Series at Harmonic Stations:**
     * Fetch 6-minute major-axis velocity predictions $V_H(t)$ (in `cm/s`, converted to `m/s`) and flood/ebb directions $(\theta_{\text{flood}}, \theta_{\text{ebb}})$ for all `15` in-box harmonic stations (plus `HUR0611`) using the existing CO-OPS Data API `product=currents_predictions&interval=6&units=metric&time_zone=gmt` (`data-pipeline/ferrydata/coops.py`).
  2. **Step 2 — Continuous 6-Minute Reconstruction at Subordinate Stations:**
     * Each subordinate station $s$ provides four time offsets $(\Delta t_{\text{sbf}}, \Delta t_{\text{f}}, \Delta t_{\text{sbe}}, \Delta t_{\text{e}})$ in minutes (`sbfTimeAdjMin`, `mfcTimeAdjMin`, `sbeTimeAdjMin`, `mecTimeAdjMin`), two speed amplitude ratios $(\alpha_{\text{f}}, \alpha_{\text{e}})$ (`mfcAmpAdj`, `mecAmpAdj`), and local directions $(\theta_{\text{flood},s}, \theta_{\text{ebb},s})$ relative to its reference station/bin (`currentpredictionoffsets`).
     *Alternatively*, CO-OPS's Data API (`product=currents_predictions&station=ACT3406&bin=1&interval=MAX_SLACK&units=metric`) directly returns the published flood, ebb, and slack event timestamps and peak velocities $(t_k, V_k)$ for every subordinate station on any scenario day (verified empirically on `2025-07-14` in `artifacts/env-research/act3406_bin1_20250714_maxslack.json`).
     * Between successive slack and maximum events $(t_k, V_k)$, reconstruct the 6-minute scalar speed $V_s(t)$ using piecewise sinusoidal / shape-preserving cubic Hermite interpolation (or piecewise time-warping of the reference station's 6-minute curve $V_{\text{ref}}(\tau(t))$ scaled by $\alpha_{\text{f}}$ during flood and $\alpha_{\text{e}}$ during ebb), and convert to local East/North components $(u_s(t), v_s(t))$ using $\theta_{\text{flood},s}$ when $V_s(t) \ge 0$ and $\theta_{\text{ebb},s}$ when $V_s(t) < 0$.
  3. **Step 3 — Channel-Constrained Spatial Interpolation & Proposed Bathymetric/Shore Modulation:**
     * **Channel-Constrained Thalweg Interpolation (Recommended Architecture):** Because tidal currents in NY Harbor are guided by channel geometry and islands, Euclidean inverse-distance weighting (IDW) across land peninsulas (e.g., interpolating across Manhattan or across Roosevelt Island) is invalid. Instead:
       * Decompose the navigable waterway into **channel branches** (Upper Bay/Narrows, Hudson River, Kill Van Kull, Lower East River, Roosevelt Island West Channel, Roosevelt Island East Channel, Hell Gate/Astoria, Upper East River, Harlem River).
       * Within each channel branch, define curvilinear **along-channel distance $s$** and **normalized cross-channel coordinate $\eta \in [-1, 1]$** (or compute the Laplace potential-flow unit tangent field $\hat{\mathbf{e}}_s(x,y)$ inside the ENC/CUDEM wet polygon with $\mathbf{u}\cdot\mathbf{n} = 0$ on shorelines).
       * Interpolate the mid-channel scalar speed $V_{\text{mid}}(s, t)$ along the channel thalweg $s$ using 1D cubic/linear spline interpolation between consecutive CO-OPS station nodes along that branch.
     * **Lateral Depth Scaling & Shoreline Boundary Decay (**`proposed, to be calibrated`**):**
       * Because CO-OPS current stations measure mid-channel flow (`0.5–3.5 km` apart) and do not observe velocities in shallow water or inside ferry slips, we propose modulating the mid-channel speed laterally across the channel and near piers using local CUDEM water depth $h(x,y)$ relative to the thalweg reference depth $h_{\text{ref}}(s)$ and distance to the nearest ENC shoreline/bulkhead $d_{\text{shore}}(x,y)$:
         $$f_{\text{depth}}(x,y) = \text{clamp}\!\left(\left(\frac{\max(h(x,y), 0)}{h_{\text{ref}}(s)}\right)^{\gamma}, \, 0, \, c_{\max}\right) \cdot \left(1 - \exp\!\left(-\frac{d_{\text{shore}}(x,y)}{L_{\text{wall}}}\right)\right)$$
       * **Provenance & status of parameters (no cited NOAA source states these values; all three are `proposed, to be calibrated`):**
         1. **Depth exponent $\gamma = 1/6$ and cap $c_{\max} = 1.15$ (**`proposed, to be calibrated`**):** Inspired qualitatively by open-channel friction scaling, but no cited NOAA NYOFS/CO-OPS report or NY Harbor field study establishes $\gamma = 1/6$ or $c_{\max} = 1.15$ for lateral current profiles in NY Harbor.
         2. **Shoreline wall-decay length scale $L_{\text{wall}} \approx 15\text{–}25\text{ m}$ (**`proposed, to be calibrated`**):** A heuristic lateral boundary-layer width proposed to taper currents smoothly near bulkheads and inside recessed ferry slips; not derived from near-pier ADCP measurements.
         3. **Evaluation grid spacing (`10–30 m` as a modelling choice):** Pre-evaluating the channel-interpolated field onto a `10–30 m` grid is a **modelling choice** selected to align with CUDEM bathymetry and ENC shoreline polygons; it is **not validated hydrodynamic resolution** (the underlying hydrodynamic data remain the discrete CO-OPS station predictions).
       * **How Wave 2 should calibrate and test these proposed parameters:**
         1. **Leave-one-out CO-OPS station cross-validation:** Hold out intermediate harmonic and subordinate stations along each channel branch (e.g., `NYH1921` Corlears Hook, `ACT3431` Manhattan off E 31st St, `ACT3416` Roosevelt Island West Channel off 67th St, `ACT3426` Roosevelt Island East Channel landing, and `NYH1918` Red Hook Channel) from the spline fit, and tune the along-channel interpolation and depth exponent $\gamma$ against their NOAA peak/slack speeds and phases.
         2. **Cross-channel profile comparison against STOFS-3D-Atlantic (`v2.1`):** Compare the cross-channel velocity decay produced by $(\gamma, L_{\text{wall}})$ against the `80–110 m` unstructured SCHISM mesh nodes (`stofs_3d_atl.t12z.fields.field2d_*.nc`) across the Lower East River and Roosevelt Island channels.
         3. **Crew handling observations and AIS crab-angle checks at named landings:** Calibrate $L_{\text{wall}}$ (`15–25 m` initial range) against permitted crew handling observations (per `docs/research/crew-questions.md`) and AIS heading-minus-COG crab angles during approaches at named landings (`Pier 11`, `Wallabout`, `East 34th Street`, `Hunters Point South`, `Roosevelt Island`, and `Astoria`).

---

### 1.4 MARACOOS / IOOS HF Radar (`https://hfr.marine.rutgers.edu/erddap/`)

* **Datasets & ERDDAP Endpoints Evaluated:**
  * Server: Rutgers Center for Ocean Observing Leadership (RUCOOL) MARACOOS ERDDAP (`https://hfr.marine.rutgers.edu/erddap/griddap/`, accessed `2026-10-24`).
  * Datasets inspected (`artifacts/env-research/hfr_1km_info.json`, `hfr_2km_info.json`, `hfr_6km_info.json`):
    1. `realtime_maracoos_1km_totals_qartod`: `25 MHz` CODAR SeaSonde hourly surface currents, nominal `1 km` grid (`0.01°` lat × `0.012°` lon), `2023-07-17` to present, domain `lat 39.991°N to 41.321°N`, `lon -74.380°E to -70.180°E`.
    2. `maracoos_2km_totals_oi_v2`: `13 MHz` optimal-interpolation `2 km` product, `lat 34.50°N to 42.50°N`.
    3. `maracoos_6km_totals_oi_v2`: `5 MHz` optimal-interpolation `6 km` product.
* **Empirical Test in NY Harbor on `2025-07-14`:**
  * We queried `realtime_maracoos_1km_totals_qartod` across `lat 40.45°N to 40.85°N`, `lon -74.20°E to -73.75°E` for all 24 hours of `2025-07-14` (`artifacts/env-research/hfr_1km_20250714_harbor.json`, accessed `2026-10-24`).
  * **Result:** Out of `3,312` daily grid-hour cells inside the NY Harbor bounding box (`lat 40.60..40.83, lon -74.12..-73.80`), **`0` cells (`0.0%`) contain valid surface current vectors (`u, v` are `NaN`)**. Across the broader query (`lat 40.45..40.85`), all `1,486` non-NaN vectors over the 24-hour day lie in Lower New York Bay and Raritan Bay between `lat 40.45698°N` and `40.59698°N` (stopping just south of The Narrows).
  * **Why HF Radar Fails in the Inner Harbor:** HF radar measures Doppler shifts of Bragg-resonant ocean surface gravity waves (`~6 m` wavelength at `25 MHz`) from at least two shore stations with unobstructed line-of-sight and geometric dilution of precision (GDOP) $< 1.5$. Narrow channels (`180–600 m` wide in the East River and Kill Van Kull), urban RF interference, and lack of multi-station radial overlap north of The Narrows preclude HF radar coverage in Upper Bay, the East River, and the Hudson River.
* **Supplied Dataset License & Terms (`NC_GLOBAL.license`):**
  * In `https://hfr.marine.rutgers.edu/erddap/info/realtime_maracoos_1km_totals_qartod/index.json` (`artifacts/env-research/hfr_1km_info.json`, line 42, accessed `2026-10-24`; identical in `hfr_2km_info.json` and `hfr_6km_info.json`), the dataset's global `license` attribute states verbatim:
    > *"The data may be used and redistributed for free but is not intended for legal use, since it may contain inaccuracies. Neither the data Contributor, ERD, NOAA, nor the United States Government, nor any of their employees or contractors, makes any warranty, express or implied, including warranties of merchantability and fitness for a particular purpose, or assumes any legal liability for the accuracy, completeness, or usefulness, of this information."*
  * And `NC_GLOBAL.acknowledgement` (`hfr_1km_info.json`, line 6) states:
    > *"This data is provided by the Mid-Atlantic Regional Association Coastal Ocean Observing System (MARACOOS). Funding is provided by the U.S. Integration Ocean Observing System (IOOS)."*
  * Note: The ERDDAP metadata does **not** specify a Creative Commons (`CC-BY`) license; any CC-BY label is unverified and must not be inferred from IOOS affiliation.

---

### 1.5 Stevens Institute NYHOPS (`https://hudson.dl.stevens-tech.edu/maritimeforecast/`)

* **System Description:**
  * NYHOPS (New York Harbor Observing and Prediction System), operated by the Davidson Laboratory at Stevens Institute of Technology, runs the 3D baroclinic `sECOM` model (`10` sigma layers) across the NY/NJ Harbor and Hudson-Raritan Estuary (`~25–100 m` horizontal resolution in inner harbor channels) (**Confidence:** Medium-High; `artifacts/env-research/stevens_info.txt`, `https://hudson.dl.stevens-tech.edu/maritimeforecast/maincontrol.shtml`, accessed `2026-10-24`).
* **Empirical Public Data Access Audit (`2026-10-24`):**
  * `https://hudson.dl.stevens-tech.edu/maritimeforecast/` (`HTTP 200`): Serves an HTML frame viewer for real-time PNG contour maps.
  * `https://hudson.dl.stevens-tech.edu/maritimeforecast/PRESENT/data.shtml` (`HTTP 403 Forbidden`): Direct data download page is access-restricted.
  * `https://hudson.dl.stevens-tech.edu/thredds/catalog.xml` (`HTTP 404 Not Found`): No THREDDS server at `hudson.dl.stevens-tech.edu`.
  * `http://colossus.dl.stevens-tech.edu/thredds/catalog.xml` and `:8080/thredds/catalog.xml` (`Connection timed out`): The legacy `colossus` OPeNDAP/THREDDS server referenced in older IOOS catalogs is unreachable from the public internet.
  * **Conclusion:** NYHOPS cannot be used as a reproducible, automated data source for the 28 past scenario days.

---

### 1.6 Comparison Table of Current-Field Sources

| Criterion | CO-OPS Harmonic + Subordinate Network (Primary v1) | NOAA NYOFS (`nyofs` + `nyofs_fg` S3 NetCDF) | NOAA STOFS-3D-Atlantic (`v2.1` S3 NetCDF, Clipped) | MARACOOS HF Radar (`1 km` ERDDAP) | Stevens NYHOPS (`sECOM`) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Primary Access URL** | `https://api.tidesandcurrents.noaa.gov/api/prod/datagetter` & `.../mdapi/prod/webapi/` | `s3://noaa-nos-ofs-pds/nyofs/netcdf/` | `s3://noaa-nos-stofs3d-pds/STOFS-3D-Atl/` | `https://hfr.marine.rutgers.edu/erddap/griddap/` | `https://hudson.dl.stevens-tech.edu/maritimeforecast/` |
| **Availability Across the 28 Scenario Days** | **`28/28` days (`100%`)** (deterministic harmonic + subordinate predictions) | **`21/28` days for `fields.nowcast.nc`** (`2024-10-07..13` **MISSING**); **`28/28` days for `stations.nowcast.nc`** | **`28/28` days (`100%`)** (`v2.1` covers all 4 scenario weeks) | **`0/28` days in inner harbor** (`0` valid points north of `40.597°N`) | **`0/28` days publicly downloadable** (`HTTP 403` / THREDDS offline) |
| **Resolution in Lower East River** | `3` harmonic (`NYH1920, 1921, 1922`) + `1` sub (`ACT3431`) (`~1.5–3.5 km` station spacing; evaluated on a `10–30 m` grid as a **modelling choice**, not hydrodynamic resolution) | `53` wet cells (`nyofs`); spacing **`354–577 m` (median `459 m`)**; `0` `nyofs_fg` cells | `253` wet nodes; spacing **`59–224 m` (median `108.7 m`)** | No coverage (`NaN`) | `~30–100 m` (not downloadable) |
| **Resolution at Roosevelt Island (West & East Channels)** | **`5` dedicated subordinate stations** (`3` West Channel, `2` East Channel) + explicit branch split | **`6` wet cells in a single `1-cell-wide` column (`j=58`, median `640 m`)**; **does NOT split channels** | **`75` wet nodes (median `99.3 m`)**; splits West (`32` nodes) and East (`36` nodes) channels | No coverage (`NaN`) | Splits channels in PNG plots only |
| **Resolution in Hell Gate & Harlem River Mouth** | `NYH1924` (`3` depth bins), `ACT3386`, `NYH1926`, `ACT3481..3491` (`6` stations) | `7` wet cells in Hell Gate (**median `712 m`**); `6` in Harlem River (**median `790 m`**) | `137` wet nodes in Hell Gate (**median `81.4 m`**); `227` in Harlem (**median `52.1 m`**) | No coverage (`NaN`) | Not downloadable |
| **Representation Near Landings / Ferry Slips** | **Station-interpolated only (no near-slip observations);** uses **proposed, to be calibrated** $(h/h_{\text{ref}})^{1/6}$ depth scaling and $L_{\text{wall}} = 15\text{–}25\text{ m}$ shore decay on a `10–30 m` modelling grid (not validated hydrodynamic resolution) | **Coarse** in East River (`450–710 m` cells span across piers); **Fine** in Kill Van Kull (`~95 m` `nyofs_fg`) | **Unstructured coastal mesh** (`50–110 m` triangles with no-normal-flow boundary; no slip-scale resolution) | No coverage (`NaN`) | Not downloadable |
| **Vertical Layers & Depth Reference Levels** | `1–3` ADCP depth bins per harmonic station (`1.52–13.41 m`; `depthType`: `B` = below water surface, `S` = above seabed/bottom, `U` = unspecified; recorded in `sensor_height_reference` column of `environment-stations.csv`) | `7` terrain-following $\sigma$ levels (`0.0` to `-1.0` in sixths) | Up to `49` SZ/LSC2 vertical layers (or 2D surface / depth-averaged / bottom in `field2d`) | Surface only (`~0.5 m` Bragg depth) | `10` $\sigma$ layers (not downloadable) |
| **Time Step** | **`6 minutes`** (`240` steps/day) | `1 hour` (`fields.nowcast.nc`, `24`/day); `6 minutes` (`stations.nowcast.nc`) | `1 hour` (`fields.*.nc`, `24`/day); `6 minutes` (`station.profile.nc`) | `1 hour` (`24`/day) | `6 min` / `1 hr` (not downloadable) |
| **Raw Download Size per Day** | **`~0.15 MB/day`** (JSON API) | **`35.1 MB/day`** (`fields.nowcast.nc` CG+FG); **`1.57 MB/day`** (`stations.nowcast.nc`) | **`2.98 GB/day`** (`out2d`) or **`6.43 GB/day`** (`field2d`); `2.59 MB/day` (`station.profile.nc`) | `~0.2 MB/day` (Lower Bay only) | N/A |
| **Packed Harbor Size per Day** | **`~0.8–2.5 MB/day`** (pre-evaluated on harbor grid or station table + spatial weights) | **`~1.5 MB/day`** (cropped to harbor wet cells, `float16`) | **`~1.1 MB/day`** (clipped to `3,763` harbor wet nodes, `float16`) | N/A | N/A |
| **Determinism & License** | **100% deterministic**, U.S. Gov public work (`17 U.S.C. § 105`) | Deterministic archived NetCDF (21/28 days), U.S. Gov public work (`17 U.S.C. § 105`) | Deterministic archived NetCDF (28/28 days), U.S. Gov public work (`17 U.S.C. § 105`) | **Free use & redistribution, not for legal use, no warranty** (`NC_GLOBAL.license` at [ERDDAP info.json](https://hfr.marine.rutgers.edu/erddap/info/realtime_maracoos_1km_totals_qartod/index.json), accessed `2026-10-24`; no `CC-BY` license specified) | Academic / restricted |
| **Implementation Effort** | **Low-Medium** (`coops.py` already exists; add subordinate synthesis + channel/depth weights) | **Medium** (curvilinear grid rotation + `nyofs`/`nyofs_fg` stitching + fallback for Oct 2024 gap) | **Medium-High** (requires heavy S3 range/download & offline clipping of 3–6 GB/day files) | Not viable for inner harbor | Not viable |

---

## 2. Hell Gate and Roosevelt Island (West and East Channels)

### 2.1 What Each Source Resolves in Hell Gate and Around Roosevelt Island

1. **NYOFS Coarse (`nyofs`) and Fine (`nyofs_fg`) Grids:**
   * **`nyofs_fg`:** Terminates at `lon -74.0243°E` (Governors Island / western Upper Bay) and has **`0` cells** in the East River (**Confidence:** High; verified from `nyofs_fg.t05z.20250714.fields.nowcast.nc`, accessed `2026-10-24`).
   * **`nyofs` (coarse grid):** Inspecting `mask(ny, nx)`, `lat(ny, nx)`, and `lon(ny, nx)` in `nyofs.t05z.20250714.fields.nowcast.nc` (`artifacts/env-research/nyofs_cg_summary.json`) reveals that the entire East River alongside Roosevelt Island (`lat 40.750°N to 40.775°N`) is represented by **a single 1-cell-wide column of 6 wet cells at grid index `j = 58, i = 18..23`**:
     * `(i=18, j=58)`: `(40.7478°N, -73.9636°E)`, `depth = 8.43 m` (Queensboro Bridge south approach)
     * `(i=19, j=58)`: `(40.7530°N, -73.9597°E)`, `depth = 8.95 m`
     * `(i=20, j=58)`: `(40.7582°N, -73.9554°E)`, `depth = 9.07 m`
     * `(i=21, j=58)`: `(40.7634°N, -73.9510°E)`, `depth = 9.21 m`
     * `(i=22, j=58)`: `(40.7686°N, -73.9465°E)`, `depth = 9.27 m`
     * `(i=23, j=58)`: `(40.7739°N, -73.9420°E)`, `depth = 10.61 m` (Hallets Point / Hell Gate south)
   * Because adjacent columns `j = 57` and `j = 59` have `mask = 0` (land) for `i = 18..22`, **NYOFS completely erases Roosevelt Island as an island and merges the West and East Channels into a single `~640 m`-wide artificial channel**.
   * In **Hell Gate** (`lat 40.772°N to 40.788°N, lon -73.945°E to -73.915°E`), `nyofs` has only **`7` wet cells** with cell spacing **`679–754 m` (median `711.9 m`)**, and at the **Harlem River mouth** (`lat 40.780°N to 40.810°N`), only **`6` wet cells** (`736–803 m` spacing, median `790.2 m`). Consequently, NYOFS cannot resolve the sharp jet around Hallets Point, Mill Rock, or the East/West Channel split at Roosevelt Island.

2. **STOFS-3D-Atlantic (`v2.1` Unstructured SCHISM Mesh):**
   * Verified from `stofs_3d_atl.20250714/stofs_3d_atl.t12z.fields.cwl.maxele.nc` (`artifacts/env-research/stofs3d_v2.1_harbor_summary.json`, accessed `2026-10-24`):
   * Around **Roosevelt Island** (`lat 40.750°N to 40.775°N, lon -73.965°E to -73.935°E`), STOFS-3D-Atlantic has **`75` wet nodes** (`depth = 2.00 m to 20.67 m`, median spacing **`99.3 m`**), with dry island nodes (`depth < 0`) down the spine of Roosevelt Island separating **`32` wet nodes in the West Channel** from **`36` wet nodes in the East Channel** (typically `2–3` wet nodes across the width of each `~180–220 m` channel).
   * In **Hell Gate** (`lat 40.772°N to 40.788°N, lon -73.945°E to -73.915°E`), STOFS-3D-Atlantic has **`137` wet nodes** (`depth = 2.00 m to 27.67 m`, spacing **`52.5–176.9 m`**, median **`81.4 m`**), resolving the Hallets Point bend, Mill Rock bifurcation, and Wards Island / Astoria shoreline.
   * In the **Harlem River mouth** (`lat 40.780°N to 40.810°N, lon -73.945°E to -73.925°E`), STOFS-3D-Atlantic has **`227` wet nodes** (`depth = 2.00 m to 9.48 m`, spacing **`25.1–128.4 m`**, median **`52.1 m`**).

3. **NOAA CO-OPS Harmonic (`H`) and Subordinate (`S`) Current Stations:**
   * CO-OPS provides **10 primary and subordinate stations** directly along the Roosevelt Island – Hell Gate – Astoria – Harlem River corridor (**Confidence:** High; verified from `artifacts/env-research/coops_curr_offsets.json` and `nyh1924_bin9_20250714_20_maxslack.json`, accessed `2026-10-24`):
     * **Hell Gate (`NYH1924`, Harmonic `H`, `40.77830°N, -73.93830°E`, off Mill Rock / Hallets Point):** `3` ADCP depth bins (`bin 9` at `1.83 m` below surface, `bin 6` at `4.57 m`, `bin 1` at `9.75 m`).
     * **Roosevelt Island West Channel (`3` Subordinate `S` stations referenced to `NYH1924` bin 6):**
       1. `ACT3406` (*Roosevelt Is., west of, off 75th Street*, `40.76670°N, -73.95000°E`)
       2. `ACT3416` (*Roosevelt Is., west of, off 67th Street*, `40.76230°N, -73.95400°E`)
       3. `ACT3421` (*Roosevelt Is., west of, off 63rd Street*, `40.75970°N, -73.95450°E`)
     * **Roosevelt Island East Channel (`2` Subordinate `S` stations referenced to `NYH1924` bin 6):**
       1. `ACT3411` (*Roosevelt Island, east of, off 36th Avenue*, `40.76360°N, -73.94550°E` — directly off the Astoria ferry landing approach)
       2. `ACT3426` (*Roosevelt Island, east of*, `40.75820°N, -73.95130°E` — directly off the Roosevelt Island ferry landing)
     * **Astoria / Hell Gate East (`ACT3386`, Subordinate `S`, `40.78670°N, -73.91670°E`, Off Winthrop Ave., Astoria):** Referenced to `NYH1924` bin 6.
     * **Harlem River South End (`NYH1926`, Harmonic `H`, `40.78610°N, -73.93690°E`):** `bin 5` at `1.52 m` depth, plus `3` Harlem River bridge subordinate stations (`ACT3481` Willis Ave. Bridge, `ACT3486` Madison Ave. Bridge, `ACT3491` Macombs Dam Bridge).

### 2.2 Published & Verified Peak Current Speeds in Hell Gate and Around Roosevelt Island

Across the 28 scenario days (`2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`), we queried the NOAA CO-OPS `currents_predictions` API (`interval=MAX_SLACK&units=metric`) for `NYH1924` (`bins 9, 6, 1`) and applied the official CO-OPS subordinate amplitude ratios (`mfcAmpAdj`, `mecAmpAdj`) and time offsets (`mfcTimeAdjMin`, `mecTimeAdjMin`) for the Roosevelt Island, Astoria, and Harlem River stations (**Confidence:** High; verified on `2026-10-24`):

| Station ID | Station Name & Channel Location | Type (Ref) | Depth (m) | Flood / Ebb Dir (°T) | Time Offset Flood / Ebb (min) | Speed Ratio Flood / Ebb | 28-Day Mean Flood `m/s` (`kt`) | 28-Day Peak Flood `m/s` (`kt`) | 28-Day Mean Ebb `m/s` (`kt`) | 28-Day Peak Ebb `m/s` (`kt`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`NYH1924` (bin 9)** | **Hell Gate (near-surface)** | `H` (self) | `1.83 m` (B) | `045°` / `241°` | `0` / `0` | `1.00` / `1.00` | `1.69 m/s` (`3.28 kt`) | **`2.21 m/s` (`4.30 kt`)** | `-2.16 m/s` (`-4.20 kt`) | **`-2.67 m/s` (`-5.19 kt`)** |
| **`NYH1924` (bin 6)** | **Hell Gate (mid-depth reference bin)** | `H` (self) | `4.57 m` (B) | `046°` / `239°` | `0` / `0` | `1.00` / `1.00` | `1.60 m/s` (`3.11 kt`) | **`2.10 m/s` (`4.08 kt`)** | `-2.05 m/s` (`-3.99 kt`) | **`-2.53 m/s` (`-4.92 kt`)** |
| **`NYH1924` (bin 1)** | **Hell Gate (deep)** | `H` (self) | `9.75 m` (B) | `050°` / `232°` | `0` / `0` | `1.00` / `1.00` | `1.25 m/s` (`2.42 kt`) | `1.65 m/s` (`3.20 kt`) | `-1.67 m/s` (`-3.24 kt`) | `-2.05 m/s` (`-3.99 kt`) |
| **`ACT3406` (bin 1)** | **Roosevelt Is. West Channel (off 75th St)** | `S` (`NYH1924_6`) | Unspec (`U`) | `037°` / `215°` | `-6` / `+8` | `0.60` / **`1.12`** | `0.96 m/s` (`1.87 kt`) | `1.26 m/s` (`2.45 kt`) | **`-2.30 m/s` (`-4.47 kt`)** | **`-2.83 m/s` (`-5.51 kt`)** |
| **`ACT3416` (bin 1)** | **Roosevelt Is. West Channel (off 67th St)** | `S` (`NYH1924_6`) | Unspec (`U`) | `011°` / `230°` | `-10` / `+12` | **`1.08`** / `0.95` | **`1.73 m/s` (`3.36 kt`)** | **`2.27 m/s` (`4.41 kt`)** | `-1.95 m/s` (`-3.79 kt`) | `-2.40 m/s` (`-4.67 kt`) |
| **`ACT3421` (bin 1)** | **Roosevelt Is. West Channel (off 63rd St)** | `S` (`NYH1924_6`) | Unspec (`U`) | `036°` / `223°` | `-10` / `+4` | `0.84` / `0.69` | `1.34 m/s` (`2.61 kt`) | `1.76 m/s` (`3.43 kt`) | `-1.42 m/s` (`-2.75 kt`) | `-1.75 m/s` (`-3.39 kt`) |
| **`ACT3411` (bin 1)** | **Roosevelt Is. East Channel (off 36th Ave)** | `S` (`NYH1924_6`) | Unspec (`U`) | `030°` / `210°` | `-6` / `-10` | **`1.05`** / `0.81` | **`1.68 m/s` (`3.27 kt`)** | **`2.21 m/s` (`4.29 kt`)** | `-1.66 m/s` (`-3.23 kt`) | `-2.05 m/s` (`-3.99 kt`) |
| **`ACT3426` (bin 1)** | **Roosevelt Is. East Channel (south/landing)** | `S` (`NYH1924_6`) | Unspec (`U`) | `028°` / `200°` | `-8` / `+8` | `0.84` / `0.62` | `1.34 m/s` (`2.61 kt`) | `1.76 m/s` (`3.43 kt`) | `-1.27 m/s` (`-2.47 kt`) | `-1.57 m/s` (`-3.05 kt`) |
| **`ACT3386` (bin 1)** | **Astoria, off Winthrop Ave. (Hell Gate N)** | `S` (`NYH1924_6`) | Unspec (`U`) | `040°` / `220°` | `0` / `-10` | **`1.02`** / `0.60` | `1.63 m/s` (`3.17 kt`) | `2.14 m/s` (`4.16 kt`) | `-1.23 m/s` (`-2.39 kt`) | `-1.52 m/s` (`-2.95 kt`) |
| **`ACT3431` (bin 1)** | **Manhattan, off E 31st St (East River)** | `S` (`NYH1924_6`) | Unspec (`U`) | `000°` / `175°` | `-13` / `+37` | `0.45` / `0.50` | `0.72 m/s` (`1.40 kt`) | `0.95 m/s` (`1.84 kt`) | `-1.03 m/s` (`-1.99 kt`) | `-1.27 m/s` (`-2.46 kt`) |
| **`NYH1926` (bin 5)** | **Harlem River, south end** | `H` (self) | `1.52 m` (B) | `210°` / `033°` | `0` / `0` | `1.00` / `1.00` | `0.96 m/s` (`1.87 kt`) | `1.19 m/s` (`2.31 kt`) | `-0.94 m/s` (`-1.83 kt`) | `-1.15 m/s` (`-2.24 kt`) |

* **Key Hydrodynamic Asymmetries Around Roosevelt Island:**
  * During **ebb** (flowing southwest from Hell Gate toward Lower East River), the current rounds Hallets Point and jets preferentially into the **northern West Channel (`ACT3406`, off 75th St)** with an amplitude multiplier of `1.12×` Hell Gate bin 6 (`2.30 m/s` / `4.47 kt` mean ebb, **`2.83 m/s` / `5.51 kt` peak ebb**), whereas the **East Channel (`ACT3411`, `ACT3426`)** is partially shadowed on ebb by Hallets Point (`0.81×` and `0.62×` multipliers, `1.57–2.05 m/s` peak ebb).
  * During **flood** (flowing northeast toward Hell Gate), the flow enters both channels briskly, peaking at **`1.08×` (`2.27 m/s` / `4.41 kt`)** at `ACT3416` (mid-West Channel off 67th St) and **`1.05×` (`2.21 m/s` / `4.29 kt`)** at `ACT3411` (northern East Channel off 36th Ave / Astoria landing), while decelerating to `0.60×` (`1.26 m/s` / `2.45 kt`) at `ACT3406` in the lee of the northern West Channel exit.
  * These values agree closely with *U.S. Coast Pilot 2* (Chapter 11, East River), which states that normal maximum currents in Hell Gate reach `3.5 to 4.5+ kt` (`1.8 to 2.3+ m/s`) on flood and `4.5 to 5.2+ kt` (`2.3 to 2.7+ m/s`) on spring ebb, with `2 to 4+ kt` (`1.0 to 2.1+ m/s`) in the channels flanking Roosevelt Island.

---

## 3. Tested Access Recipes on Scenario Day `2025-07-14`

All recipes below were executed on `2026-10-24` and their outputs saved in `artifacts/env-research/`.

### Recipe 1: NOAA CO-OPS 6-Minute Harmonic & Subordinate `MAX_SLACK` Predictions (`2025-07-14`)

* **Harmonic 6-minute query (`NYH1924` Hell Gate, bin 9 at `1.83 m` depth):**
  ```text
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=NYH1924&bin=9&product=currents_predictions&begin_date=20250714&end_date=20250714&interval=6&units=metric&time_zone=gmt&format=json
  ```
  * **Verified Response Summary (`artifacts/env-research/nyh1924_bin9_20250714_6min.json`):** Returns HTTP `200` (`42,812 B`), `240` 6-minute records (`00:00` to `23:54` UTC) with `Velocity_Major` in `cm/s`, `meanFloodDir = 45°`, `meanEbbDir = 241°`, `Depth = "1.8m"`. On `2025-07-14`, `Velocity_Major` ranges from **`-229.0 cm/s` (`-2.29 m/s` / `-4.45 kt` ebb at `07:54` UTC)** to **`+188.0 cm/s` (`+1.88 m/s` / `+3.65 kt` flood at `14:06` UTC)**.

* **Subordinate `MAX_SLACK` query (`ACT3406` Roosevelt Island West Channel off 75th St, bin 1):**
  ```text
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=ACT3406&bin=1&product=currents_predictions&begin_date=20250714&end_date=20250714&interval=MAX_SLACK&units=metric&time_zone=gmt&format=json
  ```
  * **Verified Response Summary (`artifacts/env-research/act3406_bin1_20250714_maxslack.json`):** Returns HTTP `200` (`1,659 B`), `8` extrema/slack events on `2025-07-14` (`meanFloodDir = 37°`, `meanEbbDir = 215°`):
    * `01:20 UTC`: `flood`, `Velocity_Major = +125.6 cm/s` (`+1.26 m/s`)
    * `04:46 UTC`: `slack`, `Velocity_Major = 0.0 cm/s`
    * `08:06 UTC`: `ebb`, `Velocity_Major = -238.9 cm/s` (**`-2.39 m/s` / `-4.64 kt`**)
    * `11:20 UTC`: `slack`, `Velocity_Major = 0.0 cm/s`
    * `13:58 UTC`: `flood`, `Velocity_Major = +106.8 cm/s` (`+1.07 m/s`)
    * `17:04 UTC`: `slack`, `Velocity_Major = 0.0 cm/s`
    * `20:26 UTC`: `ebb`, `Velocity_Major = -222.8 cm/s` (`-2.23 m/s` / `-4.33 kt`)
    * `23:44 UTC`: `slack`, `Velocity_Major = 0.0 cm/s`

### Recipe 2: NOAA NYOFS Gridded & Station Nowcast NetCDFs (`2025-07-14`)

* **Coarse-Grid 3D Fields (`nyofs`):**
  ```text
  https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/2025/07/14/nyofs.t05z.20250714.fields.nowcast.nc
  ```
  * **Verified Response Summary (`artifacts/env-research/nyofs.t05z.20250714.fields.nowcast.nc`, `5,793,796 B`):** NetCDF3 classic format; dimensions `nx=134, ny=73, sigma=7, time=6` (`2025-07-13 23:00` to `2025-07-14 04:00` UTC). Contains `u, v, zeta, depth, mask, angle, air_u, air_v, Pair`. Peak surface speed across all 4 cycles on `2025-07-14` in the Hell Gate cell `(i=23, j=58)` is `1.62 m/s` (flood) and `1.44 m/s` (ebb).

* **Fine-Grid 3D Fields (`nyofs_fg`):**
  ```text
  https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/2025/07/14/nyofs_fg.t05z.20250714.fields.nowcast.nc
  ```
  * **Verified Response Summary (`artifacts/env-research/nyofs_fg.t05z.20250714.fields.nowcast.nc`, `2,986,532 B`):** Dimensions `nx=40, ny=126, sigma=7, time=6`. Covers Newark Bay, Kill Van Kull, and western Upper Bay (`lat 40.6310..40.6822, lon -74.1981..-74.0243`). Peak surface current in Kill Van Kull during `t05z` is `1.06 m/s`.

* **6-Minute Station Nowcast (`nyofs` & `nyofs_fg`):**
  ```text
  https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/2025/07/14/nyofs.t05z.20250714.stations.nowcast.nc
  https://noaa-nos-ofs-pds.s3.amazonaws.com/nyofs/netcdf/2025/07/14/nyofs_fg.t05z.20250714.stations.nowcast.nc
  ```
  * **Verified Response Summary (`269,568 B` and `121,664 B`):** Dimensions `time=61` (`6-minute` step), `nstation=45` (`nyofs`) / `20` (`nyofs_fg`), `sigma=7`. Unlike `fields.nowcast.nc`, these files ARE present for all `28/28` scenario days including `2024-10-07..13`.

### Recipe 3: NOAA STOFS-3D-Atlantic Station Profile & Mesh Field NetCDFs (`2025-07-14`)

* **6-Minute 3D Station Profiles (`2.59 MB`):**
  ```text
  https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl.20250714/stofs_3d_atl.t12z.ncast.station.profile.nc
  ```
  * **Verified Response Summary (`artifacts/env-research/stofs_ncast_profile_20250714.nc`, `2,586,372 B`):** NetCDF4; dimensions `time=240` (`6-minute` steps from `2025-07-13 12:06:00` to `2025-07-14 12:00:00` UTC), `nstation=291`, `nvrt=49` vertical layers. Includes `u, v, temp, salt, zeta` (`datum = "xGEOID20B"`), `wind_u, wind_v` at `8` NY Harbor station locations (`NYH1927, NYH1919, NYH1918, NYH1915, NYH1914, n05010, n03020, 8518750`).

* **Unstructured Mesh Coordinates & Bathymetry (`maxele.nc` / `out2d`):**
  ```text
  https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl.20250714/stofs_3d_atl.t12z.fields.cwl.maxele.nc
  https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl.20250714/stofs_3d_atl.t12z.fields.out2d_n001_012.nc
  ```
  * **Verified Response Summary (`artifacts/env-research/stofs3d_v2.1_harbor_summary.json`):** `nSCHISM_hgrid_node = 2,926,236`, `nSCHISM_hgrid_face = 5,654,157`. Inside `lat 40.60..40.83, lon -74.12..-73.80`, `8,312` nodes (`3,763` wet nodes with `depth > 0`, up to `38.68 m` depth at The Narrows).

### Recipe 4: MARACOOS 1 km HF Radar ERDDAP Query (`2025-07-14`)

* **ERDDAP JSON Query:**
  ```text
  https://hfr.marine.rutgers.edu/erddap/griddap/realtime_maracoos_1km_totals_qartod.json?u[(2025-07-14T00:00:00Z):1:(2025-07-14T23:00:00Z)][(40.45):1:(40.85)][(-74.20):1:(-73.75)],v[(2025-07-14T00:00:00Z):1:(2025-07-14T23:00:00Z)][(40.45):1:(40.85)][(-74.20):1:(-73.75)]
  ```
  * **Verified Response Summary (`artifacts/env-research/hfr_1km_20250714_harbor.json`):** Returns `45,264` grid-hour rows across `lat 40.45..40.85`. A total of `1,486` rows have non-null `u, v`, all located in Lower Bay / Raritan Bay (`lat 40.45698°N to 40.59698°N`); **`0` valid points exist north of `40.59698°N`**.

---

## 4. Conflicts Between Sources

| Topic / Location | Source A | Source B | Nature of Conflict | Resolution for FerrySim |
| :--- | :--- | :--- | :--- | :--- |
| **Hell Gate (`NYH1924`) Peak Ebb Speed on `2025-07-14`** | **NOAA CO-OPS `NYH1924` (bin 9 at `1.83 m`):** `-2.29 m/s` (`-4.45 kt`) ebb, `+1.88 m/s` (`+3.65 kt`) flood (`nyh1924_bin9_20250714_6min.json`) | **NOAA NYOFS Coarse Grid (`i=23, j=58`, `k=0` surface):** `1.44 m/s` (`2.80 kt`) ebb, `1.62 m/s` (`3.15 kt`) flood (`nyofs.t*z.20250714.fields.nowcast.nc`) | NYOFS underpredicts peak ebb speed in Hell Gate by `0.85 m/s` (`37%`) because its `712 m` coarse grid smooths the `~220 m` Hallets Point constriction. | Trust **CO-OPS `NYH1924` ADCP harmonic predictions** (and *Coast Pilot 2*), which are derived directly from in-situ ADCP deployments in Hell Gate. |
| **Roosevelt Island Channel Geometry (`lat 40.75..40.775°N`)** | **NOAA CO-OPS (`ACT3406..3426`) & STOFS-3D (`v2.1`):** Two distinct navigable channels (West and East Channels) with different flood/ebb directions and speed multipliers | **NOAA NYOFS Coarse Grid (`nyofs`):** Single `1-cell-wide` wet column (`j=58`, `~640 m` spacing) with land (`mask=0`) on both sides (`j=57, 59`) | NYOFS's `134×73` grid cannot resolve the `~200 m`-wide channels on either side of Roosevelt Island. | Never use raw NYOFS coarse-grid geometry around Roosevelt Island; use **ENC/CUDEM channel polygons** with CO-OPS West/East Channel subordinate stations (`ACT3406..3426`) or STOFS-3D wet nodes. |
| **NYOFS Archive Completeness in October 2024** | **NOAA S3 (`s3://noaa-nos-ofs-pds/nyofs/netcdf/202410/`):** `stations.nowcast.nc` present for `2024-10-01..31`, but `fields.nowcast.nc` missing for `2024-10-01..19` | **Existing `docs/research/harbor-environment.md` (Claim 4):** Assumed NYOFS S3 NetCDFs could be fetched uniformly for historical scenario days | `2024-10-07..13` (`7` of `28` scenario days) have no `fields.nowcast.nc` files in `s3://noaa-nos-ofs-pds/nyofs/`. | Use **CO-OPS harmonic + subordinate predictions** (and/or `stations.nowcast.nc` / clipped `STOFS-3D-Atlantic`) so all `28/28` scenario days are covered identically. |
| **Vertical Datum of Model Water Levels (`zeta`)** | **NOAA NYOFS (`nyofs`):** `zeta` is referenced to local model `MSL` (requires `nyofs_vdatums.nc` `navd88_msl` array to convert to `NAVD88`) | **NOAA STOFS-3D-Atlantic (`v2.1`):** `zeta` / `elevation` is referenced to `xGEOID20B` (`stofs_ncast_profile_20250714.nc`) | Model free-surface heights are not natively in `NAVD88` or `MLLW`. | Use CO-OPS `8518750` (The Battery) and `8516945` (Kings Point) verified 6-minute `NAVD88` water-level observations (see `docs/research/environment-wind-and-water.md`). |

---

## 5. What Could Not Be Verified / Unverified Leads

1. **Stevens Institute NYHOPS Historical Archive (`sECOM`):** While NYHOPS (`https://hudson.dl.stevens-tech.edu/maritimeforecast/`) runs a high-resolution (`~25–100 m`) inner-harbor mesh, public HTTP download access (`PRESENT/data.shtml`) returned `HTTP 403 Forbidden` and both Stevens THREDDS URLs (`hudson` and `colossus`) failed (`HTTP 404` / connection timeout) on `2026-10-24`. Whether Stevens provides an alternative authenticated academic rsync/FTP mirror could not be verified without contacting Stevens staff.
2. **Sub-Grid Eddy Shedding & Boils at Hell Gate / Mill Rock:** Neither CO-OPS 1D harmonic/subordinate station predictions nor `80–100 m` hydrostatic models (STOFS-3D-Atlantic, NYOFS) explicitly resolve transient `10–30 m` horizontal shear eddies ("Pot Cove", "Hog Back" eddies, and vertical boils over subway tunnel blankets) described qualitatively in *U.S. Coast Pilot 2*. If Wave 2 desires localized yaw-inducing eddy perturbations in Hell Gate, they must be parameterized synthetically from the shear gradient $\nabla \times \mathbf{u}$ near Hallets Point and Mill Rock rather than read directly from a NOAA dataset.
3. **NOAA NYOFS2 Operational Cutover Date:** NOAA CO-OPS's models page mentions an FVCOM-based `NYOFS2` upgrade in development, but no public S3 prefix, grid file, or retrospective hindcast for `2024–2025` exists on `s3://noaa-nos-ofs-pds/` as of `2026-10-24`.

---

## 6. Primary-Source Citations (`Accessed 2026-10-24`)

1. **NOAA NOS Open Data Dissemination (NODD) — NYOFS S3 Bucket:** `https://noaa-nos-ofs-pds.s3.amazonaws.com/` (specifically `nyofs/netcdf/202410/`, `nyofs/netcdf/2025/01/`, `nyofs/netcdf/2025/04/`, `nyofs/netcdf/2025/07/14/nyofs.t05z.20250714.fields.nowcast.nc`, `nyofs_fg.t05z.20250714.fields.nowcast.nc`, `nyofs.t05z.20250714.stations.nowcast.nc`, `nyofs_fg.t05z.20250714.stations.nowcast.nc`, and `nyofs_vdatums.nc`, accessed `2026-10-24`).
2. **NOAA Technical Report NOS CS 12 (2001):** Wei, E., & Sun, L. C., *Development of the New York and New Jersey Harbor Operational Forecast System (NYOFS)*, NOAA/NOS Coast Survey Development Laboratory, `https://tidesandcurrents.noaa.gov/ofs/publications/CS_Techrpt_012_Dev_NYOFS_2001.pdf` (accessed `2026-10-24`).
3. **NOAA Technical Report NOS CO-OPS 037 (2002):** Chu, P., et al., *New York / New Jersey Operational Forecast System (NYOFS)*, NOAA/NOS Center for Operational Oceanographic Products and Services, `https://tidesandcurrents.noaa.gov/ofs/publications/CO-OPS_Techrpt_037_NYOFS_2002.pdf` (accessed `2026-10-24`).
4. **NOAA CO-OPS Operational Forecast Systems Portal & THREDDS Catalog:** `https://tidesandcurrents.noaa.gov/ofs/nyofs/nyofs.html`, `https://tidesandcurrents.noaa.gov/models.html`, and `https://opendap.co-ops.nos.noaa.gov/thredds/catalog/NOAA/NYOFS/MODELS/catalog.xml` (accessed `2026-10-24`).
5. **NOAA STOFS-3D-Atlantic S3 Bucket & Documentation:** `https://noaa-nos-stofs3d-pds.s3.amazonaws.com/README_Atl.html`, `https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl_staout_ncast_dflow.in`, `https://noaa-nos-stofs3d-pds.s3.amazonaws.com/STOFS-3D-Atl/stofs_3d_atl.20250714/stofs_3d_atl.t12z.ncast.station.profile.nc`, and `.../stofs_3d_atl.t12z.fields.cwl.maxele.nc` (accessed `2026-10-24`).
6. **NOAA CO-OPS Metadata API (MDAPI) — Current Prediction Stations & Offsets:** `https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets,harcon&units=metric` (accessed `2026-10-24`).
7. **NOAA CO-OPS Data API — Current Predictions (`NYH1924` & `ACT3406`):** `https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=NYH1924&bin=9&product=currents_predictions&begin_date=20250714&end_date=20250714&interval=6&units=metric&time_zone=gmt&format=json` and `https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=ACT3406&bin=1&product=currents_predictions&begin_date=20250714&end_date=20250714&interval=MAX_SLACK&units=metric&time_zone=gmt&format=json` (accessed `2026-10-24`).
8. **MARACOOS / IOOS HF Radar ERDDAP Server (Rutgers University):** `https://hfr.marine.rutgers.edu/erddap/info/realtime_maracoos_1km_totals_qartod/index.json`, `https://hfr.marine.rutgers.edu/erddap/info/maracoos_2km_totals_oi_v2/index.json`, and `https://hfr.marine.rutgers.edu/erddap/griddap/realtime_maracoos_1km_totals_qartod.json` (accessed `2026-10-24`).
9. **Stevens Institute of Technology — NYHOPS Maritime Forecast Portal:** `https://hudson.dl.stevens-tech.edu/maritimeforecast/`, `https://hudson.dl.stevens-tech.edu/maritimeforecast/maincontrol.shtml`, and `https://hudson.dl.stevens-tech.edu/maritimeforecast/info.shtml` (accessed `2026-10-24`).

---

## 7. Changelog (`2026-10-25` — M3 Wave 1 Review Fixes)

* **Finding 2 (Major — Facts vs. Proposals in Current Modulation & Landing Resolution):**
  * Separated the channel-constrained thalweg interpolation recommendation (§1.3 Step 3 and Executive Summary) from the lateral depth-scaling exponent ($\gamma = 1/6$, cap $c_{\max} = 1.15$) and shoreline wall-decay length scale ($L_{\text{wall}} \approx 15\text{–}25\text{ m}$), which are now explicitly labeled **proposed, to be calibrated** rather than established Manning-Strickler physical results.
  * Specified three calibration/validation protocols for $(\gamma, c_{\max}, L_{\text{wall}})$: leave-one-out cross-validation against held-out CO-OPS current stations (`NYH1921`, `ACT3431`, `ACT3416`, `ACT3426`, `NYH1918`), cross-channel profile comparison against STOFS-3D-Atlantic (`v2.1`) wet nodes, and crew handling observations / AIS crab-angle checks at named landings (`Pier 11`, `Wallabout`, `East 34th Street`, `Hunters Point South`, `Roosevelt Island`, `Astoria`).
  * Updated the §1.6 comparison table (`Representation Near Landings / Ferry Slips` and `Resolution in Lower East River`) to state that the `10–30 m` evaluation grid is a **modelling choice** and **not validated hydrodynamic resolution**, removing unsupported claims of high landing resolution.
* **Finding 8 (Minor — MARACOOS HF Radar License Terms):**
  * Replaced `"Public / CC-BY (IOOS)"` in §1.6 and added §1.4 license documentation quoting the dataset's actual `NC_GLOBAL.license` and `NC_GLOBAL.acknowledgement` attributes from `artifacts/env-research/hfr_1km_info.json` (`https://hfr.marine.rutgers.edu/erddap/info/realtime_maracoos_1km_totals_qartod/index.json`, accessed `2026-10-24`), noting that no Creative Commons (`CC-BY`) license is granted in the source metadata.
* **Finding 9 & Integration Cleanups (ADCP Bin Depth Reference Levels & Whitespace):**
  * Documented the CO-OPS ADCP bin depth reference levels (`B` = below water surface, `S` = above seabed/bottom, `U` = unspecified) in §1.6 and recorded them in the dedicated `sensor_height_reference` column of `docs/research/environment-stations.csv`.
  * Replaced trailing double-space line breaks in the document header with a Markdown list so `git diff --cached --check` passes cleanly.
