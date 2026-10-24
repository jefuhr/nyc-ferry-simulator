# Build

Owner: **release-build**. This folder holds the editor-only assembly `FerrySim.Unity.Build.Editor` (namespace `FerrySim.Build`), which builds and packages players. The release process, its checks and the install steps are in [docs/releasing.md](../../../../docs/releasing.md). The player-side pieces are in `Slice/Player/` ([Slice/README.md](../Slice/README.md), Players).

## Pieces

| File | Role |
|---|---|
| `PlayerBuild` | The command-line build (`-executeMethod FerrySim.Build.PlayerBuild.BuildFromCommandLine -ferrysimTarget mac\|windows -ferrysimOutput <absolute folder> [-ferrysimVersion <x.y.z>]`): see below |
| `PlayerPayload` | Finds the repository files a player reads at run time and packages them into its StreamingAssets with `build-info.json` |
| `PlayerPayloadBuildStep` | Runs `PlayerPayload` in every standalone build, scripted or from the Build menu |
| `GitState` | The commit a build comes from, and whether the tree was dirty |

### `PlayerBuild`

- **Players.** On the Mac a universal .app sits at the top of the folder; on Windows an x64 .exe sits in a folder named after the product.
- **Strict mode.** Any error fails the build.
- **Report.** It writes `build-report.json` beside the player.
- **Exit codes.** 0 for success, 1 for a failure or a throw, 2 for an unusable command line.

## Packaged files

`PlayerPayload` reads serialized string fields, or arrays of them, whose names end in `File` or `Files`. It reads them on the components of the built scenes and on the project assets those components reference under `Assets/`: ScriptableObjects, prefabs, and prefabs behind component references. Each field is a repository-relative path with forward slashes, as `SimPaths.Resolve` reads it. Every file is copied to the same relative path under StreamingAssets.

These fail the build:
- a path that is absolute, climbs out of the repository, passes through a symbolic link, or is written with backslashes or surrounding spaces
- a missing file
- a scenario JSON file or a replay in a built scene, because each names further files the build would not package

Scenes the build did not process are opened from the build settings and read too.

## Tests

| Test | What |
|---|---|
| `PlayerPayloadTests` | The slice scene names exactly the vessel and geoid files. Also covered: field names and arrays, prefabs behind component references, unprocessed scenes, and every rejected path (missing, absolute, `..`, symbolic links, backslashes, padding, scenario files and replays). Each platform's StreamingAssets layout, and the copies and their hashes, are checked as well |
| `PlayerBuildTests` | The command line, the player layout and the exit codes |
| `ReleaseSettingsTests` | The version is major.minor.patch, and the bundle identifier, build scenes and product name are the release's |
