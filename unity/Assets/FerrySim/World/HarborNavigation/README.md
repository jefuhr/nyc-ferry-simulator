# Harbor navigation

`HarborNavigationWorld.Build(pack, georeference, water, atmosphere)` builds the verified `HarborRuntimePack` once. Call it after the scene's georeference and canonical water service exist, and before starting the run. `Follow(runner, camera)` follows the runner's interpolated render time; `SelectedStopId` controls label selection. `Tick(renderTime, camera, selectedStopId, night, visibilityMetres)` is the explicit preview/test entry point. A replacement pack gets a new world; the owner destroys the old component. The component owns and disposes its generated meshes, materials and roots.

`Atmosphere.RegisterNavigationBatch(aids, nominalBridgeSignals)` registers the completed batch immediately, including when called after `Start`. There is one `SunController`, one global chart-light/fog state and one global signal-optics state. Signal points use the existing Allard shader without a second fog pass. Near aid bodies are culled at 2 km or the meteorological visibility, whichever is closer. Far signal points remain resident and their optical shader decides visibility independently of terrain selection. Imported `BuoyFlasher` components are disabled after `AidLights` registers them; their immutable compiled rhythms are evaluated once by the atmosphere instead of again in `Update`.

## Geometry and evidence

- One rigid barge mesh per source structure ID, never per berth face. Its closed outline comes from the runtime export, not from contact segments. Freeboard 1.2 m and draft 1.5 m are the existing `GreyboxBuilder` estimates. Each barge samples its own `IWaterSurface` position at render time.
- Floating barges and stop-label roots retain individual Cesium anchors. Static aids and drawn bridge spans share anchors in 2 km north/east cells, centred at ellipsoid height zero. Cell size is a rendering estimate, not a datum or physics-grid choice. Child offsets subtract ECEF doubles and rotate into the cell frame before conversion to floats; each child retains its own local east/up/north orientation, including curvature and light-sector bearings. Mesh vertices still use their object-local frame. Anchors cache ellipsoid-zero ECEF and local up at build time; barge tide updates only translate in double precision, with no per-frame geodesy. Regional children cannot change height.
- Stop label groups keep the TextMesh on an unanchored child: only that child billboards and scales. The anchored root stays untouched after placement, so Cesium origin shifts cannot reinterpret a billboard change as a new globe position. They contain stop and berth names, use `InstrumentPalette.Day/Night.Text`, and default to selected or within 750 m. Confidence, derived-outline disclosure and berth crew notes stay in `Details`. A derived outline is explicitly "estimated from GTFS, not surveyed".
- `HarborSpanView.Source` keeps each span's MHW clearance and null bounds unchanged. `DisplayUndersideNavd88` and `DisplayHeightBasis` are presentation-only estimates. Deck thickness is estimated at 3 m; uncharted underside is estimated at NAVD88 +15 m; tower tops default to 12 m above that display underside. Source polygon outlines are extruded; source line spans get estimated 1 m ribbons. Towers are estimated proxies, not a pier inventory. Nominal lights do not claim surveyed lantern locations.
- `Queries` is exactly `pack.Queries`: a resident camera/radar broad-phase index, with explicit saturation and no dependency on view or render tiles. All source null vertical bounds and `CollisionAuthority == false` remain intact. This adapter creates no colliders, including no streamed/proxy mesh colliders. The collision/contact parser remains the sole contact authority. The terrain pipeline already excludes landing-barge source strips; do not overlay the old imported slice terrain beneath this world.

## Display datum

Display-only MHW conversion uses the nearest cached datum station among two stations in the harbor environment manifest. These are NOAA CO-OPS 1983–2001 station-datum values, in metres: The Battery 8518750, MHW 2.445 minus NAVD88 1.848 = +0.597 m; Kings Point 8516945, MHW 6.195 minus NAVD88 5.209 = +0.986 m. Station positions match the environment manifest. Willets Point 8516990 is a third water-level gauge but its datum file is absent from the existing cache; this wave permits no downloads. Therefore nearest *available datum station* is used and explicitly labeled approximate. This does not establish a surveyed local MHW surface or change any clearance authority.

Raw cached datum SHA-256:

- `datums_8518750.json`: `e99b3f733f8efd1b9f1d6f56a5baabeaba0c86d1a511c7f1235ab23b2ed2504e`
- `datums_8516945.json`: `f953b42feb9d6dcbf4f466a824d821efacbcc1fb299d10dd022233722525d031`

## Explicit limitations

The two boundary aids `0226022294660032` and `02260222946E0032` remain in the pack but are not drawn and receive no datum. The five incomplete light characters use the coordinator-approved estimate: four `Fl W` records use 4 s and one `AlFl W G` uses a 4 s cycle with 2 s white, 2 s green. Each affected aid's details says "period estimated, chart gives none". All original records remain attached. Missing ranges use the existing colour-based range estimates. Aid body dimensions and lamp elevations are presentation estimates; published MHW lamp heights use the approximate station conversion above.

No output claims survey accuracy, collision clearance, Windows-player acceptance or an integrated harbor trip. Real-data capture tests are separate from the small fixtures and report their own evidence.

Boundary bridge policy (approved by the coordinator): all 193 source spans remain resident. Seven have no source vertex in active datum coverage and receive neither an anchor nor visuals: `enc:0226025404261C82`, `enc:02260970BCD21C82`, `enc:02260FBD09671C82`, `enc:02261EB384631600`, `enc:02262C3DA195214E`, `enc:02262213F2361C82`, `enc:0226299AD2181C82`. The edge-crossing `enc:0226121BF3401600` is anchored rigidly at the first in-coverage source vertex, in source order. No lattice value is sampled or extrapolated beyond coverage. The source footprint and null bounds still serve camera/radar queries for every omitted span.

## Verification

Small EditMode fixtures exercise compiled fallback rhythm parity and zero allocation, day/night/fog batch registration, closed-outline construction, per-anchor tide and origin shifts, retained null clearance evidence, and omitted/edge-anchored spans with unchanged query results. PlayMode tests exercise registration after `Start`. Explicit real-pack tests check 28 barges, 24 label groups, 285 drawn aids, 193 retained spans, five estimated characters and allocation-free updates.

The explicit capture harness builds the navigation batch with the real local terrain/skyline tiles and the shared atmosphere. Its six images cover Pier 11, the Brooklyn Bridge and St. George by day and night. Its water is an isolated local planar datum fixture; these captures do not prove the sibling harbor-water integration, an integrated driven trip, start eligibility, or a player build. The coordinator owns those combined gates.

Allocation acceptance uses a calibrated `GC.Alloc` recorder on the current thread. A deliberate string allocation must produce one sample before each measurement; measured loops change tide samples, label selection and palettes, camera visibility, aid flash phases and day/night/fog lighting. Receipts publish marker sample counts; editor Mono allocation-byte counts are unavailable.

## Origin-shift regression and measurement

`HarborWorldTests.BillboardLabelsKeepTheirGlobePositionAcrossRepeatedOriginShifts` changes camera rotation and distance before three origin relocations in different directions, requiring each label's retained ECEF position to stay within 1 mm. `RegionalAnchorTests.CellCornersMatchIndependentAnchorsBeforeAndAfterShifts` compares grouped cell corners against independent per-object anchors, including elevated/offset points and distant origins: 1 mm near the object, 1 cm across the harbor.

The explicit real-pack `NavigationOriginTests` check every drawn aid at all 24 landing origins and preserve object/lamp/signal frames within 1 cm. Its cost test measures 200 main-thread origin relocations under `FerrySim.Navigation.OriginShift`, then checks 100 relocations with the calibrated `GC.Alloc` recorder (exactly one positive-control allocation). These are isolated Editor measurements, not installed-player frame-budget acceptance. The 100 m scene origin-shift distance is unchanged.

`Tests/HarborE2E/PlayMode/SemanticSceneTests.WarmCatalogSwitchesKeepTheStartedLandingLabelNearOwnShip` is a separate explicit real-scene test: sequential accepted Pier 11, Astoria and Soundview starts require the nearest shown label to identify the started stop. It sets selection from the accepted catalog choice, independently of picker binding. Player measurements and capture comparisons live under `artifacts/labels-origin-shift/`.
