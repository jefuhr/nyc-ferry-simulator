# Navigation display maths

Engine-independent pieces of the wheelhouse chartplotter (`unity/Assets/FerrySim/Chartplotter`), tested in .NET under `core/dotnet/FerrySim.Core.Tests/Navigation`. Nothing here changes the simulation, the recording or the replay: these are display calculations over the run's state.

| Type | What it does |
|---|---|
| `ChartView` | World north/east metres to screen pixels and back, for a centre, a scale and the bearing that points up the screen (0 north-up, the own course for course-up). Doubles throughout; screen y grows downward. |
| `CpaAlarmLimits` | When a displayed target is an alarm: CPA under the threshold and TCPA in (0, limit]. Defaults 0.1 nautical mile and 6 minutes, estimates pending crew input. The encounter itself is the core's `Traffic.Encounter`, the same formula the run's monitor uses; the run's alarms stay the authority. |
| `TargetRanking` | The closest few targets for a CPA list: alarms first by TCPA, then the rest by CPA, ties by target index. Fixed length, allocation-free. |
| `LabelDeclutter` | Greedy no-overlap label placement with a spacing, on a fixed bucket grid. Deterministic for the same offers in the same order. |
| `FixRing` | Own ship's track: a fixed ring of fixes taken by distance or time, allocation-free on wrap. |
| `PolygonTriangulator` | Ear clipping with holes (after Mapbox earcut), so chart polygons are tessellated once at connect. |
| `SafetyContour` | The ECDIS rule for a safety contour the chart does not carry (the next deeper charted contour) and the depth bands against it. |
