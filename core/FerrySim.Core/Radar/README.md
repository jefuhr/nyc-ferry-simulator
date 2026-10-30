# Radar core

Deterministic render-only radar model in SI units, independent of Unity and streamed scenery. `RadarScene.FromPacks(chart, runtime)` compiles the shared resident semantic data once; `RadarEngine.Advance(renderTime, frame, targets, count)` updates only passed spokes into preallocated polar storage. `RadarConfiguration` labels all estimated equipment/response parameters; `RadarScale` converts its display presets from nautical miles and nanoseconds into SI.

`RadarFrame` carries double north/east position, heading radians, tide in NAVD88 metres, antenna height above current water and wind speed in metres/second. `RadarTarget` carries double position, length, height above water and estimated response amplitude. Callers own and reuse the target array. Rewinding, replacing a run or changing range clears old returns. Noise depends only on revolution, spoke and bin, never wall time.

The Unity adapter's README documents model approximations, parameters, wiring, controls and evidence. **Not for navigation.**

The estimated default sweep uses 512 spokes and a hard four-spoke work limit per frame. Pending spokes carry forward; a jump longer than one revolution drops older work and retains the latest revolution. At sustained frame rates below 51.2 Hz the displayed sweep can lag; there is no unbounded catch-up burst.

The compiled index retains each double-precision segment once, one feature ID per edge, one roof/land/bridge record per feature, and flat integer cell references with offsets/counts. Each engine owns per-ray edge visit stamps so a multi-cell edge is tested once per ray. `RadarConfiguration.DeduplicateEdges = false` is retained as an evidence-only reference path; normal construction enables deduplication. Real capture amplitudes are bit-identical to the frozen pre-optimization reference, with zero tolerance.
