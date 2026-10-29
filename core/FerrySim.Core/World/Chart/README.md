# Resident Chart API

`FerrySim.Core.Serialization.HarborChartPackJson.Load(folder, runtime)` verifies the fixed chart inventory, every SHA-256 and the manifest-first DIGEST before decoding. It refuses a different runtime origin, coordinate-frame text or runtime DIGEST, malformed geometry, unsorted/duplicate identities, invalid references and incomplete or incorrect grid entries. A `HarborChartPackException` identifies the failing file. Serialization stays in the existing serialization assembly; no new assembly or package reference is required.

```csharp
var chart = HarborChartPackJson.Load(chartFolder, runtime);
var layer = chart[ChartLayer.RadarObstructions];
var query = layer.CreateQueryBuffer(4096); // Allocate once before frame updates.
layer.QueryCircle(north, east, rangeMetres, query);
for (var i = 0; i < query.Count; i++)
{
	var feature = layer.Features[query[i]];
	// feature.Geometry uses double [north, east] metres, including polygon holes.
	// feature.Properties["roof_navd88_m"].Number is null for unknown roofs.
}
// query.TotalMatches and query.Truncated explicitly report insufficient capacity.
```

`QueryBox(minNorth, minEast, maxNorth, maxEast, buffer)` and `QueryCircle(north, east, radius, buffer)` use the exported 250 m fixed grid, deduplicate candidates, perform exact geometry intersections and return ascending feature indices. Boundaries are closed, tangencies count, and circle radius zero is valid. References only have runtime bounding boxes; their query is a broad phase. `feature.Record["runtime_layer"].Text` and `feature.Record["runtime_id"].Text` resolve geometry from the linked runtime pack. These are semantic queries, never collision authority.

The query path allocates **zero bytes**, even for the first query after load: buffer creation owns candidate/dedup/result arrays and sorting uses an internal heap sort without delegates or comparer initialization. Create one buffer per layer and concurrent caller. Reusing a buffer overwrites its results; layer data is immutable. IDs are deterministic within a verified pack, while indices are scoped to that pack/version. `Features` is read-only and cannot be cast back to its backing array.

`ChartFeature` exposes `Id`, `SourceId`, `FeatureClass`, `Geometry`, north/east bounds and immutable `Properties`. Its `Record` holds semantic metadata and shared attributes, excluding duplicate geometry/bounds. Source datums are explicit: ENC depth values use MLLW; building roofs use NAVD88. No conversion or fallback height occurs. References have null geometry/source class; land radar obstructions have no invented roof height.

`HarborChartPack.Manifest` retains provenance, datum, source licence and simplification/quantisation evidence. `Digest` identifies exact chart bytes and `RuntimeDigest` identifies its required runtime. `ChartLayer` enumerates `Land`, `DepthAreas`, `Contours`, `Soundings`, `Hazards`, `Channels`, `RadarObstructions` and `References`.

Focused tests: `lockf -k "$HOME/FerrySim-cache/heavy.lock" dotnet test core/dotnet/FerrySim.Core.Tests --filter FullyQualifiedName~HarborChartPackTests`. The explicit `FullPackResidentMemoryAndQueries` test requires copied adopted products and a built chart; select its exact fully qualified name to measure `GC.GetTotalMemory(true)` around chart loading and validate all eight real layers. Neither .NET measurements nor previews certify Unity/player memory or rendering budgets. **Not for navigation.**
