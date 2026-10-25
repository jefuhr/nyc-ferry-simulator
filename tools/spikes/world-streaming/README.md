# World streaming spike: workload and runs

Throwaway tooling for M3 wave 1's `world-streaming-spike`. It builds a benchmark workload on the harbor tiles v1 grid, measures two ways of streaming it in Unity on this machine, and writes the numbers the report uses. The report is `artifacts/world-streaming-spike/report.md`. Everything generated stays under `artifacts/world-streaming-spike/` (gitignored).

> **On `main`** this folder keeps the Python half: the grid, the camera path, the tiles, the glTF writer, the generator and the analysis. They are the starting point for wave 2's 3D Tiles writer ([decision 0004](../../../docs/decisions/0004-harbor-world-local-3d-tiles.md)). The Unity half (`unity/Assets/FerrySim/Spikes/`), `unity.sh`, `run_player.sh`, `run_matrix.sh` and `compare_imports.py` are on branch `jefuhr/world-streaming-spike`. The report is kept as `docs/research/world-streaming-spike.md`.

## Pieces

| File | Role |
|---|---|
| `spike_grid.py` | the 36-tile grid (4 km squares, i north and j east from -2 to 3) and each tile's ENU frame through ECEF |
| `camera_path.py` | the measured path: Pier 11, up the East River to Hell Gate, back, then across the Upper Bay toward St. George, at the T-class service speed |
| `tiles.py` | per tile, 4 x 4 chunks of 1 km at L0 (11.9 m, plus stand-in buildings) and L1 (47.6 m, plus buildings 30 m and taller). Heights come from the slice's merged raster, then cached CUDEM, then a synthetic field |
| `glb.py` | a minimal glTF 2.0 binary writer and reader (numpy only) |
| `generate.py` | writes `data/camera_path.json`, `data/tiles3d/` (3D Tiles 1.1 with external tilesets per tile) and `data/tiles3d/manifest.json` |
| `unity.sh` | runs one Unity editor command under the heavy lock, then the Unity lock |
| `run_player.sh` | runs the development player once under both locks, sampling its resident memory from outside |
| `run_matrix.sh` | the measured runs in order, A and B interleaved |
| `analyze.py` | turns the runs' CSVs into the report's tables and figures |
| `compare_imports.py` | compares two approach-A import reports' file hashes |
| `tests/` | pytest for the grid, the glTF writer and the tiles |

## Commands

From the repository root:

```sh
# Inputs: the slice outputs (reads the raw cache only); export-unity completes them for the World import tests.
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river --skip ais
uv run --project data-pipeline python data-pipeline/ferrydata export-unity --slice lower-east-river

# Tests, then the workload (about 30 s; under 200 MB of memory).
uv run --project data-pipeline pytest tools/spikes/world-streaming/tests
uv run --project data-pipeline python -I tools/spikes/world-streaming/generate.py

# Approach A's assets, the bench scene and the development player.
S=tools/spikes/world-streaming
$S/unity.sh bench-scene -batchmode -nographics -executeMethod FerrySim.Spikes.EditorTools.SpikeBenchSceneBuilder.BuildFromCommandLine
$S/unity.sh import -batchmode -nographics -executeMethod FerrySim.Spikes.EditorTools.SpikeTileImporter.ImportFromCommandLine -spikeClean 1
$S/unity.sh player -batchmode -executeMethod FerrySim.Spikes.EditorTools.SpikePlayerBuilder.BuildFromCommandLine

# The measured matrix (each run takes the locks for its own length only): x5, x1 legs, editor, then the
# headline repeat with the no-tile control. Single runs work too, for example:
$S/run_matrix.sh x5 x1 editor headline
$S/run_player.sh b-x5 -spikeApproach B -spikeSpeed 5
$S/unity.sh editor-b-x5 -batchmode -executeMethod FerrySim.Spikes.EditorTools.SpikeEditorRunner.RunFromCommandLine \
	-spikeApproach B -spikeSpeed 5 -spikeRun editor-b-x5 -spikeOut "$PWD/artifacts/world-streaming-spike/runs/editor-b-x5"

# Tables and figures, and approach A's reproducibility (two import reports).
uv run --project data-pipeline python -I tools/spikes/world-streaming/analyze.py
python3 tools/spikes/world-streaming/compare_imports.py artifacts/world-streaming-spike/unity-import/import-report-clean.json artifacts/world-streaming-spike/unity-import/import-report.json
```

## What is real and what is not

- Terrain is real wherever a source has data: the slice's merged NAVD88 raster (CUDEM with chart corrections) inside the slice, the four cached CUDEM 1/9 arc-second tiles (read at their 4x overview) across the rest of the grid. The synthetic field only fills holes, and is tinted lavender.
- Buildings are synthetic everywhere: extruded boxes, 2,000 to 6,000 per tile on land, taller near the downtowns. They stand in for the skyline's cost, not its look.
- None of it is simulator data. Generated tiles are benchmark geometry and must not feed the core, the pipeline's validated outputs or the release.
