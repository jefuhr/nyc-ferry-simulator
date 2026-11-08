# Landing render kit

`LandingKit.Build` consumes a local X/Z outline, local berthing faces and a `LandingStyle`. It does not reference the core, contacts, physics, Cesium, or the data pipeline. The HarborNavigation adapter copies the source outline and explicit face coordinates into that input; no source records are modified. An unassigned pontoon uses its longest outline edge as a visual frame, with `FaceOrientationEstimated` set. That choice never creates a usable berth.

The kit has three mesh renderers and one shared vertex-colour material:

- Floating hull, deck edge, alternating dark/pale vertical fender panels, end buffers and curved braced shelter.
- A pair of fixed guide piles.
- A shore gangway with a deck and three rails per side. Equal width/span gangways share a reference-counted mesh; the slice builder also shares the baked ramp asset.

The JSON at `Assets/FerrySim/Harbor/Data/landing-looks.json` is referenced directly by the `Resources/LandingLookLibrary` asset. It is not duplicated into Resources. The resource also retains the surface shader/material for player builds. `values.defaults` supplies every dimension; stop-ID groups override individual dimensions with the same source/method/confidence shape. Unsupported or invalid dimensions fail at construction instead of quietly selecting another landing's measurements.

`SetTideOffset` takes metres relative to the build-time water height. Floating geometry remains local to the barge anchor; the piles receive the opposite offset and the gangway rotates/scales between its moving barge hinge and fixed shore hinge. The harbor calls it from the existing tide tick, after updating its double-precision anchor. The slice uses `FollowSliceTide` with its existing `FloatOnWater` parent. No per-frame mesh generation or parsing is performed.

The published approximate 7 ft freeboard and 11 ft hull depth become 2.1336 m and 3.3528 m, giving a visual draft of 1.2192 m. These dimensions do not change physical contacts or vessel state. Canopy height is clearance above the deck; shore and pile heights are relative to build-time water. The shore direction is opposite the primary face and the shore endpoint is an unsurveyed span. Guide-pile siting, shelter fit and boarding openings are visual estimates. Source polygons remain the hull boundary, and the shelter footprint is reduced to fit inside them.

The slice's original source hull, face arrows and fender-strip renderers remain available for inspection but are disabled. Fixed pier/bulkhead/shoreline diagnostics retain their existing behavior. Generated slice assets are produced by the normal builder; this change does not regenerate or stage its output prefab.

## Verification

The EditMode fixtures cover deterministic meshes, bounded part counts, resource sharing/release, packaged look values, invalid inputs, all 24 stops/28 barges, tide/origin behavior, zero tick allocation with a positive control, and unchanged contact bytes/segments/state digests. Their measured cost output goes to `artifacts/landing-models/`. Shader appearance and actual rendered-frame cost require the coordinator's Windows runs described by `artifacts/landing-models/romeo-run.json`; a headless editor or compile-only check cannot verify those results.
