# New York Harbor Water-Level, Wind-Field, and Station Inventory Research (`environment-wind-and-water.md`)

* **Research date:** `2026-10-24` (updated `2026-10-25` for M3 Wave 1 review fixes)
* **Target scenario days (28 days):** `2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`
* **Harbor bounding box:** `lat 40.60°N to 40.83°N`, `lon -74.12°E to -73.80°E`
* **Companion station inventory:** `docs/research/environment-stations.csv` (`79` in-box station records, `17` columns)
* **Local sample artifacts:** `artifacts/env-research/`

---

## Executive Summary & Recommendations for Wave 2

### Water-Level Architecture Recommendation
1. **Do NOT use a single water-level gauge (`8518750` The Battery) north of the Williamsburg Bridge:** While a single gauge at **The Battery (`8518750`)** is accurate to within `±0.12 m` across Upper Bay, Kill Van Kull, the Lower Hudson River, and the Lower East River south of Williamsburg Bridge, the tidal wave in the East River is a hydraulic strait connecting two out-of-phase basins (Upper New York Bay and Long Island Sound). Between **The Battery (`8518750`)** and **Kings Point (`8516945`)**, the $M_2$ tide lags by **`+97.5°` (`201.8 minutes` = `3.36 hours`)** and grows in amplitude from **`0.671 m` to `1.150 m` (`+71.4%`)**, creating an **$M_2$ hydraulic head difference of `1.410 m` (`2.82 m` peak-to-peak; `>2.0 m` instantaneous difference on spring tides)** across the East River.
2. **Use Two-Gauge Harmonic + Subtidal Residual Interpolation (`8518750` The Battery + `8516945` Kings Point, plus `8531680` Sandy Hook for Lower Bay/Jamaica Bay):** Both `8518750` and `8516945` have **published `NAVD88` datums** and **100% verified 6-minute coverage (`6,720 / 6,720` rows each)** across all 28 scenario days. Decomposing each gauge into its astronomical tide prediction plus subtidal storm-surge residual ($\eta^{\text{sub}}_g(t) = \eta^{\text{obs}}_{\text{NAVD88},g}(t) - \eta^{\text{pred}}_{\text{NAVD88},g}(t)$) and combining the local CO-OPS harmonic/subordinate tide $\eta^{\text{tide}}_{\text{MSL}}(x,t)$ along the East River (`8517847`, `8518699`, `8518687`, `8518668`, `8517401`, `8518639`, `8517251`) with the local datum offset $\Delta z_{\text{MSL}-\text{NAVD88}}(x) = \text{MSL}(x) - \text{NAVD88}(x)$ (`-0.063 m` at `8518750` The Battery, `-0.039 m` at `8516945` Kings Point) and linearly interpolated subtidal surge reproduces the `3.36 hr` phase progression and consistent `NAVD88` water levels throughout the harbor (anchored at the 9 stations with leveled `NAVD88` datums and interpolated within `±0.02 m` at intermediate subordinate stations; see §1.3 and §6).

### Wind-Field Architecture Recommendation
1. **Use CO-OPS `8530973` (Robbins Reef, NJ) + NWS ASOS `KLGA` (LaGuardia Airport) / CO-OPS `8516945` (Kings Point) as the primary over-water wind forcing:**
   * **CO-OPS `8518750` (The Battery) has NO wind sensor** (`sensors.json` verified on `2026-10-24`).
   * **CO-OPS `8530973` (Robbins Reef, `40.6580°N, -74.0656°E`)** sits on an offshore reef in the middle of Upper New York Bay inside the harbor bounding box and has **`99.94%` (`6,716 / 6,720`) valid 6-minute wind observations** across the 28 scenario days (`100%` after interpolating two 12-minute gaps).
   * **CO-OPS `8516945` (Kings Point, `100.0%` valid 6-minute rows)** and **NWS ASOS `KLGA` (LaGuardia Airport, `40.7795°N, -73.8803°E`, `100%` 1-minute and 5-minute coverage right on the East River shoreline)** anchor the northern East River / Hell Gate wind field, while **CO-OPS `8519532` (Mariners Harbor, `100.0%` valid 6-minute rows)** anchors Kill Van Kull.
   * **Never use `KNYC` (Central Park ASOS) for over-water or ferry-slip wind speed:** In NCEI HOMR (`artifacts/env-research/homr_KNYC.json`, accessed `2026-10-24`), `KNYC` (`42.7 m MSL` ground elevation) is documented verbatim as *"HIGHEST POINT IN CENTRAL PARK. NATURAL ENVIRONMENT SURROUNDED BY SKYSCRAPERS ON ALL SIDES, HIGHEST TO SOUTH. HEAVILY POPULATED. HUDSON RIVER IS LOCATED 1 MILE WEST. EQUIPMENT IS LOCATED ON A STONE BASE IN A FENCED IN ENCLOSURE WITH TREES ON ALL SIDES JUST SOUTH OF BELVEDERE CASTLE"*, with `TREES` obstructions `25 ft` away (`20–54°` elevation angle across azimuths `020–340°`). Because `KNYC` measures an inland tree-enclosed park rather than an over-water or ferry-slip wake, it should not be used to force harbor winds or to calibrate ferry-slip sheltering.
2. **Reduce marine station winds from their over-water (`MSL`) sensor heights to $z_{\text{ref}} = 10\text{ m}$ ($U_{10}$) and optionally apply a `proposed, to be calibrated` directional shoreline/building sheltering multiplier $k_{\text{shelter}}(x, y, \psi_{\text{wind}})$:**
   * Using the over-water (`MSL`) anemometer elevation $z_{\text{MSL}} = z_{\text{sensor,site}} + z_{\text{site,MSL}}$ for marine gauges and the `10.0 m AGL` height for airport ASOS stations (without adding airport ground elevation) with $z_0 = 0.0002\text{ m}$, the neutral logarithmic normalization multipliers $U_{10} / U(z_{\text{eff}})$ are:
     * **`8530973` (Robbins Reef):** $16.46\text{ m above site} + 4.57\text{ m site MSL} = \mathbf{21.03\text{ m MSL}} \to \times \mathbf{0.936}$ (`0.935713`; matches NDBC `robn4` at `21.0 m above sea level` $\to \mathbf{0.936}$ / `0.935828`);
     * **`8516945` (Kings Point):** $8.56\text{ m above site} + 3.43\text{ m site MSL} = \mathbf{11.99\text{ m MSL}} \to \times \mathbf{0.984}$ (`0.983503`; or `1.015` / `1.014580` if evaluated at `8.56 m` above site);
     * **`8519532` (Mariners Harbor):** $46.0\text{ m above site} + 2.57\text{ m site MSL} = \mathbf{48.57\text{ m MSL}} \to \times \mathbf{0.873}$ (`0.872549`; or `0.876` / `0.876391` if evaluated at `46.0 m` above site);
     * **`8531680` (Sandy Hook):** $9.45\text{ m above site} + 2.34\text{ m site MSL} = \mathbf{11.79\text{ m MSL}} \to \times \mathbf{0.985}$ (`0.985009`; or `1.005` / `1.005256` if evaluated at `9.45 m` above site);
     * **`KLGA` / `KJFK` / `KEWR` (ASOS airports):** $\mathbf{10.0\text{ m AGL}} \to \times \mathbf{1.000}$.
   * Any near-shore building wake reduction $k_{\text{shelter}}(x, y, \psi_{\text{wind}})$ along the East River (such as the **proposed, to be calibrated** deficit $\Delta k_{\max} = 0.50$, wake decay scale $c_{\text{wake}} = 7.0$, and along-channel factor $1.00\text{–}1.10$ in §2.2) is a **modelling proposal that must be calibrated** against cross-station wind data and crew observations at named landings rather than treated as an established physical result.

---

## 1. Water Levels Across New York Harbor (Question 3)

### 1.1 Harbor Water-Level Gauges: `NAVD88` Datums & 28-Day 6-Minute Availability

We queried the NOAA CO-OPS Metadata API (`type=waterlevels` and `type=historicwl`, `expand=datums,harcon,tidepredoffsets`) and audited the CO-OPS Data API (`product=water_level&datum=NAVD` and `datum=MLLW`) across all four 7-day scenario windows (`2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`, totaling `28 × 240 = 6,720` expected 6-minute observations per gauge) (**Confidence:** High; `artifacts/env-research/water_level_28days_availability.json` and `water_level_stations_metadata.json`, accessed `2026-10-24`):

| Station ID | Station Name | Location `(lat, lon)` | In Harbor Box? | Active 6-Min Gauge on 28 Days? | Verified 6-Min Rows (`MLLW` / `NAVD88`) | `NAVD88` Datum Available? | `MLLW - NAVD88` (`m`) | `MSL - NAVD88` (`m`) | Mean Range `MN` / Diurnal `GT` (`m`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8518750`** | **The Battery, NY** | `(40.700556, -74.014167)` | **Yes** | **Yes (`100%`)** | **`6,720 / 6,720`** | **Yes** | **`-0.846 m`** | `-0.063 m` | `1.380 m` / `1.542 m` |
| **`8516945`** | **Kings Point, NY** | `(40.810306, -73.764889)` | `3.0 km` E of `-73.80` | **Yes (`100%`)** | **`6,720 / 6,720`** | **Yes** | **`-1.282 m`** | `-0.039 m` | `2.182 m` / `2.456 m` |
| **`8531680`** | **Sandy Hook, NJ** | `(40.466944, -74.009389)` | `14.8 km` S of `40.60` | **Yes (`100%`)** | **`6,720 / 6,720`** | **Yes** | **`-0.858 m`** | `-0.084 m` | `1.431 m` / `1.592 m` |
| **`8519483`** | **Bergen Point West Reach, NY** | `(40.636700, -74.141700)` | `1.8 km` W of `-74.12` | **No (Decommissioned `2024-10-29`)** | **`0 / 6,720`** (HTTP error: *No data was found*) | **No** (`null` in MDAPI) | N/A | N/A | `1.564 m` / `1.737 m` |

* **Datum Conversion Convention (`MLLW` $\leftrightarrow$ `NAVD88`):**
  * At **The Battery (`8518750`)**, station datum elevations (`1983–2001` National Tidal Datum Epoch) are `NAVD88 = 1.848 m`, `MSL = 1.785 m`, and `MLLW = 1.002 m` ([CO-OPS Datums API `8518750`](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8518750/datums.json?units=metric), accessed `2026-10-24`). Thus:
    $$\eta_{\text{NAVD88}} = \eta_{\text{MLLW}} - 0.846\text{ m} \qquad (\text{at The Battery, } 8518750)$$
    $$\eta_{\text{NAVD88}} = \eta_{\text{MLLW}} - 1.282\text{ m} \qquad (\text{at Kings Point, } 8516945)$$
    $$\eta_{\text{NAVD88}} = \eta_{\text{MLLW}} - 0.858\text{ m} \qquad (\text{at Sandy Hook, } 8531680)$$
  * Because `MLLW` is a local tidal datum that drops by `0.436 m` between The Battery (`-0.846 m` relative to `NAVD88`) and Kings Point (`-1.282 m` relative to `NAVD88`) due to the amplified tidal range in Long Island Sound, **all spatial water-level interpolation and CUDEM depth addition (`total_depth = -z_bathy_NAVD88 + eta_NAVD88`) MUST be performed in the geopotential datum `NAVD88`, never in `MLLW`**.

* **Additional Historical Harmonic/Subordinate Stations Inside the Harbor Box with Published `NAVD88` Datums:**
  * Out of the `38` CO-OPS water-level / tide-prediction stations inside `lat 40.60..40.83, lon -74.12..-73.80` (`docs/research/environment-stations.csv`), **`6` historical stations** (in addition to `8518750`) have leveled `NAVD88` datums (`artifacts/env-research/water_level_stations_metadata.json`, accessed `2026-10-24`):
    1. `8518699` (**Williamsburg Bridge**, `40.71170°N, -73.96830°E`, Harmonic `R`): `MLLW - NAVD88 = -0.793 m`, `MSL - NAVD88 = -0.045 m`.
    2. `8518687` (**Queensboro Bridge**, `40.75830°N, -73.95830°E`, Harmonic `R`): `MLLW - NAVD88 = -0.794 m`, `MSL - NAVD88 = -0.061 m`.
    3. `8518639` (**Port Morris**, `40.80170°N, -73.90670°E`, Harmonic `R`): `MLLW - NAVD88 = -1.073 m`, `MSL - NAVD88 = -0.019 m`.
    4. `8517201` (**North Channel, Jamaica Bay**, `40.64500°N, -73.83670°E`, Harmonic `R`): `MLLW - NAVD88 = -1.044 m`, `MSL - NAVD88 = -0.104 m`.
    5. `8519050` (**USCG Station NY, The Narrows**, `40.61200°N, -74.05986°E`, Harmonic `R`): `MLLW - NAVD88 = -0.826 m`, `MSL - NAVD88 = -0.056 m`.
    6. `8530772` (**Kearny Point, Hackensack River**, `40.72830°N, -74.10330°E`, Subordinate `S`): `MLLW - NAVD88 = -0.940 m`, `MSL - NAVD88 = -0.049 m`.

---

### 1.2 Spatial Phase Lag and Amplitude Progression Up the East River into Long Island Sound

We extracted the published 37-constituent harmonic constants (`harcon.json?units=metric`) and subordinate tide prediction offsets (`tidepredoffsets.json`) for all `19` stations along the 31 km waterway from The Battery (`8518750`) through the Lower East River, Roosevelt Island, Hell Gate, and Upper East River to Kings Point (`8516945`) (`artifacts/env-research/east_river_harmonic_progression.json`, accessed `2026-10-24`):

| Station ID | Station Name | Along-River Dist from Battery (`km`) | Type (`Ref`) | $M_2$ Amp (`m`) | $M_2$ Phase $\kappa_{\text{GMT}}$ (`°`) | $M_2$ Phase Lag vs Battery (`min`) | Subordinate High / Low Time Offset (`min`) | Subordinate High / Low Amp Ratio | `MLLW - NAVD88` (`m`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8518750`** | **The Battery, NY** | `0.0 km` | `R` (self) | **`0.671 m`** | **`18.2°`** | **`0.0 min`** | `0` / `0` | `1.00` / `1.00` | `-0.846 m` |
| **`8517847`** | **Brooklyn Bridge** | `2.0 km` | `R` (self) | `0.653 m` | `24.0°` | `+12.0 min` | — | — | N/A |
| **`8517732`** | **Wallabout Bay (Navy Yard)** | `4.1 km` | `S` (`8518750`) | — | — | — | `+32` / `+22` | `0.94` / `1.05` | N/A |
| **`8518699`** | **Williamsburg Bridge** | `5.0 km` | `R` (self) | **`0.578 m`** | **`36.8°`** | **`+38.5 min`** | — | — | `-0.793 m` |
| **`8518695`** | **East 41st Street Pier** | `8.8 km` | `S` (`8518750`) | — | — | — | `+63` / `+46` | `0.95` / `1.09` | N/A |
| **`8517673`** | **Hunters Point (Newtown Cr.)** | `8.5 km` | `S` (`8518750`) | — | — | — | `+82` / `+56` | `0.89` / `0.90` | N/A |
| **`8518687`** | **Queensboro Bridge (Roosevelt Is. S)** | `10.8 km` | `R` (self) | **`0.574 m`** | **`61.4°`** | **`+89.4 min`** | — | — | `-0.794 m` |
| **`8518668`** | **Horns Hook (E 90th St / Hell Gate S)** | `13.5 km` | `R` (self) | **`0.677 m`** | **`93.8°`** | **`+156.5 min`** | — | — | N/A |
| **`8517401`** | **Wards Island (Hell Gate N)** | `14.8 km` | `S` (`8518750`) | — | — | — | **`+178` / `+225`** | **`1.33` / `1.59`** | N/A |
| **`8518643`** | **Randalls Island (Bronx Kill / Harlem R.)** | `16.0 km` | `R` (self) | `0.652 m` | `78.4°` | `+124.5 min` | — | — | N/A |
| **`8518639`** | **Port Morris (East River N of Hell Gate)** | `17.5 km` | `R` (self) | **`1.006 m`** | **`113.4°`** | **`+197.1 min`** | — | — | `-1.073 m` |
| **`8518635`** | **North Brother Island** | `18.3 km` | `S` (`8516945`) | — | — | — | `+18` / `+18` (vs KP) | `0.93` / `1.11` | N/A |
| **`8518621`** | **Hunts Point** | `20.2 km` | `S` (`8516945`) | — | — | — | `+12` / `+10` (vs KP) | `0.97` / `1.07` | N/A |
| **`8517251`** | **Worlds Fair Marina (Flushing Bay)** | `21.5 km` | `R` (self) | **`1.093 m`** | **`117.1°`** | **`+204.7 min`** | — | — | N/A |
| **`8517276`** | **College Point** | `22.8 km` | `S` (`8516945`) | `1.090 m` | `118.2°` | `+207.0 min` | `+17` / `+16` (vs KP) | `0.95` / `1.04` | N/A |
| **`8517125`** | **Whitestone** | `26.5 km` | `S` (`8516945`) | — | — | — | `+7` / `+9` (vs KP) | `1.00` / `1.04` | N/A |
| **`8516945`** | **Kings Point, NY (Long Island Sound)** | `31.0 km` | `R` (self) | **`1.150 m`** | **`115.7°`** | **`+201.8 min`** | `0` / `0` (KP ref) | `1.00` / `1.00` | `-1.282 m` |

* **Physical Interpretation of the East River Wave Progression:**
  1. **Destructive Interference Minimum at Williamsburg–Queensboro Bridge (`5–11 km`):** As the Upper Bay tide ($M_2 = 0.671\text{ m}, \kappa = 18.2^\circ$) propagates northward into the Lower East River, it meets the out-of-phase Long Island Sound tide ($M_2 = 1.150\text{ m}, \kappa = 115.7^\circ$) propagating southward through Hell Gate. This creates an **amphidromic-like amplitude minimum** between Williamsburg Bridge ($M_2 = 0.578\text{ m}$) and Queensboro Bridge ($M_2 = 0.574\text{ m}$), where the tidal range is `15%` smaller than at The Battery.
  2. **Rapid Phase Jump & Steep Water-Surface Slope Across Roosevelt Island & Hell Gate (`10.8–17.5 km`):** Between **Queensboro Bridge (`8518687`, `10.8 km`)** and **Port Morris (`8518639`, `17.5 km`)**, the $M_2$ phase jumps by **`+52.0°` (`107.7 minutes`) over just `6.7 km`**, and the $M_2$ amplitude nearly doubles from `0.574 m` to `1.006 m`.
  3. **Instantaneous Water-Level Difference (`\Delta\eta`):** The complex $M_2$ phasor difference between Kings Point and The Battery is:
     $$\Delta Z_{M_2} = 1.150 e^{-i 115.7^\circ} - 0.671 e^{-i 18.2^\circ} \implies |\Delta Z_{M_2}| = \sqrt{1.150^2 + 0.671^2 - 2(1.150)(0.671)\cos(97.5^\circ)} = \mathbf{1.410\text{ m}}$$
     During spring tides (when $M_2 + S_2 + N_2$ reinforce), the instantaneous water surface at Kings Point / Port Morris differs from The Battery by **`1.8 to 2.1 m` (`2.82 m` mean $M_2$ peak-to-peak swing, `>3.6 m` spring peak-to-peak swing)**.

---

### 1.3 When Is a Single Gauge Good Enough vs. Two-Gauge Interpolation?

* **When a Single Gauge (`8518750` The Battery) IS Good Enough (`|\Delta\eta| \le 0.12\text{ m}`):**
  * **Upper New York Bay, Kill Van Kull, and Lower Hudson River (south of 79th St):** Tide phase lags relative to `8518750` are small (`-18 min` at Constable Hook `8530985`, `-17 min` at St. George `8519112`, `+13 min` at Union City `8530645`, `+31 min` at Edgewater `8530505`), and amplitude ratios are `0.93–1.03`.
  * **Lower East River Slice (The Battery to Wallabout / Williamsburg Bridge, `0–5 km`):** At Brooklyn Bridge (`+12 min`) and Wallabout (`+22 to +32 min`), `8518750` alone has an RMS water-level error of `0.06–0.12 m`.

* **When Two-Gauge Interpolation (`8518750` The Battery + `8516945` Kings Point) IS Mandatory:**
  * **Everywhere in the East River north of Williamsburg Bridge (`>5 km` from The Battery):** At E 34th St (`+63 min`), Hunters Point South (`+82 min`), Queensboro Bridge / Roosevelt Island (`+89 min`), Horns Hook / Hell Gate (`+157 min`), Astoria / Wards Island (`+178 min`), and Port Morris / Soundview / Throgs Neck (`+197 to +207 min`), using `8518750` alone introduces **`0.5 m` to `2.1 m` of vertical water-level error**.

* **Recommended Two-Gauge Spatial Water-Level Formulation (in `NAVD88`):**
  1. For any timestamp $t$ and location $x$ along the harbor waterway, separate the water level $\eta_{\text{NAVD88}}(x, t)$ into its **local astronomical tide** $\eta^{\text{tide}}_{\text{MSL}}(x, t)$ (relative to local Mean Sea Level `MSL`, from the nearest CO-OPS harmonic/subordinate tide station along the channel, e.g., `8518750`, `8517847`, `8518699`, `8518687`, `8518668`, `8518639`, `8517251`, `8516945`), plus the **station-specific MSL-to-NAVD88 datum offset** $\Delta z_{\text{MSL}-\text{NAVD88}}(x) = \text{MSL}(x) - \text{NAVD88}(x)$, plus the **interpolated subtidal storm-surge residual** $\eta^{\text{sub}}(x, t)$:
     $$\eta^{\text{sub}}_g(t) = \eta^{\text{obs}}_{\text{NAVD88}, g}(t) - \left(\eta^{\text{pred}}_{\text{MLLW}, g}(t) + (\text{MLLW}_g - \text{NAVD88}_g)\right) \quad \text{for } g \in \{8518750, 8516945, 8531680\}$$
     $$\eta_{\text{NAVD88}}(x, t) = \eta^{\text{tide}}_{\text{MSL}}(x, t) + \Delta z_{\text{MSL}-\text{NAVD88}}(x) + (1 - w(x))\,\eta^{\text{sub}}_{8518750}(t) + w(x)\,\eta^{\text{sub}}_{8516945}(t)$$
     where:
     * $w(x) \in [0, 1]$ varies smoothly with along-river distance $s$ from $w = 0$ at The Battery ($s = 0\text{ km}$) to $w = 1$ at Kings Point ($s = 31\text{ km}$).
     * $\Delta z_{\text{MSL}-\text{NAVD88}}(x) = \text{MSL}(x) - \text{NAVD88}(x)$ converts the zero-mean astronomical tide $\eta^{\text{tide}}_{\text{MSL}}(x, t)$ from local `MSL` into `NAVD88` (**provenance:** NOAA CO-OPS MDAPI `stations/{id}/datums.json?units=metric`, saved in `artifacts/env-research/water_level_stations_metadata.json`, accessed `2026-10-24`; see §1.1 table):
       * At **`8518750` (The Battery, $s = 0.0\text{ km}$):** $\text{MSL} = 1.785\text{ m}$, $\text{NAVD88} = 1.848\text{ m}$ above station datum $\implies \Delta z_{\text{MSL}-\text{NAVD88}}(8518750) = 1.785 - 1.848 = \mathbf{-0.063\text{ m}}$ (so at zero tide relative to `MSL` and zero subtidal residual, $\eta_{\text{NAVD88}} = -0.063\text{ m}$, not $0\text{ m}$).
       * At **`8518699` (Williamsburg Bridge, $s = 5.0\text{ km}$):** $\Delta z_{\text{MSL}-\text{NAVD88}}(8518699) = \mathbf{-0.045\text{ m}}$.
       * At **`8518687` (Queensboro Bridge, $s = 10.8\text{ km}$):** $\Delta z_{\text{MSL}-\text{NAVD88}}(8518687) = \mathbf{-0.061\text{ m}}$.
       * At **`8518639` (Port Morris, $s = 17.5\text{ km}$):** $\Delta z_{\text{MSL}-\text{NAVD88}}(8518639) = \mathbf{-0.019\text{ m}}$.
       * At **`8516945` (Kings Point, $s = 31.0\text{ km}$):** $\text{MSL} = 3.218\text{ m}$, $\text{NAVD88} = 3.257\text{ m}$ above station datum $\implies \Delta z_{\text{MSL}-\text{NAVD88}}(8516945) = 3.218 - 3.257 = \mathbf{-0.039\text{ m}}$.
       * At **`8531680` (Sandy Hook):** $\Delta z_{\text{MSL}-\text{NAVD88}}(8531680) = \mathbf{-0.084\text{ m}}$.
       * *(Note: If astronomical tide predictions $\eta^{\text{tide}}_{\text{MLLW}}(x,t)$ are fetched relative to `MLLW` instead of `MSL`, replace $\eta^{\text{tide}}_{\text{MSL}}(x,t) + \Delta z_{\text{MSL}-\text{NAVD88}}(x)$ with $\eta^{\text{tide}}_{\text{MLLW}}(x,t) + \Delta z_{\text{MLLW}-\text{NAVD88}}(x)$, where $\Delta z_{\text{MLLW}-\text{NAVD88}} = -0.846\text{ m}$ at `8518750` and $-1.282\text{ m}$ at `8516945`.)*
  2. *Simpler Complex-Transfer Alternative (requiring only the two active gauge series):* Interpolate the demeaned 6-minute gauge series directly using the local station time lag $\Delta t(x)$ and amplitude factor $\alpha(x)$ from the table in §1.2:
     $$\eta_{\text{NAVD88}}(x, t) = \bar{\eta}_{\text{NAVD88}}(x) + (1 - w(x))\,\alpha_{\text{Bat}}(x)\,\tilde{\eta}_{8518750}\!\left(t - \Delta t_{\text{Bat}}(x)\right) + w(x)\,\alpha_{\text{KP}}(x)\,\tilde{\eta}_{8516945}\!\left(t - \Delta t_{\text{KP}}(x)\right)$$
     where $w(x) = 0$ for stations referenced to `8518750` (south of Hell Gate), $w(x) = 1$ for stations referenced to `8516945` (north of Hell Gate), and $w(x)$ transitions linearly across the `4 km` Hell Gate reach (`Horns Hook` to `Port Morris`).

---

## 2. Wind Fields Across New York Harbor (Question 4)

### 2.1 Marine & Airport Wind Stations: Sensor Heights, Reference Levels, Products, and 28-Day Availability

We queried CO-OPS MDAPI (`type=met`, `stations/{id}/sensors.json`), NDBC station metadata pages (`https://www.ndbc.noaa.gov/station_page.php?station=...`), and NCEI HOMR / ASOS / Global Hourly archives across all 28 scenario days (`artifacts/env-research/met_stations_summary.json`, `ndbc_*.html`, `homr_*.json`, `asos_5min_KLGA_202507_sample.txt`, accessed `2026-10-24`):

| Station ID (NDBC / ICAO) | Station Name | Location `(lat, lon)` | In Harbor Box? | Network | Sensor Height Above Site / Ground (`z_sensor`) | Sensor Height Reference Level | Site / Ground Elevation (`z_site`) & Reference Level | Effective Normalization Height `z_eff` & Reference Level | Sampling Interval & Products | Verified 28-Day Wind Availability | Recommended Role in FerrySim |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8530973` (`robn4`)** | **Robbins Reef, NJ** | `(40.658028, -74.065583)` | **Yes** | CO-OPS / NDBC | **`16.46 m`** (CO-OPS `C1`) / **`21.0 m`** (NDBC) | `Site Elevation` (CO-OPS) / `sea level` (NDBC) | **`4.57 m` `MSL`** (CO-OPS `site`) / **`0.0 m` (`sea level`)** (NDBC) | **`21.03 m` `MSL`** (CO-OPS) / **`21.00 m` `MSL`** (NDBC) | `6-min` `wind` (speed, dir, gust), `air_temperature`, `air_pressure` | **`28/28` days (`6,716 / 6,720` valid rows, `99.94%`)** | **Primary open-water gauge** for Upper Bay, Hudson mouth & Lower East River |
| **`8518750` (`batn6`)** | **The Battery, NY** | `(40.700556, -74.014167)` | **Yes** | CO-OPS / NDBC | **None (NO wind sensor)** | `N/A` (no `C1` sensor) | **`1.96 m` `MSL`** (CO-OPS `site`) / `2.0 m` `MSL` (NDBC) | **`N/A`** (no wind sensor) | `6-min` `water_level`, `air_temperature`, `water_temperature`, `air_pressure` | **`0/28` days for wind** (no anemometer installed) | Water level, air temp, pressure & water temp only |
| **`KLGA` (`USW00014732`)** | **LaGuardia Airport, NY** | `(40.779450, -73.880270)` | **Yes** | NWS ASOS / NCEI | **`10.0 m`** (`33 ft` standard ASOS ultrasonic) | **`AGL`** (Above Ground Level) | **`3.0 m` `MSL`** (`10 ft` HOMR `GROUND`; `7.0 m` `AIRPORT`) | **`10.00 m` `AGL`** (do NOT add ground elevation) | `1-min` (`asos-1min-pg1`), `5-min` (`asos-5min`), `ISD` hourly | **`28/28` days (`100%`)** in 1-min, 5-min, and ISD archives | **Primary East River / Hell Gate / Astoria gauge** (coastal runway on Bowery Bay) |
| **`KNYC` (`USW00094728`)** | **NY City Central Park, NY** | `(40.778980, -73.969250)` | **Yes** | NWS ASOS / NCEI | **`10.0 m`** (`33 ft` standard ASOS ultrasonic) | **`AGL`** (Above Ground Level) | **`42.7 m` `MSL`** (`140 ft` HOMR `GROUND`) | **`10.00 m` `AGL`** (do NOT add ground elevation) | `1-min` (`asos-1min-pg1`), `5-min` (`asos-5min`), `ISD` hourly | **`28/28` days (`100%`)** in 1-min, 5-min, and ISD archives | **Inland urban-park station — do NOT use for over-water or ferry-slip wind** (trees at `25 ft` in HOMR) |
| **`8516945` (`kpnn6`)** | **Kings Point, NY** | `(40.810306, -73.764889)` | `3.0 km` E of `-73.80` | CO-OPS / NDBC | **`8.56 m`** (CO-OPS `C1`) / `8.6 m` (NDBC) | `Site Elevation` (CO-OPS & NDBC) | **`3.43 m` `MSL`** (CO-OPS `site`) / `2.1 m` `MSL` (NDBC) | **`11.99 m` `MSL`** (CO-OPS) / `10.70 m` `MSL` (NDBC) | `6-min` `wind`, `water_level`, `air_temperature`, `air_pressure` | **`28/28` days (`6,720 / 6,720` valid rows, `100.0%`)** | **Primary Upper East River / Sound entrance marine gauge** |
| **`8519532` (`mrhn6`)** | **Mariners Harbor, NY** | `(40.640083, -74.161667)` | `3.5 km` W of `-74.12` | CO-OPS / NDBC | **`46.0 m`** (CO-OPS `C1` & NDBC) | `Site Elevation` (CO-OPS & NDBC) | **`2.57 m` `MSL`** (CO-OPS `site`) / `2.6 m` `MSL` (NDBC) | **`48.57 m` `MSL`** (CO-OPS) / `48.60 m` `MSL` (NDBC) | `6-min` `wind`, `air_temperature`, `air_pressure` | **`28/28` days (`6,720 / 6,720` valid rows, `100.0%`)** | **Primary Kill Van Kull / Newark Bay gauge** (after height reduction to `10 m`) |
| **`8531680` (`sdhn4`)** | **Sandy Hook, NJ** | `(40.466944, -74.009389)` | `14.8 km` S of `40.60` | CO-OPS / NDBC | **`9.45 m`** (CO-OPS `C1`) / **`5.5 m`** (NDBC) | `Site Elevation` (CO-OPS & NDBC) | **`2.34 m` `MSL`** (CO-OPS `site`) / `1.9 m` `MSL` (NDBC) | **`11.79 m` `MSL`** (CO-OPS) / `7.40 m` `MSL` (NDBC) | `6-min` `wind`, `water_level`, `air_temperature`, `air_pressure` | **`28/28` days (`6,719 / 6,720` valid rows, `99.99%`)** | **Primary Lower Bay / Narrows / Rockaway boundary gauge** |
| **`KJFK` (`USW00094789`)** | **JFK Intl Airport, NY** | `(40.639150, -73.763900)` | `3.0 km` E of `-73.80` | NWS ASOS / NCEI | **`10.0 m`** (`33 ft` standard ASOS ultrasonic) | **`AGL`** (Above Ground Level) | **`2.7 m` `MSL`** (`9 ft` HOMR `GROUND`) | **`10.00 m` `AGL`** (do NOT add ground elevation) | `1-min`, `5-min`, `ISD` hourly | **`28/28` days (`100%`)** in 1-min, 5-min, and ISD archives | Secondary Jamaica Bay / eastern boundary gauge + visibility/ceiling |
| **`KEWR` (`USW00014734`)** | **Newark Liberty Intl, NJ** | `(40.682750, -74.169270)` | `4.1 km` W of `-74.12` | NWS ASOS / NCEI | **`10.0 m`** (`33 ft` standard ASOS ultrasonic) | **`AGL`** (Above Ground Level) | **`1.9 m` `MSL`** (`6 ft` HOMR `GROUND`; `4.9 m` `AIRPORT`) | **`10.00 m` `AGL`** (do NOT add ground elevation) | `1-min`, `5-min`, `ISD` hourly | **`28/28` days in ISD**; `21/28` days in 1-min/5-min (`2025/07` missing) | Secondary Newark Bay / western boundary gauge |

* **Complete Marine Sensor & Site Reference-Level Audit (`met_stations_summary.json` & `ndbc_*.html`, accessed `2026-10-24`):**
  * In NOAA CO-OPS's `stations/{id}/sensors.json` API, wind (`sensorID = "C1"`) and air temperature (`sensorID = "D1"`) elevations are reported relative to **`refdatum = "Site Elevation"`**, while a separate record (`sensorID = "site"`) gives the pier/deck/lighthouse platform's elevation relative to **`refdatum = "MSL"`**. Converting a marine sensor to height above Mean Sea Level (`MSL`) therefore requires summing $z_{\text{MSL}} = z_{\text{sensor, Site Elevation}} + z_{\text{site, MSL}}$:

| Marine Station | Sensor / Record | CO-OPS Elevation (`m`) | CO-OPS `refdatum` | CO-OPS Site Elevation (`m` `MSL`) | CO-OPS Height Above `MSL` (`m`) | NDBC Station Page Value | NDBC Reference Level | Reconciliation / Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8530973` (`robn4`) Robbins Reef** | `site` (Platform) | `4.57 m` | `MSL` | `4.57 m` | `4.57 m` `MSL` | `sea level` (`0.0 m`) | `mean sea level` | NDBC defines `robn4` site datum directly at sea level (`MSL`). |
| **`8530973` (`robn4`) Robbins Reef** | `C1` (**Wind**) | `16.46 m` | `Site Elevation` | `4.57 m` | **`21.03 m` `MSL`** | **`21 m`** | `above site elevation` (`sea level`) | **Agrees (`21.03 m` vs `21 m` above `MSL`). No conflict.** |
| **`8530973` (`robn4`) Robbins Reef** | `D1` (Air Temp) | `16.18 m` | `Site Elevation` | `4.57 m` | **`20.75 m` `MSL`** | **`20.8 m`** | `above site elevation` (`sea level`) | **Agrees (`20.75 m` vs `20.8 m` above `MSL`).** |
| **`8516945` (`kpnn6`) Kings Point** | `site` (Pier deck) | `3.43 m` | `MSL` | `3.43 m` | `3.43 m` `MSL` | `2.1 m` | `above mean sea level` | `1.33 m` site-elevation difference between CO-OPS (`3.43 m`) and NDBC (`2.1 m`). |
| **`8516945` (`kpnn6`) Kings Point** | `C1` (**Wind**) | `8.56 m` | `Site Elevation` | `3.43 m` | **`11.99 m` `MSL`** | `8.6 m` (`10.7 m` `MSL`) | `above site elevation` (`2.1 m MSL`) | Sensor height above site agrees (`8.56 m` vs `8.6 m`); `MSL` height is `11.99 m` (CO-OPS) vs `10.70 m` (NDBC). |
| **`8516945` (`kpnn6`) Kings Point** | `D1` (Air Temp) | `3.12 m` | `Site Elevation` | `3.43 m` | `6.55 m` `MSL` | `4 m` (`6.1 m` `MSL`) | `above site elevation` (`2.1 m MSL`) | `0.45 m` difference above `MSL`. |
| **`8516945` (`kpnn6`) Kings Point** | `F1` (Barometer) | `5.05 m` | `MSL` | `3.43 m` | `5.05 m` `MSL` | `4.9 m` | `above mean sea level` | Agrees to `0.15 m` (`5.05 m` vs `4.9 m` above `MSL`). |
| **`8516945` (`kpnn6`) Kings Point** | `E1` (Water Temp) | `-1.08 m` | `MLLW` | `3.43 m` | `-2.32 m` `MSL` | — | — | Referenced to `MLLW` (`MLLW - MSL = -1.243 m` at `8516945`). |
| **`8519532` (`mhrn6`) Mariners Harbor** | `site` (Structure) | `2.57 m` | `MSL` | `2.57 m` | `2.57 m` `MSL` | `2.6 m` | `above mean sea level` | **Agrees (`2.57 m` vs `2.6 m` above `MSL`).** |
| **`8519532` (`mhrn6`) Mariners Harbor** | `C1` (**Wind**) | `46.0 m` | `Site Elevation` | `2.57 m` | **`48.57 m` `MSL`** | `46 m` (`48.6 m` `MSL`) | `above site elevation` (`2.6 m MSL`) | **Agrees (`46.0 m` above site; `48.57 m` vs `48.60 m` above `MSL`).** |
| **`8531680` (`sdhn4`) Sandy Hook** | `site` (Pier deck) | `2.34 m` | `MSL` | `2.34 m` | `2.34 m` `MSL` | `1.9 m` | `above mean sea level` | `0.44 m` site-elevation difference (`2.34 m` vs `1.9 m` `MSL`). |
| **`8531680` (`sdhn4`) Sandy Hook** | `C1` (**Wind**) | `9.45 m` | `Site Elevation` | `2.34 m` | **`11.79 m` `MSL`** | `5.5 m` (`7.40 m` `MSL`) | `above site elevation` (`1.9 m MSL`) | Genuine conflict (`9.45 m` vs `5.5 m` above site; `11.79 m` vs `7.40 m` above `MSL`; see §5). |
| **`8531680` (`sdhn4`) Sandy Hook** | `D1` (Air Temp) | `5.18 m` | `Site Elevation` | `2.34 m` | `7.52 m` `MSL` | `5.1 m` (`7.0 m` `MSL`) | `above site elevation` (`1.9 m MSL`) | Sensor height above site agrees (`5.18 m` vs `5.1 m`). |
| **`8531680` (`sdhn4`) Sandy Hook** | `F1` (Barometer) | `6.90 m` | `MSL` | `2.34 m` | `6.90 m` `MSL` | `2.4 m` | `above mean sea level` | `4.5 m` difference in barometer height (`6.9 m` vs `2.4 m` `MSL`). |
| **`8531680` (`sdhn4`) Sandy Hook** | `E1` (Water Temp) | `0.12 m` | `MLLW` | `2.34 m` | `-0.65 m` `MSL` | — | — | Referenced to `MLLW` (`MLLW - MSL = -0.774 m` at `8531680`). |
| **`8518750` (`batn6`) The Battery** | `site` (Pier deck) | `1.96 m` | `MSL` | `1.96 m` | `1.96 m` `MSL` | `2 m` | `above mean sea level` | **Agrees (`1.96 m` vs `2 m` above `MSL`).** |
| **`8518750` (`batn6`) The Battery** | `D1` (Air Temp) | `2.44 m` | `Site Elevation` | `1.96 m` | `4.40 m` `MSL` | `3.9 m` (`5.9 m` `MSL`) | `above site elevation` (`2 m MSL`) | `1.46 m` difference (`2.44 m` vs `3.9 m` above site). |
| **`8518750` (`batn6`) The Battery** | `F1` (Barometer) | `5.17 m` | `MSL` | `1.96 m` | `5.17 m` `MSL` | `4.1 m` | `above mean sea level` | `1.07 m` difference (`5.17 m` vs `4.1 m` above `MSL`). |
| **`8518750` (`batn6`) The Battery** | `E1` (Water Temp) | `-0.68 m` | `MLLW` | `1.96 m` | `-1.46 m` `MSL` | — | — | Referenced to `MLLW` (`MLLW - MSL = -0.783 m` at `8518750`). |
| **`8519483` (`bgnn6`) Bergen Point** | `C1` (Decomm.) | `null` | `null` | `null` | `null` | `9 m` (`12 m` `MSL`) | `above site elevation` (`3 m MSL`) | Decommissioned `2024-10-29`; `0` observations on the 28 days. |

* **Height Normalization to Standard $z_{\text{ref}} = 10.0\text{ m}$ ($U_{10}$) — Formula & Recomputation Table:**
  * Ship aerodynamic load models (e.g., Fossen 2011, *Handbook of Marine Craft Hydrodynamics and Motion Control*, already used in `core/FerrySim.Core`) define wind forces relative to the **10-meter wind speed $U_{10}$**.
  * Using the neutral logarithmic marine boundary layer profile $U(z) = \frac{u_*}{\kappa} \ln(z / z_0)$ with reference height $z_{\text{ref}} = 10.0\text{ m}$ and open-harbor water roughness length $z_0 = 0.0002\text{ m}$:
    $$U_{10} = U(z_{\text{eff}}) \cdot k_{10}(z_{\text{eff}}), \qquad k_{10}(z_{\text{eff}}) = \frac{\ln(z_{\text{ref}} / z_0)}{\ln(z_{\text{eff}} / z_0)} = \frac{\ln(10.0 / 0.0002)}{\ln(z_{\text{eff}} / 0.0002)} = \frac{10.81977828}{\ln(z_{\text{eff}} / 0.0002)}$$
  * **Reference-level rule for $z_{\text{eff}}$:**
    1. **Marine stations (`8530973`, `8516945`, `8519532`, `8531680`):** Because the logarithmic boundary layer develops over the water surface (`MSL`), the primary normalization uses the **over-water height above `MSL`**, $z_{\text{eff}} = z_{\text{sensor, Site Elevation}} + z_{\text{site, MSL}}$. For completeness and transparency, the table below also lists the factor evaluated at the unconverted above-site height $z_{\text{sensor, Site Elevation}}$ alone.
    2. **Airport ASOS stations (`KLGA`, `KJFK`, `KEWR`, `KNYC`):** Because the logarithmic boundary layer at an airfield develops over the local runway/ground surface (`AGL`), $z_{\text{eff}} = z_{\text{sensor, AGL}} = 10.0\text{ m}$. **Never add airport ground elevation (`MSL`) to an `AGL` sensor height.**

| Station & Height Variant | Role / Status | $z_{\text{ref}}$ (`m`) | $z_0$ (`m`) | $z_{\text{sensor}}$ (`m`) & Ref Level | $z_{\text{site}}$ (`m` `MSL`) | Effective Height $z_{\text{eff}}$ (`m`) & Ref Level | $\ln(z_{\text{ref}}/z_0)$ | $\ln(z_{\text{eff}}/z_0)$ | Exact Multiplier $k_{10}$ | Rounded $k_{10}$ (`3 d.p.`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8530973` Robbins Reef (CO-OPS over-water `MSL`)** | **Recommended primary (`8530973`)** | `10.0` | `0.0002` | `16.46 m` (`Site Elevation`) | `4.57 m` (`MSL`) | **`21.03 m` (`MSL`)** | `10.819778` | `11.563143` | **`0.935713`** | **`0.936`** |
| **`8530973` / `robn4` Robbins Reef (NDBC over-water `MSL`)** | Reconciled NDBC equivalent | `10.0` | `0.0002` | `21.00 m` (`sea level`) | `0.00 m` (`MSL`) | **`21.00 m` (`MSL`)** | `10.819778` | `11.561716` | **`0.935828`** | **`0.936`** |
| `8530973` Robbins Reef (CO-OPS above-site only) | *Unconverted site datum (do not use)* | `10.0` | `0.0002` | `16.46 m` (`Site Elevation`) | `4.57 m` (`MSL`) | `16.46 m` (`Site Elevation`) | `10.819778` | `11.318126` | `0.955969` | `0.956` |
| **`8516945` Kings Point (CO-OPS over-water `MSL`)** | **Recommended primary (`8516945`)** | `10.0` | `0.0002` | `8.56 m` (`Site Elevation`) | `3.43 m` (`MSL`) | **`11.99 m` (`MSL`)** | `10.819778` | `11.001266` | **`0.983503`** | **`0.984`** |
| `8516945` / `kpnn6` Kings Point (NDBC over-water `MSL`) | NDBC site-elevation variant | `10.0` | `0.0002` | `8.60 m` (`Site Elevation`) | `2.10 m` (`MSL`) | `10.70 m` (`MSL`) | `10.819778` | `10.887437` | `0.993786` | `0.994` |
| `8516945` Kings Point (CO-OPS above-site only) | *Above-pier height only* | `10.0` | `0.0002` | `8.56 m` (`Site Elevation`) | `3.43 m` (`MSL`) | `8.56 m` (`Site Elevation`) | `10.819778` | `10.664293` | `1.014580` | `1.015` |
| **`8519532` Mariners Harbor (CO-OPS over-water `MSL`)** | **Recommended primary (`8519532`)** | `10.0` | `0.0002` | `46.00 m` (`Site Elevation`) | `2.57 m` (`MSL`) | **`48.57 m` (`MSL`)** | `10.819778` | `12.400199` | **`0.872549`** | **`0.873`** |
| `8519532` / `mhrn6` Mariners Harbor (NDBC over-water `MSL`) | Reconciled NDBC equivalent | `10.0` | `0.0002` | `46.00 m` (`Site Elevation`) | `2.60 m` (`MSL`) | `48.60 m` (`MSL`) | `10.819778` | `12.400817` | `0.872505` | `0.873` |
| `8519532` Mariners Harbor (above-site height `46.0 m`) | *Above-site height (`46 m` formula check)* | `10.0` | `0.0002` | `46.00 m` (`Site Elevation`) | `2.57 m` (`MSL`) | `46.00 m` (`Site Elevation`) | `10.819778` | `12.345835` | **`0.876391`** | **`0.876`** |
| **`8531680` Sandy Hook (CO-OPS over-water `MSL`)** | **Recommended primary (`8531680`)** | `10.0` | `0.0002` | `9.45 m` (`Site Elevation`) | `2.34 m` (`MSL`) | **`11.79 m` (`MSL`)** | `10.819778` | `10.984445` | **`0.985009`** | **`0.985`** |
| `8531680` Sandy Hook (CO-OPS above-site only) | *Above-pier height only* | `10.0` | `0.0002` | `9.45 m` (`Site Elevation`) | `2.34 m` (`MSL`) | `9.45 m` (`Site Elevation`) | `10.819778` | `10.763208` | `1.005256` | `1.005` |
| `8531680` / `sdhn4` Sandy Hook (NDBC over-water `MSL`) | Alternative NDBC height (`MSL`) | `10.0` | `0.0002` | `5.50 m` (`Site Elevation`) | `1.90 m` (`MSL`) | `7.40 m` (`MSL`) | `10.819778` | `10.518673` | `1.028626` | `1.029` |
| `8531680` / `sdhn4` Sandy Hook (NDBC above-site only) | *Alternative NDBC height (above site)* | `10.0` | `0.0002` | `5.50 m` (`Site Elevation`) | `1.90 m` (`MSL`) | `5.50 m` (`Site Elevation`) | `10.819778` | `10.221941` | `1.058486` | `1.058` |
| **`KLGA` LaGuardia Airport ASOS (`10.0 m AGL`)** | **Recommended primary (`KLGA`)** | `10.0` | `0.0002` | `10.00 m` (`AGL`) | `3.00 m` (`MSL`) | **`10.00 m` (`AGL`)** | `10.819778` | `10.819778` | **`1.000000`** | **`1.000`** |
| **`KJFK` JFK Intl Airport ASOS (`10.0 m AGL`)** | **Recommended boundary (`KJFK`)** | `10.0` | `0.0002` | `10.00 m` (`AGL`) | `2.70 m` (`MSL`) | **`10.00 m` (`AGL`)** | `10.819778` | `10.819778` | **`1.000000`** | **`1.000`** |
| **`KEWR` Newark Liberty Intl ASOS (`10.0 m AGL`)** | **Recommended boundary (`KEWR`)** | `10.0` | `0.0002` | `10.00 m` (`AGL`) | `1.90 m` (`MSL`) | **`10.00 m` (`AGL`)** | `10.819778` | `10.819778` | **`1.000000`** | **`1.000`** |

---

### 2.2 Manhattan & Brooklyn/Queens Building Sheltering Along the East River (Facts vs. Proposed Parameterization)

* **Observed / Source-Backed Baseline Facts:**
  * The Lower and Mid East River (`lat 40.70°N to 40.78°N`) is a `400 m` to `800 m` wide tidal strait flanked by urban waterfronts in Manhattan, Brooklyn, and Queens.
  * No NOAA or NDBC meteorological station measures wind speed directly inside the East River ferry slips or across the mid-river wake of the Manhattan skyline. In NCEI HOMR (`artifacts/env-research/homr_KNYC.json`, accessed `2026-10-24`), the Central Park ASOS station (`KNYC`, `42.7 m MSL`) is documented as being surrounded by skyscrapers and enclosed by `TREES` at `25 ft` distance (`20–54°` elevation angle across azimuths `020–340°`), while `KLGA` (`artifacts/env-research/homr_KLGA.json`, accessed `2026-10-24`) is on an open coastal airfield beside Bowery Bay / Rikers Island Channel (`3.0 m MSL` ground elevation).
  * In ship maneuvering simulation (e.g., Fossen 2011, implemented in `core/FerrySim.Core`), vessel aerodynamic surge force $X_{\text{wind}}$, sway force $Y_{\text{wind}}$, and yaw moment $N_{\text{wind}}$ are proportional to the squared apparent wind speed $V_{\text{app}}^2$ at $z = 10\text{ m}$:
    $$X_{\text{wind}} = \frac{1}{2}\rho_a V_{\text{app}}^2 A_F C_X(\gamma_{\text{app}}), \quad Y_{\text{wind}} = \frac{1}{2}\rho_a V_{\text{app}}^2 A_L C_Y(\gamma_{\text{app}}), \quad N_{\text{wind}} = \frac{1}{2}\rho_a V_{\text{app}}^2 A_L L_{OA} C_N(\gamma_{\text{app}})$$
    where $\mathbf{V}_{\text{app}} = \mathbf{V}_{\text{wind,local}}(x, y, t) - \mathbf{V}_{\text{vessel}}$ and $A_F, A_L$ are the T-class ferry frontal and lateral projected windage areas.

* **Proposed Urban Wind-Sheltering Parameterization (**`proposed, to be calibrated`**):**
  * ** Ambient Open-Water Wind Interpolation:** Interpolate the height-normalized (`10 m` over-water `MSL`) 6-minute ambient wind vector $\mathbf{U}_{10,\text{open}}(x, y, t)$ between **`8530973` (Robbins Reef)** ($\mathbf{0.936} \, \mathbf{U}_{8530973}(t)$) in Upper Bay and **`KLGA` (LaGuardia)** ($1.000 \, \mathbf{U}_{\text{KLGA}}(t)$) / **`8516945` (Kings Point)** ($\mathbf{0.984} \, \mathbf{U}_{8516945}(t)$) in the northern East River based on latitude / along-river coordinate (and **`8519532` Mariners Harbor**, $\mathbf{0.873} \, \mathbf{U}_{8519532}(t)$, in Kill Van Kull).
  * **Directional Upwind-Fetch Sheltering Multiplier $k_{\text{shelter}}(x, y, \psi_{\text{wind}})$ (**`proposed, to be calibrated`**):**
    To represent wind shadowing in the lee of waterfront buildings when wind blows across the East River, we propose casting a ray upwind (in meteorological wind-from direction $\psi_{\text{wind}}$) to find the upwind shoreline distance $d_{\text{upwind}}(x, y, \psi_{\text{wind}})$ and upwind skyline height $H_{\text{upwind}}$, and multiplying $\mathbf{U}_{10,\text{open}}(x, y, t)$ by:
    $$k_{\text{shelter}}(x, y, \psi_{\text{wind}}) = 1 - \Delta k_{\max} \exp\!\left(-\frac{d_{\text{upwind}}(x, y, \psi_{\text{wind}})}{c_{\text{wake}} \, H_{\text{upwind}}}\right)$$
  * **Provenance & status of all sheltering ranges and coefficients (no cited public source states these values for NY Harbor; all are `proposed, to be calibrated`):**
    1. **Near-shore sheltering ranges (`40–60%` speed reduction within $0 \le d_{\text{upwind}} \le 3 H_{\text{upwind}}$, `45–60%` reduction inside upwind-shore ferry slips, and exponential recovery over $10 H_{\text{upwind}}$ to $15 H_{\text{upwind}}$ / `500–1,500 m`):** **Proposed, to be calibrated.** These ranges are heuristic engineering estimates for porous bluff-body wakes and are **not** established for NYC Ferry slips by any cited wind-tunnel or field study in this workspace.
    2. **Maximum leeward velocity deficit $\Delta k_{\max} = 0.50$ and wake recovery length scale $c_{\text{wake}} = 7.0$ ($7 H_{\text{upwind}}$ $e$-folding distance, `~250–560 m`):** **Proposed, to be calibrated.** Note that an earlier draft compared $\Delta k_{\max} = 0.50$ to `KNYC` vs `8530973`/`KLGA`; however, no `KNYC` wind-speed ratio was computed in our saved artifacts, and `KNYC` is an inland tree-enclosed park site rather than a shoreline ferry slip. Under this proposed formula, a velocity factor of $k_{\text{shelter}} = 0.60$ inside a sheltered slip reduces lateral wind force $Y_{\text{wind}} \propto V_{\text{app}}^2$ to $0.60^2 = 36\%$ of its open-stream value, providing a tunable parameterization for wind-shadow entry and gust-on-exit handling near piers.
    3. **Along-channel wind factor (`1.00–1.10` when $\psi_{\text{wind}}$ aligns within $\pm 25^\circ$ of the channel axis $\approx 030^\circ\text{–}050^\circ\text{T}$ or $210^\circ\text{–}230^\circ\text{T}$):** **Proposed, to be calibrated.** Intended to allow unobstructed or slightly channeled wind along the river axis; no cited NY Harbor measurement establishes the `1.00–1.10` range.
    4. **Upwind building height ranges ($H_{\text{bldg}} \approx 80\text{–}250+\text{ m}$ for Lower/Midtown Manhattan, $40\text{–}180\text{ m}$ for DUMBO/Brooklyn/Williamsburg/LIC, $15\text{–}30\text{ m}$ for Wallabout/Navy Yard, and simplified zone values $H_{\text{upwind}} = 80\text{ m}, 35\text{ m}, 15\text{ m}, 0\text{ m}$):** **Proposed zone estimates, to be calibrated / replaced by actual building heights** from the Wave 1 harbor tiles.
  * **How Wave 2 should calibrate and validate these proposed sheltering parameters:**
    1. **Direct building heights from Wave 1 harbor tiles:** Replace the simplified zone heights ($H_{\text{upwind}}$) with actual shoreline building elevations sampled from the Wave 1 NYC building footprint / skyline tiles along the upwind ray.
    2. **Cross-station directional wind ratios (`8530973` vs `KLGA` vs `8516945` vs `8519532`):** Compare simultaneous 6-minute winds across the 28 scenario days stratified by $30^\circ$ wind-direction sectors to bound directional exposure differences between Upper Bay, Kill Van Kull, Bowery Bay (`KLGA`), and Long Island Sound (`8516945`).
    3. **Crew handling observations and AIS leeway checks at named landings:** Calibrate $\Delta k_{\max}$, $c_{\text{wake}}$, and the along-channel factor against permitted NYC Ferry crew handling observations (per `docs/research/crew-questions.md`) and AIS heading-minus-COG crab angles during docking maneuvers under cross-shore winds at named landings (`Pier 11`, `Wallabout`, `East 34th Street`, `Hunters Point South`, `Roosevelt Island`, and `Astoria`).

---

## 3. Tested Access Recipes on Scenario Day `2025-07-14`

All recipes below were executed on `2026-10-24` and saved in `artifacts/env-research/`.

### Recipe 1: CO-OPS 6-Minute Verified `NAVD88` Water Levels (`8518750` The Battery & `8516945` Kings Point on `2025-07-14..20`)

* **Exact Request URLs:**
  ```text
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=8518750&product=water_level&datum=NAVD&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=8516945&product=water_level&datum=NAVD&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json
  ```
* **Verified Response Summary (`artifacts/env-research/water_level_28days_availability.json`):**
  * Both stations return `1,680 / 1,680` (`100%`) verified (`q = "v"`) 6-minute observations in meters relative to `NAVD88` for `2025-07-14..20` (and `6,720 / 6,720` across all 28 scenario days).
  * During `2025-07-14..20`, `8518750` (The Battery) ranges from **`-0.831 m` to `+1.077 m` `NAVD88`** (`1.908 m` week range), while `8516945` (Kings Point) ranges from **`-1.258 m` to `+1.671 m` `NAVD88`** (`2.929 m` week range).

### Recipe 2: CO-OPS 6-Minute Marine Wind Observations (`8530973` Robbins Reef & `8516945` Kings Point on `2025-07-14..20`)

* **Exact Request URLs:**
  ```text
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=8530973&product=wind&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json
  https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=8516945&product=wind&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json
  ```
* **Verified Response Summary (`artifacts/env-research/met_stations_summary.json`):**
  * Returns 6-minute JSON records with `s` (wind speed in `m/s`), `d` (wind direction in degrees true, meteorological "from" convention), `dr` (cardinal direction), `g` (gust speed in `m/s`), and `f` (QC flags).
  * Across `2025-07-14..20`, `8530973` returns `1,678 / 1,680` valid rows (`99.88%`) with max sustained wind `14.50 m/s` (`28.2 kt`) and max gust `18.30 m/s` (`35.6 kt`), while `8516945` returns `1,680 / 1,680` valid rows (`100.0%`) with max sustained wind `11.50 m/s` (`22.4 kt`).

### Recipe 3: NCEI ASOS 5-Minute & ISD Global Hourly Airport Wind Archives (`KLGA` on `2025-07-14`)

* **Exact Request URLs:**
  ```text
  https://www.ncei.noaa.gov/data/automated-surface-observing-system-five-minute/access/2025/07/asos-5min-KLGA-202507.dat
  https://www.ncei.noaa.gov/data/automated-surface-observing-system-one-minute-pg1/access/2025/07/asos-1min-pg1-KLGA-202507.dat
  https://www.ncei.noaa.gov/data/global-hourly/access/2025/72503014732.csv
  ```
* **Verified Response Summary (`artifacts/env-research/asos_5min_KLGA_202507_sample.txt`):**
  * Returns fixed-width / METAR 5-minute (`asos-5min`), 1-minute (`asos-1min-pg1`), and CSV (`global-hourly`) observations including `10 m` ultrasonic wind direction, sustained speed, gust, visibility (`10SM`), ceiling, temperature/dewpoint, and altimeter setting.

---

## 4. Station Inventory Schema & Boundary Stations (`environment-stations.csv`, Question 5)

### 4.1 Column Schema of `docs/research/environment-stations.csv`

The file `docs/research/environment-stations.csv` contains **79 station records** strictly inside the harbor bounding box **`lat 40.60°N to 40.83°N`, `lon -74.12°E to -73.80°E`**:
* **`37` `current_prediction` stations** (`15` harmonic `H`, `20` subordinate `S`, `2` weak-and-variable `W`, summarizing all `63` CO-OPS depth bins)
* **`38` `water_level` stations** (`1` active 6-minute harmonic gauge `8518750` with `NAVD88`, `9` historical harmonic tide stations `tp_type=R` of which `5` have `NAVD88`, `22` subordinate tide stations `tp_type=S` of which `1` has `NAVD88`, and `6` historical datum stations)
* **`4` `meteorological` stations** (`8530973` Robbins Reef CO-OPS/NDBC wind+met, `8518750_MET` The Battery CO-OPS/NDBC met-without-wind, `KLGA` LaGuardia ASOS, and `KNYC` Central Park ASOS)

| Column Name | Type | Description |
| :--- | :--- | :--- |
| `station_id` | `string` | Official NOAA CO-OPS station ID (`NYH1924`, `ACT3406`, `8518750`, `8530973`) or ICAO identifier (`KLGA`, `KNYC`). Note: `8518750` appears once under `water_level` (`8518750`) and once under `meteorological` (`8518750_MET`). |
| `name` | `string` | Official station name from NOAA CO-OPS MDAPI or NCEI HOMR (with NDBC/USAF-WBAN cross-reference where applicable). |
| `category` | `string` | One of `current_prediction`, `water_level`, or `meteorological`. |
| `network` | `string` | Operating agency/network (`NOAA_COOPS`, `NOAA_COOPS / NDBC`, or `NWS_ASOS / NCEI`). |
| `lat` | `float` | Latitude in decimal degrees (`WGS84 / NAD83`), strictly within `[40.600000, 40.830000]`. |
| `lon` | `float` | Longitude in decimal degrees (`WGS84 / NAD83`), strictly within `[-74.120000, -73.800000]`. |
| `station_type` | `string` | For currents: `harmonic` (`H`), `subordinate` (`S`), or `weak_and_variable` (`W`). For water levels: `active_harmonic_gauge`, `harmonic_tide_station`, `subordinate_tide_station`, or `historical_datum_station`. For met: `continuous_obs` or `continuous_obs_no_wind`. |
| `reference_station` | `string` | `self` for harmonic/primary stations; reference station ID (and reference bin for currents, e.g. `NYH1924 (bin 6)`, `n03020 (bin 7)`, `HUR0611 (bin 16)`, `8518750`, `8516945`, `8531680`) for subordinate stations; `none` for weak/datum-only stations. |
| `bins_and_depths` | `string` | For `current_prediction`: semicolon-separated list of all bins with depth in meters, depth reference (`B` = below surface, `S` = above bottom, `U` = unspecified), and mean flood/ebb directions in degrees true (`N/A` for water-level and met stations). |
| `subordinate_offsets` | `string` | For subordinate current stations: `dT_flood`, `dT_ebb`, `dT_sbf`, `dT_sbe` (minutes) and `amp_flood`, `amp_ebb` ratios. For subordinate tide stations: `dT_high`, `dT_low` (minutes) and `amp_high`, `amp_low` ratios. For harmonic tide stations: $M_2$ amplitude (`m`) and GMT phase (`deg`). |
| `navd88_available` | `string` | `true` or `false` for `water_level` stations (`true` for `7` in-box stations: `8518750`, `8518699`, `8518687`, `8518639`, `8517201`, `8519050`, `8530772`); `N/A` for current/met stations. |
| `mllw_minus_navd88_m` | `float` / empty | Signed elevation offset $\text{MLLW} - \text{NAVD88}$ in meters (`-0.846` at `8518750`) where `navd88_available == true`; empty otherwise. |
| `sensor_height_m` | `string` | For `meteorological` stations: anemometer (and air/water/barometer) height in meters with both above-site/ground and over-water `MSL` values (`N/A` for current/water-level rows). |
| `sensor_height_reference` | `string` | Dedicated reference-level column for every row: for `meteorological` stations, the vertical reference level of the wind/met sensors (`MSL over water` with CO-OPS `Site Elevation` + `site` `MSL` offset, or `AGL` for airport ASOS); for `current_prediction` stations, the ADCP bin depth reference (`B` = below water surface, `S` = above seabed/bottom, `U` = unspecified); for `water_level` stations, the vertical datum availability (`NAVD88 & MLLW/MSL` vs `Tidal datums only`). |
| `products` | `string` | Semicolon-separated list of verified data products available from NOAA CO-OPS or NCEI. |
| `scenario_28d_availability` | `string` | Verified data completeness across the 28 scenario days (`2024-10-07..13`, `2025-01-13..19`, `2025-04-14..20`, `2025-07-14..20`). |
| `source_url` | `string` | Primary-source metadata URL accessed on `2026-10-24`. |

### 4.2 Critical Boundary & Reference Stations Just Outside `lat 40.60..40.83, lon -74.12..-73.80`

Because several subordinate stations inside the harbor bounding box reference primary gauges located a few kilometers outside the `[40.60, 40.83] × [-74.12, -73.80]` box, we document those `7` boundary stations here so Wave 2 can fetch them alongside the in-box stations:

| Station ID | Name | Category | `(lat, lon)` | Distance Outside Box | Sensor / Datum Reference Level | Role & Key Parameters |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`8516945` (`kpnn6`)** | **Kings Point, NY** | `water_level` & `meteorological` | `(40.810306, -73.764889)` | `3.0 km` E of `-73.80°E` | Water level: `NAVD88` & `MLLW`; Wind `C1`: `Site Elevation` (`8.56 m`) + `site` (`3.43 m MSL`) = **`11.99 m MSL`** (NDBC: `8.6 m` + `2.1 m MSL` = `10.70 m MSL`) | Active 6-min water-level gauge (`6,720/6,720` rows, `MLLW - NAVD88 = -1.282 m`, `MSL - NAVD88 = -0.039 m`, $M_2 = 1.150\text{ m}, \kappa = 115.7^\circ\text{ GMT}$); reference gauge for `8517125`, `8517276`, `8518621`, `8518635`; active 6-min wind (`100%` complete, $k_{10} = \mathbf{0.984}$ over `MSL` or `1.015` above site). |
| **`8531680` (`sdhn4`)** | **Sandy Hook, NJ** | `water_level` & `meteorological` | `(40.466944, -74.009389)` | `14.8 km` S of `40.60°N` | Water level: `NAVD88` & `MLLW`; Wind `C1`: `Site Elevation` (`9.45 m`) + `site` (`2.34 m MSL`) = **`11.79 m MSL`** (NDBC: `5.5 m` + `1.9 m MSL` = `7.40 m MSL`) | Active 6-min water-level gauge (`6,720/6,720` rows, `MLLW - NAVD88 = -0.858 m`, `MSL - NAVD88 = -0.084 m`, $M_2 = 0.686\text{ m}, \kappa = 5.4^\circ\text{ GMT}$); reference gauge for `8517942`, `8517519`, `8517381`; active 6-min wind (`99.99%` complete, $k_{10} = \mathbf{0.985}$ over `MSL` or `1.005` above site). |
| **`8519532` (`mrhn6`)** | **Mariners Harbor, NY** | `meteorological` | `(40.640083, -74.161667)` | `3.5 km` W of `-74.12°E` | Wind `C1`: `Site Elevation` (`46.0 m`) + `site` (`2.57 m MSL`) = **`48.57 m MSL`** (NDBC: `46.0 m` + `2.6 m MSL` = `48.60 m MSL`) | Active 6-min wind station at western Kill Van Kull (`6,720/6,720` rows, `100%` complete; $k_{10} = \mathbf{0.873}$ at `48.57 m MSL`, or `0.876` at `46.0 m` above site). |
| **`8519483` (`bgpn6`)** | **Bergen Point West Reach, NY** | `water_level` | `(40.636700, -74.141700)` | `1.8 km` W of `-74.12°E` | Tidal datums only (no `NAVD88`; CO-OPS `sensors = null`; NDBC `bgnn6` lists `9 m` above `3 m MSL` site) | **Decommissioned `2024-10-29`**; `0 / 6,720` water-level or wind rows on the 28 scenario days; no `NAVD88` datum. Do not use. |
| **`HUR0611`** | **George Washington Bridge** | `current_prediction` | `(40.851100, -73.952200)` | `2.3 km` N of `40.83°N` | ADCP bin depth reference: `B` (below water surface, `bin 16` at `3.96 m` depth) | Harmonic (`H`) current station in Upper Hudson River; reference station for in-box subordinate station `ACT3656` (Grant's Tomb). |
| **`KJFK` (`USW00094789`)** | **JFK International Airport, NY** | `meteorological` | `(40.639150, -73.763900)` | `3.0 km` E of `-73.80°E` | **`AGL`** (`10.0 m AGL`; HOMR `GROUND` elevation `2.7 m MSL` — do not add ground elevation) | ASOS `10.0 m AGL` wind ($k_{10} = \mathbf{1.000}$), visibility, and ceiling; `28/28` days in NCEI 1-min, 5-min, and ISD archives. |
| **`KEWR` (`USW00014734`)** | **Newark Liberty Intl Airport, NJ** | `meteorological` | `(40.682750, -74.169270)` | `4.1 km` W of `-74.12°E` | **`AGL`** (`10.0 m AGL`; HOMR `GROUND` elevation `1.9 m MSL` — do not add ground elevation) | ASOS `10.0 m AGL` wind ($k_{10} = \mathbf{1.000}$), visibility, and ceiling; `28/28` days in ISD (`72502014734.csv`), missing `2025/07` in NCEI 1-min/5-min folder. |

---

## 5. Reconciliations & Conflicts Between Sources

| Station / Parameter | Source A | Source B | Nature of Comparison / Conflict | Resolution for FerrySim |
| :--- | :--- | :--- | :--- | :--- |
| **`8530973` / `robn4` (Robbins Reef) Wind Sensor Height (Reconciled — No Conflict)** | **NOAA CO-OPS MDAPI (`stations/8530973/sensors.json`):** Wind (`C1`) `elevation = 16.46 m` (`refdatum = "Site Elevation"`), and `site` `elevation = 4.57 m` (`refdatum = "MSL"`), summing to **`21.03 m` above `MSL`** ([URL](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8530973/sensors.json?units=metric), accessed `2026-10-24`) | **NOAA NDBC (`station_page.php?station=robn4`):** *"Site elevation: sea level"* and *"Anemometer height: 21 m above site elevation"*, i.e., **`21.0 m` above sea level (`MSL`)** ([URL](https://www.ndbc.noaa.gov/station_page.php?station=robn4), accessed `2026-10-24`) | **No conflict once converted to a common reference level:** CO-OPS reports the sensor height above the reef lighthouse platform (`16.46 m` above `Site Elevation`) and the platform height above `MSL` (`4.57 m`), yielding **`21.03 m` above `MSL`**, which agrees with NDBC's **`21 m` above sea level** (and similarly for air temperature `D1`: `16.18 + 4.57 = 20.75 m` vs NDBC's `20.8 m`). | Use the reconciled over-water height **`21.03 m` above `MSL`** ($k_{10} = \ln(10/0.0002)/\ln(21.03/0.0002) = \mathbf{0.935713 \approx 0.936}$, identical to 3 decimal places with NDBC's `21.0 m` factor `0.935828`). |
| **`8531680` / `sdhn4` (Sandy Hook) Wind Sensor Height** | **NOAA CO-OPS MDAPI (`stations/8531680/sensors.json`):** Wind (`C1`) `elevation = 9.45 m` (`Site Elevation`) + `site = 2.34 m` (`MSL`) = **`11.79 m` above `MSL`** ([URL](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8531680/sensors.json?units=metric), accessed `2026-10-24`) | **NOAA NDBC (`station_page.php?station=sdhn4`):** *"Site elevation: 1.9 m above mean sea level"* and *"Anemometer height: 5.5 m above site elevation"* = **`7.40 m` above `MSL`** ([URL](https://www.ndbc.noaa.gov/station_page.php?station=sdhn4), accessed `2026-10-24`) | Even after converting both to `MSL`, CO-OPS (`11.79 m MSL`, `9.45 m` above site) and NDBC (`7.40 m MSL`, `5.5 m` above site) differ by `4.39 m` (`3.95 m` above site plus `0.44 m` in site elevation). | Trust CO-OPS's NWLON engineering sensor table (`11.79 m` above `MSL`, $k_{10} = \mathbf{0.985}$; or `9.45 m` above site, $k_{10} = 1.005$), as CO-OPS operates the NWLON station at Sandy Hook. |
| **`8516945` / `kpnn6` (Kings Point) Site Elevation** | **NOAA CO-OPS MDAPI (`stations/8516945/sensors.json`):** Wind (`C1`) `elevation = 8.56 m` (`Site Elevation`) + `site = 3.43 m` (`MSL`) = **`11.99 m` above `MSL`** ([URL](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8516945/sensors.json?units=metric), accessed `2026-10-24`) | **NOAA NDBC (`station_page.php?station=kpnn6`):** *"Site elevation: 2.1 m above mean sea level"* and *"Anemometer height: 8.6 m above site elevation"* = **`10.70 m` above `MSL`** ([URL](https://www.ndbc.noaa.gov/station_page.php?station=kpnn6), accessed `2026-10-24`) | Sensor height above site agrees (`8.56 m` vs `8.6 m`), but reported site elevation above `MSL` differs by `1.33 m` (`3.43 m` vs `2.1 m`). | Use CO-OPS's primary NWLON sensor and site table (`11.99 m` above `MSL`, $k_{10} = \mathbf{0.984}$; vs `0.994` for NDBC's `10.70 m MSL`). |
| **`8518750` (The Battery) Wind Sensor Availability** | **CO-OPS Station Listing (`stations.json?type=met`):** Includes `8518750` because it carries `air_temperature` and `air_pressure` sensors | **CO-OPS Sensor Inventory (`stations/8518750/sensors.json`) & Data API (`product=wind`):** No wind sensor (`sensor_id=C1`) exists at `8518750`; `product=wind` returns `"No data was found"` | Being listed under `type=met` does not mean a station has an anemometer. | Never query `product=wind` on `8518750`; use `8530973` (Robbins Reef, `5.6 km` SW in Upper Bay) for Lower Harbor marine wind. |
| **NCEI ASOS 1-Min / 5-Min Archive for `KEWR` in July 2025** | **NCEI ISD Global Hourly (`2025/72502014734.csv`):** Complete for July 2025 | **NCEI `asos-1min-pg1/access/2025/07/` & `asos-5min/access/2025/07/`:** `KEWR` file is absent from the `2025/07/` directory listing (while `KLGA`, `KJFK`, `KNYC` are present in all 4 scenario months) | NCEI's monthly 1-min/5-min tape ingest omitted `KEWR` in `2025/07`. | Use `KLGA` (and `8530973` / `8519532`) for sub-hourly wind across all 28 days, or read `KEWR` from NCEI Global Hourly (`72502014734.csv`) if Newark Airport is needed in July 2025. |

---

## 6. What Could Not Be Verified

1. **`NAVD88` Leveling at Subordinate East River Tide Stations (`8517401` Wards Island, `8518668` Horns Hook, `8517847` Brooklyn Bridge):** While `7` water-level/tide stations inside the harbor box (`8518750`, `8518699`, `8518687`, `8518639`, `8517201`, `8519050`, `8530772`) and `2` boundary gauges (`8516945`, `8531680`) have published `NAVD88` datums in CO-OPS MDAPI, the remaining `31` historical subordinate/harmonic stations list only tidal datums (`MLLW, MLW, MSL, MHW, MHHW`) with `NAVD88 = null`. Across all `9` stations that *do* have `NAVD88`, the local Mean Sea Level offset $\Delta z_{\text{MSL}-\text{NAVD88}} = \text{MSL} - \text{NAVD88}$ varies within a narrow band (`-0.063 m` at The Battery, `-0.045 m` at Williamsburg Bridge, `-0.061 m` at Queensboro Bridge, `-0.019 m` at Port Morris, `-0.039 m` at Kings Point), allowing $\Delta z_{\text{MSL}-\text{NAVD88}}(x)$ to be interpolated along the East River to within an estimated `±0.02 m`.
2. **Site-Specific Wind-Tunnel / CFD Wake Measurements and Near-Shore Sheltering Coefficients for NYC Ferry Landings:** No public wind-tunnel, CFD, or near-slip anemometer dataset was found for the NYC Ferry slips along the East River. Consequently, all near-shore building-wake parameters in §2.2—the velocity deficit $\Delta k_{\max} = 0.50$, the wake recovery scale $c_{\text{wake}} = 7.0$, the `40–60%` / `45–60%` speed reduction ranges over `0–3 H`, the `10–15 H` recovery fetch, the `1.00–1.10` along-channel wind factor, and the zone building heights—are **proposed modelling choices, to be calibrated** using Wave 1 3D building tiles, cross-station wind comparisons (`8530973`, `KLGA`, `8516945`, `8519532`), and crew observations at named landings.

---

## 7. Primary-Source Citations (`Accessed 2026-10-24`)

1. **NOAA CO-OPS Metadata API — Water-Level Stations, Datums, Harmonic Constituents & Tide Offsets:** `https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=waterlevels`, `.../stations.json?type=historicwl`, `.../stations.json?type=tidepredictions`, `.../stations/8518750/datums.json?units=metric`, `.../stations/8516945/datums.json?units=metric`, `.../stations/8531680/datums.json?units=metric`, and `.../stations/{id}/harcon.json?units=metric` (accessed `2026-10-24`).
2. **NOAA CO-OPS Data API — 6-Minute Verified Water Levels (`NAVD` & `MLLW`) and Winds:** `https://api.tidesandcurrents.noaa.gov/api/prod/datagetter?station=8518750&product=water_level&datum=NAVD&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json` and `.../datagetter?station=8530973&product=wind&begin_date=20250714&end_date=20250720&units=metric&time_zone=gmt&format=json` (accessed `2026-10-24`).
3. **NOAA CO-OPS Metadata API — Meteorological Stations & Sensor Elevations:** `https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=met`, `.../stations/8530973/sensors.json?units=metric`, `.../stations/8518750/sensors.json?units=metric`, `.../stations/8516945/sensors.json?units=metric`, `.../stations/8519532/sensors.json?units=metric`, and `.../stations/8531680/sensors.json?units=metric` (accessed `2026-10-24`).
4. **NOAA National Data Buoy Center (NDBC) Station Pages & Active Station Catalog:** `https://www.ndbc.noaa.gov/activestations.xml`, `https://www.ndbc.noaa.gov/station_page.php?station=robn4`, `https://www.ndbc.noaa.gov/station_page.php?station=batn6`, `https://www.ndbc.noaa.gov/station_page.php?station=kpnn6`, `https://www.ndbc.noaa.gov/station_page.php?station=mrhn6`, and `https://www.ndbc.noaa.gov/station_page.php?station=sdhn4` (accessed `2026-10-24`).
5. **NOAA NCEI Historical Observing Metadata Repository (HOMR):** `https://www.ncei.noaa.gov/access/homr/services/station/search?qid=ICAO:KLGA`, `...?qid=ICAO:KJFK`, `...?qid=ICAO:KEWR`, and `...?qid=ICAO:KNYC` (accessed `2026-10-24`).
6. **NOAA NCEI ASOS 1-Minute, ASOS 5-Minute, and Global Hourly (ISD) Archives:** `https://www.ncei.noaa.gov/data/automated-surface-observing-system-one-minute-pg1/access/2025/07/`, `https://www.ncei.noaa.gov/data/automated-surface-observing-system-five-minute/access/2025/07/asos-5min-KLGA-202507.dat`, and `https://www.ncei.noaa.gov/data/global-hourly/access/2025/72503014732.csv` (accessed `2026-10-24`).

---

## 8. Changelog (`2026-10-25` — M3 Wave 1 Review Fixes)

* **Finding 2 (Major — Facts vs. Proposals in East River Wind Sheltering & Water-Level Accuracy Claims):**
  * Separated source-backed observations in §2.2 (and Executive Summary) from the **proposed, to be calibrated** urban wind-sheltering parameters: the leeward deficit $\Delta k_{\max} = 0.50$, the wake recovery scale $c_{\text{wake}} = 7.0$ ($7 H_{\text{upwind}}$), the `40–60%` / `45–60%` speed reduction ranges over `0–3 H`, the `10–15 H` recovery fetch, the `1.00–1.10` along-channel factor, and the zone building height bands.
  * Quoted the verbatim NCEI HOMR topography and obstruction record for `KNYC` Central Park (`artifacts/env-research/homr_KNYC.json`, accessed `2026-10-24`), removed the unsupported claim that `KNYC` establishes an empirical ferry-slip wake ratio, and specified calibration protocols using Wave 1 3D building tiles, cross-station directional wind ratios, and crew observations at named landings (`Pier 11`, `Wallabout`, `East 34th Street`, `Hunters Point South`, `Roosevelt Island`, `Astoria`).
  * Replaced `"exact NAVD88 under-keel clearance throughout the harbor"` in the Executive Summary with a bounded statement distinguishing stations with leveled `NAVD88` datums from subordinate stations where $\Delta z_{\text{MSL}-\text{NAVD88}}(x)$ is interpolated (`±0.02 m`).
* **Finding 6 (Minor — Explicit `MSL`-to-`NAVD88` Datum Offset in Water-Level Equation):**
  * Added the explicit datum offset term $\Delta z_{\text{MSL}-\text{NAVD88}}(x) = \text{MSL}(x) - \text{NAVD88}(x)$ to the displayed `NAVD88` water-level equation in §1.3, together with its exact station values (`-0.063 m` at `8518750` The Battery, `-0.045 m` at `8518699` Williamsburg Bridge, `-0.061 m` at `8518687` Queensboro Bridge, `-0.019 m` at `8518639` Port Morris, `-0.039 m` at `8516945` Kings Point, `-0.084 m` at `8531680` Sandy Hook) and CO-OPS `datums.json` provenance.
* **Finding 7 (Minor — Recomputation of Logarithmic Wind Height Multipliers):**
  * Regenerated all $U_{10}$ logarithmic height-normalization multipliers directly from $k_{10}(z_{\text{eff}}) = \ln(10.0 / 0.0002) / \ln(z_{\text{eff}} / 0.0002)$ and added the complete recomputation table in §2.1 showing every input and output (e.g., Mariners Harbor `8519532` gives `0.876391` / `0.876` at `46.0 m` above site and `0.872549` / `0.873` at `48.57 m` above `MSL`, correcting the previous `0.859` typo).
* **Finding 9 & Integration Cleanups (Minor — Sensor Height Reference Levels, Robbins Reef Reconciliation, & Formatting):**
  * Reconciled `8530973` / `robn4` (Robbins Reef) wind and air-temperature heights in §2.1 and §5 by adding CO-OPS's `site` elevation (`4.57 m` above `MSL`) to the `C1` sensor height (`16.46 m` above `Site Elevation`) to obtain `21.03 m` above `MSL` ($k_{10} = 0.936$), matching NDBC's `21 m` above `sea level`.
  * Audited every marine sensor (`C1`, `D1`, `E1`, `F1`, `site`) across all harbor and boundary stations in §2.1, §4.2, §5, and `docs/research/environment-stations.csv` (which now includes a dedicated `sensor_height_reference` column and `LF` line endings), while keeping airport ASOS heights at `10.0 m AGL` without adding ground elevation.
  * Replaced trailing double-space line breaks in the document header with a Markdown list so `git diff --cached --check` passes cleanly.
