# Console screens

`FerrySim.Console` owns the render-only console composition. The simulation remains the instrument authority: this assembly reads `IVesselTelemetrySource` and `IInstrumentSource` and never supplies controls or changes state. The same `RenderTexture` serves the physical screen and every HUD pop-out through `IHudScreenSource`.

## Composition

Add `ConsoleScreens` to a host and call `Connect(interior, telemetry, instruments, tessellation, radarScene, targets, traffic, environment, skins)` with the loaded indices. The first five arguments are required; optional target, traffic and environment sources default to the runner's sources when telemetry is a `VesselSimulationRunner`. A custom `DisplaySkinLibrary` is optional. The generic engine and alarm skins come from the existing private-first skin library.

Four meshes in the interior must be named `RadarScreenSurface`, `PlotterScreenSurface`, `PortEngineScreenSurface`, and `StarboardEngineScreenSurface`. Their UV origin is the operator's lower left. Missing surfaces fail connection before target allocation. A self-lit unlit shader shows the display pixels without lighting gain, specular highlights or emission boost; the existing night palettes control brightness. Original materials are restored when disabled or destroyed.

| Screen id | Console mount | Target | Maximum data/picture-upload rate |
|---|---|---|---|
| `radar` | `RadarScreen` | 1024 × 576 | 20 Hz |
| `plotter` | `PlotterScreen` | 1024 × 576 | 10 Hz |
| `engine-port` | `PortEngineScreen` | Skin reference, shipped 800 × 480 | 10 Hz |
| `engine-starboard` | `StarboardEngineScreen` | Skin reference, shipped 800 × 480 | 10 Hz |
| `alarms` | None | Skin reference, shipped 640 × 480 | 10 Hz |

The compact radar interface is uniformly enlarged 1.5× and centred; its PPI remains square. Engine skin values may refresh more slowly than the host cap (the generic skin specifies 5 Hz). Alarm lamps always use simulation time. The alarm panel is rendered only on demand from its HUD pop-out.

`Enabled = false` releases all five targets and reports no signal through `TryGetScreen`; enabling rebuilds them. `OnDisable` also releases resources. With no viewers, enabled targets retain their last images without content refresh. `TargetColourBytes` is the explicit RGBA32 colour storage, 9,019,392 bytes for the shipped sizes. Targets have no depth/stencil, mipmaps or MSAA. The radar raster, UI/font atlases and driver overhead are additional; runtime measurement belongs in the report.

## Integration seams

- Assign `wheelhouseUI.View.ScreenSource = screens` once. `TryGetScreen` returns the shared texture without allocation.
- Drive `SetWheelhouseVisible(bool)` from camera mode, picker and debrief visibility. This is demand for the four mounted screens, not the alarm-only target.
- Drive `SetPopOutShown(screenId, bool)` for each actually visible HUD pop-out, using the bare id (`radar`, not `screen:radar`). This keeps a pop-out live outside the wheelhouse.
- Drive `SetFocus(screenId)` from the lean-in camera; null or an unknown id clears focus. The host never searches for the rig or HUD.
- Connect saved `console.screens` to `Enabled`, `display.vectorMinutes` to `VectorMinutes` and `display.predictor` to `Predictor` (`PredictorMode.Straight`/`Curved`). Defaults are enabled, 2 minutes, straight. These properties persist across target release/rebuild.
- Set `DaylightSource` to follow the scene, or `Palette` explicitly for day/night. The palette is applied in each screen's scheduled slot.
- `Plotter`, `Radar.View`, `PortEngine`, `StarboardEngine` and `Alarms` expose the existing display controls. The composition root can route existing keys, units and visibility into them.
- Automatic `LateUpdate` calls `Tick(unscaledTime, frameCount)`. Set `AutomaticRefresh = false` only when a test or another owner calls `Tick` itself.

The old `HarborDisplays` flat `UIDocument`, radar/chart hosts, positioning and automatic display refresh become redundant when integration adopts this composition. Its shared chart load, input gating, bindings, units, palette preference and run lifecycle still need routing; they are not replaced by this component. All existing public radar/chart APIs remain available, and `HarborDisplays` was not edited.

## Cadence and retained layout

`ScreenScheduler` chooses at most one screen for a frame id. It uses unscaled monotonic time for rates, discards missed slots rather than bursting, and picks the earliest due screen. A due focused screen takes first claim unless another due screen has waited half a second. This bounds starvation below the aggregate requested rate; caps are maxima, not promises at low frame rates. Duplicate calls in a frame never refresh a second screen's data. Demanded panels render their retained geometry every frame; the scheduler limits data refreshes and radar raster uploads, not those retained render passes.

`ScreenPanel` leaves a demanded panel visible between data refreshes. Hiding/showing its root at each slot forced UI Toolkit to re-upload unchanged chart geometry and update styles/layout at 10 Hz; a stationary-chart regression test protects against that behavior. A panel freezes only when it has no viewer: `visibility` and root `opacity` change once on a demand transition, never `display`. Zero root opacity suppresses explicitly visible descendants while retaining viewport dimensions and the last target image. The target does not clear between renders; the live root draws an opaque background. No timers or per-frame Painter2D work are scheduled by this host. A demand transition may still repaint the whole panel once. The zero-viewer frozen-image test remains, and returning demand must still draw the chart.

Per-screen data calls have `FerrySim.Console.Refresh.<screen-id>` profiler markers. `LastRefreshFrame` and `LastRefreshScreen` identify the most recent tick and its selected slot (-1 if none); they allow a profiler harness to correlate a completed frame with native UI work, which runs outside the data-call marker. Existing `FerrySim.Chartplotter.StaticChart` and generation markers distinguish mesh upload from static chart regeneration.

The radar advances and accumulates every rendered frame with radar demand. Its raster receives every engine step through `Accumulate`; only the chosen radar slot uploads through `UploadPicture`. This keeps intermediate spokes at 20 Hz picture cadence, without repainting the whole raster each time. The core radar's existing four-spoke per-frame work cap and long-gap policy are unchanged.

## Adding a fifth mounted screen

The alarm target is already the fifth picture. To add another instrument, append its stable screen id, cadence and mount mapping, add its target/view in composition, and extend the HUD registry contract with its owner. Do not rename existing ids or reuse another display's texture. Add lifecycle, demand, cap, fairness and retained-image coverage; include the new target in memory accounting and the actual prefab surface/FocusEye in captures. An alarm console mount can instead reuse the existing `alarms` picture after adding its explicit mapping and wheelhouse demand.

## Validation

Tests live in `Tests/EditMode` and `Tests/PlayMode`. `artifacts/console-screens/run.sh` is the reviewed local runner, taking both shared locks and writing XML/logs by label. The report distinguishes isolated managed refresh allocations, actual rendered texture evidence, real harbor recording digests and editor timing. The cost follow-up compares paired editor p95 deltas against the flat overlays plus 0.3 ms and also checks slow-frame frequency; final player acceptance belongs to integration on the other machine.
