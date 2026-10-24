# Lower East River Import

The importer builds a reusable `Generated/LowerEastRiver/lower-east-river.prefab`, tiled depth meshes, chart structure colliders, navigation aid lights and oriented landing contact strips. It does not modify the world scene or connect the physics bridge.

## Rebuild

From the repository root:

```sh
mkdir -p artifacts/unity
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river
uv run --project data-pipeline python data-pipeline/ferrydata export-unity --slice lower-east-river
lockf -k "$HOME/FerrySim-cache/unity.lock" /Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath "$PWD/unity" -executeMethod FerrySim.World.Import.SliceImporter.Import -logFile "$PWD/artifacts/unity/import.log"
```

In the Editor, use **FerrySim > Import > Lower East River**. Keep the `.meta` files: re-import updates assets in place and preserves GUIDs. The importer owns only `Generated/LowerEastRiver/` and removes stale outputs there. Keep hand-authored assets elsewhere.

Run tests with `-batchmode -nographics -runTests -testPlatform EditMode -testFilter FerrySim.World.Import.Tests -testResults <absolute XML path> -logFile <absolute log path>` under the same lock, without `-quit`. Run `FerrySim.World.Import.ImportScreenshots.Capture` in batchmode **without** `-nographics` to generate `artifacts/unity/import-*.png` using URP.

## Coordinates and resolution

Every lon/lat goes through `LocalTangentPlane.World.ToLocal`, then Unity `(east, NAVD88 + geoid, north)`. The geoid comes from `geoid_offset.json`'s slice centre, matching the slice-wide datum convention; the published corner variation remains in the source file. The raster exporter converts CUDEM NAD83 coordinates to WGS84 using installed GDAL/PROJ with nearest-neighbour sampling, one thread and no network fetches. The raw grid is little-endian float32, row-major, with sample centres at origin plus `(column + .5, row + .5)` times cell size.

Terrain samples every fourth raster cell, approximately 12 m, and retains the last row/column. Tiles contain at most 65 by 65 vertices. Nodata is omitted, never replaced by invented bathymetry. Vertex colours show shallow cyan through deep blue and positive NAVD88 terrain in green/brown.

Chart line/polygon structures become double-sided MeshCollider strips on `Structure` (layer 8). Their vertical extent, NAVD88 -60 to +1 m, is a deliberately approximate greybox extrusion, not a surveyed structure height. Point-only chart objects have no supplied extent and are reported as omitted. The import manifest records all input SHA256 hashes, counts and limitations.

## Lights

`LightCharacteristic` supports `F`, `Fl`, `LFl`, `Q`, `Iso`, `Oc` and flash groups, including `Fl(2+1)`. Only nonempty, valid sequences marked `UsesChartSequence` override nominal timing; empty serialized arrays use the fallback. Published `SIGSEQ` durations must sum to the chart period. When the chart supplies rhythm and period without a sequence, pulse widths are visibly labelled nominal in the manifest: Fl 0.3 s, Q 0.3 s at 1 Hz, LFl 2 s, Iso 50 percent duty, Oc 75 percent duty. Short-period flash groups compress their nominal pulse widths and spacing together, preserving every flash, a longer gap between composite groups, and a final eclipse. Grouped occulting lights preserve every eclipse, with longer light between composite groups. Long flashes retain 2 s pulses and compress gaps; infeasible periods and grouped fixed lights require chart SIGSEQ or produce an explicit import warning and remain grey markers. Unsupported characteristics or missing periods also remain grey markers. The supplied slice contains three such period-less fog lights and one nominal-width light; consult the generated manifest for current counts.

Published `height_m` values are above MHW. The importer verifies the cached `datums_8518750.json` bytes against `data/manifests/coops.json`, then adds `MHW - NAVD88` from NOAA CO-OPS The Battery (8518750), followed by the slice geoid offset. The cache follows the pipeline's `FERRYSIM_CACHE` override, defaulting to `$HOME/FerrySim-cache`. Missing, mismatched, non-metric, or incomplete datum data stop the import. `import-manifest.json` records the source URL, checksum, epoch and both station-datum elevations. This uses one station relationship across the slice rather than a surveyed local datum surface. Lights with unpublished heights retain an explicit greybox estimate of NAVD88 +3 m.

`BuoyFlasher.SetSimulationTime(seconds)` accepts scenario time from a bridge; until called, standalone previews use elapsed Unity play time. Every source light has its own flasher. Greybox lens and light colour follow the chart, while beacon/buoy hull models remain generic.

## Contact geometry

`contacts.json` contains stable positive IDs, stop identity, double-precision `start_ne_m`, `end_ne_m`, `water_normal_ne` and source/confidence/verification fields. Endpoints are reversed when needed so the core's left-hand water normal matches the pipeline outward normal. `properties.face_geometry` is the face, not the feature's barge-outline polygon. All GTFS stops require a face; missing ones fail the import.

The bridge can construct `ContactSegment`s and then `SegmentListGeometry` from this file. ContactSurface calibration is intentionally absent: the bridge must supply reviewed material parameters. The pipeline's `needs-crew-verification` and derived-face confidence remain explicit; these assets are not surveyed berth acceptance.
