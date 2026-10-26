# Traffic

Owner: **traffic-unity** (M3 wave 2b, plan item C2). The harbor's real AIS traffic in the slice: greybox hulls on the run's clock, from the core's replay (`core/FerrySim.Core/Traffic/README.md`), floating on the drawn water. The run itself (`Sim/VesselSimulationRunner`) owns the traffic, its CPA alarms and collisions, and records and replays them (Sim/README.md, Traffic); this folder only draws it. Units, frames and signs follow [docs/conventions.md](../../../../docs/conventions.md).

| Folder | Assembly | Contents |
|---|---|---|
| `Traffic/` | `FerrySim.Unity.Traffic` (namespace `FerrySim.Traffic`) | `TrafficView` (places the hulls every frame), `TrafficHull` (the public per-hull component), `TrafficHullMesh` (the greybox shapes) |
| `Traffic/Tests/EditMode/` | `FerrySim.Unity.Traffic.Tests.EditMode` | The scenario's `traffic` field, traffic through the runner, recording and replay, the hulls, and the byte-for-byte check of runs without traffic |

## The hulls

`TrafficView` (the slice's `SliceBootstrap` adds and connects it) builds, at every run start whose traffic changed, one hull per vessel of the day that may come within the slice's area (the drawn water's datum box) with a 250 m margin, as the replay moves it: `TrafficReplay.MayEnter` bounds every report, the straight line of every interval the replay moves on straight, and the box around the Bézier control points of every Hermite interval, so a curve that bulges into the area while its reports and the lines between them stay out still gets a hull. Only a vessel proven never to enter gets none. Hulls are destroyed with the next traffic.

**Every frame** (`LateUpdate`, after the runner has stepped and drawn the learner's vessel), at the time the learner's vessel is drawn (`TrafficView.RenderTime`: between the last two physics states by the driver's interpolation factor, so the two never drift apart):
- a hull is shown only while its vessel is in the run's traffic (inside one of its segments and not the one left out), its position is inside the area with its margin, and it is within the view range (default 4 km) of the camera;
- it is placed in the world frame at the replay's (north, east), at the drawn water's height there (`WaterSurface.SurfaceUp`, looked up again only when the hull has moved 20 m or the level changed), turned to the replay's heading: the same heading the monitor turns its collision rectangle to, so a slow vessel without an AIS heading holds one heading on screen too (core Traffic/README.md, The replay);
- static vessels (the pack's `moving` false) sit still at their median position.

Placing allocates nothing (tested). Its CPU time is kept: `LastMilliseconds`, `MaximumMilliseconds`, `MeanMilliseconds`.

**The shapes** (`TrafficHullMesh`) are greybox: a hull from the keel (the AIS draught, or the class's) to a freeboard, with a pointed bow, and a superstructure, sized from the vessel's length and beam (AIS, else the class defaults in the core README). Freeboards and superstructure sizes by class are greybox estimates, not the vessels' shapes: a ferry's long cabin, a tug's house forward, a ship's house aft with a container deck forward, a sailboat's mast. One colour per class. The hull's origin is the reported position at the waterline, which MarineCadastre gives without the antenna's offsets, so the hull is centred on it.

**`TrafficHull`**, on every hull, says who it is and what it is doing, for its navigation lights and inspection: `Mmsi`, `VesselName`, `Class`/`ClassName`, `Length`, `Beam`, `Draft`, `Height` (the greybox's top above the waterline, where a masthead light would sit), `LengthFromAis`/`BeamFromAis`, `SpeedOverGround`, `CourseOverGround`, `Heading` and its `HeadingSource`, `Status` (`Underway`; `Stopped`, a moving vessel under 0.25 m/s, which AIS cannot tell from made fast; `Moored`, a static vessel), `Shown`, and `Alarmed`/`Colliding` from the run's monitor.

**Traffic is only drawn.** It never exerts a force on the learner's vessel (tested bit for bit); collisions are the runner's events.

## Players

A player reads traffic packs from StreamingAssets' `data/processed/harbor/traffic/<date>` first, then the repository (`ScenarioTraffic.DefaultPacks`), but no player build copies them yet (`Build/Editor/PlayerPayload` is unchanged: the report proposes the choice). The 28 packs are 184.7 MB on disk (3.9 to 8.3 MB a day; about 37% of that gzipped); the pipeline's `_sources/` cache beside them (72 MB) is not part of any pack and must never ship.

## Tests

EditMode, `-assemblyNames FerrySim.Unity.Traffic.Tests.EditMode` (synthetic packs in a temporary folder: still water and air, vessels on straight lines, so every number can be checked by hand):

| Test | Checks |
|---|---|
| `RecordingBaselineTests` | Calm, uniform and real-pack runs record the same bytes as before traffic existed (against `artifacts/traffic-unity/recordings/base/`, recorded on the code before traffic with `FERRYSIM_RECORDING_LABEL=base`) |
| `TrafficScenarioTests` | The `traffic` field: none by default and in JSON without it, its JSON round trip, an unknown day, another day than the environment pack's, calm or uniform water, a bad leave-out or MMSI refused, and the runner keeping its run when a traffic scenario cannot start (a pack that does not verify named by its file) |
| `TrafficRunTests` | The monitor on the run's clock at every step, the alarm before the collision, traffic never pushing the vessel (bit for bit), the automatic leave-out, a reset starting the traffic over, no garbage per step |
| `TrafficRecordingTests` | The header's traffic keys and the event log, a run without traffic writing exactly two files and no traffic key, bit-for-bit replay with events through `ReplayPlayer.Verify` and the runner, another day's or a changed pack refused naming both digests, a run without traffic given a pack refused, an edited event refused |
| `TrafficViewTests` | Hulls where the replay says at the learner's drawn time on the drawn water, only the run's traffic inside the area and the view range drawn, sizes from AIS and the class defaults, no garbage per frame |

End to end, in the integrated slice (`Tests/E2E/TrafficTripTests.cs`, PlayMode, artifacts under `artifacts/e2e/traffic/`): the hulls against an independent replay at Pier 11 in the evening rush; the learner's own ferry left out at Pier 11 and everyone else there; a synthetic vessel's CPA alarm, then the collision, in the panel and the debrief; bit-for-bit replay with events and another day's traffic refused; and the frame cost at the day's busiest minute.

## Navigation lights

Every hull configures `NavigationLights.ConfigureGeneric` once from its length, beam and greybox height. The mast is 1.5 m above the hull height; the deck is at 40% of that height (placement estimates). Underway and Stopped show the Rule 23 length-dependent power-driven set. Moored shows none. The lights follow the own vessel's daylight and restricted-visibility rule, using the same `SunController` and run visibility. Hidden hulls are inactive; placement only changes the existing light set's On state.

The `ferrysim-traffic/1` pack does not carry AIS navigational status, so it cannot distinguish at anchor from other stationary traffic. Rule 30 anchor lights cannot be assigned from this format. Tows, restricted-manoeuvrability vessels and other special lights are also unmodelled. The paired busiest-night-hour cost test and its allocation scope are in `PickerBerthTripTests`; results are written to `artifacts/pier11-and-picker/traffic-light-cost.json`.
