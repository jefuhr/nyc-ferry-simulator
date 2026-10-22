# 0003: Free world data first; Google tiles stay an opt-in

**Status:** accepted (2026-10-22)

## Context

Learners navigate by landmarks and the skyline, but they steer and dock against things at water level:
- landings and piers
- bulkheads and bridges
- buoys, channels, depths and currents

Google Photorealistic 3D Tiles look photoreal. They need a billing account and internet access, and the terms ban caching or offline use.

## Decision

**Free and open data first:**

| Layer | Source |
|---|---|
| Depth and shore terrain | NOAA CUDEM 1/9 arc-second topobathy |
| Channels, aids to navigation (with light characteristics), shoreline constructions, bridge clearances | NOAA ENC |
| Landings, route shapes, schedules | NYC Ferry GTFS |
| Currents and water levels | NOAA tidal current predictions and the NYOFS archive |
| Skyline | NYC open-data-derived CityGML LoD2 (TUM) or extruded OSM footprints, licenses checked before redistribution |

- Water-level geometry is hand-built, starting as greybox and refined from crew photos.
- Cesium for Unity handles georeferencing from day one.

## Consequences

- Turning on Google tiles later means adding a tileset under the existing georeference, not reworking the world. Doing so needs explicit approval because it costs money.
- The world works offline and stays free.
- Raw data is cached outside the repo. Only small source manifests are committed (`data/manifests/`).
