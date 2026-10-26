# Slice landings

Owner: **landings-harbor** (wave 2a). In the `FerrySim.Unity.Slice` assembly (namespace `FerrySim.Slice`): the landing structures the hull can touch, read at run time from the world importer's `World/Generated/LowerEastRiver/contacts.json`, and their fenders from `Slice/Data/landing-contact-surfaces.json`. Units, frames and signs follow [docs/conventions.md](../../../../../docs/conventions.md); the file format is documented in [data-pipeline/README.md](../../../../../data-pipeline/README.md) (contacts.json version 2).

| File | What |
|---|---|
| `LandingGeometry.cs` | `contacts.json` to `ContactSegment`s and a `SegmentListGeometry`; `PrimaryFace(stop)`; what was kept, left out and why |
| `LandingSurfaceTable.cs` | The fender file: a default, per-landing and per-kind `ContactSurface` entries, every value with provenance |
| `LandingApproach.cs` | Bow-on scenario starts off a primary face |
| `FaceClearance.cs` | Whether a hull outline at a pose starts clear of every face |
| `LandingDataException.cs` | A missing or malformed landing file, with the file and every problem |

## Which segments are contact

contacts.json version 2 holds each landing's whole structure: the barge's every side and end, every berth of the stop, nearby pontoons, and the piers, bulkheads and shoreline within 150 m of the GTFS stop. A segment is used for contact when its `water_normal_ne` agrees with the left-hand normal of start to end within 1 degree, and:

1. **Primary face** (`primary_face: true`): its water side is verified by the pipeline's chart test (`water_side_verified_by_pipeline`) or reported by crew (`water_side_crew_reported`). A selectable crew-use berth may also pass the narrow `CrewBerthSolidSide` gate: current note and digest, listed berth, verified solid geometry, a clear 0.5 m UNSARE probe, and nonzero outward slow T/K reports at least equal to inward. Its distinct basis is `solid-side + crew berth use (water side unsurveyed: UNSARE)`; neither water-side flag is changed. Pipeline validation independently checks the exported evidence against current notes, cached ENC and AIS.
2. **Any other segment**: the same, or its charted solid side is verified (`solid_side_evidence.verified`). That proof, approved by the coordinator for collision geometry, means the segment is an edge of a closed charted outline (ENC LNDARE merged with dry SLCONS areas, a PONTON, FLODOC or HULKES, or the documented ASCE derived barge) whose interior is on its right by winding, with charted water and no land or other structure's polygon 0.5 m out on its left. Charted water is a depth area (DEPARE or DRGARE) or ENC UNSARE, unsurveyed water of unknown depth. Each segment records the basis: the outline's `layer` and `lnam`, the `winding_check`, and the probe point (`probe_ne_m`, `probe_lonlat`) with what was found there, including `probe_water_layer` and `probe_water_lnam`. It proves which side is solid, not depth, approach room or a berth. Segments kept only on UNSARE keep `needs-crew-verification`.

A start face has a stricter gate than collision retention: every subdivision of every selectable berth, including non-default berths, must have pipeline water-side proof, crew-reported water side, or the complete crew-use exception. `BerthFace` refuses a missing basis with the stop and berth; the catalog excludes that berth and records the reason. If the default is ineligible, the whole landing is excluded. The solid-side fallback still retains collision-only geometry.

Everything else is left out and listed in `Excluded` with its reason (and as a warning at startup); `Describe()` lists every kept segment with its kind, berth, basis (`pipeline`, `crew`, `pipeline+crew` or `solid-side`) and fender. Segments kept on the crew's report alone are in `CrewReportedOnly`, segments kept on their solid side alone in `SolidSideOnly`. A version 1 file (no `kind`, no `solid_side_evidence`) still loads with the version 1 gate.

The pipeline never turns a point (MORFAC dolphins and piles), an open line in water (gangways, pier lines with no charted solid side) or an always-submerged structure into a contact segment, and lists them for crew review.

## Fenders

`LandingSurfaceTable.For(stopId, kind)`: a landing's own entry (under `landings`, keyed by GTFS stop_id) on that landing's barges (`berth-face`, `barge-side`, `barge-end`), else the kind's entry under `kinds`, else `default`. A landing's own fender never reaches the piers and bulkheads beside it. `For(stopId)` is the version 1 lookup. The file keeps `schemaVersion` 1: `kinds` is an optional section, and unknown kinds are errors like any unknown property.

Every value is `estimated` and says so ("Not a landing survey"): the barge kinds use the elastomeric barge fender of the core's contact report (1.0e6 N/m per m, 5e4 N·s/m per m, friction 0.3, 0.25 m travel); a pile pier with timber fender piles is stiffer with less travel (2.0e6, 7.1e4, 0.4, 0.15 m); an unfendered bulkhead and a rip-rap or natural shoreline are nearly rigid (1.0e7, 1.6e5, 0.5 and 0.6, 0.05 m). Damping scales with the square root of stiffness so the damping ratio stays the barge fender's.

## Tests

`Slice/Tests/EditMode/LandingGeometryV2Tests.cs` (this task's file in unity-environment's test folder): a version 2 file's kinds and berths, a version 1 file still loading, one test per eligibility path (solid side kept, a failed proof left out, a primary face never kept on it alone, the chart basis unchanged, version 1 left out), malformed version 2 fields, fenders by kind and the landing's own entry staying on its barge, the shipped file's estimated entry for every kind, and the importer's real file. `LandingGeometryTests.cs` and `LandingSurfaceTableTests.cs` (unity-environment's) cover the version 1 contract.
