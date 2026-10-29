# Harbor water

`FerrySim.HarborWater` owns the harbor's spatial height service and visual water patches. It references the core, Sim, World, Cesium and Unity Mathematics; it supplies neither physics depth nor collision geometry.

`HarborDatum` wraps the verified runtime export's shared-node, masked 250 m float64 lattice. `HarborWaterService.EllipsoidalHeight(north, east, time)` returns that canonical NAVD88-zero ellipsoidal height plus `RunEnvironment.WaterLevel` at the same position and time. Outside the active lattice the datum refuses a sample; nothing extrapolates. The composition root creates a service for each successful harbor start and binds it to every floating object's `IWaterSurface` dependency.

## Baking and updating

- `WaterFrame.AtNorthEast` establishes a local east/up/north basis once. ECEF positions are subtracted in double precision before any float conversion.
- `BakeNode` stores a node's local base, local up and normalized inverse-distance gauge weights in caller-owned storage. Geodesy runs only while baking.
- `SampleGaugeLevels` samples the day's gauges once at the render time into a supplied array, using the environment's coverage clamp.
- `UpdateNodes` combines those levels with the baked weights and writes to a supplied `Vector3[]`. It does no geodesy, LINQ, allocation or depth access.

`HarborWaterPatch.Configure` takes integer common-lattice coordinates (`firstNorth`, `firstEast`), 1–40 cells per side, the service, a shared material and optionally a runner. Its lattice spacing is 100 m. Every cell reuses its corner indices; adjacent patches use exactly the same world edge coordinates and weights. No hanging nodes or independent edge refinement are introduced. A cell is emitted only when all canonical datum cells touched by its positive-area footprint are active.

The patch has a `CesiumGlobeAnchor` under a `CesiumGeoreference`, retaining its double ECEF anchor through origin shifts. `LateUpdate` uses the runner's interpolated `RenderTime`; callers can invoke `UpdateWater` explicitly instead. Mesh positions are updated with the existing vertex array and `Mesh.SetVertices`. Normals and a conservative static vertical bound are baked once. The component owns and disposes its mesh; its material is caller-owned. It creates no collider.

The uniform lattice is the initial accepted topology. Refinement near landings is only necessary if the sampled 2 cm error check fails; the explicit real-export test measures the uniform lattice before making that decision.

## Verification

Small committed fixtures cover local tide variation, datum refusal, baked/exact parity, a calibrated `GC.Alloc` measurement and component behavior across tide updates and a 120 m origin shift. The PlayMode test checks common-edge vertices and absence of colliders.

`HarborWaterAcceptanceTests.RealHarbor_SurfaceAnchorsSeamsAndAllocation` is an explicit EditMode check requiring the copied `data/processed/harbor/runtime` and environment outputs. It writes `water-surface.csv`, `water-anchors.csv` and `water-summary.csv` under `artifacts/harbor-runtime`:

- Every 100 m cell in every depth tile is sampled at nine interior points, at Pier 11's daily low/high tide and the two greatest spatial-spread extremes. Float local mesh quantization and triangle interpolation are included. The reference ECEF conversion uses Cesium independently of the service's baking conversion.
- All 24 primary landing midpoints and every distinct depth-grid corner are checked at each position's daily low and high tide, at four headings and before/after a 120 m northward origin shift. Both the anchor centre and a near-field hull point must stay within 1 cm.
- Adjacent patch frames are compared at every common edge node, and the full patch set is updated for 300 frames with a calibrated allocation recorder, including mesh submission. Calibration must observe exactly one intentional string allocation before a zero-allocation result is accepted.

These tests measure Editor CPU geometry and submission. They do not claim player GPU performance, rendered visual acceptance, datum survey accuracy or a new depth product.
