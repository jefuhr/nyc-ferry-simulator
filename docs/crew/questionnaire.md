# Crew questionnaire: the numbers public data cannot give us

About 30 minutes with a captain or engineer, in person or on a ride-along. Items marked ★ are the ones to get if time is short; the rest are worth asking when the conversation allows. Rough figures are fine. "About 650" beats no answer, and every answer can carry a tolerance or a range.

Every item says what it informs (the exact `vessels/*.json` parameter or behaviour), why it matters for the simulator, and what form the answer takes. Structured answers go into the files described in [answers-template/README.md](answers-template/README.md); the id in brackets is the `question_id` for `direct_answers.csv`. Text answers go into `control_layout.md` and `landing_procedures.md` in the same folder; they are reproduced in the patch report, so they pass the same no-names check as every note.

Public AIS could not calibrate the handling model: reports come every 66 to 69 seconds and carry no rpm, lever or rudder state. Crew figures are now the main path to realism.

## Before you ask anything

Follow [the reference capture kit](../reference-capture-kit.md). In short:

- **Operator first.** Get the operator's OK before interviewing anyone and before any photo, video or ride-along. Record it in `session.json` as `consent.operatorApproved: true` with `consent.approvedByRole` (a role from the list in the answer format, never a name) and `consent.approvalDate`. The importer refuses the answers until all three are there.
- **The respondent agrees** to their answers being used, anonymised, in a public repository: `consent.respondentAgreed: true`.
- **No names.** The respondent is a role from a fixed list (`captain`, `mate`, `deckhand`, `chief-engineer`, `engineer`, `port-captain`, `training-captain`, `other-crew`) plus an anonymous code (`A`, `B2`); reports show them as "Captain A". The importer accepts only those roles, refuses obvious contact details and title-plus-name patterns ("Captain Smith") in every note and text answer, and holds back capitalised phrases it cannot classify until a person reviews them. It cannot recognise every name, so the interviewer keeps names out of every answer, note and photo caption.
- **No security-sensitive information.** Nothing about vessel or facility security plans, access, codes or procedures. Nothing from internal documents unless the operator says it may be shared.
- **No personal data.** No faces, no screens with personal data, no crew schedules.
- **Numbers and summaries only** go into the repo. Raw photos, video and notes stay outside it (for example `~/FerrySim-reference/`), referenced by file name.
- **Record the conditions** next to every number: load (light, half, full), wind in knots, current (slack, flood, ebb) and water depth (deep or shallow), and whether a speed is through the water or over the ground. The resistance fit uses only rows it can model (calm, deep water, one load) and lists the rest as excluded, so a number without its conditions is a number that cannot be used.
- Say which boat: class, variant (River T, deep-draft Rockaway T, K) and hull number only if the operator is happy to share it.

Do not ask anyone to perform manoeuvres for this questionnaire. Numbers from memory and from normal operations are what we want.

## V. Which boat (3 minutes)

**V1 ★ Which class and variant is this about?** River-class T boat, deep-draft Rockaway T boat, or K boat; and did the answers come from one boat or the class in general?
Answer: text in `session.json` (`vesselClass`, `variant`). Informs: which parameter file the answers belong to. Why: the three types have different engines, propellers and drafts.

**V2 Hull number, only if the operator is happy to share it.**
Answer: text in `session.json` (`hull`). Informs: session metadata. Why: lets later observations of the same boat be joined up.

**V3 ★ Roughly how much does the boat weigh in service?** Lightship or loaded, in tonnes, from the stability book or load line if known.
Answer: number, t or kg [V3]. Informs: `particulars.displacement`. Why: mass sets acceleration, stopping distance and how hard the boat is to turn. It is currently an estimate from hull proportions.

**V4 ★ What is the deepest draft, including propellers?**
Answer: number, ft or m [V4]. Informs: `particulars.maxDraft`. Why: depth under keel and grounding warnings.

**V5 Do sister boats differ in ways you notice?** Builder, engines, controls, handling.
Answer: text [V5]. Informs: whether one parameter set covers the class. Why: the model has one T and one K file.

**V6 Is there a shareable source for this boat's particulars?** A public spec sheet, builder page or operator-approved document.
Answer: text [V6]. Informs: provenance of published values. Why: a readable public source beats an interview figure.

**V7 Has the boat been repowered or converted, and is there a public record?**
Answer: text [V7]. Informs: which machinery configuration the parameters describe. Why: K-class repowers are reported publicly; the engine and gear values must match the as-built state.

## C. Controls and control heads (6 minutes)

**C1 ★ What control heads are fitted?** Make and model; one lever per engine? Photo of each head with permission.
Answer: text in `control_layout.md` [C1]. Informs: the control model's shift logic. Why: the model assumes ZF-style electronic controls with hydraulic clutches.

**C2 Which stations are there, and how does control transfer?** Main helm, wing or docking stations; station-select buttons; what the levers must match.
Answer: text [C2]. Informs: station transfer, not modelled yet. Why: decides whether the simulator needs a second station.

**C3 ★ Where are the ahead and astern idle detents?** How far from neutral, as a percentage of the lever's travel to full.
Answer: number, % or fraction [C3]. Informs: `controls.clutchInLever`. Why: where the clutch engages on the lever; currently assumed at 12%.

**C4 Where does the rpm start to rise past the detent?** As a percentage of travel.
Answer: number, % [C4]. Informs: `controls.throttleStartLever`. Why: the size of the idle-in-gear band; assumed at 20%.

**C5 Is there a warm-up mode (throttle with the gear in neutral)?**
Answer: yes, no or unknown [C5]. Informs: control feature list. Why: lets instructors rev an engine in neutral in the simulator.

**C6 Single-lever or sync mode: is it there, and is it used?**
Answer: text [C6]. Informs: control feature list.

**C7 Is there a docking joystick?** If so, when is it used?
Answer: text [C7]. Informs: control feature list. Why: a joystick needs its own control model.

**C8 After the clutch goes in, is there a pause before the rpm follows the lever?**
Answer: number, s [C8]. Informs: `controls.throttlePause`. Why: the delay between the gear biting and the boat pulling; assumed 0.5 s.

**C9 Is there an rpm the engine must drop below before a shift is allowed?**
Answer: number, rpm [C9]. Informs: `controls.shiftInterlockSpeed`. Why: the interlock that makes a quick reversal wait; assumed 900 rpm.

**C10 ★ From neutral, how long after the lever reaches the detent does the clutch bite?** Ahead and astern separately if they differ, and the engine rpm it started from.
Answer: rows in `shift_timing.csv` (from `neutral` to `ahead` or `astern`, `from_rpm`, seconds, `lever_technique`). Informs: `transmission.clutchFillTime`. Why: the lag every docking move is built around; assumed 0.4 s.

**C11 ★ From ahead to astern (or back), how long until the opposite clutch bites?** Two different things can fill that time: the system's own delay (it holds in neutral, or waits for the rpm to drop below the interlock) and the captain's deliberate pause in neutral. Ask which it was, and what rpm the reversal started from.
Answer: rows in `shift_timing.csv` (from `ahead` to `astern`, `from_rpm`, seconds, `lever_technique` = `straight-through` or `paused-in-neutral`). Informs: `controls.neutralDwell`, derived as the reversal time less the clutch release and fill times, but only from a straight-through reversal that started at or below the shift interlock rpm (C9). A reversal with a pause, or from higher rpm, is reported as a calibration target instead: its time includes the captain's technique or the interlock wait, which are not model parameters. Why: the model makes a reversal take at least 2.6 s; a faster or slower real system changes every stop.

**C12 How do you move the lever when reversing?** Straight through, or pause in neutral?
Answer: `lever_technique` in `shift_timing.csv`: `straight-through`, `paused-in-neutral` or `unknown` [C12]. Informs: whether a C11 timing measures the system or the captain, and how instructors describe the shift.

## S. Steering (3 minutes)

**S1 ★ Is the helm follow-up or non-follow-up?** Follow-up: the wheel position sets the rudder angle. Non-follow-up: a jog lever moves the rudder while you hold it. Wheel, jog lever, tiller, or several?
Answer: `follow-up` or `non-follow-up` [S1], details in `control_layout.md`. Informs: `controls.steeringMode`. Why: the single biggest open question about how steering feels; the model ships as follow-up.

**S2 ★ What rudder angle do you get at hard over?**
Answer: number, deg [S2]. Informs: `controls.hardOverAngle`. Why: turning circle size; assumed 35°.

**S3 ★ How many seconds from hard over to hard over?**
Answer: number, s [S3]. Informs: `controls.steeringRate` (derived: twice the hard-over angle divided by the time). Why: how quickly the boat answers the helm; assumed about 10 s.

**S4 Does the rudder angle indicator show one rudder or both?**
Answer: text [S4]. Informs: instrument rendering.

**S5 Is there an autopilot, and when is it used?**
Answer: text [S5]. Informs: feature list.

**S6 Are interceptors or trim tabs fitted, and how are they used?**
Answer: text [S6]. Informs: whether a trim device needs modelling. Why: the designer lists interceptors as an option; nothing says which hulls have them.

## E. Engine and speed table (6 minutes)

**E1 ★ What rpm do the engines idle at with the clutch in?**
Answer: number, rpm [E1]. Informs: `engines.idleSpeed`. Why: with the current estimate of 650 rpm the model makes about 8 kn at idle in gear, which is the first thing to check.

**E2 ★ Rpm and boat speed, both engines ahead, calm water.** Idle in gear, a normal manoeuvring setting, the usual cruising setting, and full. For each: rpm on both engines, boat speed, whether that speed is through the water (log) or over the ground (GPS), and the load, wind, current and water depth it was seen in.
Answer: rows in `rpm_speed.csv` (`gear` = `ahead`) with `load`, `wind_kn`, `current` and `depth`; empty condition cells inherit the session's. Only calm (under 10 kn), deep-water rows at one load are fitted; the rest are listed as excluded. Informs: `hull.residuaryResistanceScale`, fitted by `fit-resistance`; the result also checks `propellers.pitchRatio` and the torque curve. Why: this is the speed-power calibration AIS could not give. The model assumes 25 kn at about 2000 rpm and 26 kn at full power.

**E3 How long from idle to full rpm in gear?** With the lever pushed straight to full.
Answer: number, s [E3]. Informs: `engines.turbochargerTimeConstant` (compared, not patched). Why: turbo lag sets how the boat picks up.

**E4 How fast does the boat go astern at a given rpm?**
Answer: rows in `rpm_speed.csv` with `gear` = `astern`. Informs: `hull.asternResistanceFactor` (compared, not patched). Why: the astern resistance is an estimate of 2.5 times ahead.

**E5 What is the gear ratio?** From the gearbox plate if known.
Answer: number, ratio [E5]. Informs: `machinery.gearRatio`. Why: published for the T-class (2.519); estimated for the K-class.

**E6 What propeller diameter is fitted?**
Answer: number, in or m [E6]. Informs: `machinery.propellerDiameter`. Why: published for the T-class (38 in); assumed 42 in for the K-class.

**E7 What propeller pitch?** From the propeller or the maker's drawing if known.
Answer: number, in or m [E7]. Informs: `propellers.pitchRatio` (derived: pitch over diameter). Why: the model picks a pitch ratio that absorbs full power at the assumed top speed; the real pitch pins it.

**E8 ★ Seen from astern going ahead, do the propellers turn outward or inward?**
Answer: `outward-turning` or `inward-turning` [E8]. Informs: `propellers.rotation`. Why: decides which way the stern walks when backing.

**E9 Blade area ratio, if known.** From the propeller drawing.
Answer: number, fraction or % [E9]. Informs: `propellers.expandedAreaRatio`. Why: shapes the open-water propeller curves; assumed 0.8 (the model accepts 0.3 to 1.05).

**E10 How far apart are the two propeller shafts?** Centre to centre.
Answer: number, m or ft [E10]. Informs: `hull.demihullSeparation`. Why: sets the twisting moment from split engines; estimated at 5.6 m for the T-class.

**E11 How many blades do the propellers have?**
Answer: number, count [E11]. Informs: `machinery.propellerBlades`. Why: published as 5 for the T-class; the open-water curves depend on it.

**E12 What gearbox is fitted?** Make and model from the gearbox plate.
Answer: text [E12]. Informs: `machinery.gearboxModel`. Why: ties the ratio (E5) and the clutch timings (C10, C11) to a real unit; ZF 2050 is published for the T-class.

## A. Acceleration and stopping (4 minutes)

**A1 ★ From a standstill, how long to reach cruising speed?**
Answer: number, s [A1]. Informs: `particulars.displacement` and `engines.turbochargerTimeConstant` (compared with an `acceleration` run, not patched). Why: the model does it in about 24 s for the T-class, which looks quick.

**A2 ★ From cruising speed, how do you normally slow to manoeuvring speed, and how far and how long does it take?** Levers to idle, astern idle, or something else; distance in metres or boat lengths; where you start.
Answer: row in `stopping.csv` with `method` = `normal`, plus the technique in the note. Informs: `hull.asternResistanceFactor`, `propellers.reversedFlowLiftFactor` (compared, not patched). Why: the everyday approach profile every landing scenario starts from.

**A3 ★ Crash stop: what happens at full astern from cruising speed?** Distance, time, and which way the bow goes.
Answer: row in `stopping.csv` with `method` = `full-astern`, `bow_swing` port or starboard. Informs: the same astern parameters and `propellers.propWalkAstern`. Why: the model stops in about 3.7 boat lengths and 16 s; the bow swing checks the prop walk sign.

**A4 Coasting in neutral: how far does the boat carry from cruising speed?**
Answer: row in `stopping.csv` with `method` = `neutral`. Informs: `hull.residuaryResistanceScale` at low speed (compared, not patched).

**A5 In a crash stop, which way does the bow swing?**
Answer: `bow_swing` column in `stopping.csv` [A5]. Informs: `propellers.propWalkAstern` sign.

**A6 Leaving a landing, do you push the levers straight to full, or ease them up?**
Answer: `at-once`, `ramped` or `depends` [A6]. Informs: how departures are scripted in scenarios. Why: explains why AIS departures look slower than the model's full-ahead run.

**A7 Do you know of trials data that could be shared?** Builder sea trials, acceptance trials, speed curves.
Answer: text [A7]. Informs: replaces every estimate above with measurements.

## T. Turning and twisting (4 minutes)

**T1 ★ Hard over at cruising speed: roughly how big is the circle, and how long to turn 180°?**
Answer: row in `turning.csv` (`helm` = `hard-over`, diameter in metres or time to 180°). Informs: `hull.derivativeNr`, `hull.derivativeYv`, `rudders.area` (compared with a `turning-circle` run, not patched). Why: the model's tactical diameter is about 5.8 boat lengths and 25 s to 180°, with no measured check.

**T2 Same at manoeuvring speed.**
Answer: row in `turning.csv` at the lower speed. Informs: the same derivatives at low speed.

**T3 How much speed do you lose in a hard turn?**
Answer: number, % [T3], or `speed_in_turn_kn` in `turning.csv`. Informs: `hull.derivativeYv` (compared, not patched). Why: the model loses about 29%.

**T4 ★ Twisting at rest with split engines: what rpm do you use, and how fast does the boat pivot?** Time to 180° or degrees per second, and whether it creeps ahead or sideways.
Answer: row in `twisting.csv`. Informs: `propellers.reversedFlowLiftFactor`, `propellers.asternThrustDeduction`, `hull.crossFlowDragCoefficient` (compared with a `pivot` run, not patched). Why: the model pivots at about 2.4°/s at idle split and creeps ahead at 1.2 m/s; both are sensitive estimates.

**T5 ★ Backing on one engine, does the stern walk, and which way?**
Answer: text [T5]. Informs: `propellers.propWalkAstern` and `propellers.rotation`. Why: checks the prop walk sign and size.

**T6 Do you hold the power constant through a turn?**
Answer: `yes`, `no` or `depends` [T6]. Informs: how turning answers are compared with the model.

## L. Landings, per landing type (5 minutes)

Ask once for each kind of landing used: floating barge, fixed pier, and any others. Text goes in `landing_procedures.md`; approach speed and angle can also go in `direct_answers.csv`.

**L1 ★ Which part of the boat touches first, and do you land bow-in or side-to?**
Answer: text [L1]. Informs: contact geometry and fender placement. Why: the contact model needs to know which hull surface meets which landing surface.

**L2 ★ Approach speed.**
Answer: number, kn [L2]. Informs: landing scenarios.

**L3 ★ Where do you start slowing?** A distance or a landmark.
Answer: text [L3]. Informs: landing scenarios.

**L4 Final angle of approach.**
Answer: number, deg [L4]. Informs: landing scenarios.

**L5 ★ How do you hold the boat on the landing?** One engine ahead with rudder, both engines, or lines.
Answer: text [L5]. Informs: holding thrust and the landing scenario script.

**L6 Which lines are used and when?**
Answer: text [L6]. Informs: landing scenario script.

**L7 What does the deckhand do, and what signals are used?**
Answer: text [L7]. Informs: landing scenario script.

**L8 ★ How does the approach change on the ebb versus the flood at each landing?**
Answer: text [L8]. Informs: current model checks and scenario design.

**L9 Which landings are tricky, and why?**
Answer: text [L9]. Informs: scenario priorities.

**L10 What fendering is on the boat and on the landing face, and where is the boarding connection?** Photos with permission.
Answer: text [L10]. Informs: contact surface stiffness and damping; where the boarding connection sits relative to the contact.

**L11 Are there shareable landing drawings or dimensions?**
Answer: text [L11]. Informs: landing contact geometry.

## W. Environment rules of thumb (3 minutes)

**W1 ★ What wind do you stop operating in, or change the approach for?**
Answer: number, kn [W1]. Informs: windage check scenarios. Why: the wind coefficients are textbook estimates; a limit gives a scenario to test them against.

**W2 Where does the current set you on each route?** Eddies near piers, places that behave differently on the ebb.
Answer: text [W2]. Informs: current model checks.

**W3 Where does the real current differ from the NOAA prediction?** At the slip versus the channel.
Answer: text [W3]. Informs: current model checks. Why: the AIS calibration found the nearest NOAA station a weak proxy for the slip.

**W4 Weather or sea limits per route.** Especially the Rockaway run.
Answer: text [W4]. Informs: scenario limits.

**W5 Radio and VTS practice.** Channels monitored, standard calls, security calls. Nothing security-sensitive.
Answer: text [W5]. Informs: scenario scripting only.

**W6 Normal tracks, turning points and slow zones per route.**
Answer: text [W6]. Informs: route scenarios.

## After the interview

1. Put the answers in a copy of [answers-template](answers-template/) named for the session.
2. Run `validate`, then `patch` and `fit-resistance` (see [README.md](README.md)).
3. Review the proposed patch and the fit report, then apply what you accept.

## Coverage: where each existing crew question lands

"Covered" means the questionnaire asks the question and the answer format has a field for the answer. "Partial" means only part of the source question is captured, and the rest is named so it is not mistaken for covered. "Deferred" means it is not asked yet. This is an interview from memory: it never produces time-stamped records or measured trials, and it never asks anyone to perform a manoeuvre.

| Source | Question | Questionnaire item(s) | Coverage |
|---|---|---|---|
| Reference kit, Priority 1 | Control head make, model, photos | C1 (`control_layout.md`) | Covered |
| Reference kit, Priority 1 | Detents: neutral, ahead idle, astern idle positions | C3 | Partial: one detent position for ahead and astern; asymmetric detents go in the note |
| Reference kit, Priority 1 | Lever travel from idle to full | C4 | Covered |
| Reference kit, Priority 1 | Warm-up mode | C5 | Covered |
| Reference kit, Priority 1 | Delay between lever into gear and clutch engaging | C10 | Covered |
| Reference kit, Priority 1 | Ahead to astern: hold in neutral or wait for rpm; how many seconds | C11, C12, C9 | Covered |
| Reference kit, Priority 1 | Single-lever or sync mode | C6 | Covered |
| Reference kit, Priority 1 | Other stations and control transfer | C2 | Covered as text |
| Reference kit, Priority 1 | Docking joystick | C7 | Covered |
| Reference kit, Priority 1 | Wheel, jog lever or tiller | S1 | Covered |
| Reference kit, Priority 1 | Hard over to hard over: degrees and seconds | S2, S3 | Covered |
| Reference kit, Priority 1 | Rudder angle indicator | S4 | Covered |
| Reference kit, Priority 1 | Autopilot | S5 | Covered |
| Reference kit, Priority 1 | Rpm at idle, manoeuvring, service and full | E1, E2 | Covered |
| Reference kit, Priority 1 | Boat speed at each rpm | E2 | Covered |
| Reference kit, Priority 1 | Time to service speed from a standstill | A1 | Covered (an estimate from memory) |
| Reference kit, Priority 1 | Slowing from service speed: distance, time, method | A2 | Covered |
| Reference kit, Priority 1 | Crash stop: distance, time, bow swing | A3, A5 | Covered |
| Reference kit, Priority 1 | Turning circle size and heading rate | T1 | Covered |
| Reference kit, Priority 1 | Twisting with split engines: rpm and pivot rate | T4 | Covered |
| Reference kit, Priority 1 | Prop rotation and walk when backing on one engine | E8, T5 | Covered |
| Reference kit, Priority 1 | Landings: side or end, bow-in or side-to | L1 | Covered |
| Reference kit, Priority 1 | Approach speed, where slowing starts, final angle | L2, L3, L4 | Covered |
| Reference kit, Priority 1 | Holding, lines, deckhand role and signals | L5, L6, L7 | Covered |
| Reference kit, Priority 1 | Ebb versus flood; tricky landings | L8, L9 | Covered |
| Reference kit, Priority 1 | Fenders on boat and landing | L10 | Covered |
| Reference kit, Priority 2 | Normal tracks, turning points, slow zones | W6 | Covered |
| Reference kit, Priority 2 | Local current knowledge | W2 | Covered |
| Reference kit, Priority 2 | Radio | W5 | Covered |
| Reference kit, Priority 2 | Weather limits | W1, W4 | Covered |
| Reference kit, Priority 3 | Photos, video and sound | Capture items, not questions; log file references in `control_layout.md` | Deferred to the capture kit |
| Reference kit, Priority 3 | Instruments: make, model and what is normally displayed | Not asked | Partial: C1 covers the control heads only; instrument make, model and display stay capture items |
| Reference kit, Priority 4 | Hull number, variant, sister-boat differences | V1, V2, V5 | Covered |
| Reference kit, Feel review | Scores after driving the simulator | Not in this questionnaire | Deferred: needs a drivable simulator first |
| Dossier, crew-questions | Vessel, hull, date, machinery configuration | V1, V2, V7, `session.json` | Covered |
| Dossier, crew-questions | Shareable source for class, dimensions, max draft, propulsion | V6, V4 | Covered |
| Dossier, crew-questions | Repower or cooling conversion record | V7 | Covered |
| Dossier, crew-questions | Engine-control make, detents, interlock, station transfer | C1, C3, C9, C2 | Covered |
| Dossier, crew-questions | Gearbox model and ratio; propeller diameter, pitch, blade area, blade count, rotation | E12, E5, E6, E7, E9, E11, E8 | Covered |
| Dossier, crew-questions | Follow-up or non-follow-up; rudder angles and slew rates | S1, S2, S3 | Covered |
| Dossier, crew-questions | Interceptors installed and modes | S6 | Covered |
| Dossier, crew-questions | Public trial curves | A7 | Covered |
| Dossier, crew-questions | Record load, trim, wind, current, depth, speed, rpm, controls, timing | `session.json` conditions; `load`, `wind_kn`, `current`, `depth` per `rpm_speed.csv` row; `from_rpm` and `lever_technique` in `shift_timing.csv` | Partial: trim and heading have no structured column (put them in `note`); the other tables carry conditions at session level only |
| Dossier, crew-questions | Distinguish speed through water, over ground and heading | `speed_reference` column in `rpm_speed.csv`; E2 | Partial: heading is not captured |
| Dossier, crew-questions | Evidence for displacement or inertia | V3 | Partial: displacement only; inertia stays estimated from the mass |
| Dossier, crew-questions | Which hull surface contacts which berth surface; boarding connection and fenders | L1, L10 | Covered |
| Dossier, crew-questions | Public drawings of berth geometry and fenders | L11 | Covered |
| Dossier, crew-questions | Non-sensitive approach, holding and line practice | L2 to L7 | Covered |
| Dossier, crew-questions | Observations with date, landing, tide, wind, configuration, uncertainty | `session.json`, tolerance columns | Covered |
| Dossier, top 10 unknowns | 1 Control heads, detents, idle settings, shift delays | C1, C3, E1, C10, C11 | Covered |
| Dossier, top 10 unknowns | 2 Displacement, centres of gravity, yaw inertia | V3 | Partial: displacement only; centres of gravity and inertia are not askable |
| Dossier, top 10 unknowns | 3 Propeller pitch, blade area, handedness, K-class diameter | E7, E9, E8, E6 | Covered |
| Dossier, top 10 unknowns | 4 Gearbox and reduction ratio | E12, E5 | Covered |
| Dossier, top 10 unknowns | 5 Rudder geometry, limits, slew rate, helm mapping | S1, S2, S3 | Partial: mode, limit and rate only; rudder geometry needs a drawing, asked for like L11 |
| Dossier, top 10 unknowns | 6 Measured turning, stopping, acceleration | T1, T2, A1, A2, A3, A4 | Partial: estimates from memory, not measured trials |
| Dossier, top 10 unknowns | 7 Shaft spacing, ahead and astern thrust, split-engine yaw | E10, T4, T5, E4 | Covered |
| Dossier, top 10 unknowns | 8 Interceptors and modes | S6 | Covered |
| Dossier, top 10 unknowns | 9 Landing contact geometry, fenders, holding thrust, lines | L1 to L11 | Covered |
| Dossier, top 10 unknowns | 10 Maximum draft, underwater offsets, post-repower configuration | V4, V7 | Partial: underwater offsets are not asked; they need drawings |
| Calibration report | 1 Identity and configuration, load, depth, wind, current; AIS names and dimensions | V1, V2, V5, `session.json` conditions | Covered |
| Calibration report | 2 Rpm, levers, clutch state and water speed at idle ahead and several settings | E1, E2 | Partial: engine rpm and speed from memory; lever position, clutch state and reciprocal-direction runs are not captured |
| Calibration report | 3 Acceleration capture (synchronised 1 Hz position, heading, levers, rpm); full power at once or ramped | A1, A6 | Partial: a memory estimate and the ramping choice, not synchronised records |
| Calibration report | 4 Rudder order and angle in a turn; follow-up; hard-over angle and rate; constant power | T1, T6, S1, S2, S3 | Covered |
| Calibration report | 5 Approach profiles, coast-down and crash stop; gear changes, dwell, interlocks, astern limits | A2, A3, A4, C10, C11, C9, E4 | Covered |
| Calibration report | 6 Current near the boat versus the NOAA station | W3 | Partial: where the current differs is asked; no current measurement or reciprocal-run estimate is captured |
