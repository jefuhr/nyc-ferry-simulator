# T-Class AIS Calibration Assessment

Date: 2026-10-23. Base revision: `b082a495502efe69590df906f58c238af88a3dc5`.

## Decision

**No vessel parameters changed.** The source supports a small current-corrected operational speed envelope, but does not identify full-power resistance, acceleration coefficients, or hard-over yaw dynamics. Neither `vessels/t-class.json` nor `vessels/k-class.json` was edited, and no estimate was promoted to `ais-derived`. “Before” and “after” model results are identical. The sensitivity variants described below are experiments, not accepted fits.

The three requested behavior verdicts are:

| Model behavior | Reproduced with the real CLI | AIS verdict |
|---|---:|---|
| About 8 kn idle in gear | 8.018 kn after 300 s at lever 0.2 | **Unconstrained: neither supported nor contradicted.** AIS has no clutch, engine rpm, lever or throttle state. Slow approach samples do not establish idle-in-gear speed. |
| 0 to 25 kn in about 24 s | First 10 Hz sample at/above 25 kn: 24.5 s | **Unconstrained: neither supported nor contradicted.** All 5,894 T departure candidates fail the cadence gate. Slower observed departures would not disprove a full-ahead capability because actual controls are unknown. |
| About 29% speed loss in a hard turn | 29.73% surge loss; final-quarter mean 17.569 kn and yaw 7.962 degrees/s | **Unconstrained: neither supported nor contradicted.** All 3,341 T turn candidates fail the cadence gate; there is no rudder-order record. The simulated 180-degree turn takes less than a typical AIS interval, making sparse heading differences aliased. |

This is a completed evidence assessment with an explicitly **unachieved physical parameter calibration**. The data insufficiency is not a test failure. The model remains provisional and must not be described as AIS-validated.

## Data and Identity

The existing cache rebuild produced **611,075 reports from 45 candidate vessels** for four Monday-to-Sunday weeks: 2024-10-07, 2025-01-13, 2025-04-14 and 2025-07-14. Candidate selection only demonstrates size and visits to ferry landings, not operator or class identity. Input hashes:

- `candidate_roster.csv`: `0be900f3a562dabb92c7a7e307e35ae47a7a2c458ff028d032e225118e251a45`
- `nycferry_tracks.parquet`: `25abfe36e975e25c71083a105510344612e953b2b4ba23de50ca114a735d956c`

The supplied research dossier contains no MMSI registry. With the coordinator's explicit direction, the analysis instead joins the AIS modal static name to a verified designer name list, then screens modal length and beam against the design within 1 m to accommodate integer AIS encoding. Only case and surrounding whitespace are normalized; no fuzzy matching is allowed. This remains an **AIS-static-name association**, not an independently certified MMSI identity or proof of historical engine/configuration continuity.

The designer pages were read through the web retrieval tool on 2026-10-23:

- [IC16019](https://www.incatcrowther.com/ships/ferries/commuter/ic16019/) supplies the 26.0 m × 8.0 m design, its vessel names, Subchapter T classification and 25 kn service speed.
- [IC18130](https://www.incatcrowther.com/ships/ferries/commuter/ic18130/) supplies Purpose and Curiosity, 29.6 m × 8.5 m dimensions and Subchapter K classification. These are retained separately as `K-IC18130`; they are not assumed equivalent to the modeled IC16112 K design.

A direct Python download of the designer page returned HTTP 403, so no raw-page checksum is claimed. The web tool successfully returned both cited pages. The IC16112 web request timed out; the analysis did not extend its class mapping to that designer list.

The [complete identity audit](identity-audit.csv) records all 45 MMSIs, static names, dimensions, source URL, design association and reason. The retained T subset is:

| MMSI | AIS name |
|---|---|
| 367776270 | LUNCHBOX |
| 367778670 | URBAN JOURNEY |
| 367779540 | WAVES OF WONDER |
| 367779550 | SUNSET CROSSING |
| 367782690 | FRIENDSHIP EXPRESS |
| 367784630 | HAPPY HAULER |
| 367784640 | GREAT EAGLE |
| 367790830 | OWLS HEAD |
| 367797260 | SPRING MALLARD |
| 367798420 | MCSHINY |
| 367798430 | STARLIGHT |
| 368004120 | ATLANTIC COMPASS |
| 368025020 | FERRY GODMOTHER |
| 368141510 | WHITE SANDS |

This yields 219,904 T reports. Purpose (368129810) and Curiosity (368135270) contribute 29,865 separate K-IC18130 reports. Twenty-nine candidates remain unclassified in this analysis, including other K names not mapped here. Bay Hopper (368130050, reported 29 m vs 26 m design) and Tooth Ferry (368152730, reported 10 m beam vs 8 m design) match names but fail dimension consistency. No class is inferred from vessel size alone.

## Source Cadence and Measurement Limits

The pipeline's `ais._read_day` and `ais.build` filter geography and static dimensions and sort reports; they do not resample time. The raw 2024-10-07 McShiny subset contains 913 reports, a median interval of 64 s, and only one interval at most 10 s. Its verified raw archive SHA-256 is `410db68866024da32e6c530830959d40c25800e6db870f6e8d78e38e53ae5a1c`. Thus reading the native cache does not recover dense trials discarded by our code. This single-day raw audit is a sample; full-source cadence below comes from the rebuilt retained reports.

Across all candidate vessels, interval percentiles 1/5/50/95/99 are **30/61/69/113/181 s**. There are 750 zero-time duplicate intervals. Of 2,540 intervals at most 10 s, 750 are those duplicates; isolated short intervals are not continuous maneuver coverage. T-class median cadence is 69 s, versus 66 s for the separate K subset. Long inter-day and seasonal gaps are not interpreted as slow reporting during a voyage.

[USCG's AIS position report specification](https://www.navcen.uscg.gov/ais-class-a-reports) defines 0.1 kn SOG, 0.1 degree COG and integer-degree heading, with unavailable-value sentinels. The existing parser removes unavailable SOG/COG/heading. Quantization alone contributes roughly ±0.05 kn and ±0.05 degree before sensor error; integer heading differences can contribute about 1 degree of difference error. Dividing sparse quantized differences by time does not recover peak yaw or acceleration. COG describes ground-track direction and never substitutes for vessel heading in accepted yaw events.

Heading is available in 481,637/611,075 reports overall, 217,814/219,904 T reports and 29,840/29,865 K-subset reports. Availability does not establish sensor accuracy. The processed source lacks position-accuracy flags, antenna offsets, receiver-quality metadata and actual helm/engine controls. Gross position jumps are screened against endpoint speed and a 30 m tolerance; this tolerance is not a measured error distribution. No derivative is fitted from position noise, and no missing current or heading is invented.

## Historical Current Correction

Twenty NOAA CO-OPS prediction files cover five harmonic stations over the four AIS weeks, with an extra day per request to bracket the final reports. Each file has 1,920 six-minute rows. The [manifest](../../data/manifests/coops-calibration.json) records exact URLs, retrieval timestamps, raw byte SHA-256, response depth, size and station coordinates. Raw responses remain outside Git under `$HOME/FerrySim-cache/data/raw/coops-calibration/`.

The stations are Dimond Reef (`NYH1919`), Brooklyn Bridge (`NYH1920`), Corlears Hook (`NYH1921`), Newtown Creek (`NYH1922`) and Hudson River Entrance (`NYH1927`). Existing station metadata is attributed to `data/manifests/coops.json`. Subordinate max/slack events are not invented into continuous predictions. Requests use API-default prediction bins; actual response depths and bin identifiers are preserved in the manifest: 9.4 m, Bin 9 at Dimond Reef and 1.5–2.4 m at the other stations. Dimond Reef's recorded metadata lists depths of 3.35, 5.49 and 9.45 m, so the default response is not established as the shallowest available bin. Its bin/depth mapping remains unresolved; a verified shallowest or hull-depth selection would require resolving that mapping and requesting the selected bin explicitly. The existing 9.4 m response remains particularly weak as a proxy for hull-depth flow; no prediction values or physical conclusions change.

NOAA major-axis metric velocities are converted from cm/s to m/s. The sign selects flood or ebb direction, both **toward** directions. Components are linearly interpolated in UTC, never beyond the available time range or across a source gap exceeding 360 s. Each AIS report uses the nearest harmonic station only when within 750 m. Ground velocity from SOG/COG minus current gives water-relative north/east velocity. The package separately outputs vector magnitude and surge projected onto valid heading. This follows [NOAA's response schema](https://api.tidesandcurrents.noaa.gov/api/prod/responseHelp.html) and [current prediction explanation](https://tidesandcurrents.noaa.gov/noaacurrents/help.html).

Current coverage passes for **127,931 T reports** and **11,253 K-subset reports**. These are local station proxies, not predictions at the boat's exact location or measured currents. Unmodeled cross-channel structure, vertical shear, weather, river discharge, piers and eddies can dominate the error. Nearest-station switching is recorded rather than smoothed into a fabricated flow field: 25 of the 34 retained T steady windows cross the NYH1919/NYH1927 assignment boundary, five cross NYH1920/NYH1921, and four stay at NYH1927. Therefore their distribution is descriptive, not trial-grade.

Only three of those T steady windows have every sample within 500 m of its selected station. A hypothetical 0.5 m/s current-vector error could move a corrected speed by as much as **0.972 kn** by the vector norm inequality; this is a sensitivity scenario, not a measured error bar or 95% confidence interval. Local current uncertainty is not resolved by having more AIS rows.

## Events Kept and Rejected

Exact thresholds and candidate construction are documented in [README.md](README.md). Counts below are **detected candidates**, not a census of every real maneuver. A candidate may have multiple rejection reasons; the failure columns overlap.

| Class | Event | Candidates | Kept | Rejected | Cadence failures | Current/velocity failures |
|---|---|---:|---:|---:|---:|---:|
| T | Departure | 5,894 | 0 | 5,894 | 5,894 | 4,438 |
| T | Arrival | 5,472 | 0 | 5,472 | 5,472 | 4,312 |
| T | Steady | 1,148 | 34 | 1,114 | 84 | 1,099 |
| T | Turn | 3,341 | 0 | 3,341 | 3,341 | 3,316 |
| K-IC18130 | Departure | 1,111 | 0 | 1,111 | 1,111 | 1,004 |
| K-IC18130 | Arrival | 1,000 | 0 | 1,000 | 1,000 | 915 |
| K-IC18130 | Steady | 475 | 2 | 473 | 50 | 473 |
| K-IC18130 | Turn | 1,775 | 0 | 1,775 | 1,775 | 1,761 |

Events also reject non-increasing time, excessive position displacement, absent heading for turns, excessive heading changes, and unstable corrected steady speed. The full row-level audit and combined rejection-reason counts are in `artifacts/calibration/events.csv` and `summary.json`. Unsupported events are discarded for calibration; they are not assigned arbitrary nonzero weights. Arrival samples remain available with their rejected status for descriptive plots only.

## Comparison and Sensitivity

The 34 retained T steady windows span 13 vessels, with speed-through-water-proxy percentiles 5/50/95 of **18.59/20.53/22.77 kn** and range **18.27–23.09 kn**. They are sampled normal operations near the current stations, not the full harbor's service-speed distribution and not maximum-power trials. The two K-IC18130 windows are insufficient for a distribution or K-model calibration (median 20.02 kn). Repeated windows and related current errors are not statistically independent.

![Cadence and operational speed envelope](../../artifacts/calibration/cadence-and-speed.png)

The real CLI was run with unchanged T parameters and temporary resistance-scale variants. Every variant used the same calm environment, full-ahead acceleration command and 180 s duration. The acceleration crossing is the first 10 Hz recorded sample, so it has 0.1 s sample resolution.

| T residuary resistance scale | Role | Final speed at 180 s | Time to 25 kn |
|---:|---|---:|---:|
| 0.56 | Sensitivity only (−20%) | 26.504 kn | 20.8 s |
| 0.70 | Before = after, existing estimate | 26.004 kn | 24.5 s |
| 0.84 | Sensitivity only (+20%) | 25.120 kn | 37.1 s |

All three capacities exceed the retained operational envelope; lower commanded power can explain lower operational speeds under any of them. Minimizing error between full-lever simulations and uncontrolled service speeds would produce a biased “fit.” The data does not select one resistance scale. The seeded grid fitter passes a known-answer synthetic test and explicitly refuses unknown-control observations; it was **not run as a physical fit to AIS**.

![Unaccepted model sensitivity variants and unchanged hard-over behavior](../../artifacts/calibration/simulator-sensitivity.png)

Acceleration and turn envelopes are unavailable after quality filtering. Arrival samples qualitatively decrease in speed near landings, but have no resolved continuous deceleration profile. The crash-stop CLI run was exercised as a model baseline; routine arrivals are not crash-stop trials and cannot calibrate astern thrust or shift behavior.

![Sparse arrival samples, explicitly rejected for deceleration fitting](../../artifacts/calibration/arrival-samples.png)

Low-speed corrected values at a landing do not imply propulsive surge: a vessel holding position in a current has water-relative motion, and a point-station prediction may poorly represent a sheltered slip. The plot must not be used to infer idle detents or throttle schedules.

## Parameter Decisions

| Parameter family | Decision and evidence |
|---|---|
| `hull.residuaryResistanceScale` | Keep 0.70 and its existing estimated provenance. Sensitivity establishes influence, but observed power is unknown and all three variants clear the retained speed envelope. |
| Propeller pitch, area ratio, wake/thrust deduction and engine acceleration constants | No changes. Mass/loading, rpm, power and controls are unobserved, and no dense departure survives. Joint fitting would trade one unknown against another. |
| Yaw derivatives, cross-flow drag and rudder coefficients | No changes. No resolved heading-based turn survives; hard-over command and actual rudder angle are unknown. |
| Shift timing, clutch behavior, idle speed, astern factors | No changes. These require control/engine telemetry or controlled crew observations, not low-speed AIS. |
| K-class parameters | No changes. Only two steady windows in a distinct K design subset, with no transient trials. |

No old estimate was overwritten, so there are no replacement provenance notes to add. Contact geometry and every C# file were untouched.

## Remaining Discrepancies and Crew Questions

The apparent difference between roughly 20.5 kn sampled operation and 26.0 kn modeled full ahead is a difference in operating conditions, not a demonstrated resistance error. The model's rapid acceleration and approximately 8 kn idle speed remain conspicuous assumptions requiring controlled validation. No claim about maximum attainable speed, stopping distance, or rudder-induced speed loss follows from the accepted AIS windows.

1. Record exact vessel identity/configuration, displacement/load, depth, wind and current during trials; confirm whether the AIS static dimensions/names are correct for the excluded boats.
2. Measure both engine rpm, shaft rpm, levers/clutch state and water-relative speed at idle ahead, then several steady rpm/power settings in reciprocal directions. What is true idle-in-gear speed after settling?
3. Capture 1 Hz or faster synchronized position, heading, levers and rpm during a supervised rest-to-service acceleration. Is full power commanded immediately, or deliberately ramped for passenger comfort and wake limits?
4. Capture rudder order/angle, heading and speed in a controlled constant-power turn. Is the helm follow-up, and what are hard-over angle and steering rate? Does the power stay constant through the turn?
5. Record routine approach profiles separately from neutral coast-down and deliberate crash-stop trials. What are gear changes, neutral dwell, engine interlocks and astern limits?
6. Obtain a current measurement near the boat or reciprocal-trial estimate. How does current differ between the slip, channel edge and the nearest NOAA prediction station?

## Validation and Artifacts

Automated checks, from this worktree:

| Command | Result |
|---|---|
| `uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river` | Passed; 45 vessels and 611,075 reports rebuilt from verified cache. |
| `uv run --project data-pipeline pytest data-pipeline/calibration/tests -q` before extraction implementation | Expected red: missing `calibration.analysis`; preserved in `tests-before.txt`. |
| Timestamp-resolution regression before its fix | Expected red: missing normalization helper; preserved in `tests-timestamp-before.txt`. The first analysis run exposed mismatched millisecond/microsecond epochs and was discarded. |
| `uv run --project data-pipeline pytest` | 145 passed, including 10 calibration tests, in 5.34 s. |
| `dotnet test core/dotnet --logger 'trx;LogFilePrefix=calibration' --results-directory artifacts/calibration/core` | 203 core + 147 CLI tests passed, zero failed/skipped; TRX artifacts retained. |
| `PYTHONPATH=data-pipeline uv run --project data-pipeline python -m calibration` | Passed against checksum-verified current cache; final counts above. |
| `PYTHONPATH=data-pipeline uv run --project data-pipeline python -m calibration.simulator` | Six real CLI runs passed: three acceleration variants, turning circle, crash stop and idle control trace. |

Plots were visually inspected for readable labels and correct “unaccepted/rejected” status. This is automated data/CLI validation and visual artifact review, **not** physical vessel validation, crew acceptance or a calibrated simulator. The observed first-run current lookup error was fixed before any evidence was accepted; regression tests cover seconds, milliseconds, microseconds and nanoseconds.

Durable artifacts are in `artifacts/calibration/`: input and event summaries, the raw-cadence audit, corrected tracks, identity audit, rejected arrival samples, parameter variants, exact simulator argv/exit codes, telemetry CSVs, three PNG plots, pytest logs and .NET TRX files. Artifacts are ignored by Git; preserve this directory when integrating the source/report. Reproduction instructions are in [README.md](README.md).
