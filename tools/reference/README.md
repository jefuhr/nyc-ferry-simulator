# Reference measurements

Offline, standard-library Python tools for turning private reference sheets into numeric layout data.
No package installation or source media is needed to run the tests or reproduce the seeded values.
Run from the repository root; use `--offline` to ensure `uv` cannot fetch anything.

```sh
uv run --offline --project tools/reference python -m unittest discover -s tools/reference/tests -v
uv run --offline --project tools/reference python tools/reference/reference.py --help
```

## Privacy boundary

Only privacy-screened, consented sheets are inputs. Never point these tools at raw intake, a bot
database, or an intermediate screening folder. The default input is
`~/FerrySim-reference/v04/sheets`; override it with `--sheets` for a different approved sheet set.
Input symlinks are refused, including directory links. Source media is read, never rewritten or copied.

Catalog paths can contain identities, so catalogs are private even when the media has been screened.
They must live **outside every Git repository**, including ignored artifact folders. The default is
`~/FerrySim-cache/reference-import/catalog.json`, written with mode `0600`. Measurement outputs
contain only source-relative paths, provenance and numbers; those can be reviewed for Git.
Credit is by role, such as `builder general arrangement (private)`, never by a person's or boat's name.

## Commands

### Catalog

```sh
uv run --offline --project tools/reference python tools/reference/reference.py catalog \
  --sheets "$HOME/FerrySim-reference/v04/sheets" \
  --output "$HOME/FerrySim-cache/reference-import/catalog.json"
```

Sorted relative paths, kind, byte size, SHA-256 and image dimensions are recorded. Installed
`ffprobe` supplies video/audio duration; when absent, duration is null with an explicit status.
PNG/JPEG/GIF/BMP headers need no external tool; TIFF/WebP need installed `ffprobe`. No tool is
installed automatically. Invalid media makes the command fail instead of omitting it silently.
There are no timestamps or absolute source paths. Identical inputs and probe availability yield
identical JSON. The output cannot be inside the input sheets or a repository.

### Measure

```sh
uv run --offline --project tools/reference python tools/reference/reference.py measure \
  tools/reference/picks/t-class.json \
  --sheets "$HOME/FerrySim-reference/v04/sheets" \
  --output artifacts/reference-import/t-class-measured.json \
  --comparisons artifacts/reference-import/t-class-comparisons.json
```

`--sheets` is optional: supplying it verifies that the private image still matches the pick's
`image_size` and `image_sha256`. Omitting it supports reproducibility without distributing the image,
but does not prove that the picks are still bound to the current private sheet. `measure` outputs a
layout-shaped value set; full class/physics consistency is checked by `validate` and `export`.

A pick file uses pixel coordinates in the original image, x right and y down:

```json
{
  "schema_version": 1,
  "subject": "t-class",
  "kind": "hull",
  "image": "drawings/example.png",
  "image_size": [800, 400],
  "origin": [400, 200],
  "axis": [[100, 200], [700, 200]],
  "calibration": {
    "method": "drawing-scale",
    "scale": "1:75",
    "dpi": 150
  },
  "measurements": {
    "deck_width": {
      "points": [[200, 300], [200, 100]],
      "operation": "y",
      "confidence": "medium"
    }
  }
}
```

- `drawing-scale`: use a positive `scale`, and exactly one of `dpi` or
  `pixels_per_printed_mm`. A printed scale alone is insufficient after resizing a raster.
  `transverse_pixels_per_printed_mm` handles independent vertical resizing. The T-class example
  derives effective raster scale from printed station/deck spans, rather than assuming file DPI.
- `known-dimension`: use two `points` and positive `length_m`. Optional `cross_dimension`
  contains its own two points and length, calibrating transverse scale independently. With two
  calibrations, the first span's longitudinal projection and the second's transverse projection
  are used. Use long spans close to the intended axes.
- `axis` gives the direction of positive longitudinal distance; its position does not set the origin.
  The default transverse direction is perpendicular and image-up. `origin` defaults to `[0,0]`.
  A single axis corrects in-plane rotation, **not perspective or shear**. If a second measured
  direction is known, `transverse_axis` gives a second pair, pointing toward positive transverse
  distance. Inverting these two directions corrects affine shear; parallel axes are rejected.
  Perspective warping and lens distortion are not supported.
- `distance` (default) takes two points, `polyline` takes at least two, and `x`/`y` give signed
  pair differences. `point` takes one point and returns a two-component metre coordinate.
  In a profile, positive transverse is up; in a plan, map its sign to the documented vessel frame.
- `printed_m` explicitly transcribes a printed dimension. It wins over pixel picks and is marked
  as a transcription in the output note. Optional points remain an independent raster self-check
  in `--comparisons`. `offset_m` translates scalar dimensions to the waterline datum, after the
  comparison; the comparison table therefore shows the original baseline-relative dimensions.
  Printed chains (e.g. deck rises added together) must be explained in the measurement note.
- Each measurement supplies `confidence` and an optional `note`. Unprinted values default to
  medium confidence. Pick precision and evidence quality are separate: the K-class plan's
  unprinted dimensions are low-confidence because the sheet says not to scale.
- Optional `estimates` contains complete layout value objects with an estimate method and
  **low** confidence. It cannot overwrite a measured key. State what evidence is missing.

The two worked pick files bind to the approved private raster hashes, and contain no image data.
Calibration-anchor comparisons are labeled: matching a dimension used to calibrate the image
is not an independent accuracy check. Synthetic tests establish the math within 1%; they do not
claim that an original sheet or manually picked point is accurate within 1%.

### Validate and export

```sh
uv run --offline --project tools/reference python tools/reference/reference.py validate \
  art/blender/data/t-class-hull.json art/blender/data/k-class-hull.json
uv run --offline --project tools/reference python tools/reference/reference.py export \
  artifacts/reference-import/t-class-measured.json --layout art/blender/data/t-class-hull.json --dry-run
uv run --offline --project tools/reference python tools/reference/reference.py export \
  artifacts/reference-import/t-class-measured.json --layout art/blender/data/t-class-hull.json
```

`validate` reads `art/blender/data/layout.schema.json`, checking all of its assertion keywords,
including nested groups, provenance, methods, confidence, units, vector sizes and finite numbers.
The small standard-library evaluator fails closed on unsupported schema keywords; it is not a
complete implementation of JSON Schema. `--schema` and `--vessels` can select explicit alternatives.
For a full hull it also requires LOA within 0.05 m of `vessels/<subject>.json`, positive lengths,
LWL no greater than LOA, ordered supplied height datums, waterline y=0, and wheelhouse fore/aft bounds.
Partial value sets may omit heights; their absence is not proof of a complete geometry specification.

`export` prints a unified diff, preserves unrelated/nested values and existing document notes,
and keeps higher-confidence values unless `--force` is explicit. Equal-confidence incoming
values replace old values. A value/group shape collision or subject/kind mismatch is refused.
It validates the complete result before atomic replacement; `--force` never bypasses validation.
A missing layout is created from the incoming metadata. `--dry-run` writes nothing.

## Seed coordinate contract and limits

All lengths are metres. `length_overall` excludes appendages, `beam` excludes sponsons,
`length_waterline` is the printed design LWL. `*_height` uses waterline y=0; `*_z` uses
half LOA as z=0 with forward positive. `frame_zero_z` places frame 0 on that axis.
`frame_spacing` is the ordinary station interval. On T-class the final frame 19–20 interval
is shorter and supplied separately as `last_frame_spacing`. Never assume all stations are uniform.
`wheelhouse_width` and fore/aft extents describe the enclosure, not its console.
`demihull_width` is a tentative plan breadth, not a surveyed waterline section. Sponson width is
omitted because these sheets do not clearly separate it from rub rails/line weight.

T-class values describe IC16018. Some physics values describe IC16019; LOA is compatible but LWL,
beam and depth differ. No physics file is changed. K-class heights are expressly unmeasured
placeholders; obtain a dimensioned K-class profile before treating them as production geometry.
