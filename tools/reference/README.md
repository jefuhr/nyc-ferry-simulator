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
`~/FerrySim-reference/v04/sheets`; override it with `--sheets` for a different approved sheet set. A validated C9 `manifest.json` is mandatory beside the sheets, or select it with `--manifest`. Every used sheet must match its manifest hash, current processing consent (unless owner/measurement-only), non-withdrawn sources and approved screening. A manifest records decisions; it does not perform visual screening.
Input symlinks are refused, including directory links. Approved source media is never rewritten. Reconstruction creates private derived files with original-source lineage.

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

`--sheets` is required for normal measurement: it verifies consent, screening, the manifest hash and any pick `image_size`/`image_sha256`. Nothing on the command line reads a sheet without that gate. `--numbers-only` recomputes the values of a committed pick file from the numbers in it, reading no sheet at all, and is refused together with `--sheets`. It reproduces the arithmetic; it is not consent evidence. For example:

```sh
uv run --offline --project tools/reference python tools/reference/reference.py measure \
  tools/reference/picks/t-class.json --numbers-only \
  --output artifacts/reference-import/t-class-measured.json
```

Measurement-only sources may be measured and catalogued but never reconstructed. Keep their normalized relative sheet path in `image` and value `source`, and credit them by role in pick `notes`, for example `builder general arrangement (private)`. This retains exact withdrawal matching without names. `measure` outputs a
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
  For perspective use `plane-homography` below. Lens distortion is not corrected.
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

## Plane homography

An oblique photo of a flat surface can use four or more corresponding image `points`
and known `plane_m` coordinates in metres. Coordinates and distances are returned in
that plane's frame; `axis`, `origin` and `transverse_axis` do not apply to this calibration.
The caller must state a positive `tolerance_px`. The fit reports RMS and maximum forward
reprojection error in image pixels and refuses a maximum above tolerance, or a degenerate
point set. More anchors and independent check lengths help expose a wrong point or a
surface that is not flat. Four anchors alone have no redundant fit check.

```json
"calibration": {
  "method": "plane-homography",
  "points": [[50,35],[250,42],[260,160],[75,175]],
  "plane_m": [[0,0],[2,0],[2,1],[0,1]],
  "tolerance_px": 1.0
}
```

Use the `measure` command above. Values export with layout method `known-dimension`
and a note containing reprojection RMS, maximum and tolerance. This respects the frozen
layout schema. Image resolution, point accuracy and lens distortion still limit accuracy;
a small fit residual is not proof of correct dimensions away from the anchors.

## Withdrawal audit

```sh
uv run --offline --project tools/reference python tools/reference/reference.py audit \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" \
  tools/reference/picks/t-class.json art/blender/data/t-class-hull.json \
  art/blender/data/landing-survey.json
```

Supply every pick, layout and survey JSON to inspect. Findings include the file, JSON
location, exact sheet citation and reason. The recursive walk covers nested values and
survey lists. A missing/excluded/withdrawn source or stale consent invalidates its derived
views and every cited value. Source ids are resolved against current manifest records;
missing ids fail closed. Existing repository-file citations (such as a vessel parameter
file) are not sheet citations; missing media and unresolved role citations are flagged.
Exit 0 means clean, 1 means findings, 2 means invalid inputs. Nothing is deleted or edited.
The manifest must first be refreshed from the private intake withdrawal record.

## Reconstruction

All reconstruction input must pass the current manifest gate. Measurement-only material
is refused. All output paths, manifests and registers must be outside **every** Git
repository, including ignored folders; symlinks and replacing existing reconstruction
outputs are refused. No external tool or package is installed. `ffmpeg`, Xcode/RealityKit
and Blender must already be present for the steps that use them.

Every persistent output is recorded in `manifest.derived` with its SHA-256, method and
flattened original `source_id` list. Frames, meshes, poses, scale reports and view metadata
are private. A coordinator must review and release each geometry view intended for worker
measurement by adding its own approved `sheets` entry. A file listed only in `derived`
is refused by `catalog` and `measure`. The same path may appear once in each collection,
but their hashes must match and `derived.from` must equal the set of source ids in the
approved sheet entry. Those sources retain their current consent and withdrawal state.
The existence of a reconstruction does not substitute for screening.

### `recon frames`

```sh
uv run --offline --project tools/reference python tools/reference/reference.py recon frames \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" \
  --input "$HOME/FerrySim-reference/v05/sheets/walk.mp4" \
  --output "$HOME/FerrySim-cache/reference-import/frames" --rate 2 --min-sharpness 100
```

ffmpeg samples the approved video at the chosen rate in Hz, strips metadata and audio,
and writes PNG frames. A 128×128 grayscale Laplacian variance rejects frames below the
sharpness threshold; `frames.json` retains every score and acceptance decision. The default
100 is a starting threshold, not a validated blur classifier. It can reject a sharp blank
surface or accept textured noise. Review the frames. No surviving frames is an error.

### `recon build`

```sh
uv run --offline --project tools/reference python tools/reference/reference.py recon build \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" \
  --input "$HOME/FerrySim-cache/reference-import/frames" \
  --output "$HOME/FerrySim-cache/reference-import/model.usdz" --detail medium
```

The wrapper validates every frame against recorded lineage, compiles `recon/recon.swift`
with installed `xcrun swiftc` (no packages), and runs RealityKit `PhotogrammetrySession`.
It requests the mesh and camera poses in the same session. Unsupported APIs/hardware and
failed requests exit non-zero. Medium is the default and minimum measurement detail;
full/raw are available. Sequential ordering is the default for a walk-around; use
`--ordering unordered` for an unordered photo set. The companion `.poses.json` contains
sample ids, frame filenames, translation and quaternion `[x,y,z,w]`. Poses are in the
original reconstruction frame and model units; scaling the mesh does not rewrite poses.
The native backend is internal; use the gated Python command to invoke it.

### `recon scale`

```sh
uv run --offline --project tools/reference python tools/reference/reference.py recon scale \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" \
  --input "$HOME/FerrySim-cache/reference-import/model.usdz" \
  --output "$HOME/FerrySim-cache/reference-import/scaled.obj" \
  --dimensions "$HOME/FerrySim-cache/reference-import/known-lengths.json"
```

The dimensions JSON is an array of mesh-coordinate endpoint pairs and known metres:

```json
[{"points": [[0,0,0],[20,0,0]], "length_m": 2.0}]
```

Pick endpoints on actual reconstructed features, in the mesh's coordinate frame. Do not
substitute expected world coordinates for picked points. USD/USDZ input is converted in
Blender; OBJ input needs only Python. The uniform factor minimizes the sum of squared
length residuals. The companion `.scale.json` records the factor, individual residuals,
RMS residual and axis-aligned extents. Multiple lengths provide a consistency check;
one length has zero residual by construction. This step changes scale, not orientation,
and writes geometry-only OBJ without textures, UVs, vertex colours or source materials.

### `recon views`

```sh
uv run --offline --project tools/reference python tools/reference/reference.py recon views \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" \
  --input "$HOME/FerrySim-cache/reference-import/scaled.obj" \
  --output "$HOME/FerrySim-cache/reference-import/views" \
  --pixels-per-metre 200 --station 0 --station 2
```

Requires a manifest entry recording known-length scaling. Blender runs in background under
`lockf -k ~/FerrySim-cache/heavy.lock`. It imports geometry into a fresh scene, discards
materials/UVs/colours, and renders orthographic side, plan and front views. Sections are
face/plane intersections at the requested **x** stations. Output uses exactly grey
`(153,153,153)` for geometry, white for background and black for the scale bar and labels.
There is no lighting, texture, vertex colour or antialiasing in the final binary silhouette.
PNG bytes, dimensions, pixels/metre and scale bars are deterministic for the same geometry,
arguments and Blender build. The flat silhouette deliberately omits internal surface cues.

The view frame is **x longitudinal, y transverse, z up**, independent of the layout frame.
A reconstruction starts with arbitrary axes; align it in a private geometry review before
choosing stations or treating a view as a boat side/plan/front. This tool does not infer
boat orientation. `views.json` states the frame, resolution, extents and bar length; the
images visibly label metres and pixels/metre. Map the reviewed coordinates explicitly to
the layout's x starboard, y up, z forward before exporting numeric measurements.

## Private request register

```sh
uv run --offline --project tools/reference python tools/reference/reference.py requests mark \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json" WH-01@t-class --status open
uv run --offline --project tools/reference python tools/reference/reference.py requests status \
  --manifest "$HOME/FerrySim-reference/v05/sheets/manifest.json"
```

`requests.json` lives beside the manifest with mode 0600. `mark` explicitly sets `open`,
`received`, `screened`, `used` or `needs-reshoot`; it creates a missing id and never infers
approval from an upload. Its sheet list is refreshed from each sheet's `tags.requests`.
`status` lists open and needs-reshoot requests with linked sheets. C9 ids use `EXT`, `WH`,
`SCR`, `MOT`, `LND`, `SND` or `HND`, two digits and optional `@class`/`@stop-id` scope.
The coordinator owns the shot-list definitions; the capture kit marks the pending list.

## Synthetic verification

Normal tests use only generated media and Python's standard library. Existing v0.4 math
and seed checks call the measuring functions directly on synthetic fixtures without a
manifest; that tests reproducibility, not consent, and is not reachable from the command line.
Blender and RealityKit checks are opt-in and require the normal local escalation approval:

```sh
REFERENCE_BLENDER_TESTS=1 uv run --offline --project tools/reference python -m unittest discover -s tools/reference/tests -p test_intake.py -k BlenderViewTests -v
REFERENCE_RECON_SLOW=1 REFERENCE_RECON_OUTPUT="$HOME/FerrySim-cache/reference-synthetic-new" \
  uv run --offline --project tools/reference python -m unittest discover -s tools/reference/tests -p test_recon_slow.py -v
```

The slow test renders exactly 40 textured views of a synthetic 2×1×0.5 m box, reconstructs
at medium detail, exports camera poses, picks the known 2 m edge for scale and requires the
independent width/height errors below 2%. Its geometry analyzer is only for this synthetic
box and ground plane, not a general measurement algorithm. Keep the private synthetic
output to inspect any failure; report the numeric errors even if the assertion fails.
