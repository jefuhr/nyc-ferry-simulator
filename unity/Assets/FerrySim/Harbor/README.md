# Harbor Scene

`Scenes/Harbor.unity` is built from `Data/harbor-scene.json` by `FerrySim.Harbor.EditorTools.HarborSceneBuilder.Build`. Rebuild it through **FerrySim > Harbor > Rebuild Harbor Scene**. A batch invocation uses both shared locks:

```sh
lockf -k "$HOME/FerrySim-cache/heavy.lock" lockf -k "$HOME/FerrySim-cache/unity.lock" \
  /Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -projectPath "$PWD/unity" \
  -executeMethod FerrySim.Harbor.EditorTools.HarborSceneBuilder.BuildFromCommandLine \
  -logFile "$PWD/artifacts/harbor-scene/build.log"
```

The builder starts from an empty scene and reuses `WorldFileIds` for deterministic hierarchy-based IDs. It does not open a slice scene, instantiate slice terrain, or modify build settings. The scene has one runner, input controller, camera rig, and wheelhouse UI. Its water, navigation, and traffic roots are explicit runtime slots; the scene stays held with a visible reason until the coordinator connects the real services.

## Integration

`HarborSceneServiceFactory.Create(HarborSceneContext)` is implemented by `HarborIntegrationFactory` in `Harbor/Integration/` (its README names each real service); the builder puts it on the "Harbor composition" object and assigns it to `HarborBootstrap.serviceFactory`, so the checked-in scene connects the real services in `Start`. The factory returns `IHarborSceneServices` with these exact members:

| Member | Real service and responsibility |
|---|---|
| `IWaterSurface WaterSurface` | The harbor water service. Create its visual patches under `context.WaterRoot`; it owns spatial tide, canonical datum and render-time updates. |
| `object RuntimePack` | The validated `HarborRuntimePack` from the core reader. This handle is deliberately opaque on the pre-integration base; the scene does not validate or parse a second copy. |
| `LandingGeometry Landings` | The resident contacts parsed with the existing slice parser. `HarborContacts.Load(contactsPath, surfaceJson, surfacePath)` reuses that parser and `LandingSurfaceTable`; `Data/landing-contact-surfaces.json` retains the existing surface estimates unchanged. |
| `IScenarioCatalog Catalog` | The harbor catalog, including its `IScenarioStartDepthSource` implementation. Its existing approach/depth eligibility logic remains authoritative. |
| `IScenarioLauncher Launcher` | The atomic harbor launcher. It prepares and validates the candidate's whole bundle before replacing the current run, then starts `context.Runner`. Failures return the exact missing/mismatched path. |
| `Action<HarborSceneContext> BuildNavigation` | Build/register the resident navigation scene under `context.NavigationRoot`, using the factory's verified pack, water and runner. This executes once during connection. |
| `Action<HarborSceneContext> BuildTraffic` | Attach the harbor traffic renderer under `context.TrafficRoot` and subscribe it to the runner. This executes once during connection; the renderer owns per-run changes. |

`HarborSceneContext` supplies `Runner`, `Georeference`, `CameraRig`, `Sun`, the three roots and `Configuration`. `HarborBootstrap.Connect(services, out problem)` is the equivalent programmatic entry point. Register dynamic aids/bridges through the navigation task's atmosphere registration before a run starts. Scene-local factories can hold the verified pack strongly typed internally without making this assembly depend on an unavailable sibling API.

The bootstrap binds `CesiumVesselPlacement.WaterSurface`, the runner's resident `WorldGeometry`, the sun and wheelhouse readouts, and the catalog/launcher interfaces. At each run start it reuses `FaceClearance` and `BowApproachFaces`, and positions the existing camera mount using `GreyboxDimensions`. It contains no simulation factory, physics, environment sampler, recording, or traffic replay implementation.

The status line also explains the runner's own holds (`IRunHoldSource`, decision 0005): "Run held — the vessel left the water this day has depth for" or "Run held — the day's tide, current and wind data has run out", each ending "Start a new run to continue." The runner stops stepping by itself; a vessel held outside covered water keeps the position it was last drawn at, because the harbor datum refuses the held pose. A new start clears the line.

The runtime sibling's integration API creates water after a successful harbor start:

```csharp
new FerrySim.HarborWater.HarborWaterService(
	new FerrySim.World.HarborDatum(runner.HarborRuntime.Geoid),
	runner.RunEnvironment);
```

Because `Connect` occurs before the first scenario and binds `IWaterSurface` once, the factory should return a stable forwarding `IWaterSurface` that receives the new concrete service on each harbor `RunStarted`. It must not supply a guessed fallback height. Subscribe that water-binding callback while constructing the bundle, before registering the navigation/traffic run callbacks. For harbor starts, the runner raises `RunStarted` before initial placement, so all bindings complete before the first vessel placement; the slice event order is unchanged. No later positional correction is needed.

Visual water is created under `context.WaterRoot` with `HarborWaterPatch.Configure(service, firstNorth, firstEast, rows, columns, material, runner)`. The first coordinates are integer 100 m lattice indices; patches are at most 40 cells per side. The runner drives their updates at `RenderTime`. These sibling signatures are documented for integration and are not reimplemented or referenced by this base's scene assembly.

The bootstrap binds the existing `WaterNormalMap` to `_FerrySimWaterNormals` once at connection, so the shared `World/Materials/Water.mat` works in a fresh harbor without a previously loaded slice. The factory supplies that material to its visual patches; no water shading or wave generator is duplicated here.

## Initial view and controls

A new run pauses before its first physics step. Every required render layer must have actual content and report 100% load progress for ten consecutive frames. A load failure or a 60-second timeout leaves the vessel paused with a visible reason in the existing UI. The timeout is a failure threshold, not the three-second performance acceptance budget. Once ready, the gate latches: later streaming never pauses, teleports, or changes the simulation timestep. The camera and vessel retain their globe anchors throughout.

The helm's camera-cycle and new-run commands use the existing camera rig and picker. Loading and the picker hold helm input; closing the picker cannot release a loading hold or an earlier user pause. An unbound scene never starts a fallback calm run. Reopen/rebuild after a missing render inventory is repaired; no live pack/directory swap is implemented.

## Local render layers

Terrain and skyline are sibling tilesets under the shared georeference at 40.7000 N, -74.0150 E, scale 1, with a 100 m origin shift. Editor paths resolve under the repository; player paths resolve only under StreamingAssets, at the same `data/processed/harbor/...` relative paths. Packaging remains coordinator-owned.

`HarborTileInventory` checks every indexed file's size/SHA-256 and refuses missing, remote, escaped, or unlisted content before enabling Cesium. Cesium stays pinned at the project's 1.26.0 version. Physics meshes and editor streaming are off. Screen-space error is 16; the checked configuration starts with a 192 MiB terrain cache and a 64 MiB skyline cache. This 256 MiB combined allowance is an experiment from the design, not a process-memory cap or final budget acceptance.

`FerrySim/HarborSurface` draws pipeline vertex colours with the existing sun, sky ambient globals, shadows and URP fog. Both layer materials use it. Ambient strength 0.65 is a rendering estimate. Skyline adds no decorative emission or navigation lights; its silhouettes remain separate from terrain and contacts.

Parameter provenance: the world origin and 100 m shift follow the accepted harbor design; ten complete frames and the 60-second failure threshold reuse the existing `TilesetProbe` policy. Chase/free-view FOV 60° and wheelhouse FOV 65° retain the existing camera defaults. The 40 km far clip and SSE 16 are initial rendering estimates for the harbor extent. None of these changes vessel parameters, clearances, or the 10 ms physics timestep.

## Verification

`Harbor/Tests/Scene/EditMode` covers local inventories, readiness transitions, deterministic scene bytes and build dependencies. `PlayMode` uses small fake services and a checked point fixture to exercise holds, UI, allocation and `CesiumVesselPlacement` precision. The point fixture contains all 24 landing coordinates plus the harbor grid's four extent corners, with the source-index hash; it is not a start-safety or survey-accuracy claim.

Real-data captures are separate and explicitly selected. Editor frame times and memory are diagnostics, not installed-player or final budget acceptance. The scene task's report is `artifacts/harbor-scene/report.md`; the integration's is `artifacts/harbor-integration/report.md`.

The integrated harbor is captured by `Harbor/Tests/Integration/PlayMode/HarborIntegrationCaptureTests` (Pier 11 by day and night, the Brooklyn Bridge and St. George, wheelhouse and chase), run with `-testPlatform PlayMode -testFilter FerrySim.Harbor.Tests.Integration.PlayMode.HarborIntegrationCaptureTests.Pier11DayAndNight_BrooklynBridge_StGeorge_WheelhouseAndChase` under both locks and with absolute XML/log paths; it writes 8 PNGs plus `index.csv` and `scope.txt` under `artifacts/harbor-integration/captures`. The earlier scene/material fixture captures (synthetic water, fake services) are superseded: the checked-in scene now connects the real services in `Start`, so a fixture bundle can no longer be connected to it. Fixture-only iteration filters `HarborBootstrapTests` and `HarborPrecisionTests`, which build their own scene.
