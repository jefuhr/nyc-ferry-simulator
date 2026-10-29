# Releasing

The release candidates are a universal macOS app in a DMG and a Windows x64 player in a ZIP. `scripts/release.sh` builds both, validates the exact offline harbor inventory, and smoke-tests a copy installed from the DMG. It never tags, publishes, changes the version, fetches data, or reaches a Windows machine.

## Before a candidate

- Use Unity `6000.3.27f1` with the existing Mac and Windows Mono build-support modules. No package or module installation is part of the release script.
- Provide the processed harbor outputs from the intended source revision. On a fresh worktree, copy the existing verified outputs with `cp -c -R`; do not write into another checkout. Missing cached inputs must be resolved before release; no downloads are authorized implicitly.
- Run the whole repository gate, `lockf -k "$HOME/FerrySim-cache/heavy.lock" scripts/check.sh`.
- Preserve `bundleVersion` unless a version change is explicitly requested. `--allow-dirty` produces a `<version>-dirty` candidate and records the actual source commit and dirty state.
- Enable only `Assets/FerrySim/Harbor/Scenes/Harbor.unity`. The slice scene, generated assets and its tests remain regression fixtures in the repository.

```sh
scripts/release.sh --allow-dirty
```

The script acquires `heavy.lock` before `unity.lock` for every Unity invocation, including preflight and the installed-player smoke, and releases both between steps. Other heavy packaging steps take `heavy.lock` alone. **Do not wrap `release.sh` in `heavy.lock`: BSD `lockf` is not reentrant and a second acquisition deadlocks.** Internally locking harbor, harbor-environment and traffic builders are not called by this script and must never be enclosed in an outer heavy lock.

## Inventory and preflight

`unity/Assets/FerrySim/Build/Editor/harbor-payload.json` is the single root configuration. Its `roots` array lists the terrain, skyline, environment, runtime, each of the 28 explicit traffic date folders, source contact/index files, both vessels, surface parameters and landing audit. The canonical geoid files travel inside `runtime/`. The surface parameters and audit are also the scene's referenced TextAssets; their source bytes are included in the inventory for review.

To add the later chart pack, add one line to that array:

```json
"data/processed/harbor/chart",
```

Keep the preceding/following JSON comma valid. New roots receive containment, exclusion, duplication, byte and hash checks automatically; a format-specific validator should be added when the chart contract requires one.

`HarborPayload` validates before building either platform:

- Every configured root exists, is nonempty, has no symlink, and has no excluded content. Overlapping roots fail on duplicate files.
- Terrain and skyline indices match every content file's SHA-256 and size, include every actual file, and all decoded tile URIs remain local and indexed.
- The source harbor index and the render/runtime source inventories match the source files they fingerprint.
- The existing core readers validate the runtime, one shared depth set, all 28 environment days and all 28 traffic packs. Depth/field dependencies must also be present in the intended inventory. The runtime and audit must reference the adopted depth and unchanged contacts/vessel/day bytes.
- Scene roots resolve into that inventory, and the configured player scene is harbor-only.

Excluded: raw cache, raw AIS, traffic `_sources/`, dot files, working rasters, GIS intermediates, reports, build logs, slice environment and slice render assets. Traffic uses date roots, never the whole traffic folder. An excluded file inside a selected root fails preflight; a sibling `_sources/` cache is simply outside the inventory.

The preflight writes `payload-inventory.json`. Each player packages the same paths into StreamingAssets and records every file in `build-info.json`, including harbor schema/source metadata and built scenes. Release verification compares the preflight inventory, source bytes, actual copies and build record, and rejects any extra StreamingAssets file. The build report lists scenes and packed asset dependencies so slice exclusion is independently inspectable.

## Candidate outputs

Under `artifacts/release/<label>/`:

| File | Evidence |
|---|---|
| `*-mac.dmg`, `*-windows-x64.zip` | Actual platform downloads; a download over 500,000,000 bytes stops the script before optimization |
| `SHA256SUMS.txt` | SHA-256 of both downloads |
| `release-manifest.json` | Version/commit/dirty state, exact download sizes and hashes, full harbor inventory, schemas/source digests, exclusions and smoke summary |
| `payload-inventory.json` | All intended source files, sizes/hashes and per-root totals/hash receipts |
| `mac/`, `windows/` | Players and `build-report.json` with scene and packed-asset inventories |
| `smoke/result/` | Installed Mac smoke JSON, PNGs and closed recording |
| `logs/` | Preflight/build/package/player logs and Windows ZIP CRC/listing checks |
| `windows-smoke/` | A later actual Windows run, once recorded |

An existing candidate folder is moved aside with a timestamp. Do not run two releases with the same label concurrently. Mac binaries and Cesium must be universal; the app is ad hoc signed and verified after payload copying, and the DMG is verified. Windows requires the x64 executable, UnityPlayer and Cesium plugin; Burst debug information is excluded and the ZIP receives a CRC test.

## Installed-player smoke

The small materials in `Harbor/Player/Resources` retain `FerrySim/SolidColour` and `FerrySim/HarborLabel`, which navigation finds by name at runtime. Their asset dependencies are covered by EditMode tests and the actual player build report.

Both platforms keep `-ferrysim-smoke <absolute-folder>`. A harbor-first player starts only `FerrySim.Harbor.Player.HarborPlayerSmokeRun`; a slice-first fixture player retains the existing slice harness. The release script mounts the DMG, copies the app to a fresh temporary Applications folder, unmounts, then launches from an empty temporary working directory with a cleared environment and no repository paths. The installed copy is retained at the path recorded in the evidence.

The harbor route selects Astoria (stop `89`), T-class, `2025-07-16` at 12:18 local, with actual traffic. It uses the real catalog and atomic launcher, waits for the scene's ten-frame actual terrain/skyline readiness gate, drives both engines astern for ten simulated seconds, captures wheelhouse and chase, closes its recording and quits. It fails on missing/unpackaged data, tile failure/timeout, no traffic, invalid state, insufficient movement, depth/time coverage holds, missing/blank screenshots, recording failure or logged errors.

`smoke.json` retains `passed`, `failures`, build/platform/environment and screenshot fields used by the release script. It adds `scene: "Harbor"`, `working_directory`, and `harbor` containing the selected landing/date, tile readiness/content counts, runtime/depth/traffic digests, traffic counts and coverage counters. A passing verdict requires actual screenshot files as well as JSON. The screenshot colour check detects blank frames; review the images before publishing.

The short 1280×720 smoke is functional acceptance. It does not accept the design's 1920×1080 multi-route memory, event-frame or first-view budgets. Report measured diagnostics separately; Windows runtime and full budget acceptance remain pending until executed on the target PC.

## Windows extracted-player smoke

A cross-build on the Mac is **not Windows runtime evidence**. The coordinator runs the extracted ZIP on Romeo afterwards. In PowerShell, substitute the absolute extracted executable path and candidate label, then run:

```powershell
$player = 'C:\path\to\extracted\NYC Ferry Simulator\NYC Ferry Simulator.exe'
$result = 'C:\ferry-smoke\0.2.0-dirty'
$empty = Join-Path $env:TEMP ('ferrysim-empty-' + [guid]::NewGuid())
New-Item -ItemType Directory -Force $result, $empty | Out-Null
$p = Start-Process -FilePath $player -WorkingDirectory $empty -ArgumentList "-ferrysim-smoke $result -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile $result\Player.log" -Wait -PassThru
$p.ExitCode
Get-Content "$result\smoke.json"
```

Expected: exit 0; `passed: true`, empty `failures`, `platform: "WindowsPlayer"`, `scene: "Harbor"`, Astoria stop `89`, actual terrain/skyline content ready, packaged environment/vessel, active traffic, zero errors/depth misses/time holds, a nonempty recording, `smoke-wheelhouse.png`, `smoke-chase.png`, and `Player.log`. Keep the complete result folder and inspect both screenshots.

Copy that folder to the Mac and record it:

```sh
scripts/record-windows-smoke.sh <label> <copied-result-folder>
```

The recorder refuses a different build/platform, overlapping source/destination folders, or a claimed passing result without harbor readiness, exact runtime/depth/day digests, active traffic and both PNGs. It preserves earlier evidence, records failed runs too, updates `windows_run`, and exits 1 for a failed run.

## S5 scene-size comparison

For release-note evidence only, `PlayerBuild.BuildFromCommandLine` accepts `-ferrysimBothScenesEvidence`. With the shipping build settings unchanged, it explicitly builds harbor first plus the retained slice, and scans precisely those requested scenes. Build an ordinary harbor-only player and this variant on the same source revision, then compare actual built file counts/bytes, payload inventories and compressed downloads. Never ship or relabel the evidence variant as the harbor-only candidate.

## Publishing

Candidate creation grants no publishing permission. Tagging, pushing, release creation and any version change are separate explicitly authorized actions. Signing/notarization purchases and public branding/licensing decisions also remain separate.

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
