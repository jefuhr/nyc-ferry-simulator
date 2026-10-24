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
| `World/` | `FerrySim.Unity.World` | unity-world-slice |
| `World/Editor/` | `FerrySim.Unity.World.Editor` (Editor only) | unity-world-slice |
| `World/Tests/EditMode/`, `World/Tests/PlayMode/` | `FerrySim.Unity.World.Tests.*` | unity-world-slice |
| `World/Import/Runtime/` | `FerrySim.World.Import.Runtime` | unity-world-import |
| `World/Import/Editor/` | `FerrySim.World.Import.Editor` (Editor only) | unity-world-import |
| `World/Import/Tests/` | `FerrySim.World.Import.Tests` (Editor only) | unity-world-import |
| `World/Generated/` | none (generated assets only) | unity-world-import |
| `UI/` | `FerrySim.Unity.UI` | wheelhouse-ui |
| `UI/Editor/` | `FerrySim.Unity.UI.Editor` (Editor only) | wheelhouse-ui |
| `UI/Tests/EditMode/`, `UI/Tests/PlayMode/` | `FerrySim.Unity.UI.Tests.*` | wheelhouse-ui |
| `Tests/EditMode/`, `Tests/PlayMode/` | `FerrySim.Unity.Tests.*` | unity-project-bootstrap (project smoke tests) |
| `Tests/E2E/` | none yet | slice-e2e-tests |

Namespaces avoid `FerrySim.Unity.*` and `FerrySim.Input`, because inside those namespaces a bare `Unity` or `Input` would no longer resolve to the engine's.

## Layers

| Index | Name | Use |
|---|---|---|
| 4 | Water | Unity's built-in layer; the water surface |
| 8 | Structure | Piers, bulkheads and landing barges |
| 10 | Vessel | The simulated vessel |

Index 9 is free. It briefly carried a second `Water` name during wave 2, which made name lookups ambiguous.
