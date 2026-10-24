# Releasing

A release is a universal macOS app (Intel and Apple silicon) in a disk image and a Windows x64 player in a zip. `scripts/release.sh` builds both on the Mac from a clean commit, smoke-tests the Mac app, and leaves everything in `artifacts/release/<version>/`. The release is then published on the private GitHub repository. Releases stay private: the NYC Ferry name and livery need their owners' permission before anything goes wider (see the README's disclaimer).

## Before a release

- **Unity.** `6000.3.27f1` with Mac Build Support (Mono), which comes with the editor, and Windows Build Support (Mono). Add the Windows module with Unity Hub:

  ```
  "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install-modules --version 6000.3.27f1 -m windows-mono
  ```

- **Processed data.** The slice's processed data must be in `data/processed/lower-east-river/`; build it with `uv run --project data-pipeline ferrydata build --slice lower-east-river`. The slice scene names `geoid_offset.json` there, and a build fails when a file a scene names is missing.
- **Checks.** `scripts/check.sh` passes on the commit.
- **Version.** The version is `bundleVersion` in `unity/ProjectSettings/ProjectSettings.asset` (Player Settings → Version). It must be major.minor.patch, raised for the release, and committed.
- **Clean tree.** The working tree is clean. `--allow-dirty` builds anyway and labels everything `<version>-dirty`; it is for trying the script, not for publishing.

## Build, package, smoke-test

```
scripts/release.sh
```

It runs Unity under the shared lock (`~/FerrySim-cache/unity.lock`), one build after the other, so it waits for any other Unity process. A first build compiles every shader variant for Metal and Direct3D and takes a while; later builds reuse the Library's caches.

In `artifacts/release/<version>/`:

| File | What |
|---|---|
| `NYC-Ferry-Simulator-<version>-mac.dmg` | The app, ad hoc signed, beside a link to Applications |
| `NYC-Ferry-Simulator-<version>-windows-x64.zip` | The Windows player's folder |
| `SHA256SUMS.txt` | The checksums of both |
| `release-manifest.json` | Version, commit, sizes, hashes and the smoke run's result |
| `smoke/result/` | The smoke run's `smoke.json` and its wheelhouse and chase screenshots |
| `mac/`, `windows/` | The built players and Unity's `build-report.json` for each |
| `logs/` | Unity's build logs, codesign, Gatekeeper and hdiutil output, and the player's log from the smoke run |

An earlier run with the same label is moved aside with a timestamp, not overwritten. Run one release at a time: two runs of the same version would race over the same folder.

### What the script checks

- **Builds.** Each build succeeds in strict mode (any error fails it), and the builds leave the working tree as it was.
- **Packaged data.** Every player build copies the repository files the slice reads at run time into the player's StreamingAssets (`FerrySim.Build.PlayerPayload`):
  - Today these are the scenario's `vessels/t-class.json` and the placement's `geoid_offset.json`.
  - **Which files.** The build packages repository-relative paths with forward slashes, in serialized string fields or arrays of them whose names end in `File` or `Files`. It reads the components of the built scenes, and the project assets they reference under `Assets/`: ScriptableObjects, prefabs, and prefabs behind component references.
  - **What fails the build:**
    - a path that is absolute, climbs out of the repository, passes through a symbolic link, or is written with backslashes or surrounding spaces
    - a missing file
    - a scenario JSON file or a replay in a built scene, because each names further files the build would not package
  - **Not found.** A file a script locates by a name built in code is not found. Give it a `*File` field or make it a TextAsset.
  - **The record.** The build writes `build-info.json` beside the files, recording the version, the commit, whether the tree was dirty, and each file's SHA-256.
  - **What the script checks.** The record matches this version and commit. Each packaged file matches its recorded hash and the repository's bytes, and resolves inside the repository.
- **Mac app.** The app and the Cesium native plugin are universal (`lipo`). The app re-signs ad hoc and passes `codesign --verify --deep --strict`. The disk image passes `hdiutil verify`.
- **Windows player.** The package holds an x64 `NYC Ferry Simulator.exe`, `UnityPlayer.dll` and the x64 Cesium native plugin, and no Burst debug information.
- **Smoke run.** The script copies the app out of the mounted disk image, as a user installs it, and launches it with `-ferrysim-smoke <folder>` (`FerrySim.Slice.PlayerSmokeRun`). A window opens for about a minute. The run:
  - waits for the slice's own run to step
  - restarts it with both engines astern for ten simulated seconds
  - selects and captures the wheelhouse view, then the chase view
  - quits with 0 only if:
    - the vessel file came from StreamingAssets
    - the landings loaded
    - the vessel moved at least a metre with a finite state
    - the run's recording was written
    - both views were captured and are not blank. This is a colour count, not a check of what they show, so look at `smoke/result/*.png` before publishing.
    - nothing logged an error, exception or assert

  The run leaves its two recordings in the app's `runs/` folder on the build Mac.

The Windows player is built and inspected but not run, because that needs a Windows PC. The first launch on a PC is a manual check.

## Publish

From the commit the release was built from, with the tree still clean:

```
git tag -a v<version> -m "NYC Ferry Simulator <version>"
git push origin main v<version>
gh release create v<version> --verify-tag --prerelease --title "NYC Ferry Simulator <version>" --notes-file <notes.md> \
	artifacts/release/<version>/*.dmg artifacts/release/<version>/*.zip artifacts/release/<version>/SHA256SUMS.txt
```

## Installing

### macOS 12 or later, Intel or Apple silicon

1. Download the `.dmg`, from the release page or with `gh release download v<version> -R jefuhr/nyc-ferry-simulator -p '*mac.dmg'`.
2. Open it and drag **NYC Ferry Simulator** to **Applications**.
3. The app is ad hoc signed, not notarized, so macOS refuses the first launch of a copy downloaded with a browser. Open it once, then choose **System Settings → Privacy & Security → Open Anyway**. Or clear the download flag:

   ```
   xattr -dr com.apple.quarantine "/Applications/NYC Ferry Simulator.app"
   ```

   A copy downloaded with `gh` has no flag to clear.

### Windows 10 or 11, x64

1. Download the `.zip` and extract it (right-click → **Extract All**); the player cannot run from inside the zip.
2. Run `NYC Ferry Simulator.exe` in the extracted folder.
3. The player is unsigned, so SmartScreen warns on the first launch: **More info → Run anyway**.

### Where the app keeps its data

Input profiles (`input-profiles-v1.json`) and recorded runs (`runs/`) are kept in the player's data folder:

- macOS: `~/Library/Application Support/NYC Ferry Simulator/NYC Ferry Simulator/`
- Windows: `%USERPROFILE%\AppData\LocalLow\NYC Ferry Simulator\NYC Ferry Simulator\`

Delete that folder to start again.

## Not yet

- **Signing.** A Developer ID with notarization (Apple Developer Program) would remove the macOS warning, and a code-signing certificate would remove the Windows one. Both cost money; ask first.
- **Windows testing.** The Windows player is not run before a release.
- **Windows installer.** There is none: the zip is portable.
- **Build paths.** The players' debug metadata carries the build machine's paths:
  - the managed DLLs' PDB paths
  - Burst's temporary paths in the Mac Burst library

  They hold no secrets, but they name the user and the folders. Build from a neutral path, or in CI, before anything is released more widely.
