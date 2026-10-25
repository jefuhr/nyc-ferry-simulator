# Environment packs in the core

The core reads environment packs (format `ferrysim-env/1`), so a run has the tide, current, wind and depth of a real day. The data pipeline writes the packs; the format is the contract between the two.

The code is in namespace `FerrySim.Core.Env`. It is not `FerrySim.Core.Environment`, because that name would hide `System.Environment` from all code in `FerrySim.Core.*` namespaces.

## Contents

| File | Role |
|---|---|
| `EnvironmentPack.cs` | One scenario day: time base, coverage window, stations and depth set. Immutable, so one pack can back any number of environments |
| `EnvironmentStation.cs` | Current, water level and wind stations with their series |
| `StationSeries.cs` | Time series and how they are read between and beyond samples (`SeriesTime`), held spans (`SeriesGap`) |
| `DepthSet.cs` | Bed-elevation grids and bilinear sampling |
| `ICurrentField.cs`, `StationBlendCurrentField.cs` | The pluggable current field and its provisional baseline |
| `DataEnvironment.cs` | `IEnvironment` for one pack day from a scenario start |
| `EnvironmentCoverage.cs` | What the environment has had to make up, for the UI |

`FerrySim.Core.Serialization` (which has Newtonsoft) holds the file side:

| File | Role |
|---|---|
| `EnvironmentPackJson.cs` | Loads and verifies a day folder and its depth set |
| `EnvironmentPackDigests.cs` | The digests and DIGEST files, and Python's float `repr` |
| `EnvironmentPackCsv.cs` | The series files |
| `EnvironmentPackWriter.cs` | Writes a pack, for synthetic test packs (core, CLI and Unity tests) |

## Reading a pack

```csharp
var pack = EnvironmentPackJson.Load("data/processed/lower-east-river/env/days/2025-07-14");
var environment = new DataEnvironment(pack, scenarioStartUtc);
var simulation = new VesselDynamicsFactory().Create(vessel, environment, initialState);
```

`Load` refuses, with an `EnvironmentPackException` naming the file, anything that does not match:

- each series file and grid file against the SHA-256 its manifest lists;
- each folder's `DIGEST` (64 lower-case hex characters and a newline) against the bytes it covers, which include the manifest itself, so a changed byte anywhere is refused (digest rule as revised on 2026-10-25);
- the day's `depth_digest` against the depth set's `DIGEST`;
- the manifests: format strings, region (the day's must be the depth set's), date and `epoch_utc` (local midnight in America/New_York on that date, by the US daylight-saving rule in force since 2007), coverage window, keys (unknown or missing keys are refused), finite numbers, grid file names (`<id>.f32`), grid shapes, `nodata` = `"NaN"`, `datum` = `NAVD88`;
- every station's `north_m`/`east_m` against its `lat`/`lon` through `LocalTangentPlane.World`, within 1 cm, which catches a swapped or misplaced frame;
- the series files: UTF-8, the exact header, `\n` line ends only, rows grouped by station with strictly increasing `t_s`, stations listed in the manifest (and every listed station with rows), finite numbers parsed with the invariant culture, `observed` or `predicted` water levels, non-negative gusts.

A day may have no wind station (`wind_stations` is `[]` and `wind.csv` only its header). The air is then still, and the coverage report says so. Current and water level stations are required.

## Sampling

Simulation time t, seconds since the scenario start, reads the pack at t + (scenario start − epoch). Outside the pack's coverage window every value is held at the window's nearer edge, and the hold is counted.

| Sample | What it is |
|---|---|
| `SampleDepth` | Water level minus bed elevation. The level is each gauge's series at that time, blended between gauges by inverse distance squared (at a gauge it is that gauge's). The bed is bilinear between the four surrounding cell centres of the grid that contains the point. Outside every grid, or where one of the four is unknown (NaN), it is the depth set's `outside_depth_m`, and the miss is counted |
| `SampleCurrent` | The `ICurrentField` at that pack time |
| `SampleWind` | Uniform: the first wind station's mean wind at that time; gusts are not applied |
| `SampleWaterLevel`, `SampleBedElevation` | The two parts of the depth, for rendering the water surface and charts |

A series is read linearly between samples up to 1800 s apart. Across a longer gap it holds the nearer sample (the earlier one at the midpoint), and before its first or after its last sample it holds that sample. `EnvironmentPack.HeldGaps` lists where that happens inside the coverage window.

Every sample is deterministic and allocation-free once the pack is loaded. The per-time caches (station values at the last time asked for) and the counters belong to one simulation thread.

## The current field

`ICurrentField` turns station data into a current anywhere: `Sample(north, east, packTime)`. `DataEnvironment` uses `StationBlendCurrentField` unless it is given another.

`StationBlendCurrentField` is a PROVISIONAL baseline. It interpolates each station's north and east components in time (so a reversing current passes through slack), then blends the stations by inverse distance squared. At a station it is that station's value exactly; with one station it is uniform, so the hull feels no spurious shear. It knows nothing of the shoreline, channels or eddies. The wave 1 env-research task picks the real method, which replaces it in wave 2 behind the same interface, without a format change.

The vessel dynamics samples the current along the hull as well as at its reference point (core/FerrySim.Core/Dynamics/README.md, Current along the hull), so the field's gradients reach the hull.

## Coverage report

`DataEnvironment.Coverage` is a snapshot for the UI:

- the pack's time window in UTC and in this run's simulation time;
- depth samples and misses (outside the grids or unknown bed, which used the outside depth);
- samples and time holds (outside the window, held at its edge);
- whether the wind is missing for the day (still air);
- the manifest's notes and the held gaps in the series.

`ResetCounters` zeroes the counts, for example before the run a report is about.

## Known limitations

- The station blend is a placeholder for the real current method (above).
- Wind is uniform and taken as the 10 m wind `IEnvironment` asks for: no correction from the sensor's height, no gusts, no shielding.
- `outside_depth_m` is returned as it is, without the tide.
- Grids in a set must not overlap. Two grids that only share a boundary are fine, but a point between two lattices that do not share their edge row of centres belongs to neither and falls back to the outside depth. The harbor tiles need a shared edge.
- The daylight-saving check knows the US rule from 2007 only.
