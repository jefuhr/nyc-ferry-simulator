# Semantic radar

A render-only, original X-band training display built with UI Toolkit. **Not for navigation.** The core model in `core/FerrySim.Core/Radar` has no Unity dependency. It does not use Cesium, render meshes, terrain residency, recording, replay output, collision geometry or simulation mutations.

## Wiring

Load and verify the chart once per process in the composition root, then pass the same resident pack to radar and chartplotter:

```csharp
// chart and runtime are already loaded and verified by the composition root.
var radar = radarObject.AddComponent<FerrySim.Radar.RadarController>();
radar.Connect(runner, chart, runtime, displayParent);
// Optional integration-owned visibility/key routing:
radar.View.ChangeRange(+1);
radar.View.SetHeadUp(true);
radar.View.SetPalette(FerrySim.Radar.RadarPalette.Night);
```

Exact overloads:

```csharp
void Connect(VesselSimulationRunner runner, HarborChartPack chart,
    HarborRuntimePack runtime, VisualElement parent = null);
void Connect(IVesselTelemetrySource source, HarborChartPack chart,
    HarborRuntimePack runtime, TrafficRun traffic,
    IEnvironmentReadoutSource environment, VisualElement parent = null);
```

The runner overload follows each run's traffic, uses `RenderPose` and `RenderTime`, and removes `TrafficRun.RemovedVessel` exactly as `TrafficView` does. The interface overload receives an explicit run; reconnect it when replacing its traffic. Neither overload searches the scene or loads a pack. Supplying a parent adds the display to that UI; omitting it creates an owned `UIDocument` with the existing wheelhouse panel settings. Destroying the component disposes its texture and owned UI. An ended run clears the picture; a new run, rewind or range change restarts the sweep.

Default size is 480 × 680 reference pixels, with a 440 × 440 PPI and a 384 × 384 texture. The existing panel settings scale 1280 × 720 to 2560 × 1440. The composition root owns placement, visibility, focus and keyboard routing. No global keys are registered here. Sliders and buttons retain UI Toolkit keyboard focus behavior.

The harbor wheelhouse (wave 3c integration) uses two seams added for the composition root: `Connect(runner, RadarScene scene, parent, compact)` takes a semantic index built ahead with the shared chart load (`RadarScene.FromPacks` is engine-free, so it runs on the loader's thread; the engine, the view and the texture are created on the main thread here), and `new RadarView(engine, rasterSize, compact: true)` is the compact wheelhouse layout: 480 × 380 with a 280 px PPI, one header row, the readouts on one line, the five sliders on one row and no buttons, every font at least the default layout's 10 px (`RadarEvidenceTests.CompactLayoutFitsTheWheelhouseWithoutClippingOrOverlap`, with captures at 720p and 1440p). `StepEbl`, `StepVrm`, `StepGain` and `StepSea` move the sliders by one keyboard step. The wheelhouse keys ("Displays" action map, Input/README.md): `2` show/hide, `[`/`]` range, `Z` north-up/head-up, `X` palette, `,`/`.` EBL, `;`/`'` VRM, `3`/`4` gain, `5`/`6` sea. The palette follows the scene's night through the composition root: at night it holds the night palette, and a palette chosen by day holds until the next night.

## Model and estimates

All settings below are **estimated**, not manufacturer or crew specifications. The speed of light and the task-specified 4/3-earth horizon relation are physical/model constants rather than equipment measurements.

| Parameter | Value | Basis / limitation |
|---|---|---|
| Rotation | 24 rpm (0.4 revolutions/s in the model) | Estimated ferry radar sweep |
| Horizontal beamwidth | 1.2° | Estimated; three deterministic edge/centre rays approximate the beam |
| Spokes | 512/revolution | Estimated display resolution; 0.703125° spacing, below the estimated beamwidth |
| T-class antenna above waterline | 9 m | Estimated; the audited vessel model manifest has no radar mount |
| K-class antenna above waterline | 11 m | Estimated; likewise no radar mount |
| Unknown building roof | 12 m NAVD88 | Estimated, used only by shadow tests; does not become surveyed evidence or a horizon height |
| Unknown bridge top | 80 m NAVD88 | Estimated tall obstruction proxy; null span/pier bounds remain null in the source |
| Aid height / size | 3 m / 2 m | Estimated small point response; no racons |
| Traffic height | `clamp(length × 0.15, 3, 35)` m | Estimated because AIS has no height field |
| Sea clutter | Wind/15, capped at 1; range envelope `1/(1+r²/40000)` | Estimated, deterministic hash modulation; STC suppresses near-range clutter and echoes |
| Noise | 0.025 × hash(revolution, spoke, bin) | Estimated amplitude; repeatable, no random or wall-clock source |

| Range (nm) | Pulse (ns), estimated | Bins |
|---:|---:|---:|
| 0.125 | 60 | 512 |
| 0.25 | 60 | 512 |
| 0.5 | 90 | 512 |
| 0.75 | 90 | 512 |
| 1.5 | 150 | 1,024 |
| 3 | 300 | 1,024 |
| 6 | 600 | 1,024 |

The 2,048-spoke trial exceeded the measured frame budget; 512 retains the three-ray beam model while sampling more finely than its estimated beamwidth. This resolution choice is a documented performance tradeoff, not a manufacturer specification.

The resident radar obstruction layer supplies shoreline and building edges, including polygon holes. Connection compiles these edges into a 50 m grid backed by one double-precision segment store, parallel feature IDs and a flat array of cell edge references; the original chart pack remains shared and unmodified. Runtime bridge spans and associated piers supply bridge/tower returns. Each ray follows the grid, stamps each edge so it is intersected only once per ray, and retains the nearest intersection per feature, then processes range bins near to far. A higher angular roof can rise out of a nearer roof's shadow. Land has an edge return but no invented height or land-volume shadow. Roof heights are NAVD88; antenna elevation is current tide plus mounting height minus heave. The antenna's height above water stays constant as a floating vessel rides the tide, while fixed roofs' heights above water change. The horizon is `2.21 × (sqrt(antennaAboveWater) + sqrt(targetAboveWater))` nautical miles; effective-earth curvature also enters shadow slopes.

Unknown roofs cannot supply an evidence-backed horizon cutoff, so only their shadow proxy is applied. Bridge extents, aid reflectivity, traffic height, pulse spreading and sea/noise response are training approximations, open to crew reference. Range-bin resolution merges features in the same bin. Three-ray beam sampling approximates, rather than integrates, continuous antenna illumination. Traffic footprint uses its length and beamwidth/pulse-length spreading. Height enters visibility/shadowing and an estimated response factor `clamp(sqrt(length × height / 240), 0.25, 1)`, referenced to a 30 m × 8 m hull. AIS positions are sampled at render time into owned reusable storage, excluding the run's own removed vessel.

The model updates only the antenna's newly passed spokes. A long render-time jump evaluates at most the latest revolution, and a rewind clears first. Pixels are preassigned to spokes once, so only the new spokes' Cartesian pixels are rewritten, directly in native texture memory. No per-frame geodesy, LINQ or managed upload array is used. Current frames use the environment's latest readout, not a new physics sample. Displays never write simulation or recording/replay bytes.

## Operation and scope

Head-up and north-up share a centred own-ship origin, heading line and four range rings. One EBL is stored as a true bearing and reads both true and relative to heading; one VRM reads nautical miles. Gain, sea/STC, rain/FTC and range controls are available. FTC is intentionally inert: no rain field exists, and fog does not generate rain echoes. Day/dusk/night use a dark background and one echo colour each, reusing the wheelhouse palette roles; dusk selects the existing dim amber role. Fog and lighting do not change model returns. No ARPA, acquisition or trails are implemented; AIS CPA belongs to the chartplotter.

This component does not display chart depths, so the panel's metre/foot depth preference has no radar readout to convert. Range, ring interval and VRM use nautical miles. No real manufacturer name, appearance or branding is reproduced.

## Verification

Failure-mode tests precede the implementation. Core tests cover shadow emergence, tide, horizon, all scale bins, true/relative bearings, incremental/reference equality, rewind/range reset, long jumps, deterministic clutter/noise, pulse/beam spreading and zero allocations per spoke. Unity tests cover literal Pier 11 and Brooklyn Bridge semantic edges, a known buoy, synthetic traffic, raster mapping/reference equality, connection/read-only lifecycle, resolution scaling and calibrated `GC.Alloc` guards. Every Unity guard first proves exactly one marker sample from a deliberate string allocation.

`RadarEvidenceTests.RealHarborCapturesAndProfile` is explicit and needs copied adopted packs. It loads the chart once, captures real semantic harbor pictures at Pier 11 (day, night and fog comparison), Brooklyn Bridge/Manhattan at 0.75 and 1.5 nm, and Upper Bay traffic at 3 nm on 2025-07-16 at 12:18 EDT. Observer poses are evidence fixtures on harbor water, not validated berth starts or a running harbor scene. The day/night/fog comparison freezes run state and changes only presentation, deliberately keeping echoes equal. No render tile is loaded. The test retains PNGs, exact data digests, an index, an alternating 1080p control/tick timing series and an allocation receipt under `artifacts/radar`.

See `artifacts/radar/report.md` for exact results and limits. Synchronous timing covers traffic sampling, sweep, native texture upload and readout update. A separate sum of runtime UI parent markers includes panel update, repaint and batch-mode offscreen rendering on the main thread; GPU time and installed-player acceptance remain unmeasured. The explicit `RadarScaleProfileTests.AllScalesWithMovingObserverAndTraffic` supplements the 65-second alternating control run with all seven ranges at a moving Pier 11 fixture. It uses fixed test tide/wind and real geometry/traffic; select it by exact name in a fresh process. Comparison against a real ferry radar picture remains open to a permitted crew reference.

The estimated default sweep uses 512 spokes and a hard four-spoke work limit per frame. Pending spokes carry forward; a jump longer than one revolution drops older work and retains the latest revolution. At sustained frame rates below 51.2 Hz the displayed sweep can lag; there is no unbounded catch-up burst.

Current performance acceptance remains unmet after the radar-perf follow-up: the final paced 1080p run measured tick plus UI p95 1.525675 ms (limit 1.5 ms), p99 2.369867 ms, and two unmatched spikes with a 7.271450 ms maximum. The compact index retained approximately 34–36 MB in .NET and 22.6 MB in Unity, plus about 5.1 MB for the engine; these are managed deltas, excluding the shared chart and full-player memory.

The measured 384 × 384 texture is now the estimated default, preserving 512 spokes and all model bins. The 384 trial reduced raster p95 from 0.4146 to 0.3253 ms compared with the compact 512 px run; paired captures are retained for visual review. `new RadarView(engine, 512)` remains available as an explicit display-resolution option. Edge deduplication and compact storage preserve every bit of all six real capture amplitude arrays in .NET and Unity; no float-coordinate tolerance or feature/ray reduction is used. See the `radar-perf follow-up` section of `artifacts/radar/report.md` for all raw results, failed trials, comparison captures and final repository gate.
