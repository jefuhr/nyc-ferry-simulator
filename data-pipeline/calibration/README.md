# AIS calibration evidence

This package audits the public AIS source, joins a conservative designer-name subset, subtracts historical NOAA current vectors, extracts events with explicit rejection reasons, and exercises the actual `ferrysim` CLI. It does not infer engine or rudder commands from AIS. See [report.md](report.md) for the results and why no physical parameter was relabelled `ais-derived`.

Run from the repository root with the existing locked environment; no new dependencies are needed:

```sh
uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m calibration.simulator
PYTHONPATH=data-pipeline uv run --project data-pipeline python -m calibration --fetch-currents
uv run --project data-pipeline pytest
uv run --project data-pipeline pytest data-pipeline/calibration/tests -q
dotnet test core/dotnet --logger 'trx;LogFilePrefix=calibration' --results-directory artifacts/calibration/core
```

Build the CLI before the simulator script on a fresh checkout (`dotnet build core/dotnet/FerrySim.Cli`). The script uses `--no-build` and writes each actual argv and exit code to `artifacts/calibration/simulator-commands.json`. It uses `maneuver acceleration`, `turning-circle`, `crash-stop`, and `run --inputs` for an idle-detent trace. All parameter variants live in `artifacts/calibration/`; vessel files are never mutated by the package.

Run the simulator script before the analysis: the cadence plot reads the factor-1 baseline's `time_to_25_kn_s` from `artifacts/calibration/model-summary.json`. Use `--model-summary PATH` to select another generated summary, including with a custom `--out`; no acceleration time is hard-coded.

`--fetch-currents` is explicit network access, restricted to NOAA current predictions. Download bytes go only under `$HOME/FerrySim-cache/data/raw/coops-calibration/` (or the existing `FERRYSIM_CACHE` override). The manifest records URLs, retrieval times, checksums, response depths, returned bin identifiers and station metadata. Requests retain the API-default bin, with no verified shallowest or hull-depth selection: Dimond Reef metadata lists 3.35/5.49/9.45 m while cached responses return 9.4 m, Bin 9. Subsequent runs verify checksums, reuse the cache and refresh bin identifiers without changing source provenance. Without this flag, analysis is entirely local. Missing or altered cache files fail rather than silently fetching replacements.

Use `--radius-m 500 --out artifacts/calibration/radius-500` to test a tighter station-distance cutoff. The default 750 m cutoff is a conservative screening assumption, not a validated local-current error bound. Each report records its nearest station and distance; no extrapolation outside prediction times or interpolation across gaps longer than six minutes is allowed. UTC timestamp resolutions are normalized explicitly before interpolation. NOAA directions point **toward**; water velocity is ground velocity minus predicted current. Speed magnitude and heading-projected surge are separate outputs. AIS does not measure the local water vector.

## Extraction rules

- A dock report has SOG at most 2 kn and is within 75 m of a GTFS landing. A departure starts at the last dock report and ends at the first report reaching 10 m/s SOG within five minutes. Arrivals reverse this procedure. The trigger is over-ground speed to preserve rejection counts when current coverage is absent; event metrics use corrected water speed.
- Acceleration/arrival events require at least four reports, at least 15 seconds, and no interval longer than 10 seconds. These limits aim to resolve a roughly 24-second transient; one-minute samples do not pass. Candidate arrival samples are exported even when rejected, with their rejection reason, and must not be mistaken for resolved deceleration profiles. Profile distance is to the final landing, not whichever terminal is closest at each point.
- Steady windows are non-overlapping within each uninterrupted vessel track, contain at least three reports, span at least 120 seconds, and have no gap longer than 90 seconds. All reports must be over 250 m from a landing, SOG at least 8 m/s, SOG range at most 0.5 m/s, corrected speed range at most 0.7 m/s, and cumulative COG change at most 10 degrees. They are operational observations, not full-power trials. Non-overlap reduces duplication but does not establish statistical independence across voyages or vessels.
- Turn windows start with four intervals where available and extend until they span at least 15 seconds or reach the track end. Adjacent windows share one endpoint but no time interval; a final short window remains subject to rejection. Candidates need at least 10 degrees of heading or course change. Accepted turns need valid actual heading at every report, at least four reports over 15 seconds, and maximum gap 10 seconds. The same gates apply at 1 Hz and lower rates, regardless of source. COG can nominate a candidate but never substitutes for heading as yaw. Heading increments wrap across north; increments exceeding 90 degrees are rejected. Sparse records cannot rule out unobserved revolutions, so no yaw inference is made from them.
- All event types require finite current-corrected speed and positions. Non-increasing times fail. An interval displacement greater than the larger endpoint SOG times the interval plus 30 m fails the position check. This is a gross-jump screen, not a claim of 30 m accuracy or a complete noise model. Curved paths and variable acceleration can also cause rejection.
- Long gaps over five minutes split tracks. The rejection audit counts detected candidate episodes/windows only, not all true maneuvers. No heading is synthesized and no missing current is set to zero.

## Identity and fitting boundaries

`inputs.py` contains factual name lists verified against Incat Crowther IC16019 and IC18130. Only case and surrounding whitespace are normalized. The AIS modal dimensions must agree within one metre in both length and beam, allowing integer encoding. Conflicting dimensions and spelling variants stay unclassified. This is a documented AIS-static-name association, not a verified MMSI register or historical configuration guarantee. IC18130 is kept separate from the model's IC16112 K-class design.

`fit_grid` offers a deterministic one-parameter grid fit with a seeded bootstrap for independent controlled trials supplied by a caller. Unknown controls are explicitly rejected. Its synthetic known-answer test verifies repeatability, not applicability to uncontrolled AIS. No observational bootstrap interval in this package should be read as a physical-parameter confidence interval without a justified error model, known controls and independent trials.

The tests were started before extraction implementation and cover known acceleration/arrival, current sign and crossflow, heading wrap, timestamp units, gaps, duplicate times, position jumps, missing currents, identity conflicts, and seeded fitting. All outputs use SI units except columns explicitly marked `kn` or `deg`; geometry reuses `ferrydata.geodesy` ECEF/NED conversion.
