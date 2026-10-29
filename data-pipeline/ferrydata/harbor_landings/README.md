# Harbor landing evidence

Offline assessment and a separately built `operational-ais` candidate. The default
harbor tile depths and environment packs do not change when these commands run.
All distances are world NED metres; elevations/water levels are NAVD88 metres.
This is simulation evidence, not a navigational survey.

Run from the repository root, with the existing processed harbor tiles, environment
packs and fleet parquet copied into this worktree. Never write another worktree's
outputs and never wrap a whole candidate build in the heavy lock: each tile's
floor application takes it separately.

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings parity \
  --s1 /path/to/read-only/s1-choices.csv --env data/processed/harbor/env \
  --out artifacts/harbor-landings
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings candidate \
  --source data/processed/harbor --out data/processed/harbor-operational-candidate-a
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings candidate \
  --source data/processed/harbor --out data/processed/harbor-operational-candidate-b
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings matrix \
  --contacts data/processed/harbor/unity/contacts.json --env data/processed/harbor/env \
  --out artifacts/harbor-landings/before
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings matrix \
  --contacts data/processed/harbor/unity/contacts.json --env data/processed/harbor/env \
  --depth data/processed/harbor-operational-candidate-a/env/depth \
  --out artifacts/harbor-landings/candidate
```

Parity is a hard prerequisite: seven spread rows from each of the 46 assessed
berth/hull strata, >=300 total, within 1 mm for depth/UKC and exactly matching status.
A failure stops before candidate construction. `parity.csv` preserves every comparison.

The floor reuses `operational_depth` unchanged constants, identity classification,
max drafts, half-length report footprints, deepest per-cell requirement, fixed masks,
charted hazard masks and downward float32 rounding. Water is the actual harbor pack's
spatial station blend at the report time. Cross-tile reports are selected globally,
then passed to each intersecting tile, including across its margins. Candidate output
includes the qualified report CSV, floor/lowering rasters, per-tile provenance, a
verified 5 m depth set and every lowered cell within 50 m of a charted hazard.
Raster overlap duplicates are compared and counted once in the headline cell count;
the hazard CSV retains tile row/column identities and may include overlap duplicates.

The 26 declared berths remain in source order, with both hulls, all 28 days, 24 hourly
samples and a low-water sample over [0, 86340] seconds at all gauge breakpoints. Poses
follow production `LandingApproach.BowOn`, with 4 m bow clearance; full contact outlines
follow `FaceClearance.WorldOutline`. The minimum uses the quarter-metre world lattice,
every enclosed depth-grid node and edges <=0.25 m, spatial tide at each point, strict
bilinear sampling and unknown propagation. Maximum draft is preferred, with an explicit
fallback flag. Negative UKC refuses; [0, 0.3) m warns. Contact/depth failures remain
independent numerical fields. The 20 m outside-depth fallback is never acceptance evidence.

`matrix.csv` preserves each result. `summary.csv` gives counts, worst UKC and the lowest
accepted reference-point water level for each berth and hull. A finite-depth start at
one level does not establish any-tide use or the safety of a departure maneuver.

## Three excluded faces

`crew-face-notes.json` is deliberately empty. Stops 113 (East 90th St), 118 (BAT) and
137 (St. George) remain excluded regardless of depth or collision-only retention.
A future reviewed confirmation of the **existing** face is a one-line data entry:

```json
{"stop_id":"137","berth_id":"enc:02266B95280E764B","geometry_sha256":"<catalogue geometry digest>","date":"YYYY-MM-DD","source":"project crew reviewer","statement":"<reviewed face-use answer>","reviewed":true,"confirms_existing_face":true}
```

This is an explicit crew face-use confirmation tied to the exact ordered geometry,
not a general UNSARE exception or a blanket instruction to trust every existing normal.
If the crew identifies a different side/orientation, rebuild and review that geometry
first. An absent, unreviewed, stale or non-confirming note cannot unlock a start. The
build reads the note and then still requires a well-formed face plus independent
contact/depth acceptance; tests show this. A downstream picker must consume assessment
eligibility/status, not infer start eligibility from collision geometry.

Reproduced readiness evidence (reports, not independent visits):

| Face | T/K out/in <=10 m | T/K out/in <=15 m | All candidates <=10 m | All candidates <=15 m | Chart probe |
|---|---:|---:|---:|---:|---|
| East 90th St (113) | 0/0 | 0/0 | 0/0 | 0/0 | DEPARE `022609ECD91920C5` |
| Sunset Park/BAT (118) | 0/60 | 0/62 | 3/372 | 4/374 | UNSARE `02262F4139E320C5` |
| St. George (137) | 1091/0 | 2249/0 | 1499/0 | 4381/0 | Both segments UNSARE `02266524C529BE22` |

East 90th has neither the UNSARE-only classification nor outward T/K evidence. BAT
favors the inward side, which does not authorize flipping its normal. St. George has
outward evidence but no current face-use confirmation; depth and approach decisions
remain separate. All four chart/winding/land/other-structure checks were reproduced in
the read-only readiness spike; this module preserves that source evidence without
inventing an answer.

## Corridor and Pier 11 questions

Red Hook's straight-astern corridor reaches -5.499 m UKC at low water because it runs
astern into the basin; the fixed-heading samples also intersect retained structures at
20/30/40 m. Its actual start passes for both hulls. The corridor is separate evidence,
not a start blocker, a driven departure or a continuous swept-clearance proof. Its
method and results are unchanged.

The dry MORFAC point about 10 m off Pier 11's outer and north faces remains an open
chart/use question. Its actual role has not been established; no geometry is moved,
removed or reclassified to guess an answer.

## Adoption boundary

Only after a coordinator/user approval tied to the measured candidate may the caller
invoke `environment.harbor.rebuild_days_with_depth(source_env, candidate_depth, destination)`.
It verifies all 28 original packs and the candidate depth before writing, preserves
current/wind/water raw CSV bytes, rebuilds all 28 depth references and aggregate day
digests and verifies the resulting set. The harbor uses station blending, so its series
are independent of seabed changes. It refuses field-based days. Adoption changes the
harbor `env/depth` set plus all 28 `env/days/*/manifest.json` and `DIGEST` files; it does
not change the slice. Rerun the matrix on those adopted packs.

## Review-requested hazard buffers and spatial breakdown

The coordinator requested separate 10 m and 25 m variants after A's 50 m audit was
nonempty. They remove floor coverage inside the chosen exact world-metre distance
from a raster **cell centre** to any charted hazard. All AIS selections, required beds,
class parameters and constants stay A's; protected cells regain their original bed.
The float32 stored floor is already rounded toward deeper water exactly as A's bed,
so `min(original, stored floor)` on the remaining cells reproduces A there. Each tile
asserts original >= buffered >= A, and the proximity audit rejects any lowering within
the selected buffer. These variants are review artifacts and do not adopt a policy.

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings buffered \
  --source data/processed/harbor --base data/processed/harbor-operational-candidate-a \
  --out data/processed/harbor-operational-buffer-10 --buffer-m 10
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings buffered \
  --source data/processed/harbor --base data/processed/harbor-operational-candidate-a \
  --out data/processed/harbor-operational-buffer-25 --buffer-m 25
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings.proximity \
  --candidate data/processed/harbor-operational-candidate-a \
  --contacts data/processed/harbor/unity/contacts.json --out artifacts/harbor-landings/proximity-a
```

The spatial breakdown deduplicates by the global 1/9 arc-second cell identity, labels
every cell/hazard pair with its nearest stop and hazard type, and retains the exact
berth/hull identities it intersects. Start and approach membership use **whole cell
footprints**; centre-in-start membership is retained separately. The first 100 m is the
continuous straight extrusion of the contact hull from the actual start 100 m astern,
at the existing fixed heading. It categorizes the same straight corridor direction;
it neither changes the existing corridor gate/method nor claims a feasible maneuver.
Excluded faces have no start pose and therefore no assessed hull region.

A source cell outside those geometric regions can affect a hull through the two
bilinear sampling stages (source raster to 5 m depth set, then depth set to hull points).
The fully recomputed per-berth matrix is the authoritative start comparison, and the
geometric split must not be treated as proof that all other cells are irrelevant.

### One approved scheduling exception

After starvation behind queued Unity gates, the coordinator explicitly authorized one
outer heavy-lock window for A-10, A-25 and their matrices. Only inside that already-held
window, `buffered --heavy-lock-held` disables this builder's inner tile locks. The default
still locks each tile, and repeat builds return to that default. This does not authorize
wrapping `ferrydata build --harbor`, the harbor environment CLI or the traffic build.
The retained batch driver runs sequentially and records start/end/elapsed time outside
the deterministic outputs. Tests verify the absence of nested locking in this mode.
