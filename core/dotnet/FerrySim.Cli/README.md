# ferrysim CLI

Headless runner for standard maneuvers and scripted control traces. It talks to the model only through the contracts (`ISimulationFactory`, `ISimulation`, `VesselParameters`, `IEnvironment`), so it works unchanged when the dynamics model replaces the kinematic placeholder.

```
dotnet run --project core/dotnet/FerrySim.Cli -- maneuver turning-circle --vessel vessels/t-class.json --out artifacts/maneuvers/t-turning-circle.csv
dotnet run --project core/dotnet/FerrySim.Cli -- summarize artifacts/maneuvers/t-turning-circle.csv [--json]
dotnet run --project core/dotnet/FerrySim.Cli -- run --vessel vessels/t-class.json --inputs trace.csv --out run.csv
uv run tools/plot_maneuver.py artifacts/maneuvers/t-turning-circle.csv --out artifacts/maneuvers/t-turning-circle.png
```

`ferrysim --help` lists every option. Exit codes: 0 ok, 1 the command could not be carried out (missing file, speed not reachable), 2 bad arguments (usage is printed).

## How a maneuver runs

1. For maneuvers that start under way, the CLI bisects on a common lever position (both engines, helm amidships) until the vessel settles at the requested speed **through water**. Each trial starts at the target speed and runs until the speed has changed by no more than 0.005 m/s over 20 s (at least 30 s, at most 900 s). The target is met within 0.01 m/s. If full ahead cannot reach the speed, the command fails with exit code 1.
2. The vessel then settles at that lever position and the maneuver inputs start on a whole second. `maneuver_start_s` in the CSV header says when; `--lead-in-s` (default 10) keeps the last seconds of steady running in the CSV so plots show the approach.
3. Maneuvers that start at rest (`pivot`, `acceleration`, `transit`) begin dead in the water, that is, drifting with the current, with the machinery at rest in neutral.

Every simulation of a run starts where the start options say, the lever search's trials included: `--start-north-m` with `--start-east-m` (world metres), or `--start-lat` with `--start-lon` (WGS84 degrees, converted through the world frame), and `--start-heading-deg` (`--heading-deg` is its older name; give one). A settled start adds the current at the start position. Without them a run starts at the world origin (north 0, east 0, near The Battery) heading north, and writes exactly the same bytes as before the options existed: the 12 calm manoeuvres (the six below for the T-class and the K-class) are pinned by their SHA-256 in `CalmManeuverBitIdentityTests`. With them the header gains `start_north_m` and `start_east_m` (and `start_lat` and `start_lon` when given) after `initial_heading_deg`. The origin is aground next to The Battery in a real pack, so pack runs normally give a start.

The default test speed is the vessel's `performance.serviceSpeed`. All metrics are measured from the maneuver start, over the ground, along and across the heading at that instant (lateral is positive to starboard).

## Maneuvers and metrics

| Maneuver | Inputs | Ends | Metrics |
|---|---|---|---|
| `turning-circle` | Helm hard over (`--direction`, default starboard), levers held | Heading change reaches 370 degrees (cap 1800 s) | Advance (along the original heading at 90 degrees of heading change), transfer (across it at 90 degrees, positive toward the turn), tactical diameter (transfer at 180 degrees), time to 90/180/360 degrees |
| `zigzag` | Helm reversed each time the heading change reaches `--angle-deg` (10), starting `--first` starboard; rudder `--rudder-deg` (10) | Third helm order (cap 1800 s) | First overshoot (peak heading change after the first reversal, minus the check angle), second overshoot (same after the second reversal) |
| `crash-stop` | Both levers to full astern, helm amidships | 5 s after the stop (cap 1800 s) | Head reach, track reach, lateral deviation at the stop and the maximum, time to stop |
| `stopping` | Both levers to neutral, helm amidships | 5 s after the stop (cap 1800 s) | Same as `crash-stop` |
| `pivot` | Port ahead, starboard astern at `--lever` (0.5); `--direction port` mirrors it | `--duration-s` (default 90 s) | Yaw rate (mean over the last quarter), heading change, drift (largest distance from the start point), net displacement |
| `acceleration` | Both levers to full ahead from rest | Speed steady to 0.02 m/s over 60 s (at least 120 s, cap 900 s) | Steady speed through water, time and distance to 90% of it |
| `transit` | From rest, both levers at the lever that gives the test speed (`--speed-kn`, default the service speed) through water in calm deep water, found before the run; the helm steers `--waypoints` in turn | The last waypoint reached (cap 3600 s) | Arrival (1 or 0) and the time to it, closest approach to each waypoint, largest distance off the route, least depth under the keel, distance, mean speed through water |

`--duration-s` replaces the end condition with a fixed length. Metrics a short record cannot reach are reported as "not reached" (`null` in JSON), never as zero.

### Transit

`--waypoints <file.csv>` holds rows of `north_m,east_m` (world metres, a `north_m,east_m` header, `#` lines skipped). The first leg runs from the start position to the first waypoint. Line-of-sight guidance turns the leg's bearing toward the leg by atan(cross-track error / 200 m), so a cross current that sets the vessel off is steered back, and a heading hold follows that course: helm = 2 × heading error (rad) − 8 s × yaw rate (rad/s), within ±1. A waypoint is reached within `--arrive-m` (default 50 m) or once the vessel is past the line through it square to its leg. The header records the route (`param.waypoints`), the gains, the lever (`settle.lever`, `settle.stw_kn`, `settle.environment: calm`) and, after the run, `result.waypoints_reached`, `result.arrived` and `result.arrived_s`. Holding a lever rather than a speed is how a ferry runs: in shallow water or in a current the speed through water differs from the calm-water figure, and the CSV shows by how much.

## CSV

`#` header comments hold the vessel id, maneuver and its parameters, the environment spec (and, for a pack, the lines above), the model type name returned by the factory, the factory type name, the physics and sample rates, and the settle result. They contain no timestamps or paths, so the same run gives the same bytes. The columns are fixed (`TelemetryColumns.Names`): SI with unit suffixes plus `kn` and `deg` convenience columns. Each row holds the state at `time_s` and the controls that start applying at that instant (`port_lever`, `starboard_lever`, `helm`). Rows are written every 1/`--sample-hz` s (default 10 Hz; it must divide the 100 Hz physics rate).

## Control traces

`run` replays `time_s,port_lever,starboard_lever,helm`. A row applies from its time until the next row. Controls are neutral before the first row. Times round to the nearest 10 ms step and must increase. The replay ends at the last row's time unless `--duration-s` says otherwise. The vessel starts at rest.

## Environment spec

`calm`, or comma-separated items:

- `current=<speed><unit>@<deg>`: the direction the water flows toward, unless prefixed `from`.
- `wind=<speed><unit>@from<deg>`: the direction the wind blows from. A bare `@<deg>` also means from; prefix `to` for the direction it blows toward.
- `depth=<n>m` or `ft`.

Speeds take `kn`, `kt`, `m/s` or `mps`. Missing items stay calm (no current, no wind, 50 m deep). The environment is uniform in space and time.

## A real day: environment packs

`--env-pack <day folder> --start-utc <time>` runs on one day of an environment pack (ferrysim-env/1) instead of `--env`, with that day's tide, current, wind and depths:

```
dotnet run --project core/dotnet/FerrySim.Cli -- maneuver turning-circle --vessel vessels/t-class.json --out turn.csv \
  --env-pack data/processed/lower-east-river/env/days/2025-07-14 --start-utc 2025-07-14T16:30:00Z
```

- The day folder is `…/<region>/env/days/<date>`; the depth set is read from `…/<region>/env/depth`. Both are verified (file hashes and DIGEST files) before anything runs, and a pack that does not match fails with exit code 1 naming the file.
- `--start-utc` is the scenario's start, simulation time zero, as ISO 8601 (`Z`, an offset, or neither for UTC, as Unity's `scenario_start_utc` reads it). It must lie inside the pack's time coverage. `--env-pack` needs it, it needs `--env-pack`, and `--env-pack` cannot be combined with `--env` (exit code 2 otherwise).
- Runs start at the world origin unless the start options place them (above): the origin is aground next to The Battery.
- The CSV header's `environment` line says `pack`, followed by `environment_pack` (region/date), `environment_digest` (the day digest), `environment_epoch_utc` (the pack's local midnight), `scenario_start_utc`, `environment_wind_station` (or `none (still air)`), and `environment_depth_misses` and `environment_time_holds`: how many depth samples fell off the depth grids and how many samples fell outside the pack's time window during the recorded run. The console says so too when either is not zero.
- Then `environment_current_field`: `channel-harmonic/1` and the field's digest when the day names a current field, or `station-blend` when it does not (the harbor's packs, for now). With a channel field, `environment_current_outside_samples` counts the current samples of the recorded run that fell outside the field's grids and used the station blend (`ChannelCurrentField.OutsideSamples`).

```
dotnet run --project core/dotnet/FerrySim.Cli -- maneuver transit --vessel vessels/t-class.json --out transit.csv \
  --env-pack data/processed/harbor/env/days/2025-07-14 --start-utc 2025-07-14T16:00:00Z \
  --start-north-m 122 --start-east-m 733 --start-heading-deg 225 --waypoints route.csv
```

Calm runs, and runs with `--env`, write exactly the same bytes as before.

## Deviations from IMO MSC.137(76) and MSC/Circ.1053

- **Test speed:** the standard asks for at least 90% of the speed at 85% of maximum engine output. The default here is the vessel's published service speed through water, and `--speed-kn` overrides it.
- **Rudder:** the turning circle uses helm 1.0 (hard over), which maps to the vessel `controls.hardOverAngle` in follow-up mode (20° in both shipped crew-reviewed vessels). The zigzag converts `--rudder-deg` to helm by dividing by `--max-rudder-deg` (by default, the vessel hard-over angle), which assumes a linear follow-up steering model. Whether the real steering is follow-up or non-follow-up is still open (docs/conventions.md); `--helm` overrides the conversion.
- **Stopped:** the standard measures to "dead in the water". Here stopped means speed through water at or below 0.1 m/s, because a coasting vessel only approaches zero. Reach is over the ground and is only comparable with the IMO criteria in calm water.
- **Stopping test:** `crash-stop` goes to full astern on both levers. `stopping` (levers to neutral) is not an IMO test.
- **Turning circle length:** it runs to 370 degrees, not 540, and does not report the steady turning diameter.
- **Zigzag:** the 10/10 test is run to the third helm order, which is enough for both overshoots. The 20/20 test is `--angle-deg 20 --rudder-deg 20`.
- **Pivot and acceleration** are not IMO maneuvers. The pivot lever (0.5) is an arbitrary default.

The calm maneuver CSV hashes were regenerated for the crew-reported 20° T/K steering stops (2026-10-26), the crew-approved 4.5 L effective area/span/yaw-damping calibration, and the parameter-derived zigzag divisor. Explicit `--max-rudder-deg` remains a conversion override; requested rudder degrees above that chosen maximum are refused, while the core still enforces the vessel physical stop. No maneuver acceptance limit changed.
