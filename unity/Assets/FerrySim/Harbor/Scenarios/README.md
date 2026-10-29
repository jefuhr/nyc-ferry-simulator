# Harbor scenarios

Owner: **harbor-picker** (wave 3b). Assembly `FerrySim.Unity.Harbor.Scenarios` (namespace `FerrySim.Harbor`): the harbor's scenario catalog, `HarborScenarioCatalog`, which implements the picker's three contracts from `Sim/` (`IScenarioCatalog`, `IScenarioStartDepthSource`, `IScenarioLauncher`) over the harbor's contacts, its 28 day packs and the vessel files. Decision 0005 (`docs/decisions/0005-harbor-runtime-contracts.md`, Scenarios and the picker) fixes the rules; design section 6 has the reasoning. Tests are in `Harbor/Tests/Scenarios/EditMode`.

## What it offers

- **Landings:** every stop in the contacts (24 in the harbor file), in stable stop id order: numeric ids by value, then any others by text (`SliceScenarioCatalog.CompareStopIds`). A landing is never dropped. One that cannot start stays listed with a `Note` saying why, and `TryCreate` refuses it naming the landing: "East 90th St: waiting for a crew answer on the landing side."
- **Berths:** the exporter's berths (`berth_face`, `berth_order`, `berth_name` in contacts.json version 2, read through `LandingGeometry.Berths`), in source order with the default first: 26 in the harbor file, three of them at Wall St/Pier 11. A berth that cannot start stays listed with a note and is refused naming the landing and the berth: "Wall St/Pier 11, South pontoon: …". An unknown berth id is refused, never replaced by another face.
- **Notes** (`ScenarioLanding.Note`, `ScenarioBerth.Note`, never null): a startable berth's note is its evidence, from the face's own fields, short enough to sit beside the name: what the structure is (`Charted face` from an ENC feature, or `Barge derived from the stop position`, with `(low confidence)` when the pipeline says so) and who vouches for its landing side (`landing side chart-checked`, `crew-reported`, `chart-checked and crew-reported`, or `unsurveyed, crew berth use` for the narrow crew-berth gate), then `crew note <id>` when a crew note applies. A landing's note is its default berth's. A berth whose face has no start basis (`LandingGeometry.BerthFace` refuses it for lacking one: an unverified landing side with no crew report) carries `Waiting for a crew answer on the landing side`; any other face problem carries `Landing face not usable` and the data error in the refusal.
- **The three crew faces** (East 90th St 113, Sunset Park/BAT 118, St. George 137) are exactly such berths in the shipped contacts: their primary faces are left out of contact (water side unverified, and a primary face is never kept on its solid side alone), so they are listed, noted and refused. The catalog never answers the crew question for them.
- **Marks from the landing audit** (`HarborBerthMark`: stop id, berth id or null for the whole landing, a note, and a refusal or null) replace a berth's note and can refuse it; the harbor's composition root hands them over at integration from `data-pipeline/ferrydata/harbor_landings`' output. Without marks, the notes are the evidence above.
- **Days:** the readable day manifests of `data/processed/harbor/env/days`, oldest first (`EnvironmentPacks.Days("harbor")`), each with its own local offset and wind flag, `CurrentMethod` = `station blend` (the harbor packs have no channel field), and `HasTraffic` when `data/processed/harbor/traffic/<date>` is in the build (`TrafficPacks`).
- **Vessels:** as configured, `vessels/t-class.json` then `vessels/k-class.json`; one without a contact outline is left out (`Excluded`).

## A start

`TryCreate` resolves the landing, the berth, the day and the vessel from the lists above and refuses, with the slice catalog's wording, an unknown landing, berth, day or vessel and a visibility that is neither 0 nor at least 10 m. The rest is the slice's start, through `Slice/Landings/BerthStart.cs`, which the two catalogs share: the time is the pack's local midnight plus 0 to 1439 minutes and must be inside the day's coverage; traffic needs the day's pack; the pose is at rest, bow 4 m off the middle of the chosen face, heading straight in (`LandingApproach.BowOn`), refused if the face bends; the hull must be at least 1 m clear of every kept contact segment (`FaceClearance.Check` over the whole harbor's geometry); and the scenario validates against the packs. No GTFS stop position is ever a pose.

**Depth.** `IScenarioStartDepthSource.TryGetStartDepth` samples the harbor day's water level minus bed under the whole contact hull at the start pose and time (`SliceScenarioCatalog.MinimumStartDepth`, a 0.25 m lattice plus every grid node and outline edge) against the vessel's maximum draft. `TryCreate` refuses a start aground (negative under-keel clearance) with the slice's sentence; the picker warns below 0.3 m. The day's environment comes from `IStartDepthEnvironments`: by default `PackDepthEnvironments`, which loads and verifies the pack through `EnvironmentPacks` (that reads and hashes the harbor's 74 MB depth set: the first preview of a day costs about a second; `artifacts/unity/harbor/scenario-catalog-matrix/preview-timing.txt` has the measured numbers). **One day stays resident at a time**; choosing another day replaces it, so the 28 days' depth copies are never held together. At integration the harbor runtime's shared verified depth set can stand behind the interface, so the catalog does not read the depth set a second time. An unknown bed under the hull gives no number and no refusal, as in the slice.

**Launching.** `TryStart` hands the settings to `Launcher`, the harbor runtime's atomic start, once the composition root connects it; until then every start is refused with "No harbor run can start yet: the harbor runner is not connected." The picker shows that sentence inline.

## Building one

```csharp
var landings = LandingGeometry.Load(contactsPath, surfacesPath);     // data/processed/harbor/unity/contacts.json (or the runtime export's copy), Slice/Data/landing-contact-surfaces.json
var catalog = new HarborScenarioCatalog(landings, vessels, EnvironmentPacks.Default, trafficPacks: ScenarioTraffic.DefaultPacks, marks: audit, launcher: atomicStart);
wheelhouseUI.ConnectScenario(runner, catalog, catalog);
```

`landings` null with a `landingError` lists nothing and refuses every choice saying why, as the slice does. `Excluded` lists the days and vessels left out with their reasons; landings are never in it.

## Tests

| Test | Checks |
|---|---|
| `HarborScenarioCatalogTests` | On a miniature harbor (`HarborTestData`): every stop listed in order including the crew faces and a stop whose every segment is left out; notes and refusals by name; berths in source order with an ineligible one noted and refused; audit marks; the bow-on pose 4 m off the face and never the stop position; days with station blend and traffic availability; the slice's refusal wording; depth refusing aground, warning shallow, starting afloat, one day resident; the launcher refused until connected |
| `HarborCatalogRealDataTests` | On the real harbor contacts and packs (ignored, naming the folder, when `data/processed/harbor` is not built): 24 landings in order, the three crew faces noted and refused, 26 berths with Pier 11's three in order, 28 station-blend days with the build's traffic, every startable berth's start for both hulls clear of every kept segment (`artifacts/unity/harbor/scenario-catalog/start-clearances.txt`), Pier 11's south pontoon aground and afloat on 2025-07-14, and a 1,248-row sample of the readiness spike's start matrix with the preview's cold and warm times (`artifacts/unity/harbor/scenario-catalog-matrix/`) |

The data for the real-data tests is copied, not built here: `data/processed/harbor/{env,unity,traffic}` from the harbor builds (README.md at the root, and docs/decisions/0005).
