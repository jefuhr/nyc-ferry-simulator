# M2: East River Trip Acceptance

Owner: **slice-e2e-tests**. Both PlayMode tests load the integrated `LowerEastRiverSlice.unity`, drive its `VesselSimulationRunner` with committed inputs, and render the real world and wheelhouse UI. No controller runs during acceptance and no production component is changed.

## Route Rationale

The northbound East River stop order is **Pier 11 → DUMBO → South Williamsburg**. Crew confirmed that DUMBO / Fulton Ferry is south (downstream) of the Brooklyn Bridge. The first leg therefore makes a direct crossing without passing under any bridge; the second leg supplies the genuine Brooklyn Bridge and Manhattan Bridge passages.

| Leg | Departure | Route | Arrival |
|---|---|---|---|
| `pier11-dumbo` | Rest, bow-on, 4 m clear of Pier 11's `primary_face` | Back clear astern, turn, direct crossing, slow bow-in approach; under 360 s including landing and at least 60 s elapsed since first contact | DUMBO's `primary_face` |
| `dumbo-south-williamsburg` | Rest, bow-on, 4 m clear of DUMBO's `primary_face` | Back clear astern, turn north/up-river, pass the Brooklyn Bridge then Manhattan Bridge main spans, slow bow-in approach and at least 60 s elapsed since first contact | South Williamsburg's `primary_face`, the long NW river side, crew confirmed on 2026-10-24 |

Faces are selected through `LandingGeometry.PrimaryFace(stop_id)`, which reads `primary_face`; no physical segment ID chooses a berth. South Williamsburg's current selected face has ID 149111046. Its separate crew water-side evidence permits runtime contact while the pipeline's chart verification remains false.

The charted dry mooring point **MORFAC `022646E95F81764B`** lies 11.2 m off South Williamsburg's NW face and 2.1 m off its centre line. Its role and clearance remain unverified. It is **not contact geometry**, so the trace does not steer around a phantom obstacle; this real-world caveat remains unresolved by software acceptance.

## Failure Modes and Assertions

Both legs guard against missing/wrong inputs, a refused or overlapping start, departure contact, touching any other arrival side or landing, a hard impact, drifting or losing contact while holding, state divergence, recording/replay divergence, geometry mismatch, and a UI detached from the real runner. Leg 1 also rejects a bridge detour or duration of six minutes or more. Leg 2 rejects a missed, out-of-order or tower-adjacent required bridge passage.

- Start at rest, bow-on, 3–5 m clear of the departure primary face and clear of every other contact segment. Back clear before the crossing. Record contact at every 100 Hz physics step; only the arrival primary face may ever be touched. The touched set must be nonempty and a subset of that face's segment IDs; a valid landing need not touch every subsegment.
- Measure first-contact SOG from the state **before** the touching step, peak contact force, deepest combined hull/landing fender compression, bottoming, and final hold force. A soft landing must satisfy both thresholds and never bottom a fender.
- Finish at least 60 s after first contact. The summary field `elapsed_since_first_contact_s` measures elapsed time from the first touching step to the final step, including initial fender rebounds; it does not measure continuous contact. At every one of the final 1,000 steps, maintain positive contact force only on the arrival face and SOG below 0.1 kn. Finish bow-in within five degrees of the face normal, with the hull overlapping that primary face.
- Read all three World's main-span polygons directly from `data/processed/lower-east-river/bridges.geojson`, independently of rendering. Leg 1's reference track may never enter **any** span polygon. Leg 2 must enter Brooklyn Bridge's and Manhattan Bridge's main spans in that order, between the actual scene greybox towers' inner faces with half a vessel length of margin. Required bridge screenshots are taken while the reference point is still inside the polygon. This is horizontal passage, not overhead collision validation.
- Create a second in-process core simulation with the same vessel, exact initial state, calm environment and independently loaded contact geometry. Compare IEEE-754 bits of **all 13 state fields initially and after every step**, plus both state digests.
- Record at 100 Hz and verify every recorded row and digest through `ReplayPlayer.Verify`. Match `world_geometry_digest` to the independently loaded geometry; require a replay without geometry to be rejected.
- Keep the real wheelhouse UI bound to the runner and assert rendered world/UI content. Release screenshot textures and unload scene state even on failure.

The segmented-face regression splits DUMBO's arrival face into five contiguous collinear segments with the same surface and replays the unchanged first-leg trace. A valid landing on the middle segment must pass, while empty contact and contact with each non-arrival segment must fail.

## Threshold Provenance

`landing-thresholds.json` sets first-contact speed to **1.5 kn**, confidence **`crew-reported`**, source **`crew-reported: project crew reviewer, 2026-10-24`**. Crew described that limit as about right.

The peak-force limit remains **estimated**: computed on every run with `ContactScenarios.BowOnLanding` at **2 kn** against `EstimatedBargeFender`. The 2 kn model reference sits above the crew's speed limit as a force margin. `ContactScenarios.cs` is verbatim core contact-scenario code because Unity does not compile `core/dotnet`; the test compares complete source bytes before using it. Neither reference is a measured real-world safe operating envelope.

## Trace Authoring

`pier11-dumbo.inputs.csv` and `dumbo-south-williamsburg.inputs.csv` use the CLI/bridge format `time_s,port_lever,starboard_lever,helm`. Each row applies until the next; changes sit on the 100 Hz grid and a final row supplies the end time. Acceptance reads these fixtures unchanged.

`TraceAuthoring.Generate` makes both traces offline with the real core dynamics, T-class file and loaded contact geometry. The start and arrival follow selected faces. River waypoints, transit speeds and feedback gains are authoring estimates. Commands are sampled every 0.1 s and rounded to hundredths; both levers hold ahead idle for 60 s after first contact, allowing initial fender rebounds. Continuous positive contact is required throughout the final ten seconds. Regenerate only when deliberately reviewing a fixture change:

```sh
lockf -k "$HOME/FerrySim-cache/unity.lock" /Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath "$PWD/unity" -executeMethod FerrySim.E2E.TraceAuthoring.Generate -quit \
  -logFile "$PWD/artifacts/e2e/trace-authoring.log"
```

Authoring writes a diagnostic track per leg under ignored `artifacts/e2e/`. It uses no new packages, live services or inputs beyond the repository's existing data. The waypoints and controls are not operating guidance.

## Run and Evidence

Build the processed slice from the shared read-only raw cache first:

```sh
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river
uv run --project data-pipeline python data-pipeline/ferrydata export-unity --slice lower-east-river
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all
mkdir -p artifacts/e2e
```

Run from the repository root. Every Unity invocation takes the shared lock. PlayMode renders, so omit `-nographics`:

```sh
lockf -k "$HOME/FerrySim-cache/unity.lock" /Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath "$PWD/unity" -runTests -testPlatform PlayMode \
  -assemblyNames FerrySim.Unity.Tests.E2E \
  -testResults "$PWD/artifacts/e2e/green-two-legs.xml" -logFile "$PWD/artifacts/e2e/green-two-legs.log"
```

For complete suites, omit `-assemblyNames`; use `-testPlatform EditMode -nographics` for EditMode. Judge the root XML `test-run` result and failed count, not Unity's exit code.

| Artifact under `artifacts/e2e/` | Contents |
|---|---|
| `reds/detour-and-missing.xml` | Required red: old 896.39 s detour fails the new no-bridge assertion, and missing leg 2 fixture fails existence |
| `reds/old-detour.inputs.csv`, `reds/old-detour.summary.json` | Exact old input and measured detour evidence |
| `reds/dumbo-wrong.xml`, `reds/dumbo-wrong.inputs.csv`, `reds/dumbo-wrong.summary.json` | Required red: deliberately wrong 20 s neutral trace fails arrival |
| `green-two-legs.xml`, `green-both-full-editmode.xml`, `green-both-full-playmode.xml` | Focused and complete suite green evidence, with matching logs |
| `<leg>/*-t-class-run*.csv` and companion `.inputs.csv` | Replayable 100 Hz recording and lossless applied controls; summary identifies the final recording |
| `<leg>/states.ieee754.csv` | Initial state and every post-step state, 13 hexadecimal IEEE-754 doubles in header order |
| `<leg>/summary.json` | Duration, elapsed time since first contact, contact speed, force/compression/final hold, touched IDs, per-bridge steps, geometry/state digests, parity/replay counts and recording filenames |
| `pier11-dumbo/departure.png`, `pier11-dumbo/held.png` | Pier 11 departure and held at DUMBO with real UI |
| `dumbo-south-williamsburg/under-brooklyn-bridge.png`, `dumbo-south-williamsburg/under-manhattan-bridge.png`, `dumbo-south-williamsburg/held.png` | Both true bridge passages and held at South Williamsburg with real UI |
| `handoff.md` | Exact commands/results, both legs' numbers and individual visual screenshot review |

## M3: Real Days

`EnvironmentTripTests` runs the integrated scene on environment packs (format `ferrysim-env/1`). Every scenario is made by the scene's `IScenarioCatalog.TryCreate` and started by its `IScenarioLauncher.TryStart` (`SliceBootstrap.ScenarioCatalog`, `ScenarioLauncher`); every run is recorded at 100 Hz, and its trace (`*.csv`, `*.inputs.csv`), `summary.json` and screenshots are kept under `artifacts/e2e/environment/<test>/`. Build the packs before running (Run and Evidence).

| Test | Scenario | Checks |
|---|---|---|
| `Drift_InNeutralByACurrentStation_IsTheStationsPredictedCurrent` | Corlears Hook's day and time on 2025-07-20 at 14:48 EDT (strong steady flood, 0.5 m/s wind), started at current station NYH1921 in open water, engines in neutral, 130 s | From 10 s on, the mean ground velocity is the station's predicted current within 0.05 m/s and 2°, and every step within 0.08 m/s; nothing touched. `drift.csv` has the track, the prediction and the field at the hull |
| `Tide_TheWaterABargeAndTheDepthStandAtThePacksLevel_NearHighAndNearLowWater` | Pier 11 on 2025-07-14 at the daylight high and low water, found from the pack | The drawn water, the readout, every floating landing and the mesh at the pack's level at the vessel (read independently), the placement reading the same surface, depth under the keel = level − bed − `maxDraft`; the range over 1 m. `tide-high.png`, `tide-low.png` |
| `Pier11_OnTheFlood_...`, `Pier11_OnTheEbb_...` | Pier 11 on the flood (2025-07-20 14:48 EDT, about 1 m/s onto the face) and on the ebb (2025-04-14 15:12 EDT, about 1 m/s off it), T-class | The scripted landing (`PackLandingControls`) backs 15 m clear without touching, comes back bow-in and holds 60 s: first contact at most 1.5 kn, peak force under the 2 kn reference landing's, no fender bottoming, never aground, only the primary face touched, the final 10 s at rest in contact at every step, square to the face within 5°; the recording replays bit for bit. `held.png` |
| `SouthWilliamsburg_AtTheScenesDayAndTime_LandsSoftWithoutGrounding` | South Williamsburg at the scene's own 2025-07-20 18:48 EDT (slack, about 0.78 m of tide) | The same landing checks at the landing where the depth grid is shallowest, with about 0.1 m under the keel |
| `Replay_APackRunReplaysBitForBit_AndAnotherDaysPackIsRefused` | 60 s of the Pier 11 ebb run | The recording's pack, day digest, epoch and geometry digest; bit-for-bit replay through `ReplayPlayer.Verify` and in the scene; the same recording against 2025-04-15's pack refused by both, naming both digests |

**The scripted landing.** `PackLandingControls` uses `TraceAuthoring.Navigate`'s helm, twist and speed laws and the trips' command rate (every 0.1 s, rounded to hundredths), but runs in the loop rather than from a committed trace: an open-loop trace authored on today's current field would miss the face once the current field changes (the current-field task replaces the provisional station blend). Its applied controls are recorded and replayed bit for bit. On the face it holds with ahead idle, more when the current pulls the hull off, and a twist that keeps the bow square: a following current turns a bow pinned on a face away from square. The back-off is 15 m because Pier 11's berth in the depth grid ends 35 m off the face (Slice/README.md, Known limits). Gains are authoring estimates, not operating guidance.

**Drift tolerance.** At rest in the water the core's relative-velocity model carries the hull with the water at the hull exactly (a run without wind gives no difference at all). What separates the drift from the station's prediction is the field between stations as the hull moves about 220 m from the station (up to 0.03 m/s with the station blend) and the day's 0.5 m/s wind (leeway up to 0.05 m/s). 0.05 m/s on the mean is about 3% of the current and 0.1 kn. A future current field that does not reproduce the station's prediction at the station would fail this test, which is intended.

## Limits

The M2 trips are deterministic software acceptance in a **calm uniform environment**, 50 m deep, without wind or current; the M3 tests above use real days, with the provisional current field and uniform wind. Faces and fenders remain estimates; crew-confirmed orientation does not survey their positions or material properties. **Pier 11 is modelled as a single berth** even though real operations use multiple positions. The two traces are independently initialized legs; they do not verify a continuous turnaround or passenger stop.

Only loaded contact faces obstruct the vessel. Shoreline, bridge-pier, overhead and the charted mooring point's collision/clearance are unverified. Many vessel parameters remain estimates. Same-runtime bit-for-bit parity includes a headless **in-process** core simulation and replay; it does not establish CoreCLR/IL2CPP equality or player-build behavior. The integrated slice is editor-only. Screenshots are rendered evidence, not physical-device, manual-helm or real-world safety acceptance.
