# Harbor Skyline

The second local tileset reads `data/processed/harbor/3dtiles-buildings/tileset.json`. `SkylineTileset` uses the terrain adapter's offline inventory validation and explicit Cesium settings. Its separate `HarborSkyline.mat` uses `FerrySim/HarborSurface`, so day, night, sunlight and fog agree with terrain and the atmosphere. No building is added to physical contacts or supplied as a camera/radar collider.

The initial cache allowance is 64 MiB, within the scene's combined 256 MiB allowance. Policy B and source provenance remain in the pipeline index; player packaging must retain those records. Captures and performance boundaries are recorded in `artifacts/harbor-scene/report.md`.
