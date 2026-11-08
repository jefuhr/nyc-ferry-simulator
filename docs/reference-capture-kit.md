# Reference capture kit (for crew)

This simulator is only as realistic as its reference material. Public sources give hull sizes, engines and speeds, but nothing about how the boats actually handle or how the wheelhouse is laid out. This page lists what to capture and what to ask. It is ordered by how much each item changes the handling model, so the first sections matter most.

## Before you capture anything

**Permission:**
- Get the operator's OK first, for photos, video, and anything learned from internal procedures.
- Ask the captain before filming in the wheelhouse.
- Never film while it could distract anyone on watch.

**Never capture:**
- anything security-related: vessel or facility security plans, access codes, security procedures (Sensitive Security Information)
- passengers' faces
- screens showing personal data

**Storage:**
- Raw photos, video and internal documents stay outside this repo. Keep them in a private folder such as `~/FerrySim-reference/`. The repo ignores `/reference/`, but don't rely on that.
- Name files `YYYY-MM-DD_<vessel>_<topic>_<n>`, for example `2026-11-03_hull-H201_portcontrolhead_02.jpg`.
- Keep one short notes file per session: date, vessel, conditions and what you captured.
- Only notes the operator is comfortable sharing get written up into `docs/`, as summaries and numbers, not copies of internal documents.

## Intake: private media to measured layout data

1. **Discord intake:** The consented crew-media bot is an intake source, not a publishing step. Keep raw originals, contributor identities and the bot database private, outside this repository. Intake consent does not authorize publishing a drawing or using withdrawn material; check the current consent and withdrawal record before promoting a batch. Do not start the bot or backfill media as part of measurement work.
2. **Privacy screen:** An authorized local processing step removes faces, paperwork, personal/security screens and identifying metadata, and makes video silent. Review the result before release. A blurred or removed patch is missing evidence, never a feature to model. Keep raw and intermediate screening material inaccessible to measurement workers; never upload raw media to a cloud processor.
3. **Approved sheets:** Release only the screened subset into a private `sheets/` folder with a README describing provenance, vessel class, allowed use and remaining uncertainty. Workers read that README and the approved sheets only. Credit by role, for example “crew reference photos (private), 2026-11” or “builder general arrangement (private)”; contributor names, operator names and boat names do not enter layout data. Proprietary drawings are measurement sources, not tracing or reproduction assets.
4. **Numeric picks:** Use [tools/reference](../tools/reference/README.md) to catalog the approved set, then record image-relative points, calibration, source-relative path and confidence in a pick file. The catalog includes private filenames, so it stays outside every Git repository, including ignored artifact directories. Only reviewed numbers, neutral observations and source-relative drawing paths enter Git. Bind picks to image dimensions and SHA-256 so a replaced/resized sheet cannot silently reuse the calibration.
5. **Layout review:** Measure, compare against printed dimensions, validate, then export into `art/blender/data/`. Preserve higher-confidence existing values unless a reviewer explicitly chooses `--force`. Printed values, pixel measurements and estimates must remain distinguishable. Use the waterline/half-LOA coordinate frame from the layout README; do not silently change physics parameters when a drawing describes another vessel revision. Ask for the specific missing shot or dimension when geometry is unresolved.

These steps describe the intake contract, not evidence that a bot is currently running or that every future batch has passed screening. The measurement tool checks paths and provenance structure; it cannot verify consent, detect sensitive pixels or establish as-built accuracy. Retain a private mapping from approved sources to their consent records so withdrawal can identify affected picks and generated assets.

## Priority 1: how the boat is driven (these define the handling model)

### Engine control heads (one lever per engine?)
- Close-up photo of each control head, the maker and model label, and any station-select or sync buttons.
- **Detents:**
  - where neutral, ahead idle (clutch in) and astern idle are
  - how far the lever travels from idle to full
  - whether there is a warm-up mode (throttle in neutral)
- **Shifting:**
  - Is there a delay between moving the lever into gear and the clutch engaging?
  - Going ahead to astern, does the system hold in neutral, or wait for rpm to drop?
  - Roughly how many seconds does that take?
- **Single-lever or sync mode:** does it exist, and is it used?
- **Other stations:** are there wing stations or a docking station? Where are they, and how does control transfer between them?
- **Docking joystick:** is there one (for example ZF JMS or similar)? If so, how and when is it used?

### Steering
- Wheel, jog lever (non-follow-up), tiller, or several of these? Photo of each.
- Hard over to hard over: degrees each way and how many seconds it takes.
- Rudder angle indicator: photo, and whether it shows one rudder or both.
- Autopilot: whether it exists and when it's used.

### Numbers a captain knows by heart

Even rough figures are gold. Write down the conditions (load, wind, current) next to each.

**Engine speeds and boat speeds:**
- engine rpm at idle, at a normal maneuvering setting, at service speed and at full
- boat speed at each of those rpm, with both engines ahead in calm water

**Acceleration and slowing:**
- how long it takes to reach service speed from a standstill
- from service speed: how far and how long to slow to maneuvering speed, and how they normally do it
- crash stop: what happens at full astern from service speed (distance, time, which way the bow goes)

**Turning:**
- turning at service speed with full rudder: a rough circle size, and how quickly the heading changes
- twisting with split engines (one ahead, one astern) at rest: rpm used, and how fast it pivots
- **Prop walk:** do the props turn outward or inward, and does the boat walk sideways when backing on one engine?

### Landings
For each kind of landing (floating barge, fixed pier, any others):
- Which side or end of the boat touches? Bow-in or side-to?
- **Approach:**
  - approach speed
  - where the captain starts slowing (distance or a landmark)
  - the final angle of approach
- **Holding position:**
  - how the boat is held on the landing (for example one engine ahead with rudder, or both engines)
  - lines used and when
  - the deckhand's role and the signals used
- **Ebb vs flood:** how the approach changes with current at that landing, and which landings are known to be tricky and why.
- **Fenders:** photos of the boat's fendering, and of the landing's fenders and face.

## Priority 2: routes and the harbor

- Normal tracks for each route: where they cross the river, standard turning points, where they slow down (no-wake zones, under bridges, near piers).
- Local knowledge about current: where it sets you, eddies near piers, places that behave differently on the ebb.
- **Radio:**
  - channels monitored and used
  - calls made when departing, before bridges or in fog
  - security calls
  - talking with the Vessel Traffic Service
- **Weather limits:** wind or sea limits for any route or landing (especially the Rockaway run).

## Priority 3: what it looks and sounds like

- **Wheelhouse, from the helm seat at eye height:** one wide photo forward, one to each side and one aft.
- **Every panel straight-on with labels readable:**
  - overhead panel
  - nav lights, wipers and horn controls
  - searchlight
  - engine monitoring screens
- **Instruments:**
  - radar
  - chartplotter or ECS
  - AIS
  - depth sounder and compass
  - VHF
  For each: make and model, and what's normally displayed.
- **Exterior:**
  - the hull from the bow, the stern and each side
  - boarding gates
  - deck layout and the deckhand's working areas
- **Video, with permission:**
  - a lever being moved through neutral, ahead and astern (close-up, with engine sound)
  - an approach and landing from the helm position
  - engine sound at idle, maneuvering and service rpm, from inside the wheelhouse and from the deck

## Priority 4: which boat

- Hull number and name of each boat captured.
- Is it a River-class T boat, a deep-draft Rockaway T boat, or a K boat?
- Any known differences between sister boats (builder, engines, controls) that captains notice.

## Feel review (once the simulator is drivable)

After driving the simulator, a crew member answers these. Each one gets a score from 1 to 5 (5 = feels like the real boat) and a sentence on what's off:
- acceleration and slowing
- turning with the rudder
- twisting with split engines
- current effects
- landing approach and contact
- control and lever behavior
- engine sound and response
- views and sightlines from the helm

Add any specific number that disagrees with the real boat, for example "real boat does about 18 knots at that rpm".
