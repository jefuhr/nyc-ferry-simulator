# Harbor Environment: Verified Metadata

Separate datum metadata from calculations and current-station identity from current predictions. No numeric current-speed acceptance band is supplied by this rewrite.

## Datum records

- **E01** — NOAA station 8518750 returns epoch 1983-2001 and metre units: MLLW 1.002, NAVD88 1.848, MSL 1.785, MN 1.380, GT 1.542; its labels are Mean Range of Tide (MN) and Great Diurnal Range (GT). [datum](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8518750/datums.json?units=metric) · confidence: high.
- **E02** — NOAA station 8516945 returns epoch 1983-2001 and metre units: MLLW 3.927, NAVD88 5.209, MSL 5.113, MN 2.183, GT 2.377; its labels are Mean Range of Tide (MN) and Great Diurnal Range (GT). [datum2](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8516945/datums.json?units=metric) · confidence: high.
- **E03** — NOAA station 8531680 returns epoch 1983-2001 and metre units: MLLW 0.766, NAVD88 1.624, MSL 1.551, MN 1.433, GT 1.593; its labels are Mean Range of Tide (MN) and Great Diurnal Range (GT). [datum3](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations/8531680/datums.json?units=metric) · confidence: high.

## Derived estimate: datum arithmetic


**D01 — Derived estimate, not a source fact or acceptance band.** Inputs: the MLLW, NAVD88 and MSL station-datum values in E01–E03, all sourced above; no estimated numerical input. Formula: offset = NAVD88 − MLLW; `z_NAVD88 = z_MLLW − offset`. Results: 0.846 m at station 8518750, 1.282 m at 8516945, 0.858 m at 8531680. Similarly `MSL_NAVD88 = MSL − NAVD88`: −0.063 m, −0.096 m, −0.073 m respectively. Apply these only at the cited station datum context; require a spatial transformation for other locations. Do not relabel GT as spring-tide range.


## Selected current metadata

- **EC01** — NOAA lists NYH1924, bin 6: Hell Gate, latitude 40.778301, longitude -73.938301, meanFloodDir 46° and meanEbbDir 239°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.
- **EC02** — NOAA lists NYH1920, bin 12: Brooklyn Bridge, latitude 40.706001, longitude -73.997704, meanFloodDir 47° and meanEbbDir 234°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.
- **EC03** — NOAA lists n03020, bin 7: The Narrows, latitude 40.606400, longitude -74.038002, meanFloodDir 325° and meanEbbDir 143°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.
- **EC04** — NOAA lists NYH1927, bin 13: Hudson River Entrance, latitude 40.707600, longitude -74.025299, meanFloodDir 11° and meanEbbDir 183°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.
- **EC05** — NOAA lists NYH1931, bin 21: Marine Parkway Bridge, Rockaway Inlet, latitude 40.571499, longitude -73.883698, meanFloodDir 77° and meanEbbDir 257°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.
- **EC06** — NOAA lists ACT3596, bin 1: Beach Channel (bridge), latitude 40.583302, longitude -73.816704, meanFloodDir 62° and meanEbbDir 225°. [currents](https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi/stations.json?type=currentpredictions&expand=currentpredictionoffsets) · confidence: high.

Keep every original monthly mean/maximum speed, harmonic phase, local cross-set, weather cutoff and wake-speed assertion in [unverified leads](unverified-leads.md) until its requested evidence is available. A usable current derivation must preserve station, bin, product, dates, timezone, units, interval and aggregation code; do not substitute metadata for a time series.
