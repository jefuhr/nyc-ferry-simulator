# FerrySim Unity layer

Unity-side code for the simulator. The physics lives in `core/FerrySim.Core` (local package `com.nycferrysim.core`); Unity only renders interpolated state and feeds normalized `ControlInputs` back in. See `docs/conventions.md` for units, frames and the world-to-Unity mapping.

| Folder | Assembly | Owning task |
|---|---|---|
| `Sim/` | `FerrySim.Unity.Sim` | unity-sim-bridge-controls |
| `Input/` | `FerrySim.Unity.Input` | unity-sim-bridge-controls |
| `Input/Profiles/` | (in `FerrySim.Unity.Input`) | unity-input-profiles |
| `World/` | `FerrySim.Unity.World` | unity-world-slice |
| `World/Import/`, `World/Generated/` | (in `FerrySim.Unity.World`) | unity-world-import |
| `UI/` | `FerrySim.Unity.UI` | wheelhouse-ui |
| `Tests/EditMode/`, `Tests/PlayMode/` | `FerrySim.Unity.Tests.*` | unity-project-bootstrap (smoke tests); later tasks add their own tests |
| `Tests/E2E/` | none yet | slice-e2e-tests |

Namespaces avoid `FerrySim.Unity.*` and `FerrySim.Input`, because inside those namespaces a bare `Unity` or `Input` would no longer resolve to the engine's.
