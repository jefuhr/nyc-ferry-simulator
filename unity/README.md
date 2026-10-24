# NYC Ferry Simulator: Unity project

Unity 6.3 LTS (`6000.3.27f1`, Apple Silicon) with the Universal Render Pipeline. The project renders and takes input; the physics lives in `core/FerrySim.Core`, which this project compiles as the local package `com.nycferrysim.core`. Units, frames and the world-to-Unity mapping are in `docs/conventions.md`.

## Open the project

1. Install Unity Hub with editor `6000.3.27f1` (Apple Silicon build) and sign in with a license (Personal is fine).
2. In Unity Hub choose Add > Add project from disk and select this `unity/` folder.
3. Open it. The first import takes about 3 minutes (about 160 s for the asset refresh on an M1 Mac mini); later opens take seconds. Unity writes `.meta` files for `core/FerrySim.Core/` into the repo. Commit them with the sources.

Editor path used below:

```
UNITY="/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity"
```

## Run the tests headlessly

Run from the repository root. Use **absolute** paths for `-testResults`; Unity resolves a relative `-testResults` against the project folder (`unity/`), not the working directory.

```
mkdir -p "$HOME/FerrySim-cache" artifacts/unity

# EditMode (no graphics needed)
lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -nographics -projectPath unity \
  -runTests -testPlatform EditMode \
  -testResults "$PWD/artifacts/unity/editmode.xml" -logFile "$PWD/artifacts/unity/editmode.log"

# PlayMode (needs graphics: do NOT pass -nographics)
lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -projectPath unity \
  -runTests -testPlatform PlayMode \
  -testResults "$PWD/artifacts/unity/playmode.xml" -logFile "$PWD/artifacts/unity/playmode.log"
```

- The process exits by itself when the run completes; do not add `-quit` to `-runTests`.
- The Unity exit code is 0 even when a test fails. Check `result="Passed"` on the `<test-run>` element of the XML.
- Always pass `-logFile`, and grep the log for `error CS` to catch compile errors.

## Artifacts

All verification output goes to `artifacts/` at the repo root, which is gitignored:

| File | Produced by |
|---|---|
| `artifacts/unity/editmode.xml`, `editmode.log` | EditMode run |
| `artifacts/unity/playmode.xml`, `playmode.log` | PlayMode run |
| `artifacts/unity/smoke.png` | PlayMode smoke test (a camera view of a stand-in vessel) |

## Lock convention

Only one Unity process may run on this machine at a time, including processes in other worktrees (16 GB RAM; two editors on one project also corrupt `Library/`). Create the lock directory with `mkdir -p "$HOME/FerrySim-cache"` first, then wrap every Unity invocation, including GUI editor launches, in `lockf -k "$HOME/FerrySim-cache/unity.lock" ...`. `lockf` waits for the lock, so concurrent workers queue up instead of colliding. Close any GUI editor launched without the lock before starting another Unity process anywhere on this machine; closing only the editor for the same project is not sufficient.

## Layout

See `Assets/FerrySim/README.md` for the folders, assembly definitions and which task owns each one.

## Packages

| Package | Version |
|---|---|
| Unity editor | 6000.3.27f1 |
| Universal Render Pipeline (`com.unity.render-pipelines.universal`) | 17.3.0 |
| Input System (`com.unity.inputsystem`) | 1.20.1 |
| Test Framework (`com.unity.test-framework`) | 1.6.0 |
| Cesium for Unity (`com.cesium.unity`) | 1.26.0 |
| FerrySim Core (`com.nycferrysim.core`) | local, `file:../../core/FerrySim.Core` |
| Newtonsoft Json (`com.unity.nuget.newtonsoft-json`) | 3.2.2 (via the core package) |

`Packages/packages-lock.json` is committed and pins every resolved version.

Project settings: linear color space, Force Text serialization, visible meta files, active input handling set to the Input System package only, company and product name "NYC Ferry Simulator". The URP asset and renderer live in `Assets/FerrySim/Settings/Rendering/` and are assigned in Graphics and in every Quality level.

## Cesium for Unity notes

- Installed from Cesium's scoped registry (`https://unity.pkg.cesium.com`, scope `com.cesium.unity`), declared in `Packages/manifest.json`. 1.26.0 is the newest release as of 2026-10-22; its minimum editor is 2022.3, and it compiles and loads on 6000.3.27f1.
- Unity logs "installed without a signature" for it. That is expected for scoped-registry packages.
- Cesium creates `Assets/CesiumSettings/` on first import. If someone adds a Cesium ion access token, it is stored in plain text in `CesiumIonServers/ion.cesium.com.asset` and `CesiumRuntimeSettings.asset`. **Never commit a token.** Clear those fields before staging, or keep the token out of the repo some other way.
- Cesium logs `Exception while loading Cesium ion app data: Request was canceled because the Unity AppDomain is reloading` as an error during test runs, because the test runner reloads the domain. It does not fail a test.
- World placement uses the shared origin and vertical-datum rules in `docs/conventions.md`; Cesium georeference setup belongs to the unity-world-slice task.
