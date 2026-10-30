# Harbor acceptance harness

These are simulation tests, not navigation advice. The harness is opt-in: ordinary player starts do not run it. Source ownership is limited to `Harbor/Player/Journeys`, the new `Tests/HarborE2E` assemblies, this folder and `artifacts/harbor-e2e`.

`matrix.json` defines the same 1920×1080 workload for Mac and Windows: three outbound journeys and an Astoria return, paired traffic and display runs, three repetitions of terrain/no-tile controls and night/fog/busy cases, all catalog choices at local tide extrema, cold named starts, warm switches, a 5x camera sweep and separate capture journeys. Each process is cold; the OS file cache is uncontrolled. Quality and LOD are the shipping defaults, with the same explicit vSync and frame pacing on both systems. Traffic uses the shipped minute-sampled AIS and its static/interpolation/gap rules; historical replay is uncertain, and prerecorded controls do not promise collision-free encounters.

## Failure modes

The automated guards reject partial radar sweeps, zero or implausible Windows memory counters, missing evidence, unjoined event frames, buffer overflow, unexpected contact, grounding, missing/out-of-order bridge crossings, depth misses, time holds, saturated geometry queries, dropped simulation time, hard first contact, excessive force, fender bottoming, an unstable final hold, and same-runtime recording/event divergence. Controls are evaluated on the 100 Hz clock and use no wall clock or unseeded random source. Frame/event/memory samples use preallocated buffers; the Unity allocation tests calibrate `GC.Alloc` with exactly one deliberate string allocation.

## Routes and preparation

From the repository root, with the existing locked Python environment:

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python tools/harbor-benchmark/prepare.py
```

This verifies adopted depth floor A and writes candidate routes, corridor samples and edge/corner seam checks. `harbor_transit.plan` only proposes deep-water legs. Bridge waypoints retain literal ENC span identities, geometry and clearance provenance; other bends are labelled authoring estimates. The East River journeys use the west Roosevelt Island channel and the western Queensboro span. ENC MHW clearances are retained with their unavailable NAVD88 conversion; ordered horizontal crossings do not validate overhead collision geometry. St. George ends at an off-face hold, with arrival acceptance blocked by the crew question. The return also ends at an approach hold, rather than claiming a second berth landing.

The explicit EditMode test `FerrySim.HarborE2E.Tests.RealJourneyTests.AuthorAndDriveTheThreeJourneys` drives those candidates against the actual core, real contacts and day packs. It adds the existing E2E's estimated 2 kn reference-force limit and emits `routes.accepted.json`, reviewable input traces and verdicts. The checked `routes.json` is that reviewed test definition, including source/depth provenance and force limits. Astoria and Soundview start times are test conditions chosen to arrive near low-current windows of the shipped station blend; the separate busy-minute cases retain the busier conditions. This is not a claim about actual channel current accuracy.

The ordinary Unity gate runs the fast synthetic/measurement tests. Real data and scene tests are explicit and must be selected by exact name; missing data does not count as acceptance. PlayMode scene loads are additive and unloaded in teardown.

## Mac

Use the shipping `.app` installed/extracted outside the repository. `run_mac.py` launches every process from an empty temporary working folder with a cleared environment and takes the heavy lock before the Unity lock for each bounded scenario. It records external 100 ms RSS samples and the kernel's complete-lifetime `wait4` peak, including launch/preparation, with no engine-baseline subtraction.

```sh
python3 tools/harbor-benchmark/run_mac.py \
  --player '/absolute/Applications/NYC Ferry Simulator.app/Contents/MacOS/NYC Ferry Simulator' \
  --routes "$PWD/tools/harbor-benchmark/routes.json" \
  --out "$PWD/artifacts/harbor-e2e/mac-shipping"
```

Select one matrix entry with `--scenario astoria-traffic0-displays1`. `--quick` runs only 60 simulated seconds, labels the journey incomplete and is instrumentation verification. It cannot accept a long route. Existing result folders are refused. The evidence includes every raw frame, including minute stalls and captures. Controls explain tails; they never erase an event-frame violation.

## Windows, from an empty working folder

Extract the supplied `windows-handoff.zip` under `C:/ferrysim`; it contains the shipping player under `windows`, the separately identified timing player under `diagnostic`, and the scripts under `benchmark`. Use Windows PowerShell; no Python or third-party package is required to collect evidence. All input paths resolve beside the script. The matrix may take several hours.

```powershell
& 'C:/ferrysim/benchmark/run-windows-matrix.ps1' `
  -Root 'C:/ferrysim' -Output 'C:/ferrysim-results'
```

The wrapper collects the shipping matrix and three paired short diagnostic display checks into one archive. Call `run-windows.ps1` directly with `-Player`, `-Output`, and `-Quick -Scenario astoria-traffic0-displays1` for a short instrumentation check. A separate diagnostic timing build uses the same script with `-Timing`; do not label it a shipping budget verdict. The script captures external native `GetProcessMemoryInfo` counters and checks that the player's own native PSAPI source reads a nonzero, plausible working set and peak. Until that runtime check succeeds on the PC, Windows memory remains unverified. The script produces exactly one return archive, `C:/ferrysim-results.zip`, with commands, host identity and every result folder. It refuses stale output/archive paths.

Analyze the returned archive on the Mac without any Windows connection:

```sh
python3 tools/harbor-benchmark/analyze.py /absolute/ferrysim-results.zip
```

The archive is extracted into a new sibling folder; unsafe paths and existing extraction targets are refused. The analysis consumes identical artifacts on either platform. Missing required files or zero counters are unverified, never passing.

## Build identity and timing limits

Shipping budget verdicts use `scripts/release.sh` players. The separately approved editor entry point `FerrySim.Harbor.Player.Journeys.EditorTools.HarborBenchmarkBuild.BuildFromCommandLine` enables `PlayerSettings.enableFrameTimingStats` only inside a `try/finally` scope and restores it even on a failed build. Its tests exercise success and a deliberate exception. Compare the settings file's SHA-256 before/after every diagnostic build.

Only the diagnostic build should receive `-ferrysim-timing 1` / `--timing` / `-Timing`. Its CPU/main/render/GPU figures come from `FrameTimingManager`; the raw native frame-start timestamp is retained. Native GPU timing may arrive after the CPU frame, so the harness does not invent a Unity-frame correlation. The wall interval for Unity frame F is measured from its Update to Update F+1, and tile lifecycle, progress and origin events carry Unity frame F. The full event frame and ±2-frame window are retained.

Cesium 1.26.0 has public tile-created and failure callbacks plus load progress, but no public queue counter. The harness also observes tile GameObject disable/destruction and georeference changes. A passing measured maximum alone cannot prove complete queue/unload instrumentation, so `streaming_instrumentation_complete` remains false and a measured passing streaming budget stays unverified. Observed over-20 ms event frames still fail. Renderer residency never supplies simulation depth, contacts or semantic queries.

## Artifacts and strict verdicts

Each scenario produces `frames.csv`, `events.csv`, `memory.csv`, `scenario.json`, `identity.json`, the packaged `build-info.json`, an exact argument array, `Player.log`, `process.json`, external memory samples and `verdict.json` on the Mac. Windows produces the same collection inputs plus `windows-memory-proof.json`; the portable analysis creates verdicts after the archive returns. Journey recordings include actual 100 Hz controls, states, environment/geometry/traffic identities and events, then `ReplayPlayer.Verify` requires matching states/digests/events on that same runtime. Capture runs write named-stop PNGs and mark screenshot frames explicitly. Starts write per-choice `starts.json`, including real catalog refusals.

Memory gates the complete run including preparation and first view against 2,000,000,000 bytes, with steady state shown separately. First view starts before catalog preview, hashing and atomic preparation; readiness also verifies own-hull geodetic/water placement and the resident barge layer. System Used Memory starts before the first scene, while the external OS peak covers process launch as well. No process baseline is subtracted.

The 20 ms gate checks every recorded streaming event frame from the first full view onwards, including the berth, turns and returns, until a new accepted Start begins its own first-view gate; preparation/readiness maxima are reported prominently and never described as a pass. Replay/capture phases remain in the raw files and are reported separately. A missing event/timing source prevents a pass. The three-second readiness budget is independent of the product's 60-second failure timeout. `functional_verdict` means that the selected workload ran without its recorded functional failures; it does not mean all performance gates passed or that a deliberately short route reached its destination.


## Switch memory diagnosis

`switch-memory-12` and `switch-memory-24` run the Pier 11, crew-refused St. George, Astoria and Soundview sequence three or six times in one process. Each accepted first view has a boundary snapshot and a second snapshot after 10 seconds. `switch-memory-24-collect` additionally calls `Resources.UnloadUnusedAssets()` and full `GC.Collect()` between choices. This opt-in diagnostic does not change the shipping product flow or turn a failed budget into a pass.

`starts.json` preserves System Used Memory, Mono heap/used, total allocated/reserved, process working/peak/pagefile and available graphics/texture counters. Unsupported counters carry explicit validity flags. Analysis writes `switch-memory.csv` and joins independent kernel RSS/physical-footprint samples by UTC, retaining the sampling offset and leaving stale/missing samples empty. macOS uses `proc_pid_rusage(RUSAGE_INFO_V4)` for independent current and lifetime-peak physical footprint, alongside complete-child `wait4` peak RSS. A collection plateau does not by itself prove absence of a leak or identify native references.

The busy case uses the global peak shipped AIS presence minute: 2025-07-19 at 16:55 local, 243 vessels before own-vessel suppression. Reproduce its provenance with `python3 tools/harbor-benchmark/traffic_peak.py data/processed/harbor/traffic artifacts/harbor-e2e/prepared/busiest-minute.json`.


Timing fields overlap: main, render and GPU durations are not additive. Shipping wall intervals are the event-frame gate; diagnostic native GPU timing has its own timestamp. `chart_tick_ms` retains the display’s last refresh duration, so it is not summed as a per-frame cost; the matched on/off distributions measure the combined display share. Mac `pagefile_usage_bytes=0` means that Windows-only counter is not collected, not that the process has zero committed memory.


Analysis counts distinct Unity frame IDs for the 20 ms violation count. `event_count` retains the event-record count; `event_frame_count` is the unique running-frame count, and `over_20_event_records` preserves multiplicity. Multiple tile/progress/origin events on one slow frame count as one violating frame, with every per-event ±2 window retained. Use the repository analyzer when consuming the returned Windows archive; the collection bundle’s raw frame/event schema is unchanged.

The camera stress path must disable the free-fly, chase and wheelhouse controllers as well as the keyboard-switching rig. `camera.csv` records expected and actual ECEF camera positions after every rendered sweep frame, using double precision throughout. Analysis requires the complete requested distance, ordered frame/time samples and at most 0.1 m position error; a camera held at the boat cannot pass because a coroutine merely requested a route. This proof is separate from the 1x streaming budget and does not turn stress into a shipping performance pass.
