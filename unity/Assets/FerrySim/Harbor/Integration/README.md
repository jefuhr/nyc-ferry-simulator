# Harbor integration

`FerrySim.Harbor.Integration` is the harbor scene's composition root: `HarborIntegrationFactory`, the `HarborSceneServiceFactory` that `HarborSceneBuilder` puts on the "Harbor composition" object beside `HarborBootstrap`. `Create(context)` verifies the runtime pack and prepares the resident services; the bootstrap connects them (`Harbor/README.md`, Integration). Nothing here loads, samples, replays or simulates on its own.

| Service | Real implementation |
|---|---|
| Water | `HarborWaterForwarder`, one stable `IWaterSurface` over the runtime pack's canonical datum. Each environment-backed harbor start or replay binds that run's `HarborWaterService` (datum plus `RunEnvironment.WaterLevel`), subscribed before the navigation and traffic builders so it is bound before they react and before the vessel is first placed. Before the first run it is the datum alone, NAVD88 zero with no tide, so the navigation geometry can be anchored; it never extrapolates. |
| Visual water | One `HarborWaterPatch` per depth grid (40 × 40 cells of 100 m) under the water root, with the shared `World/Materials/Water.mat`, rebuilt for each run and driven by the runner's `RenderTime`. |
| Runtime pack | `HarborRuntimePackJson.Load` of `runtimePackFolder`; the runner loads its own copy for each start and the two are checked by digest. |
| Landings | `HarborContacts.Load` of `contactsFile` with `Data/landing-contact-surfaces.json`. |
| Catalog | `HarborScenarioCatalog` over the landings, both vessel files, the runner's `EnvironmentPacks` and `TrafficPacks`, the landing audit's marks and `SharedDepthEnvironments`, which previews depth through `EnvironmentPacks.LoadHarborDay` so the verified depth set is read once for the preview and the atomic start. |
| Launcher | `HarborLauncher`: `VesselSimulationRunner.InitializeHarbor` with the slice contact parser and `FaceClearance` as the start gate, so a refused candidate leaves the current run going. |
| Navigation | `HarborNavigationWorld.Build` with the pack, the georeference, the forwarding water and the sun's `Atmosphere`, then `Follow(runner, camera)`. The picker's landing selection sets `SelectedStopId`. |
| Traffic | `TrafficView.ConnectHarbor` with the forwarding water and `HarborTrafficCoverage`, the active tiles of `data/processed/harbor/index.json`. |
| Holds | `HarborBootstrap` shows the runner's `IRunHoldSource` hold in its status line; a new start resumes. |

## Landing audit

`Data/landing-audit.json` is written from `python -m ferrydata.harbor_landings matrix` on the adopted harbor packs (`data-pipeline/ferrydata/harbor_landings/README.md`): per berth, whether it has a start pose and, per hull, how many of the 700 sampled starts (28 days × 25 samples) were afloat. `HarborLandingAudit` turns each berth with a start pose into a `HarborBerthMark` whose note the picker shows beside the berth ("Landing audit: afloat at every sampled tide", or "Landing audit: afloat T 467/700 · K 598/700"); the three faces without a start pose keep the catalog's crew-answer note. The audit refuses nothing: an aground start is still refused per tide by the catalog. The factory refuses an audit assessed on another depth set than the runtime pack links. `matrix_provenance` retains the assessed depth digest, contacts hash, vessel-file hashes, day digests and the `berths.json` hash. The writer requires that provenance beside `berths.json`, refuses a different adopted depth, changed output or changed vessel file, and the runtime parser rechecks both shipped vessel hashes before publishing any marks.

Regenerate it after a depth or matrix change, from the repository root, with the matrix's `berths.json`:

```sh
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.harbor_landings matrix \
  --contacts data/processed/harbor/unity/contacts.json --env data/processed/harbor/env --out artifacts/harbor-integration/matrix-after
PYTHONPATH=data-pipeline uv run --project data-pipeline python unity/Assets/FerrySim/Harbor/Data/write_landing_audit.py \
  artifacts/harbor-integration/matrix-after/berths.json data/processed/harbor/env/depth/DIGEST unity/Assets/FerrySim/Harbor/Data/landing-audit.json
```

## Verification

`Harbor/Tests/Integration/EditMode` covers the forwarder (datum only before a run, the run's service after, refusal outside the lattice), the audit's marks and refusals, the traffic coverage and the hold text. `Harbor/Tests/Integration/PlayMode` loads `Harbor.unity` with the real services and data (ignored, naming the folder, when `data/processed/harbor` is not copied): a start at Pier 11 on a traffic day, St. George refused through the picker with its reason shown, the run stepping on the run's water with the visual patches under the hull, 28 barges and 285 drawn aids, traffic present, and a hold with its explanation when a fixture start leaves covered water. `HarborIntegrationCaptureTests` is explicit: Pier 11 by day and night, the Brooklyn Bridge and St. George from the wheelhouse and the chase view, under `artifacts/harbor-integration/captures`.

The factory assigns its captured default `EnvironmentPacks` back to the runner before constructing the catalog, so preview and active runs share the same verified depth object across days. The general harbor load path used by replay reuses that verified set too; the slice loader is unchanged. Replay binds against its recorded `RunEnvironment`, using the scene's verified runtime datum when the replay has no runtime pack; water and patches bind before navigation, traffic and first placement. `HarborReplayTests` exercises the actual default scene, including replay before any ordinary run.
