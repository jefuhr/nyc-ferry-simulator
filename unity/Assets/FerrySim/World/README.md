# World

Owner: **unity-world-slice**. Assembly `FerrySim.Unity.World` (namespace `FerrySim.World`): the georeferenced Lower East River scene the vessel runs in. `World/Import/` and `World/Generated/` belong to **unity-world-import** (terrain, shoreline, buoys); nothing here duplicates them.

Scene: `Scenes/LowerEastRiver.unity`, from Wall St/Pier 11 past DUMBO and the Brooklyn and Manhattan bridges to the Williamsburg Bridge, inside the pipeline's slice box (latitude 40.695 to 40.750, longitude -74.020 to -73.960).

## Layout

| Path | Content |
|---|---|
| `Georeference/` | `WorldFrame`, `GeoreferenceCheck`, `SliceDatum`, `WorldLayers` |
| `Water/` | `WaterSurface` (curved mesh, water level), `WaterNormalMap` (generated ripples), `FloatOnWater` |
| `Sky/` | `SolarPosition` and `SolarDay` (NOAA), `NewYorkTime`, `SunController`, `SkyGradient` |
| `Cameras/` | `CameraRig`, `FreeFlyCameraController`, `ChaseCameraController`, `WheelhouseCameraController`, `WheelhouseCameraMount` |
| `Structures/` | `LandingBarge`, `BridgeGreybox`, `Billboard` (labels) |
| `Shaders/` | `Water.shader`, `Sky.shader`, shared `FerrySimSky.hlsl` |
| `Materials/` | `Water.mat`, `Sky.mat` |
| `Data/LowerEastRiverDatum.asset` | generated: geoid samples and tidal datums |
| `Greybox/` | generated: `LowerEastRiverGreybox.prefab`, barge meshes, greybox materials |
| `Editor/` | `FerrySim.Unity.World.Editor` (Editor-only): pipeline reader and the builders |
| `Tests/EditMode/`, `Tests/PlayMode/` | `FerrySim.Unity.World.Tests.*` |

## Rebuilding from the pipeline outputs

The datum asset, the greybox and the scene are generated, never hand-edited. Build the pipeline outputs first (they are gitignored and read only the raw cache), then run the builder:

```
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river --skip ais

lockf -k "$HOME/FerrySim-cache/unity.lock" "$UNITY" -batchmode -nographics -projectPath "$PWD/unity" \
  -executeMethod FerrySim.World.EditorTools.WorldBuildCommands.RebuildAllBatch -logFile "$PWD/artifacts/unity/world-rebuild.log"
```

In the editor: **FerrySim > World > Rebuild All** (or the greybox and scene steps separately). The builder reads `landings.geojson`, `landing_faces.geojson`, `landing_structures.geojson`, `bridges.geojson`, `geoid_offset.json` and `depth_merged_navd88.json` from `data/processed/lower-east-river/`, plus the datum station's CO-OPS datums file from the raw cache (`$FERRYSIM_CACHE` or `~/FerrySim-cache`, then `data/raw/coops/`), checked against the sha256 the pipeline recorded. Every position goes through the core's `LocalTangentPlane`; the pipeline's own `local_coordinates` are only a cross-check (they agree to 0.65 mm). Rebuilding the prefab gives its objects new file IDs, so expect a noisy diff even when nothing moved.

## World frame and georeference

- `CesiumGeoreference` sits at the shared world origin (40.7000, -74.0150, ellipsoidal height 0) with scale 1. No Cesium ion or Google tileset is used, so no token is needed.
- `World` (under the georeference) carries `WorldFrame` and a `CesiumGlobeAnchor` pinned to the world tangent plane. Its children use world coordinates as local positions with the conventions' mapping: **x = east, y = up (-down), z = north**.
- The main camera has a `CesiumGlobeAnchor` and `CesiumOriginShift` (shift distance 100 m). When the camera moves, Cesium moves the georeference origin to it and re-places `World`, so everything stays put on the globe and coordinates near the camera stay small. Across the slice the Cesium-vs-core error stays under a millimetre (0.23 mm for Pier 11 with the origin shifted to Hunters Point).
- `GeoreferenceCheck` runs on Start: it converts the Wall St/Pier 11 GTFS stop (stop 87, from `landings.geojson`) through Cesium and through `LocalTangentPlane.World` plus the mapping, and logs an error if they differ by more than 0.05 m, the scale is not 1 or the world frame has drifted.

### Placing things (bridge task)

- Parent the vessel under `World` and set `localPosition = WorldFrame.ToLocal(north, east, down)` and `localRotation = Quaternion.Euler(0, heading in degrees, 0)`. Unity world positions then follow every origin shift. `WorldFrame.ToUnityPosition` and `HeadingToUnityRotation` do the same for objects outside the frame.
- `WaterSurface.EllipsoidalHeight(north, east)` is the water surface's WGS84 ellipsoidal height (geoid offset there plus the NAVD88 water level), for a globe-anchored vessel and the `IWaterSurface` adapter. `WaterSurface.SurfaceUp(north, east)` is the same surface as a height in the world frame (it includes Earth curvature).
- Parent `World/WheelhouseMount` (`WheelhouseCameraMount`) to the vessel at the helm eye point, forward = bow, or assign another mount to `CameraRig.WheelhouseMount`. Set `CameraRig.ChaseTarget` to the vessel.
- Camera controllers run in `LateUpdate` at execution order -100, before `CesiumOriginShift`, and call `CameraAnchor.Commit` after moving the camera. Anything else that moves the main camera must do the same, or the camera jumps by the shift.

## Vertical datums and the water

- NAVD88 zero is the geoid: its ellipsoidal height comes from `geoid_offset.json` (GEOID18, -31.91 m to -31.69 m across the slice, -31.80 m at the centre) and is interpolated bilinearly between the box corners (the centre sample agrees to 1 cm). Nothing hard-codes 32.
- The water surface is at constant NAVD88 height, so in the world frame it curves down away from the origin: about 0.05 m at Pier 11 and 4.1 m at the slice's north-east corner. `WaterSurface` builds that mesh at NAVD88 zero (100 m cells over the slice plus 1 km, then growing cells out to 20 km; fog hides the edge) and raises it by **`WaterLevelNavd88`**, the tide in metres above NAVD88. Set it at runtime from tide data; `LevelChanged` fires.
- Floating landing barges ride the tide: `FloatOnWater` keeps the `Landings` group at the water level. Bridges stay fixed.
- Bridge clearances are metres above mean high water (MHW), not NAVD88. MHW at The Battery (8518750, epoch 1983-2001) is 0.597 m NAVD88 (MHW 2.445 m minus NAVD88 1.848 m above station datum); MLLW is -0.846 m, matching the pipeline.

### Water shader and the depth-copy setting

`Shaders/Water.shader` is a hand-written URP shader (no Shader Graph, no paid assets): depth-tinted colour, two scrolling samples of a ripple normal map generated in code (`WaterNormalMap`, so there is no texture asset), Schlick's fresnel approximation against the same sky model the skybox draws (`FerrySimSky.hlsl`), and a sun glint. Ripple coordinates are object space, which is world metres east and north, so origin shifts don't move them.

The depth tint reads `_CameraDepthTexture` during the transparent pass. That needs two settings:

- **`Settings/Rendering/FerrySim_URP_Renderer.asset`: Copy Depth Mode = After Opaques** (`m_CopyDepthMode: 0`, changed from After Transparents with the coordinator's approval). With After Transparents, URP copies depth after the water has drawn and the tint never sees the ground: the PlayMode test `WaterTint_ShowsShallowGroundAndHidesDeepGround` fails with it (0% of pixels change over a slab 0.8 m down, against 66% with After Opaques).
- The scene camera requests depth on its own `UniversalAdditionalCameraData` (Depth Texture: On); the URP asset's global Depth Texture stays off. Any other camera that renders the water needs the same, or the water falls back to its deep colour everywhere.

## Sun and sky

- `SolarPosition` implements the NOAA Global Monitoring Laboratory solar calculator (equations from Jean Meeus, *Astronomical Algorithms*; https://gml.noaa.gov/grad/solcalc/calcdetails.html), column for column from `NOAA_Solar_Calculations_day.ods`, with its refraction model. `SolarDay` gives solar noon, sunrise and sunset (zenith 90.833 deg). Tests compare against the spreadsheet's own cached values (1e-6 deg), NREL's SPA example (0.03 deg) and USNO rise, transit and set times for the slice (1 minute).
- `SunController` takes a New York date and time of day in the inspector (`NewYorkTime`: the US daylight-saving rule since 2007, no platform time-zone database), or `SetUtc` from code. It points the directional light, fades it out as the sun sets, and sets trilight ambient, fog and the sky colours from `SkyGradient` (hand-tuned keys from night through blue hour to day; greybox values, not a sky model). The fog colour is the horizon colour so far water meets the sky without a seam.
- `Shaders/Sky.shader` (`Materials/Sky.mat`) draws the same gradient with a glow around the sun, a warm band toward the sun at dusk and dawn, and the sun's disk.

## Cameras

| View | Select | Controls |
|---|---|---|
| Free-fly debug | F1 | hold right mouse to look; W/A/S/D move, E/Q up/down, Shift faster, Ctrl slower, scroll changes speed |
| External chase | F2 | follows `ChaseTarget` behind and above with heading lag; right mouse orbits, scroll zooms |
| Wheelhouse | F3 | camera at `WheelhouseMount`; right mouse looks around within the mount's limits, middle button recentres |

Controls read the Input System devices directly and only while their view is active.

## Greybox structures

Generated by `Editor/GreyboxBuilder` into `Greybox/` and placed in the scene under `World`:

- **Landing barges**, one per feature of `landing_faces.geojson`: the outline extruded 1.2 m above and 1.5 m below the NAVD88-zero waterline (freeboard and draft are greybox estimates), a cyan bar on the berthing line with an arrow along the outward normal, and a label with the stop name, the outline's source (ENC outline or estimated from GTFS), the pipeline's confidence and its landing-level verification (`needs-crew-verification`: depth and structure roles are unresolved even where crew confirmed the face). Hull colour follows confidence: orange low, amber medium, green high. `LandingBarge` keeps the face in world metres for contact geometry and debriefs.
- **Whole landing structures**, from `landing_structures.geojson`: a fender strip on every side and end of each landing barge (`Fenders`, one mesh with a submesh per kind), each other berth and nearby pontoon of the stop as its own floating prism with fenders (`Landings/<stop> berth <LNAM>` or `pontoon <LNAM>`, not a `LandingBarge`), and the fixed piers, bulkheads and shoreline within 150 m as water-facing walls with a cap on their solid side (`Structures/<stop>/Walls`, NAVD88 -1.5 to +2.5 m on the datum's curved surface: greybox estimates). Colours: berth face cyan, barge side dark rubber, barge end grey-violet, pier timber brown, bulkhead concrete, shoreline olive, and pink for any segment the runtime does not use for contact (see `Slice/Landings/README.md`). Walls and caps are single-sided, facing the water and the sky. Barges and berths float with the tide; walls stay fixed. `FerrySim > World > Capture Greybox Screenshots` (`GreyboxScreenshots.CaptureBatch`, batchmode without `-nographics`) writes `artifacts/unity/greybox-pier11-*.png`, `greybox-south-williamsburg-*.png` and `greybox-dumbo-*.png` over the importer's seabed.
- **Bridges**: Brooklyn, Manhattan and Williamsburg from `bridges.geojson`. The deck's underside is at the published vertical clearance above MHW (Brooklyn 38.7 m, Manhattan 40.8 m, Williamsburg 40.5 m), along the main span's axis and over all of the bridge's ENC pieces. Tower positions and heights are not in the ENC data: the towers are greybox estimates, placed just outside both the main-span polygon and the published horizontal clearance (so they never narrow the channel) and topped 45 m above the clearance. `BridgeGreybox.TowerHeightIsEstimate` says so.
- Labels face the camera, keep roughly the same size on screen, sit on a dark backing plate, and hide beyond 2.5 km.

## Layers

Coordinator decision: **4 = Water** (Unity's built-in layer) for the water surface, **8 = Structure** for barges, piers and bridges, **10 = Vessel**. Use the constants in `WorldLayers`, never `LayerMask.NameToLayer`; layer 9 is retired (it briefly carried a second "Water" name, which a name lookup would not find).

## Tokens: local-only workflow

Nothing in this scene needs a token. When Cesium ion or Google tiles are added later (decision 0003: Google tiles cost money and need explicit approval), tokens and keys must never be serialized, because Unity stores them in plain text in assets that get committed:

- `Assets/CesiumSettings/Resources/CesiumRuntimeSettings.asset` (`_defaultIonAccessToken`, `_defaultIonAccessTokenID`)
- `Assets/CesiumSettings/Resources/CesiumIonServers/ion.cesium.com.asset` (`defaultIonAccessToken`, `defaultIonAccessTokenId`)
- any `Cesium3DTileset`, `CesiumIonRasterOverlay`, `CesiumCartographicPolygon` or GeoJSON overlay (`_ionAccessToken`), `CesiumGoogleMapTilesRasterOverlay` (`_apiKey`), Bing (`_bingMapsKey`) or Azure (`_key`) component in a scene or prefab
- a tileset `url` with `?key=` in it

So: do not sign in with the Cesium panel's "use as project default token", and do not paste a token into an inspector. Instead:

1. Keep the secrets in a developer-local file outside the repository, readable only by you:
   - macOS and Linux: `~/.config/ferrysim/tokens.json` (`chmod 600`)
   - Windows: `%APPDATA%\FerrySim\tokens.json`

   ```json
   { "cesiumIonAccessToken": "<token>", "googleMapTilesApiKey": "<key>" }
   ```

   Environment variables `FERRYSIM_CESIUM_ION_TOKEN` and `FERRYSIM_GOOGLE_MAP_TILES_API_KEY` override the file (for CI or a one-off shell).
2. Put tilesets in the scene **disabled**, with every token field empty. At startup a small loader (not written yet: add it with the first tileset) reads the file (or the variables) and sets the token from code before enabling the tileset. Values set in play mode are not saved:

   ```csharp
   var token = LocalTokens.Read("cesiumIonAccessToken"); // env var first, then the local file; null if absent
   if (string.IsNullOrEmpty(token)) { Debug.LogWarning("No Cesium ion token: photoreal tiles stay off."); return; }
   tileset.ionAccessToken = token; // runtime only, never in the inspector
   tileset.enabled = true;
   ```

   Do this only in play mode or in player builds, never from edit-mode code, which would mark the scene dirty with the token in it. Builds read the same file at run time, so no token is baked into a player either. Without a token the world still runs on the free data.
3. Before committing, run the EditMode test `TokenSafetyTests.NoSerializedTokenUnderAssets`. It checks direct token fields and property-path/value records in scene instances, nested prefabs, prefab variants and presets under `unity/Assets`, including ScriptableObject and settings assets. It scans `.asset`, `.unity`, `.prefab`, `.preset`, `.mat`, `.lighting` and other assets handled by Unity's native serialized-asset importer. Binary or unreadable candidates fail closed; reserialize them as text for inspection rather than skipping them. Generic `_apiKey` and `_key` overrides are checked against their source component; unresolved targets fail closed. The test also requires the known Cesium settings fields so a rename cannot make it pass vacuously, and never prints values. This is a serialized-field guard, not a general secret detector: it does not inspect URL query parameters, arbitrary JSON/text files, or source-code strings. A direct-field grep alone misses overrides; run the test.
4. If a token ever reaches a commit, revoke or rotate it in the Cesium ion or Google Cloud console straight away (rewriting history does not un-publish it), then clear the field.

## Tests and artifacts

Run with the commands in `unity/README.md` (`-assemblyNames FerrySim.Unity.World.Tests.EditMode` or `.PlayMode` to run only these).

- EditMode: NOAA sun position and rise and set times, New York time, polygon helpers, the datum and water heights, the georeference (Cesium vs core across the slice, the scene's origin, every asmdef reference resolving), the greybox against the pipeline outputs, the scene wiring, and the token check. Comparisons with `data/processed/` are ignored, with a message, when the outputs are not built.
- PlayMode (needs graphics): screenshots `artifacts/unity/world-pier11-noon.png`, `world-brooklyn-bridge-midstream.png`, `world-dusk.png` and `world-water-depth-tint.png`, each checked to show its subject where it projects; origin shifting across the slice; the tide floating the barges; the chase and wheelhouse views; and `artifacts/unity/world-performance.json` (frame time at three viewpoints, 1920x1080).

## Known limits

- No land yet: terrain and shoreline arrive from unity-world-import, so the water reaches the horizon in every direction for now.
- Tower heights and positions, barge freeboard and draft, and deck depth are greybox estimates. Barge outlines and berthing faces are the pipeline's, with its confidence, and all need crew verification.
- The datum station's MHW is read from the raw cache because the pipeline does not publish it; adding it to `coops_stations.json` would remove that dependency.
- The sky colours are hand-tuned, and the sun position ignores the 69 s difference between UT and terrestrial time (well under NOAA's stated accuracy).
