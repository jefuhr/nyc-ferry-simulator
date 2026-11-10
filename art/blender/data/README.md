# Layout data

Measured and estimated geometry for the ferry exterior, wheelhouse interior and landing looks.
Generators read these files instead of hard-coding numbers, so better reference data improves the
models without code changes.

## Files and owners

| File | Contents | Consumer |
|---|---|---|
| `<class>-hull.json` | Hull, deck and superstructure profile, heights, frame stations, wheelhouse position | `art/blender/ferries.py` |
| `<class>-wheelhouse.json` | Wheelhouse footprint, console, controls, screens, seats, eye points | `art/blender/interior.py` |
| `unity/Assets/FerrySim/Harbor/Data/landing-looks.json` | Per-landing visual overrides (same value shape) | landing kit in `World/Landings` |

`<class>` is `t-class` or `k-class`, matching `vessels/*.json`. The physics files in `vessels/` are not
changed by layout data.

## Value shape

Every number is an object (see `layout.schema.json`):

```json
"upper_deck_height": {
  "value": 5.10,
  "unit": "m",
  "source": "drawings/t-class/IC16018-general-arrangement.jpg",
  "method": "drawing-scale",
  "confidence": "high",
  "note": "profile dimension, baseline to upper deck"
}
```

- `source` is a path relative to the private reference sheets folder, a repository path, or
  `crew-confirmed`. Reference images themselves never enter the repository.
- `method` is one of `drawing-scale`, `known-dimension`, `photo-estimate`, `video-estimate`,
  `crew-confirmed`, `published` or `simulator-estimate`.
- `confidence` is `high`, `medium` or `low`.
- Credit is by role only. Builder drawings are cited as "builder general arrangement (private)".

## Frame

Unity metres: x starboard, y up, z forward. The origin is on the centreline, at the design waterline,
at half of LOA, which is the same frame as the mounts and receipts described in `art/blender/README.md`.
Landing values use metres along the berthing face: the face start is the origin, x is along the face and
y is up from the water.

## Wheelhouse screen orientation (v2)

Screen `tilt` is degrees about +x, leaning the top forward away from the operator.
Optional `yaw` follows about +y: positive yaw turns the outward screen normal toward
port (-x). Zero yaw faces aft. The composed rotation is yaw times tilt; a mount
points +z into the display and UV (0, 0) is the operator's lower left. A missing yaw
means zero. Controls retain identity rest rotations; their geometry is authored
around each pivot, and positive travel is positive rotation about the stated axis.
The radar yaw is an explicit low-confidence simulator estimate for readability,
not a measured change to the physical console; WH-04 would replace it.
