# FerrySim Unity layer

Unity-side code for the simulator. The physics lives in `core/FerrySim.Core` (local package `com.nycferrysim.core`); Unity only renders interpolated state and feeds normalized `ControlInputs` back in. See `docs/conventions.md` for units, frames and the world-to-Unity mapping.

| Folder | Assembly | Owning task |
|---|---|---|
| `Sim/` | `FerrySim.Unity.Sim` | unity-sim-bridge-controls |
| `Sim/Cesium/` | `FerrySim.Unity.Sim.Cesium` | unity-sim-bridge-controls |
| `Sim/Authoring/` | `FerrySim.Unity.Sim.Authoring` (Editor only) | unity-sim-bridge-controls |
| `Sim/Tests/EditMode/`, `Sim/Tests/PlayMode/` | `FerrySim.Unity.Sim.Tests.*` | unity-sim-bridge-controls |
| `Input/` | `FerrySim.Unity.Input` | unity-sim-bridge-controls |
| `Input/Profiles/` | (in `FerrySim.Unity.Input`) | unity-input-profiles |
| `Input/Profiles/Editor/` | `FerrySim.Input.Profiles.Editor` (Editor only) | unity-input-profiles |
| `Input/Profiles/Tests/EditMode/`, `Input/Profiles/Tests/PlayMode/` | `FerrySim.Input.Profiles.Tests.*` | unity-input-profiles |
| `World/` | `FerrySim.Unity.World` | unity-world-slice |
| `World/Editor/` | `FerrySim.Unity.World.Editor` (Editor only) | unity-world-slice |
| `World/Tests/EditMode/`, `World/Tests/PlayMode/` | `FerrySim.Unity.World.Tests.*` | unity-world-slice |
| `World/Import/Runtime/` | `FerrySim.World.Import.Runtime` | unity-world-import |
| `World/Import/Editor/` | `FerrySim.World.Import.Editor` (Editor only) | unity-world-import |
| `World/Import/Tests/` | `FerrySim.World.Import.Tests` (Editor only) | unity-world-import |
| `World/Generated/` | none (generated assets only) | unity-world-import |
| `World/Atmosphere/` | (in `FerrySim.Unity.World`) | night-fog |
| `World/Tiles3D/Runtime/` | `FerrySim.World.Tiles3D` (the Windows probe) | world3d-tiles |
| `World/Tiles3D/Editor/` | `FerrySim.World.Tiles3D.Editor` (Editor only) | world3d-tiles |
| `World/Tiles3D/Tests/EditMode/` | `FerrySim.World.Tiles3D.Tests.EditMode` | world3d-tiles |
| `Traffic/` | `FerrySim.Unity.Traffic` | traffic-unity |
| `Traffic/Tests/EditMode/` | `FerrySim.Unity.Traffic.Tests.EditMode` | traffic-unity |
| `UI/` | `FerrySim.Unity.UI` | wheelhouse-ui |
| `UI/Editor/` | `FerrySim.Unity.UI.Editor` (Editor only) | wheelhouse-ui |
| `UI/Tests/EditMode/`, `UI/Tests/PlayMode/` | `FerrySim.Unity.UI.Tests.*` | wheelhouse-ui |
| `Slice/` | `FerrySim.Unity.Slice` | unity-slice-integration |
| `Slice/Authoring/` | `FerrySim.Unity.Slice.Authoring` (Editor only) | unity-slice-integration |
| `Slice/Tests/EditMode/`, `Slice/Tests/PlayMode/` | `FerrySim.Unity.Slice.Tests.*` | unity-slice-integration |
| `Slice/Player/` | (in `FerrySim.Unity.Slice`) | release-build |
| `Traffic/Tests/PlayMode/` | `FerrySim.Unity.Traffic.Tests.PlayMode` | harbor-traffic |
| `World/HarborTiles/`, `World/Skyline/` | `FerrySim.World.HarborTiles`, `FerrySim.World.Skyline` | harbor-scene |
| `World/HarborWater/` | `FerrySim.HarborWater` | harbor-runtime |
| `World/HarborWater/Tests/EditMode/`, `World/HarborWater/Tests/PlayMode/` | `FerrySim.HarborWater.Tests.*` | harbor-runtime |
| `World/HarborNavigation/` | `FerrySim.World.HarborNavigation` | harbor-navigation |
| `World/HarborNavigation/Tests/EditMode/`, `World/HarborNavigation/Tests/PlayMode/` | `FerrySim.World.HarborNavigation.Tests.*` | harbor-navigation |
| `Harbor/Runtime/` | `FerrySim.Harbor.Runtime` | harbor-scene |
| `Harbor/Editor/` | `FerrySim.Harbor.Editor` (Editor only) | harbor-scene |
| `Harbor/Tests/Scene/EditMode/`, `Harbor/Tests/Scene/PlayMode/` | `FerrySim.Harbor.Scene.Tests.*` | harbor-scene |
| `Harbor/Scenarios/` | `FerrySim.Unity.Harbor.Scenarios` | harbor-picker |
| `Harbor/Tests/Scenarios/EditMode/` | `FerrySim.Unity.Harbor.Scenarios.Tests.EditMode` | harbor-picker |
| `Harbor/Integration/` | `FerrySim.Harbor.Integration` (the harbor composition root) | harbor-integration |
| `Harbor/Tests/Integration/EditMode/`, `Harbor/Tests/Integration/PlayMode/` | `FerrySim.Harbor.Integration.Tests.*` | harbor-integration |
| `Build/Editor/` | `FerrySim.Unity.Build.Editor` (Editor only) | release-build |
| `Build/Tests/EditMode/` | `FerrySim.Unity.Build.Tests.EditMode` | release-build |
| `Tests/EditMode/`, `Tests/PlayMode/` | `FerrySim.Unity.Tests.*` | unity-project-bootstrap (project smoke tests) |
| `Tests/E2E/` | `FerrySim.Unity.Tests.E2E` | slice-e2e-tests |

Namespaces avoid `FerrySim.Unity.*` and `FerrySim.Input`, because inside those namespaces a bare `Unity` or `Input` would no longer resolve to the engine's.

## Layers

| Index | Name | Use |
|---|---|---|
| 4 | Water | Unity's built-in layer; the water surface |
| 8 | Structure | Piers, bulkheads and landing barges |
| 10 | Vessel | The simulated vessel |

Index 9 is free. It briefly carried a second `Water` name during wave 2, which made name lookups ambiguous.
