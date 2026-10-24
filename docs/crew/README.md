# Crew data intake

Public AIS could not calibrate the handling model: reports arrive every 66 to 69 seconds with no rpm, lever or rudder state. What a captain or engineer knows by heart is now the main path to realism. This folder makes it easy to ask, easy to record, and mechanical and safe to turn the answers into simulator parameters with `crew-reported` provenance.

| Piece | Where |
|---|---|
| The questionnaire (30 minutes, prioritised, plain language) | [questionnaire.md](questionnaire.md) |
| The answer format (CSV tables plus `session.json`) | [answers-template/](answers-template/README.md) |
| A complete synthetic answer set | [example-synthetic/](example-synthetic/) |
| The importer: validate, patch, fit, apply | [tools/crew_intake](../../tools/crew_intake/README.md) |
| Permissions, privacy and what to capture | [reference-capture-kit.md](../reference-capture-kit.md) |

## Rules

- **Consent is recorded in substance, not by copying a template.** `session.json` must say `consent.respondentAgreed: true` and `consent.operatorApproved: true`, name the operator's approving role (from a fixed list, never a person) in `consent.approvedByRole`, and give `consent.approvalDate`. The blank template fails validation until a person fills those in; nothing is proposed, fitted or applied before that.
- **Respondents are roles plus codes, never names.** `respondentRole` is one of `captain`, `mate`, `deckhand`, `chief-engineer`, `engineer`, `port-captain`, `training-captain`, `other-crew`; `respondentCode` is a short anonymous code (`A`, `B2`). Reports and the vessel file's `sources` entry show them as "Captain A". The session id is derived from date, role and code, so no identity field holds free text.
- **Free text crosses a publication boundary.** Every note, text answer, session text field and Markdown line is checked before it can appear in a patch or report. Email addresses, phone numbers, social-media handles and a title followed by a name ("Captain Smith") are refused; a capitalised phrase the importer cannot classify is held back until a person records it in `textReview.acceptedPhrases`. Ordinary marine and equipment terms pass. This is a net, not a guarantee: it cannot recognise every name, so the interviewer keeps names out of the answers.
- **Nothing security-sensitive or personal.** See the capture kit and the top of the questionnaire.
- **Proposed patches are proposals, bound to their inputs.** Each patch records the SHA-256 of the vessel file and of the answer set it was computed from; `apply` refuses it if the vessel file has changed since, so a derived steering rate or a fitted resistance scale never lands on a vessel it was not computed for. `vessels/*.json` never changes until you run `apply --approved` after reading the patch.
- **The combined answers must still make a boat.** Before a patch is written, and again before `apply` writes a file, the whole parameter set is checked against the simulator's cross-parameter rules (throttle start at or above the clutch-in detent, shift interlock above idle, pitch and area ratios inside the propeller model's bounds) and run through the real `ferrysim` CLI from neutral to the idle detent to full lever. Answers that pass one by one but fail together are refused with the reason.
- **Every proposed value keeps its history.** The old value, confidence and source stay in the new `note`, and the session becomes a `sources` entry such as `"crew-2026-11-03-captain-a": "Crew interview (anonymised), Captain A, 2026-11-03, T-class"`.
- **Synthetic examples carry `"synthetic": true`**, a session id starting `synthetic-`, and a source text that says so. Reports print a SYNTHETIC banner.

## Workflow

1. Copy [answers-template](answers-template/) to a session folder and fill it in during or after the interview.
2. Check it:
   ```
   uv run tools/crew_intake/intake.py validate <answers folder>
   ```
   Types, consent, roles, units, ranges, enumerations, CSV shape and the publication boundary are checked; errors name the file, line and field.
3. Propose the direct parameters (idle rpm, detents, hard-over angle, steering rate, shift timings, rotation, and so on):
   ```
   uv run tools/crew_intake/intake.py patch <answers folder> --vessel vessels/t-class.json
   ```
   Builds the ferrysim CLI if needed, checks the combined model with it, and writes `artifacts/crew-intake/<sessionId>/proposed-patch.json`, `patch-report.md` and `manifest.json`. A set that fails the model check gets the report and the manifest entry, but no patch file.
4. Fit the resistance scale to the rpm-speed table by running the real simulator:
   ```
   uv run tools/crew_intake/intake.py fit-resistance <answers folder> --vessel vessels/t-class.json [--with-patch artifacts/crew-intake/<sessionId>/proposed-patch.json] [--load half] [--allow-mixed-conditions]
   ```
   Writes `resistance-fit.json` (a patch) and `resistance-fit.md` (conditions, residuals, iterations, excluded rows, the simulator build used). Only rows the calm, deep-water forward model can represent are fitted: both engines ahead and balanced, through-water speed (or over-ground at slack), one known load, wind under 10 kn, deep water. Rows at mixed loads are refused unless `--load` picks one or `--allow-mixed-conditions` is passed; either way the report says what was used. The fit is refused, with the residuals still reported, when rows contradict each other within their own tolerances or when the fitted curve misses any row's stated tolerance. `--with-patch` fits on top of the direct patch, which matters when it changes the idle rpm; that fit then applies only after the direct patch.
5. Read both reports. Apply what you accept:
   ```
   uv run tools/crew_intake/intake.py apply --patch artifacts/crew-intake/<sessionId>/proposed-patch.json --vessel vessels/t-class.json --out vessels/t-class.json --approved
   ```
   `apply` refuses a patch whose base fingerprint no longer matches the vessel file, a patch whose folder manifest records a refused rerun, and a result that fails the model check. Without `--approved` it refuses to write under `vessels/`; use another `--out` to inspect the result first. The file is written in the repository's style, so the diff shows only the changed lines.
6. Behaviour answers that have no single parameter (stopping, turning, twisting, acceleration, paused or interlock-limited reversals) are listed in `patch-report.md` as calibration targets with the parameters they inform, and every accepted answer, row and text file appears in the report's inventory. Compare the targets with `ferrysim maneuver` runs by hand; the calibration knobs are listed in [the dynamics README](../../core/FerrySim.Core/Dynamics/README.md#parameters-and-calibration).

Each session folder under `artifacts/crew-intake/` holds one `manifest.json` with an entry per command (`patch`, `fit-resistance`): status, timestamp, the answer-set and vessel fingerprints, the outputs and the reasons for a refusal. A rerun deletes that command's earlier outputs before it starts, so a refused rerun can never leave an earlier success beside its refusal report.

## Worked example (synthetic)

Every number in this section is **synthetic**. [example-synthetic/](example-synthetic/) is a made-up answer set: its rpm-speed rows were generated by running the ferrysim CLI on a copy of `vessels/t-class.json` with `hull.residuaryResistanceScale` set to 0.85 (rounded to 10 rpm and 0.1 kn), and every other number was invented to exercise the importer. The commands below were run from the repository root on 2026-10-24.

```
$ uv run tools/crew_intake/intake.py validate docs/crew/example-synthetic
0 error(s), 0 warning(s) in docs/crew/example-synthetic

$ uv run tools/crew_intake/intake.py patch docs/crew/example-synthetic --vessel vessels/t-class.json
0 error(s), 0 warning(s) in docs/crew/example-synthetic
proposed 12 change(s), 21 calibration target(s), 0 refused, 1 warning(s); model check passed with the ferrysim CLI
wrote artifacts/crew-intake/synthetic-2026-01-15-captain-a/proposed-patch.json and artifacts/crew-intake/synthetic-2026-01-15-captain-a/patch-report.md (nothing under vessels/ was changed)

$ uv run tools/crew_intake/intake.py fit-resistance docs/crew/example-synthetic --vessel vessels/t-class.json
0 error(s), 0 warning(s) in docs/crew/example-synthetic
fitted hull.residuaryResistanceScale = 0.8531 (was 0.7), RMS residual 0.08 kn, 21 simulator runs
wrote artifacts/crew-intake/synthetic-2026-01-15-captain-a/resistance-fit.json and artifacts/crew-intake/synthetic-2026-01-15-captain-a/resistance-fit.md (nothing under vessels/ was changed)
```

The fit took about 8 seconds and recovered 0.853 against the 0.85 the synthetic speeds were generated with; the difference is the 0.1 kn rounding of the example rows.

`patch-report.md` (synthetic) proposes:

| Question | Parameter | Previous | Proposed | How |
|---|---|---|---|---|
| E1 | `engines.idleSpeed` | 68.0678 rad/s (650 rpm), estimated | 68.0678 rad/s (650 rpm) | direct |
| C3 | `controls.clutchInLever` | 0.12, estimated | 0.15 (15 %) | direct |
| C4 | `controls.throttleStartLever` | 0.2, estimated | 0.22 (22 %) | direct |
| C8 | `controls.throttlePause` | 0.5 s, estimated | 0.5 s (0.5 s) | direct |
| C9 | `controls.shiftInterlockSpeed` | 94.2478 rad/s (900 rpm), estimated | 94.2478 rad/s (900 rpm) | direct |
| S1 | `controls.steeringMode` | follow-up, estimated | follow-up | direct |
| S2 | `controls.hardOverAngle` | 0.610865 rad (35 deg), estimated | 0.610865 rad (35 deg) | direct |
| E5 | `machinery.gearRatio` | 2.519 (2.519:1 reduction), published | 2.519 (2.519 ratio) | direct |
| E8 | `propellers.rotation` | outward-turning, estimated | outward-turning | direct |
| S3 | `controls.steeringRate` | 0.122173 rad/s (7 deg/s), estimated | 0.135748 rad/s (9 s hard over to hard over at 35 deg) | derived |
| C10 | `transmission.clutchFillTime` | 0.4 s, estimated | 0.55 s (0.55 s) | derived |
| C11 | `controls.neutralDwell` | 2.0 s, estimated | 2.05 s (2.8 s ahead to astern) | derived |

with one warning (the synthetic gear ratio would replace a published value), a model-check section saying the combined parameters passed the ferrysim run, and the stopping, turning, twisting and target rows listed for manual calibration. The example's fourth shift row, a 6.5 s reversal from 1500 rpm with a deliberate pause in neutral, is **not** turned into a parameter: it appears among the calibration targets with the reason (the time includes the captain's pause and the interlock wait), and `controls.neutralDwell` is derived only from the straight-through reversal at idle. An answer that confirms an estimate (E1 here) is still worth applying: the confidence moves from `estimated` to `crew-reported`.

The proposed `engines.idleSpeed` entry (synthetic) shows how provenance is kept:

```json
{ "value": 68.06784, "unit": "rad/s", "asPublished": "650 rpm", "source": "crew-synthetic-2026-01-15-captain-a", "confidence": "crew-reported", "note": "Previously 68.0678 rad/s (650 rpm), confidence estimated, source: Baudouin 6M26.3 datasheet idle speed; vessel setting unconfirmed. Old note: Ask crew: engine rpm at idle in gear. Crew note: SYNTHETIC: confirms the datasheet idle" }
```

`resistance-fit.md` (synthetic) reports the conditions it fitted (load half, wind 5 kn under the 10 kn calm limit, deep water, through-water and slack over-ground speeds) and the residual of every row at the lever that reproduces its rpm:

| Line | Reported rpm | Lever | Simulated rpm | Reported kn (± tol) | Simulated kn | Residual kn | Settled | Load | Wind kn | Depth | Caveat |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 2 | 650 | 0.1600 | 650 | 7.9 (±0.5) | 7.93 | +0.03 | yes | half | 5 | deep |  |
| 3 | 1100 | 0.4483 | 1100 | 12.3 (±0.5) | 12.27 | -0.03 | yes | half | 5 | deep |  |
| 4 | 1560 | 0.7021 | 1560 | 17.7 (±0.5) | 17.79 | +0.09 | yes | half | 5 | deep |  |
| 5 | 1920 | 0.9007 | 1920 | 23 (±0.5) | 23.00 | -0.00 | yes | half | 5 | deep |  |
| 6 | 2060 | 0.9765 | 2057 | 25.1 (±1) | 24.95 | -0.15 | yes | half | 5 | deep |  |

Every residual is inside its row's tolerance, which is now a condition of acceptance. The astern row (900 rpm, 4.5 kn) is listed as excluded: it informs `hull.asternResistanceFactor`, not the resistance scale. The iterations table shows the bisection from 0.70 through 1.05, 0.875, 0.7875 and 0.83125 to 0.85312, and the Simulator section names the `ferrysim.dll` build (SHA-256 and timestamp) the fit ran against.

Applying the synthetic patch to a copy (never to `vessels/` without `--approved`):

```
$ uv run tools/crew_intake/intake.py apply --patch artifacts/crew-intake/synthetic-2026-01-15-captain-a/proposed-patch.json --vessel vessels/t-class.json --out artifacts/crew-intake/synthetic-2026-01-15-captain-a/t-class.patched.json
applied 12 change(s) from artifacts/crew-intake/synthetic-2026-01-15-captain-a/proposed-patch.json to vessels/t-class.json; model check passed with the ferrysim CLI; wrote artifacts/crew-intake/synthetic-2026-01-15-captain-a/t-class.patched.json
```

`diff vessels/t-class.json artifacts/crew-intake/synthetic-2026-01-15-captain-a/t-class.patched.json` then shows the new `sources` line (and the comma added to the line before it) and the twelve patched parameter lines, nothing else.

## How the resistance fit works

1. Rows are admitted only when the forward model can represent them: both engines ahead and balanced within 5%, through-water speed (or over-ground at slack current), a known load shared by every fitted row, wind under 10 kn and deep water. Rows that leave `wind_kn`, `current` or `depth` empty inherit the session's conditions. Everything else is listed under Excluded rows with its reason; `--load` selects one load group and `--allow-mixed-conditions` fits the rest knowingly, flagged in the report.
2. Rows at the same rpm must have overlapping tolerance intervals and speed must not fall as rpm rises; otherwise the fit is refused before the simulator runs.
3. Each reported rpm maps to the lever that orders it: the middle of the idle-in-gear band for idle rpm, else linear from `controls.throttleStartLever` to full lever, because the governor holds its set point until the engine is torque limited. The simulated rpm is checked, and a miss is refined by bisection on the lever.
4. One `ferrysim run` holds both levers at each position in turn, in calm deep water, for 150 s each (doubled when speed or rpm have not settled over the last 20 s). The CLI is built incrementally first and refused when its DLL is older than the core source, and the build's SHA-256 is recorded in the patch and the report.
5. The weighted mean residual (simulated minus reported speed, weights 1/tolerance) falls as the scale rises, so the scale is bracketed and bisected until the mean residual is zero.
6. The fit is accepted only when every row's residual is within that row's stated tolerance and under `--max-residual-kn` (default 1.5), the rpm was reproduced, and the scale stayed in 0.02 to 10. Otherwise it is refused with the residuals still reported, naming the rows that conflict. One scale cannot explain such data; the curve shape (`hull.residuaryResistance`) or the propeller pitch would need attention, which is a decision for a person.

Only `hull.residuaryResistanceScale` is fitted. Telemetry and `commands.jsonl` (every CLI invocation) stay under `artifacts/crew-intake/<sessionId>/work/`; the temporary parameter variants are deleted when the fit ends, whether it succeeded or not.

## Tests

```
uv run --project tools/crew_intake pytest tools/crew_intake
```

or, equivalently, `uv run --with pytest pytest tools/crew_intake`. The suite covers parsing and validation on synthetic answer sets (good, missing or null metadata, wrong types, consent and approval, roles and codes, template placeholders, wrong units, out of range, malformed CSV), the publication boundary (contact details and titled names refused, marine terms accepted, ambiguous phrases held for review, invented identifiers only), patch generation and provenance (old value, source and note kept; derived values use the patched inputs; shift timings with a pause or an interlock wait reported instead of derived; base fingerprints; stale patches, colliding sources and failed model checks refused), the model check against the core's invariants and the real CLI, the vessel JSON writer (byte-for-byte round trip of both vessel files), the command line on the synthetic example (manifests, stale-output cleanup, approval guard), the simulator build's freshness, and the resistance fit (admissible conditions, tolerance checks, recovering a known scale from speeds generated by the real CLI with a modified parameter file, variant cleanup). A session-wide fixture checks that nothing under `vessels/` changed.
