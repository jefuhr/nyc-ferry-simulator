# crew_intake

Turns crew questionnaire answers into proposed vessel parameter patches. Standard library only; runs with `uv run tools/crew_intake/intake.py`. The documentation, questionnaire and worked example are in [docs/crew/README.md](../../docs/crew/README.md).

```
uv run tools/crew_intake/intake.py validate <answers>
uv run tools/crew_intake/intake.py patch <answers> --vessel vessels/t-class.json [--out <dir>] [--no-build]
uv run tools/crew_intake/intake.py fit-resistance <answers> --vessel vessels/t-class.json [--out <dir>] [--with-patch <proposed-patch.json>] [--load light|half|full] [--allow-mixed-conditions] [--no-build]
uv run tools/crew_intake/intake.py apply --patch <proposed-patch.json> --vessel vessels/t-class.json --out <file> [--approved] [--no-build]
```

Nothing under `vessels/` changes unless `apply` is run with `--approved`.

`patch`, `fit-resistance` and `apply` all run the real `ferrysim` CLI (`core/dotnet/FerrySim.Cli`): `patch` and `apply` to check that the combined parameters shift and move, `fit-resistance` as the forward model. The CLI is built incrementally first (`--no-build` skips the build), and a `ferrysim.dll` older than the newest file under `core/` is refused as stale either way. Each run records its outputs in `manifest.json` in the output folder and deletes the previous run's patch, report and `work/` first, so a refused rerun never leaves an earlier success behind.

Tests:

```
uv run --project tools/crew_intake pytest
```

The fit and model-check tests build and run the real `ferrysim` CLI and need the .NET SDK; they skip with a message when `dotnet` is missing.

| Module | Role |
|---|---|
| `crew_intake/mapping.py` | Questionnaire ids, accepted units, ranges, the parameter each one informs, and which table an item lives in |
| `crew_intake/privacy.py` | The publication boundary for free text: rejects contact details and titled names, holds ambiguous capitalised phrases for review |
| `crew_intake/answers.py` | Reads and validates `session.json` (typed, consent, roles), the CSV tables (strict) and the Markdown files |
| `crew_intake/vesseljson.py` | Loads and writes vessel JSON in the repository's style; fingerprints a vessel |
| `crew_intake/modelcheck.py` | Cross-parameter invariants mirrored from the core, and the neutral-then-ahead sanity run through the CLI |
| `crew_intake/patch.py` | Builds, applies and reports proposed patches bound to the vessel and answer-set fingerprints |
| `crew_intake/ferrysim.py` | Builds and verifies the CLI, runs `ferrysim run` with stepped levers and reads steady states |
| `crew_intake/fit.py` | Selects admissible rpm-speed rows, bisects `hull.residuaryResistanceScale`, refuses fits outside the rows' tolerances |
| `crew_intake/cli.py` | The four commands, output-folder manifests and stale-output cleanup |
