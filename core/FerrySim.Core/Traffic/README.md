# Traffic

Owner: **traffic-unity** (M3 wave 2b, plan item C2). The harbor's real AIS traffic on a scenario day, for the simulator: the replay of a traffic pack, the encounter geometry, collisions with the learner's hull, the per-step monitor that turns them into alarms and events, the event log a run records, and the leave-out of the learner's own ferry. Engine-agnostic, C# 9 / netstandard2.1, deterministic, and allocation-free per query and per step. Units, frames and signs follow [docs/conventions.md](../../../docs/conventions.md).

| File | Contents |
|---|---|
| `TrafficPack.cs` | `TrafficPack`, `TrafficVessel`, `TrafficReports`, `TrafficClass`: a day's pack in memory, checked as the format promises |
| `TrafficReplay.cs` | `TrafficReplay`: a vessel's position, velocity and heading at any pack time (below) |
| `TrafficHullSize.cs` | The rectangle a vessel gets: AIS length and beam, or the class's defaults |
| `Encounter.cs` | CPA and TCPA between the learner and a vessel |
| `HullOutline.cs` | The learner's contact outline as a ring, and its overlap with a vessel's rectangle |
| `TrafficMonitor.cs` | The alarm limits, the events, the readout and `TrafficMonitor`, which watches every step |
| `TrafficEventLog.cs` | The `.traffic.csv` event log a run records beside its telemetry, written and read |
| `TrafficRun.cs` | `TrafficRun` (one scenario's traffic: pack, replay, time base, the vessel left out) and the automatic leave-out (`OwnFerryFinder`) |

`FerrySim.Core.Serialization` holds the disk side: `TrafficPackJson` (load and verify), `TrafficPackWriter` (synthetic packs for tests and fixtures) and `TrafficPacks` (find a day's pack in `<root>/harbor/traffic/<date>`).

## The pack format

`ferrysim-traffic/1`, built by the data pipeline (`data-pipeline/ferrydata/traffic/README.md`): per scenario day, `manifest.json`, `vessels.csv` (the roster), `reports.bin` (fixed 44-byte little-endian records) and `reports.json` (their layout), under `data/processed/harbor/traffic/<date>/`. Reports are the cleaned MarineCadastre AIS, about one a minute, in the harbor's area (40.50 to 40.90 N, 74.10 to 73.75 W), times in seconds since the day's local-midnight epoch, positions in world (north, east) metres.

**Loading verifies every byte** (`TrafficPackJson.Load`), as the environment packs are verified, and refuses with a `TrafficPackException` that names the file:
- the manifest: format, a yyyy-MM-dd date, an epoch that is New York's local midnight that day (by the 2007 daylight-saving rule, never a time-zone database), the format's area, and no key the format does not have;
- the file inventory, in order, each file's byte size and SHA-256;
- the digest: SHA-256 over `vessels.csv`, `reports.bin`, `reports.json` (each as name, NUL, bytes, NUL), then the epoch text and a NUL;
- the counts (vessels, reports, kept plus dropped equal to source reports, vessels by class against the roster);
- the record layout **as `reports.json` publishes it**: the reader takes every field's offset and type from it (refusing an unknown, missing, overlapping or mistyped field, or a byte order other than little-endian), and a `reports.bin` that is not whole records;
- the roster's columns, classes, flags (`True`/`False`) and quoted names;
- every report (`TrafficPack`): vessels in MMSI order indexed 0 to n − 1, reports grouped by vessel in strictly increasing time, segments numbered from 0 and split exactly at gaps over 300 s, each vessel's report and segment counts and first and last times, finite times and positions, and SOG, COG and heading NaN or in range.

## The replay

`TrafficReplay.TrySample(vessel, packTime, out sample)` is a pure function of the vessel and the time, allocation-free: a binary search in the vessel's reports, then:

- **Existence.** A vessel exists only inside its segments, from a segment's first report to its last, both included. Before its first report, after its last and between segments it is absent. A one-report segment exists only at its report's own time, so it is never drawn for more than a frame.
- **Between two reports of a segment**, over the interval's time dt with Δ the step from one report to the next:
  - the AIS end velocities are v = SOG (cos COG, sin COG) at each end;
  - an end is **valid and consistent** when its SOG and COG are both finite and dead reckoning with its velocity over the step misses the other report by no more than the step's length plus 30 m: |v dt − Δ| ≤ |Δ| + 30 m. The 30 m is the pipeline's position-jump slack. A straight run at a steady speed misses by nothing; a turn of up to about 110° in the interval passes; a stale velocity (the vessel stopped but still reporting 5 m/s) or one pointing back along the step fails;
  - when **both ends** are valid and consistent the position is the **cubic Hermite** curve through the two reports with those end velocities, p(s) = h₀₀ p₀ + h₁₀ dt v₀ + h₀₁ p₁ + h₁₁ dt v₁, s = (t − t₀)/dt; **otherwise it is the straight line** at constant speed, written (1 − s) p₀ + s p₁ so that a report's own time gives its position exactly;
  - the velocity is the curve's derivative (the chord's for the line).
- **At a report's own time** the interval starting there is used, or the one ending there for a segment's last report: the vessel is exactly at the reported position.
- **Heading**, in this order: the AIS heading interpolated the short way when both reports have one. Then, for a **slow** interval (below):
  - one valid AIS heading of the two is held (`AisHeld`);
  - with none, the interval takes the heading its **stretch** holds. A stretch is a run of consecutive slow intervals in a segment with no AIS heading at either end. Its heading is **the last valid AIS heading before the stretch in the segment** (`LastAis`); **else the median of the segment's COGs reported at or above the slow limit** (`MedianCog`); **else unknown**, which reads 0 (north). The heading is the same for the whole stretch: no heading comes from GPS jitter.

  For an interval that is not slow, the curve's tangent when it moves at 0.1 m/s or more; else the one valid AIS heading of the two; else the direction from one report to the next when they are at least 1 m apart; else unknown. `TrafficSample.HeadingSource` says which. A one-report segment takes its AIS heading, else its course when its SOG is at or above the slow limit, else unknown.
- **Slow**: an interval is slow when its own speed (the step's length over its time) and both reports' SOGs, where given, are under **0.5 m/s (about 1 kn)**, `TrafficReplay.SlowSpeed`, an estimate for crew review. A berthed or drifting vessel's AIS positions wander a metre or two between reports, so below that speed the direction of its motion is noise. The step's own speed counts too, so a stale low SOG on a vessel that is plainly moving does not make it slow.
- **The median COG** is taken on the circle: the COGs (reported at or above the slow limit; below it COG is jitter too) are sorted, the circle is cut at the widest gap between neighbours (the first of equal widest, counting the gap through north last), and the middle one is taken (the mean of the two middle ones for an even count). COGs of 350°, 10° and 20° give 10°, not the 20° a plain sort would. For a ferry that shuttles both ways the median is one of its courses or between them: stable, but not a claim about how it lies at its berth (aligning moored boats to their berth or the shore is on the backlog).
- Only headings change below the slow limit: positions and velocities follow the rule above exactly as for any interval.
- **A static vessel** (the pack's `moving` false: every report within 50 m of its median) sits at the componentwise median of all its reports, at rest, heading the circular mean of its valid AIS headings (unknown without any), and exists inside its segments like any other vessel.

"Valid and consistent" applies to the interval as a whole, the plain reading of the format's rule: one bad end makes the interval linear. The golden vectors check every one of these branches (Tests).

**The time base.** Simulation time t reads the pack at t + (scenario start − the pack's epoch), the offset taken to the tick as the environment does (`TrafficRun.EpochOffset`, `PackTime`).

**Where a vessel may go.** `TrafficReplay.MayEnter(vessel, minNorth, maxNorth, minEast, maxEast)` is false only when the vessel provably never comes inside the area (edges included) while it exists, for the motion above: a static vessel's one position; for a moving one every report, the straight line of every interval the replay moves on straight, and, for every Hermite interval, the box around the curve's Bézier control points p₀, p₀ + v₀ dt/3, p₁ − v₁ dt/3, p₁ (a cubic never leaves the hull of its control points). A curve can bulge far outside the line between its reports (the wave 2b review's case: reports 1 m outside an area, the curve 254 m inside), so the line alone is not a bound. The area is widened by a micrometre against rounding. The view uses it to decide which vessels get a hull.

## Hull sizes

A vessel's hull, for collisions and drawing, is a rectangle of its length and beam **centred on the reported position**: MarineCadastre gives AIS length and width but not the antenna's offsets (A, B, C, D), so the rectangle can be off the hull's middle by up to half its length. AIS dimensions are used when plausible: length 2 to 460 m, beam 1 to 80 m and no wider than the length, draught up to 25 m. A missing or implausible dimension follows from the other by the class's length-to-beam ratio; with neither, the class default. **These defaults are estimates for New York harbor traffic, not data:**

| Class | Default length × beam, m | Draft, m | Length/beam | Basis |
|---|---|---|---|---|
| ferry-passenger | 26 × 8 | 1.5 | 3.25 | The harbor's commuter catamarans (the T-class is 26 × 8 m) |
| tug-tow | 25 × 9 | 3.5 | 2.8 | Harbor tugs, 25 to 30 m |
| cargo | 150 × 25 | 8.0 | 6.5 | Small container and bulk ships calling in the harbor |
| tanker | 120 × 20 | 7.0 | 6.0 | Product tankers and tank barges |
| pleasure | 10 × 3.5 | 1.0 | 3.0 | Cruisers and runabouts |
| sailing | 10 × 3.5 | 1.5 | 3.0 | Sailboats |
| fishing | 15 × 5 | 2.0 | 3.5 | Charter and small fishing boats |
| law-enforcement | 12 × 4 | 1.0 | 3.0 | Police and Coast Guard patrol boats |
| pilot | 16 × 5 | 1.5 | 3.2 | Pilot boats |
| search-rescue | 12 × 4 | 1.0 | 3.0 | Rescue boats |
| military | 30 × 7 | 2.0 | 4.5 | Patrol craft |
| other | 15 × 5 | 1.5 | 3.5 | Unknown types |

## CPA and TCPA

`Encounter.Between` takes the learner's and a vessel's positions and **velocities over the ground** (both held constant from now, ARPA's point-target model, reference point to reference point):
- r = the vessel's position less the learner's, w = its velocity less the learner's;
- **TCPA = −(r·w)/|w|²**, seconds: **positive while they close, negative once the closest point is past** (opening);
- **CPA = |r + w TCPA|**, metres, never negative: the distance at that time, past or future, as an ARPA shows it;
- with no relative motion (|w| below 10⁻⁶ m/s) the range stays: TCPA 0, CPA the range;
- the **bearing** is the true bearing of the vessel from the learner, radians clockwise from north in [0, 2π).

## Alarm thresholds (estimates, for crew review)

`TrafficAlarmLimits.Default`. A vessel is a **threat** while it closes (TCPA > 0) at 0.25 m/s or more, with a CPA of 100 m or less within 120 s. The alarm for it is raised at the first threatening step and **clears 2 s after its last threatening step**, so it does not chatter at its edges. Every value is an estimate; none comes from a regulation or the operator, and all of them are crew-review questions:

| Limit | Value | Reasoning |
|---|---|---|
| CPA | 100 m (about 0.05 nm) | About four T-class lengths. In a river 300 to 600 m wide, vessels pass each other at 100 to 200 m as a matter of course; an open-water ARPA setting (0.5 to 1 nm) would alarm for everything in the river. 100 m catches a close-quarters approach, with the rectangles of two 26 m boats still 70 to 80 m apart |
| TCPA | 120 s | Two minutes of warning: at a closing speed of 10 m/s (two ferries at about 10 kn each), 1.2 km of range, time to alter course or slow down |
| Closing speed | at least 0.25 m/s (0.5 kn) | Two vessels at rest side by side (a ferry at its landing beside a moored one) have no relative motion and never alarm; a vessel the learner approaches at a crawl still does |
| Clear hold | 2 s | Interpolated velocities change at report boundaries, so CPA can step across the limit; the hold keeps one alarm one alarm |
| Collision hold | 1 s | Overlaps closer together than this are one collision, so a hull grazing along a rectangle's edge is not counted many times |
| Alongside margin | 5 m | A vessel whose rectangle, grown by this on every side, touches the learner's hull at the run's first step is alongside (below). More than an AIS position's wander at a berth (a metre or two), less than the gap to the next berth over |
| Slow limit | 0.5 m/s (about 1 kn) | Below it a vessel without an AIS heading takes no heading from its motion (The replay) |

Questions for crew (Open questions in the task report): what CPA and TCPA a captain would want in the East River, the alongside margin and the slow limit, whether the alarm should know vessels manoeuvring at the same landing, and whether CPA should be hull to hull rather than reference point to reference point.

## Collisions

`HullOutline.FromContact` makes the learner's vessel file's contact outline (the fender line) a closed ring: the starboard sheer aft to forward, the bow fender line, the port sheer forward to aft, the stern fender line. A **collision** is that ring at the learner's pose overlapping or touching a vessel's rectangle at its sampled position and heading: a bounding-circle check, then any ring edge meeting the rectangle (Liang–Barsky clipping), or the rectangle wholly inside the ring. It is checked at every physics step for every vessel present, so a one-step overlap is never missed. **Traffic never exerts forces on the learner's vessel**: a collision is an event only, and the hull passes through.

## The monitor and its events

`TrafficMonitor.Update(stepCount, state, sink)` looks at the state after every physics step: every vessel present, except the one left out, gets its encounter, alarm and collision check.

**Alongside at start.** At the first step the monitor sees (the run's first physics step), a vessel whose rectangle, **grown by the alongside margin (5 m) on every side**, touches the learner's hull is **alongside**: the run starts beside it (a ferry at its slip next to another operator's boat). While alongside it raises **no alarm and no collision**, whatever its CPA or overlap. Alongside ends **once and for good** at the first step the vessel is present and its grown rectangle no longer touches the hull; from that step on it alarms and collides like any other vessel, so coming back and touching it is a collision. While the vessel is absent (between its segments) nothing changes: it is still alongside when it reappears touching. Only the first step starts it: a vessel that comes within the margin later is an ordinary vessel.

It hands **events** to a sink, in vessel order:

| Event | When | Carries |
|---|---|---|
| `alarm` | A vessel becomes a threat | The encounter now: range, bearing, CPA, TCPA, relative speed |
| `alarm-end` | 2 s after its last threatening step | The episode's first and last threatening steps, its least range and CPA |
| `collision` | The hull starts to overlap the vessel's rectangle | The encounter now; the relative speed is the closing speed at impact |
| `collision-end` | 1 s after the last overlapping step | The episode's first and last overlapping steps, its least range and CPA |
| `alongside` | At the first step, a vessel within the alongside margin | The encounter then |
| `alongside-end` | The first step its grown rectangle no longer touches the hull | The first step, the last step it touched, its least range and CPA |

An episode a run ends inside has no end event. **`ReportOpen(sink)`** hands over a snapshot of every episode still open (`alarm-open`, `collision-open`, `alongside-open`), at the last step seen, with its first step, its last step with the condition, the last encounter and its least range and CPA so far: the recorder writes it when the run ends, so the debrief has the minimum of an alarm the run ended inside. A snapshot is not an end: it changes nothing, is not counted as an event and does not enter the digest. The **readout** (`TrafficAlarmReadout`, what the wheelhouse shows) is the nearest vessel in collision, else the nearest under alarm, else the nearest within a nautical mile (state clear), else none. The **encounter digest** (FNV-1a 64 over every step's alarm and collision counts, the readout's target and its range, CPA and TCPA bits, and every event) lets a replay prove it saw the same traffic. A fresh monitor per run (`TrafficRun.StartRun`) starts with no alarms.

## The event log

`<run>.traffic.csv`, beside the run's telemetry, LF line ends, UTF-8:

```
# ferrysim-traffic-events: 1
# telemetry: <run>.csv
# traffic: pack
# traffic_pack: 2025-07-14
# traffic_digest: <64 hex>
# traffic_epoch_utc: 2025-07-14T04:00:00Z
# traffic_leave_out: auto
# traffic_removed_mmsi: <MMSI or none>
# cpa_alarm_m: 100
# tcpa_alarm_s: 120
# closing_speed_mps: 0.25
# alarm_clear_hold_s: 2
# collision_hold_s: 1
# alongside_margin_m: 5
step,time_s,event,mmsi,start_step,last_step,range_m,bearing_rad,cpa_m,tcpa_s,relative_speed_mps,min_range_m,min_cpa_m,class,length_m,beam_m,name
...one row per event, numbers at full precision (shortest round-trip text), the AIS name quoted when it has a comma or a quote...
...the open-episode snapshot: one row per episode still open at the end (alarm-open, collision-open, alongside-open), same columns, at the last step, with its least range and CPA so far...
# events: <n>
# open_episodes: <m>
# steps: <steps>
# encounter_digest: fnv1a64:<16 hex>
```

`step` is the physics step count at the state the event is at (`time_s` = step / 100); `event` is `alarm`, `alarm-end`, `collision`, `collision-end`, `alongside` or `alongside-end`, and in the snapshot `alarm-open`, `collision-open` or `alongside-open`. The snapshot rows come after every event, and `TrafficEventLog.Parse` keeps them apart (`Open`, not `Records`). A log written before the snapshot existed has no `open_episodes` line (`HasOpenSnapshot` false). A replay formats its own events and snapshot the same way and compares them character for character, with the limits the header records (the alongside margin among them); a log without a snapshot is replayed for its events alone.

## Leaving out the learner's ferry

`TrafficRun.ForScenario(pack, start, leaveOut, mmsi, north, east)`: off (everything), a given MMSI (refused when the pack has no such vessel), or **automatic** (`OwnFerryFinder`): the **ferry candidate** (the pack's `ferry_candidate`, the fleet's T and K boats) nearest the learner's start at the start time, **within 150 m**: a candidate present then is taken where the replay puts it; one that is not, at its report nearest in time **within 120 s**; ties go to the smaller time offset, then the smaller MMSI. With none that near, nothing is left out. 150 m covers the berths of one landing; 120 s is two of the pack's one-minute reports either side. A replay leaves out the MMSI the run recorded, never choosing again.

## Tests

`core/dotnet/FerrySim.Core.Tests/Traffic/` (`dotnet test core/dotnet --filter FullyQualifiedName~FerrySim.Core.Tests.Traffic`, under the heavy lock):

| Test | Checks |
|---|---|
| `TrafficReplayTests` | 622 golden vectors from an independent Python implementation (every motion and heading source), exact positions at every report, existence only inside segments (a microsecond past an end, a gap, a one-report segment), static vessels, the turn through north, stale and missing velocities, slow stretches (the last valid heading held, the median COG at speed through north, north with nothing to go by, one valid heading held, no tangent for a slow one-report segment), the time base, determinism and no garbage per query; `MayEnter` keeps the review's bulging curve and rules out its straight-line twin, and on a grid of areas over the whole fixture never rules out a vessel seen inside |
| `TrafficPackJsonTests` | The Python-written pack loads with Python's digest; a written pack loads back to the same bytes; a flipped byte in each file, a missing file, a size, the digest, the manifest, the layout (followed when moved, refused when wrong), a truncated record, the roster and report order each refused, naming the file; finding packs by day |
| `EncounterTests` | Head-on, crossing, opening (negative TCPA), overtaking, no relative motion; the threat rule |
| `HullOutlineTests` | The T-class ring; sides touching, a cross with no corner inside, one inside the other, bounding circles that overlap without the hulls, rotated poses, no garbage |
| `TrafficMonitorTests` | A head-on approach: the alarm 97 s before the collision, each once, at the steps the geometry gives; the readout; a one-step overlap caught, a graze that parts briefly counted once and one that parts for 2 s twice; the left-out vessel silent; the digest; no garbage per step. Alongside: overlapping at the first step and threatening, yet no alarm or collision while they stay together; within the margin at the start, parting for good, then an alarm and a collision as usual; only the first step counts; absent in between, still alongside. The open snapshot: the review's alarm (rising at 79.95 m, still up at 7.10 m) reported with its least CPA so far, changing nothing; every open alarm, collision and alongside, in order |
| `OwnFerryTests` | The nearest candidate present, one by its report within the window, none near, a given MMSI, ties |
| `TrafficEventLogTests` | Rows read back bit for bit, quoted names, the alongside rows and the margin's header line, the open snapshot read apart from the events (and an older log without one), malformed logs refused naming the log; hull sizes from AIS and the defaults |
| `RealTrafficPackTests` (explicit, needs the built packs) | Every day loads, verifies and replays with every report exact; how often each motion and heading source is used; a moored ferry with no AIS heading (MMSI 367434220, 2025-07-14 18:00) holds one heading; the Pier 11 start beside it is alongside, with no alarm or collision for it |

**Golden vectors.** `python_traffic.py` writes a small synthetic pack with `struct`, `json` and `hashlib`, reads its float32 fields back from the bytes, and replays it by its own implementation of the rule above; it writes `PythonTrafficFixture.cs` (the pack, embedded) and `PythonTrafficVectors.csv` beside itself. Regenerate with `python3 -I core/dotnet/FerrySim.Core.Tests/Traffic/python_traffic.py core/dotnet/FerrySim.Core.Tests/Traffic`. The C# replay must match it within 10⁻⁶ m and 10⁻⁹ m/s and rad. The vessels and reports are made up, not AIS data.

## Limits

- One-minute AIS: between reports the curve is a reconstruction, good to tens of metres in a tight turn.
- One-report segments exist for an instant only; a moored boat whose AIS drops out for over five minutes vanishes until it reports again (the format's rule, kept faithfully).
- Positions are the AIS antenna's, the rectangle centred on it.
- Unknown headings read north; such a hull is drawn and checked pointing north. A slow vessel with no AIS heading holds one heading (the last valid AIS heading, else its segment's median COG at speed), which says nothing about how it actually lies at its berth.
- Alongside is decided at the first step only; a start beside a vessel that is more than the margin off but drifts into the learner a moment later is a collision.
- Traffic does not react to the learner: it is a replay.
