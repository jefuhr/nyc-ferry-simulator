# Answer format

Copy this folder to a session folder outside `docs/` (for example `~/FerrySim-reference/answers/2026-11-03-captain-a/`) or, once the operator has approved publication, under `docs/crew/sessions/<sessionId>/`. Fill in `session.json`, add rows to the CSV tables, and write the text answers into the two Markdown files. Leave a table's header row in place when it has no rows.

The importer (`tools/crew_intake`) reads these files with the Python standard library only. Units are fixed per column or per question, so write plain numbers: `12.5`, not `12.5 kn`. Empty cells are allowed where the column says optional. The untouched template does **not** validate: its identity fields are placeholders in angle brackets and its consent block says no agreement was recorded. `validate` lists exactly what is left to fill in.

## What the importer enforces

- **Consent in substance.** `consent.respondentAgreed` and `consent.operatorApproved` must both be literally `true`, `consent.approvedByRole` must be one of the approving roles below, and `consent.approvalDate` must be a date. Nothing is proposed, fitted or applied otherwise.
- **Roles, not names.** `respondentRole` is one of a fixed list and `respondentCode` is a short anonymous code. The session id is derived from them. No free-text identity field exists.
- **A publication boundary on free text.** Every note, `walk` cell, text answer, session text field and Markdown line is checked before it can appear in a patch or report. An email address, phone number, social-media handle or a title followed by a name (`Captain Smith`, `Mr. Jones`) is refused outright. A capitalised phrase the importer cannot classify (it could be a person, a place or a product) is held back until a person records it in `textReview.acceptedPhrases`. Ordinary marine and equipment terms (`ZF 2050`, `Michigan Wheel`, `East River`, `Captain A`) pass. The check is a net, not a guarantee: it cannot recognise every name, so keep names out of every answer.
- **Strict CSV.** Duplicate, unknown or missing columns, rows with the wrong number of cells, and broken quoting are errors that name the file and line. A quoted cell may span lines; a quote inside a plain cell (`38" prop`) is kept as written.
- **Typed JSON.** Every required `session.json` field must be present, non-null and of the right type; unknown keys are errors so a typo cannot hide a required field.

## session.json

| Field | Required | Content |
|---|---|---|
| `sessionId` | yes | Derived: `<date>-<respondentRole>-<respondentCode in lower case>`, for example `2026-11-03-captain-a`. Synthetic examples prefix `synthetic-`. Becomes the source key `crew-<sessionId>` in the vessel file. |
| `synthetic` | no | `true` only for made-up examples. The patch then says "Synthetic ... not real data". |
| `date` | yes | `YYYY-MM-DD` |
| `respondentRole` | yes | One of `captain`, `mate`, `deckhand`, `chief-engineer`, `engineer`, `port-captain`, `training-captain`, `other-crew` |
| `respondentCode` | yes | One capital letter, optionally followed by up to two capitals or digits: `A`, `B2`. Never initials. The label in reports and sources is built from role and code (`Captain A`, `Chief Engineer B`). |
| `respondentExperience` | no | Free text, if they want to say (passes the publication boundary) |
| `vesselClass` | yes | `t-class` or `k-class`: the `id` of the `vessels/*.json` the answers describe |
| `variant` | no | `river`, `rockaway-deep-draft`, `k-class` or `unknown` |
| `hull` | no | Hull number, only if the operator is happy to share it |
| `method` | no | `interview` (default), `ride-along` or `both` |
| `conditions.load` | yes | `light`, `half`, `full` or `unknown`: the default for rows that leave their own cell empty |
| `conditions.windKn` | no | Number or `null`: the default wind for `rpm_speed.csv` rows with an empty `wind_kn` |
| `conditions.current` | yes | `slack`, `flood`, `ebb` or `unknown`: the default for rows with an empty `current` |
| `conditions.currentKn` | no | Number or `null` |
| `conditions.depth` | yes | `deep`, `shallow` or `unknown`: the default for rows with an empty `depth` |
| `conditions.windFrom`, `tide`, `notes` | no | Free text |
| `consent.respondentAgreed` | yes | Must be `true`: the respondent agrees to their anonymised answers being used in a public repository |
| `consent.operatorApproved` | yes | Must be `true` |
| `consent.approvedByRole` | yes | Who gave the operator's OK, by role: `operations-manager`, `port-captain`, `marine-superintendent`, `safety-manager`, `training-manager`, `fleet-manager`, `general-manager` or `operator-representative`. Never a name. |
| `consent.approvalDate` | yes | `YYYY-MM-DD`; a date after the session is accepted with a warning |
| `consent.notes` | no | Free text |
| `textReview` | when needed | `{"reviewedByRole": <an approving role>, "date": "YYYY-MM-DD", "acceptedPhrases": ["Example Controls"]}`. Lists the capitalised phrases a person checked and confirmed are not people. Required only when `validate` asks for it. |
| `photos` | no | Where the raw material lives, outside the repo |
| `notes` | no | Anything else about the session |

## rpm_speed.csv (questionnaire E2, E4)

| Column | Content |
|---|---|
| `port_rpm`, `starboard_rpm` | Engine rpm, 300 to 2500. Rows whose engines differ by more than 5% are kept but never fitted. |
| `gear` | `ahead` or `astern`. Astern rows are reported as targets for `hull.asternResistanceFactor`, never fitted. |
| `speed_kn` | Boat speed, knots |
| `speed_tolerance_kn` | Optional, default 0.5. "About 20" is `20` with tolerance `1`. The fit must match every row within its own tolerance. |
| `speed_reference` | `through-water` (log) or `over-ground` (GPS). Over-ground rows are fitted only at `slack` current. |
| `load` | `light`, `half`, `full` or `unknown`. The fit uses one load at a time (`--load` selects it when rows mix). |
| `wind_kn` | Optional; empty inherits `conditions.windKn`. Rows above 10 kn, or with no wind recorded, are excluded from the fit (its forward model is calm water). |
| `current` | Optional; empty inherits `conditions.current` |
| `depth` | Optional; empty inherits `conditions.depth`. Only `deep` rows are fitted. |
| `note` | Free text |

`fit-resistance` refuses to mix loads or to use windy, shallow or unknown-condition rows unless `--allow-mixed-conditions` is passed, and its report lists every excluded row with the reason.

## shift_timing.csv (C10, C11, C12)

| Column | Content |
|---|---|
| `from_gear`, `to_gear` | `neutral`, `ahead` or `astern`; must differ |
| `from_rpm` | Optional: engine rpm when the lever started moving. Needed before a reversal can be derived. |
| `seconds` | From the lever reaching the detent (or leaving the old gear) until the clutch bites, 0 to 30 |
| `lever_technique` | `straight-through` (the lever moved without stopping), `paused-in-neutral` (the captain held it in neutral) or `unknown` |
| `note` | Free text |

Rows from `neutral` give `transmission.clutchFillTime`. A reversal between `ahead` and `astern` gives `controls.neutralDwell` (the reversal time less the clutch release and fill times) **only** when it was `straight-through` and started at or below the shift interlock rpm; a reversal with a pause, from higher rpm, or without `from_rpm` is reported as a calibration target because the time includes the captain's pause or the interlock wait. Timings that disagree by more than 0.5 s (fills) or 1 s (reversals) are refused rather than averaged.

## stopping.csv (A2, A3, A4, A5)

| Column | Content |
|---|---|
| `initial_speed_kn` | Speed when the stop starts |
| `method` | `normal` (the everyday slowdown), `neutral` (coast), `astern-idle`, `half-astern` or `full-astern` (crash stop) |
| `distance_m`, `distance_boat_lengths`, `time_s` | At least one; the rest optional |
| `bow_swing` | `port`, `starboard`, `none`, `unknown` or empty |
| `note` | Free text, including the technique for `normal` |

## turning.csv (T1, T2, T3)

| Column | Content |
|---|---|
| `speed_kn` | Speed entering the turn |
| `helm` | `hard-over`, `half` or `quarter` |
| `rudder_deg` | Optional, if the indicator was read |
| `diameter_m`, `time_to_180_s` | At least one |
| `speed_in_turn_kn` | Optional |
| `note` | Free text |

## twisting.csv (T4)

| Column | Content |
|---|---|
| `ahead_engine` | `port` or `starboard` (the other engine is astern) |
| `ahead_rpm`, `astern_rpm` | Engine rpm on each side |
| `yaw_rate_deg_s`, `time_to_180_s` | At least one |
| `walk` | Free text: does the boat creep ahead, astern or sideways while pivoting |
| `note` | Free text |

## direct_answers.csv

One row per questionnaire item answered with a single number or choice. Items that belong in a table (E2, E4, C10, C11, C12, A2 to A5, T1, T2, T4) are refused here with a pointer to the right file.

| Column | Content |
|---|---|
| `question_id` | The id in brackets in [the questionnaire](../questionnaire.md): `E1`, `S2`, ... |
| `value` | A number, or the choice for text items |
| `unit` | One the item accepts (see below), or `text` |
| `note` | Free text; it is kept in the parameter's note |

| Item | Parameter | Accepted units | Plausible range |
|---|---|---|---|
| V3 | `particulars.displacement` | `t`, `kg`, `lb` | 30 to 200 t |
| V4 | `particulars.maxDraft` | `ft`, `m`, `in` | 0.8 to 3 m |
| C3 | `controls.clutchInLever` | `%`, `fraction` | 2% to 50% of travel; must not exceed C4 |
| C4 | `controls.throttleStartLever` | `%`, `fraction` | 3% to 70%; must be at or above C3 |
| C8 | `controls.throttlePause` | `s` | 0 to 5 s |
| C9 | `controls.shiftInterlockSpeed` | `rpm` | 300 to 1800 rpm; must be above the idle rpm (E1) |
| S1 | `controls.steeringMode` | `text`: `follow-up`, `non-follow-up` | |
| S2 | `controls.hardOverAngle` | `deg` | 15° to 60° |
| S3 | `controls.steeringRate` (derived) | `s` hard over to hard over | 2 to 60 s |
| E1 | `engines.idleSpeed` | `rpm` | 400 to 1200 rpm |
| E5 | `machinery.gearRatio` | `ratio`, `1` | 1 to 4 |
| E6 | `machinery.propellerDiameter` | `in`, `m`, `mm` | 0.5 to 1.5 m |
| E7 | `propellers.pitchRatio` (derived from pitch and diameter) | `in`, `m`, `mm` | 0.4 to 2.5 m of pitch; the ratio must land in 0.5 to 1.4 |
| E8 | `propellers.rotation` | `text`: `outward-turning`, `inward-turning` | |
| E9 | `propellers.expandedAreaRatio` | `fraction`, `%` | 0.3 to 1.05 |
| E10 | `hull.demihullSeparation` | `m`, `ft`, `in` | 3 to 8 m |
| E11 | `machinery.propellerBlades` | `count` | 2 to 7 |
| E12 | `machinery.gearboxModel` | `text` | |
| E3, A1 | targets, reported only | `s` | |
| T3 | target, reported only | `%` | |
| L2, W1 | targets, reported only | `kn` | |
| L4 | target, reported only | `deg` | |
| any other id | text, reported only | `text` | |

The combined answers are checked as a whole before a patch is written: the core's cross-parameter rules (C4 at or above C3, C9 above E1, pitch ratio and area ratio inside the propeller model's bounds) and a short neutral-then-ahead run through the real `ferrysim` CLI. A set that fails is refused with the reason, even though every answer passed on its own.

## control_layout.md and landing_procedures.md

Free text with the questionnaire ids as headings. Reference photos by file name only; the files stay outside the repository (see [the reference capture kit](../../reference-capture-kit.md)). Both files are reproduced in full in the patch report, so they pass the publication boundary like every other free text.
