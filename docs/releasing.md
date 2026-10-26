# Releasing

A release is a universal macOS app (Intel and Apple silicon) in a disk image and a Windows x64 player in a zip. `scripts/release.sh` builds both on the Mac from a clean commit, smoke-tests the Mac app, and leaves everything in `artifacts/release/<version>/`. The Windows player's smoke run on a PC is recorded beside it (`scripts/record-windows-smoke.sh`). The release is then published on the private GitHub repository. Releases stay private: the NYC Ferry name and livery need their owners' permission before anything goes wider (see the README's disclaimer).

## Before a release

- **Unity.** `6000.3.27f1` with Mac Build Support (Mono), which comes with the editor, and Windows Build Support (Mono). Add the Windows module with Unity Hub:

  ```
  "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install-modules --version 6000.3.27f1 -m windows-mono
  ```

- **Processed data.** The players package the slice's processed data from `data/processed/lower-east-river/`: its `geoid_offset.json`, and every file of its environment packs (`env/`: the 28 scenario days and the current field, about 26 MB). It is not in git, so build it from the release commit, the slice first. The slice build is the full one, because the depth floor needs the AIS extract. On the shared build Mac, both take the heavy lock (`lockf -k ~/FerrySim-cache/heavy.lock …`):

  ```
  uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river
  PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all
  ```

  A build fails when a file or folder a scene names is missing, and `scripts/release.sh` verifies the packs' digests before it builds anything. Traffic packs are not packaged: players refuse traffic for now.
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
| `windows-smoke/` | The Windows smoke run's report, screenshots and player log, once recorded (Windows smoke run, below) |
| `mac/`, `windows/` | The built players and Unity's `build-report.json` for each |
| `logs/` | Unity's build logs, codesign, Gatekeeper and hdiutil output, and the player's log from the smoke run |

An earlier run with the same label is moved aside with a timestamp, not overwritten. Run one release at a time: two runs of the same version would race over the same folder.

### What the script checks

- **Data first.** The environment packs verify (`python -m ferrydata.environment verify`) before anything is built; the log is `logs/environment-verify.log`.
- **Builds.** Each build succeeds in strict mode (any error fails it), and the builds leave the working tree as it was.
- **Packaged data.** Every player build copies the repository files the slice reads at run time into the player's StreamingAssets (`FerrySim.Build.PlayerPayload`):
  - Today these are both vessel files (`vessels/t-class.json`, `vessels/k-class.json`), the placement's `geoid_offset.json`, and the environment packs' folder, whole: about 150 files and 26 MB.
  - **Which files.** The build packages repository-relative paths with forward slashes, in serialized string fields or arrays of them whose names end in `File` or `Files`, and every file under the folders named in fields ending in `Folder` or `Folders` (a path with a part starting with a dot, such as `.DS_Store`, is left out). It reads the components of the built scenes, and the project assets they reference under `Assets/`: ScriptableObjects, prefabs, and prefabs behind component references.
  - **What fails the build:**
    - a path that is absolute, climbs out of the repository, passes through a symbolic link, or is written with backslashes or surrounding spaces
    - a missing file
    - a scenario JSON file or a replay in a built scene, because each names further files the build would not package
  - **Not found.** A file a script locates by a name built in code is not found. Give it a `*File` field or make it a TextAsset.
  - **The record.** The build writes `build-info.json` beside the files, recording the version, the commit, whether the tree was dirty, and each file's SHA-256.
  - **What the script checks.** The record matches this version and commit. Both vessel files, the geoid offset and every file of the environment packs are packaged, and no traffic file is. Each packaged file matches its recorded hash and the repository's bytes, and resolves inside the repository. StreamingAssets holds nothing the record doesn't list, apart from `build-info.json` and dot files such as `.DS_Store`.
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
    - on a pack scenario (the slice's is one), the pack came from StreamingAssets and the drawn water stood at the pack's water level at the vessel

  The run leaves its two recordings in the app's `runs/` folder on the build Mac.

## Windows smoke run

The Windows player cannot run on the Mac, so it runs on a Windows PC before the release is published:

1. Copy `NYC-Ferry-Simulator-<version>-windows-x64.zip` to the PC and extract it. A folder path with spaces is fine.
2. In PowerShell, in the extracted folder:

   ```
   New-Item -ItemType Directory -Force C:\ferry-smoke | Out-Null
   $p = Start-Process -FilePath '.\NYC Ferry Simulator.exe' -ArgumentList '-ferrysim-smoke C:\ferry-smoke -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile C:\ferry-smoke\Player.log' -Wait -PassThru; $p.ExitCode
   ```

   A window opens for about a minute. The run makes the same checks as the Mac's, and the exit code is 0 only if all of them passed.
3. Copy `C:\ferry-smoke` back to the Mac and record it:

   ```
   scripts/record-windows-smoke.sh <version> <the copied folder>
   ```

   It refuses a run that is not this release's Windows build, and it refuses the release's own `windows-smoke/` folder as the source. Otherwise it copies the report, the screenshots and the log into `artifacts/release/<version>/windows-smoke/`, writes the run's summary into `release-manifest.json` as `windows_run`, and exits 1 if the run failed. Look at the screenshots as well.

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
- **Windows installer.** There is none: the zip is portable.
- **Build paths.** The players' debug metadata carries the build machine's paths:
  - the managed DLLs' PDB paths
  - Burst's temporary paths in the Mac Burst library

  They hold no secrets, but they name the user and the folders. Build from a neutral path, or in CI, before anything is released more widely.
