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

## Intake: the ten-stage private reference loop

1. **Request:** The coordinator opens a shot-list request in the private `requests.json` register beside the manifest. Request ids identify the subject and, when useful, its class or landing.
2. **Consent:** Before recording or submitting anything, obtain the required permission and accept the current processing policy. Consent to private processing is not permission to publish media. Owner and measurement-only sources retain their narrower scopes.
3. **Capture:** Collect the requested views without distracting anyone on watch. Include known dimensions and overlap as described below; do not capture people, private screens, paperwork or security details.
4. **Private intake:** Submit through the private crew-media bot with the request ids. Originals, contributor identities, messages and consent records stay private and outside Git.
5. **Coordinator screen:** The coordinator checks every source for privacy and permitted use. Remove sensitive regions and metadata, and silence video. Only a recorded `approved` or `approved-with-edits` decision releases a sheet. Edited regions are missing evidence.
6. **Approved sheets:** Place only the screened subset in the private sheets folder. Its `manifest.json` binds each sheet hash to source ids, current consent, withdrawal state, screening and `tags.requests`. Workers receive only these approved sheets. Credit by role, for example “crew reference photos (private)” or “builder general arrangement (private)”. Measurement-only drawings supply numbers, never traced or reproduced artwork.
7. **Gate and catalogue:** Use [tools/reference](../tools/reference/README.md) to validate the manifest and catalogue the set. Missing entries, changed hashes, stale processing consent, withdrawal and exclusion are refusals. The explicit legacy flag is only for reproducing old measurements and supplies no consent evidence. Catalogues and request registers remain outside every Git repository.
8. **Measure or reconstruct:** Record image-bound numeric picks. A plane homography supports an oblique panel only on its calibrated plane; retain the reprojection error. For approved reference footage, extract sharp frames, reconstruct privately at medium detail or higher, scale against known lengths, and render grey orthographic geometry views. Each output records original source ids in the manifest. A coordinator reviews generated views before workers use them; reconstruction does not approve itself.
9. **Review and use:** Compare independent dimensions, validate layout and survey values, and review the numeric diff before export. Use the documented coordinate frame and confidence. Mark answered requests `received`, `screened` and `used` when those steps have actually happened; use `needs-reshoot` for unusable evidence.
10. **Withdraw and repeat:** Refresh the private manifest when consent or screening changes. Run `audit` over all pick, layout and survey JSON files. Withdrawal of any contributing source affects its derived views and every cited value. The coordinator quarantines affected outputs and requests replacements; the audit lists findings and changes no data automatically.

The gate checks recorded evidence, hashes and policy versions. It does not detect sensitive pixels or establish physical accuracy. Keep the private source-to-consent mapping current; a stale manifest cannot reveal a later withdrawal. No raw media belongs in cloud processing or in this repository.

## How to shoot so it can become a model

- Use still, well-lit surfaces with visible texture, sharp focus and fixed zoom. Shiny, transparent, blank, moving or blurred surfaces reconstruct poorly. Never add markings or move equipment without permission.
- Walk slowly around the stationary subject. Keep roughly 70–80% overlap between successive views, vary height, and show corners from both sides. Avoid a pure pan from one position: reconstruction needs changing viewpoints. Fill the frame while keeping the whole subject connected across views.
- Include at least one long, accurately measured length between identifiable points. Prefer several independent lengths in different directions. Write down metres, endpoint descriptions and measurement uncertainty. An unscaled reconstruction is not a measuring ruler.
- For a panel or berthing face, measure four or more non-collinear points on the same flat plane. Capture all points in the same photo. Extra anchors let the fit expose mistakes; independent check lengths test accuracy. A homography cannot measure a lever sticking out of that plane or correct lens distortion.
- For exterior geometry, include the bow, stern, both sides, deck transitions and obscure corners. For sections, identify the longitudinal datum and station locations. Keep a known level or vertical reference when safely possible; reconstruction axes are arbitrary until aligned.
- Shoot close details and a wider context view so the coordinator can tell where each belongs. Record anything hidden, cropped, blurred or edited as unresolved. Do not invent the missing shape.

### Shot-list request ids

| Prefix | Subject |
|---|---|
| `EXT` | Exterior geometry |
| `WH` | Wheelhouse geometry |
| `SCR` | Screens, gauges, alarms and buttons |
| `MOT` | Controls in motion |
| `LND` | Landings |
| `SND` | Sound |
| `HND` | Handling answers |

Use two digits after the prefix, with an optional class or GTFS stop scope: `EXT-01@k-class`, `WH-01@t-class`, `LND-03@87`. Each request has `open`, `received`, `screened`, `used` or `needs-reshoot` status. The manifest's `tags.requests` lists the requests a sheet answers.

**PLACEHOLDER — coordinator-approved shot list pending.** The ids above illustrate the C9 format; numbered shot-list definitions must be supplied by the coordinator before this list is treated as an agreed capture brief. The priority notes below are background guidance.

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
