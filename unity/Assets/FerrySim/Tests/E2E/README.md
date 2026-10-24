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

## Limits

This is deterministic software acceptance in a **calm uniform environment**, 50 m deep, without wind or current. Faces and fenders remain estimates; crew-confirmed orientation does not survey their positions or material properties. **Pier 11 is modelled as a single berth** even though real operations use multiple positions. The two traces are independently initialized legs; they do not verify a continuous turnaround or passenger stop.

Only loaded contact faces obstruct the vessel. Shoreline, bridge-pier, overhead and the charted mooring point's collision/clearance are unverified. Many vessel parameters remain estimates. Same-runtime bit-for-bit parity includes a headless **in-process** core simulation and replay; it does not establish CoreCLR/IL2CPP equality or player-build behavior. The integrated slice is editor-only. Screenshots are rendered evidence, not physical-device, manual-helm or real-world safety acceptance.
