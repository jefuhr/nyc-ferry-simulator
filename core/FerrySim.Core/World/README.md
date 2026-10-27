# Resident harbor runtime

`FerrySim.Core.Serialization.HarborRuntimePackJson.Load(folder)` verifies the exact `ferrysim-harbor-runtime/1` inventory, raw-byte DIGEST framing and every payload SHA-256 before decoding the resident data. It throws `HarborRuntimePackException` with the failing `File` path. This follows the existing environment/traffic serialization assembly; the types here are engine-agnostic C# 9 in `FerrySim.Core`.

`HarborRuntimePack` exposes immutable `Aids`, `Bridges` (each with `Spans` and `Piers`), `Stops` (with `Berths`), `Barges`, `Geoid`, `Coverage`, `Footprints` and `Queries`. Positions use `HorizontalVector` world north/east doubles. `HarborGeometry` keeps polygons with exterior/hole rings, lines and points. A geometry collection retains its type and flattened component geometry. Collections cannot be cast back to mutable arrays.

Each semantic item also retains its full immutable `Record` as `HarborValue`. Read optional evidence without inventing values: `aid.Properties["lights"].Items`, `span.Clearances[0]["vertical_clearance_m"].Number`, `span.Clearances[0]["underside_navd88_m"].IsNull`, `bridge.Names`, `berth.Record["berth_note"].Text`. Missing properties return `HarborValue.Null`; numbers and booleans are nullable. Null heights remain unknown.

`Geoid.TrySample(north, east, out height)` bilinearly samples the shared nodes, choosing the same adjacent active cell as Python at exact edges/corners. It returns false outside coverage, through masked cells and for nonfinite inputs. It never extrapolates. `CellActive`, `Node`, shape, origin and cell size support one-time visual mesh baking.

`Queries.Query(north, east, radius, callerBuffer, out saturated)` returns deterministic bounding-box candidates from a north-sorted immutable index. It is a broad phase for chart/camera/radar queries; consumers do their exact geometry tests. Truncation is explicit. Sampling and queries allocate nothing and do not depend on render tiles. `CollisionAuthority` is always false; `ContactsJson` is the verified original contact text for the existing contact parser/gate.

The small fixture and Python sampler vectors are under `core/dotnet/FerrySim.Core.Tests/World`. The explicit `RealExport_CountsParityAndNormals` test additionally checks the copied wave 3a export and 200 seeded reference points without making ordinary tests depend on the large outputs.
