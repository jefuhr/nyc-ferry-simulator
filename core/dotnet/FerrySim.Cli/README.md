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
3. Maneuvers that start at rest (`pivot`, `acceleration`) begin dead in the water, that is, drifting with the current, with the machinery at rest in neutral.

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

`--duration-s` replaces the end condition with a fixed length. Metrics a short record cannot reach are reported as "not reached" (`null` in JSON), never as zero.

## CSV

`#` header comments hold the vessel id, maneuver and its parameters, the environment spec, the model type name returned by the factory, the factory type name, the physics and sample rates, and the settle result. They contain no timestamps or paths, so the same run gives the same bytes. The columns are fixed (`TelemetryColumns.Names`): SI with unit suffixes plus `kn` and `deg` convenience columns. Each row holds the state at `time_s` and the controls that start applying at that instant (`port_lever`, `starboard_lever`, `helm`). Rows are written every 1/`--sample-hz` s (default 10 Hz; it must divide the 100 Hz physics rate).

## Control traces

`run` replays `time_s,port_lever,starboard_lever,helm`. A row applies from its time until the next row. Controls are neutral before the first row. Times round to the nearest 10 ms step and must increase. The replay ends at the last row's time unless `--duration-s` says otherwise. The vessel starts at rest.

## Environment spec

`calm`, or comma-separated items:

- `current=<speed><unit>@<deg>`: the direction the water flows toward, unless prefixed `from`.
- `wind=<speed><unit>@from<deg>`: the direction the wind blows from. A bare `@<deg>` also means from; prefix `to` for the direction it blows toward.
- `depth=<n>m` or `ft`.

Speeds take `kn`, `kt`, `m/s` or `mps`. Missing items stay calm (no current, no wind, 50 m deep). The environment is uniform in space and time.

## Deviations from IMO MSC.137(76) and MSC/Circ.1053

- **Test speed:** the standard asks for at least 90% of the speed at 85% of maximum engine output. The default here is the vessel's published service speed through water, and `--speed-kn` overrides it.
- **Rudder:** the turning circle uses helm 1.0 (hard over), which is the 35 degree rudder only if the control model maps it that way. The zigzag converts `--rudder-deg` to helm by dividing by `--max-rudder-deg` (35), which assumes a linear follow-up steering model. Whether the real steering is follow-up or non-follow-up is still open (docs/conventions.md); `--helm` overrides the conversion.
- **Stopped:** the standard measures to "dead in the water". Here stopped means speed through water at or below 0.1 m/s, because a coasting vessel only approaches zero. Reach is over the ground and is only comparable with the IMO criteria in calm water.
- **Stopping test:** `crash-stop` goes to full astern on both levers. `stopping` (levers to neutral) is not an IMO test.
- **Turning circle length:** it runs to 370 degrees, not 540, and does not report the steady turning diameter.
- **Zigzag:** the 10/10 test is run to the third helm order, which is enough for both overshoots. The 20/20 test is `--angle-deg 20 --rudder-deg 20`.
- **Pivot and acceleration** are not IMO maneuvers. The pivot lever (0.5) is an arbitrary default.
