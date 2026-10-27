# Generic passenger catamarans

`ferries.py` is the source of the T-class and K-class visual models. No reference images,
third-party geometry, names, numbers or operator branding are used. No `.blend` is needed.
The native FBX exports are tracked through the repository's existing LFS rule.

## Rebuild

From the repository root, with Blender 5.2.2 already installed:

```sh
lockf -k "$HOME/FerrySim-cache/heavy.lock" /Applications/Blender.app/Contents/MacOS/Blender --background --factory-startup --python-exit-code 1 --python art/blender/ferries.py -- --out unity/Assets/FerrySim/Vessels/Models --renders artifacts/ferry-models/blender
lockf -k "$HOME/FerrySim-cache/heavy.lock" /Applications/Blender.app/Contents/MacOS/Blender --background --factory-startup --python-exit-code 1 --python art/blender/ferries.py -- --out artifacts/ferry-models/export-repeat
python3 art/blender/verify_exports.py unity/Assets/FerrySim/Vessels/Models artifacts/ferry-models/export-repeat
python3 -m unittest discover -s art/blender/tests -v
```

Then invoke `FerrySim.Vessels.Editor.FerryModelImporter.Build` in the Unity editor under
both the heavy and Unity locks, heavy first. It writes only `Assets/FerrySim/Vessels/`.
Keep all Unity-created `.meta` files. LOD0 uses seven neutral surface materials with different metal/smoothness settings.
LOD1 uses a single neutral palette texture and material. Each LOD has one renderer. There are no animation or physics meshes.

## Dimensions and provenance

`dimensions.py` reads `vessels/t-class.json` and `vessels/k-class.json` without changing them.
LOA, beam, draft, moulded depth, demihull beam and centre spacing are literal parameter values.
The files contain no cabin, wheelhouse, eye or lantern coordinates. Those use the existing
`GreyboxDimensions.From` windage formulas and `VesselLightLayout.ForGreybox`, including the
2.2 m wheelhouse, eye 1.7 m above cabin roof and its forward offset of half cabin length minus 2 m.
These remain simulator estimates, not surveyed vessel geometry. Cabin window subdivisions,
rail/stair/seat/vent detail, neutral paint and chined hull station shapes are original visual estimates.
The K-class file specifies 29.6 m; the model uses that exact value rather than converting the nominal 97 ft.

All receipt/mount vectors use Unity metres: x starboard, y up, z forward. Blender authors
x starboard, y forward, z up. Native FBX axis conversion is checked after import, not assumed.
`Eye`, `Bow`, `Masthead`, `PortSidelight`, `StarboardSidelight`, `Sternlight`, `ForwardAnchor`,
`AfterAnchor` and `AfterMasthead` are named transforms. The after masthead/anchor are inactive
layout support for the existing rules; these sub-50 m vessels do not show extra underway lights.
The bow is centreline, freeboard above water, exactly LOA/2 forward. Mesh LOA and keel draft,
bridge eye and all light mounts must agree within **1 mm** after import. The prefab author validates
every native mount first, then canonicalizes the sub-millimetre FBX unit-conversion noise to the
shared GreyboxDimensions/light formulas, preserving the existing exact own-vessel coordinates.
Each original import error is logged; a mount outside tolerance aborts authoring. The deck is 20 mm
narrower than the specified maximum beam to keep rail/fender details inside it; hull spacing
and demihull beam follow the file (the T-class has a 1 mm beam rounding difference).

## Determinism

`manifest.json` records a SHA-256 of the ordered evaluated vertices, triangle indices,
corner normals, UVs, material slots and material indices (numeric values serialized to seven
decimal places), plus dimensions, mounts, counts
and the FBX container SHA-256. `verify_exports.py` compares two independent exports. FBX
container metadata may differ; that is reported explicitly instead of claiming byte equality.
The installed Blender FBX writer reads `datetime.datetime.now()` for its header and
creation-time fields (`io_scene_fbx/export_fbx_bin.py`, lines 3234–3245 and 3287–3290).
Evaluated LOD0 UVs are reset to exact palette centres after bevels, eliminating interpolation
rounding drift without omitting UVs from the hash. No geometry randomness or clock input exists. Palette bytes and audited identity JSON must also match.

AIS class selection comes exclusively from `data-pipeline/calibration/identity-audit.csv`:
`T` and `K-*` rows. Unclassified and dimension-conflict identities remain generic traffic.
The audit's existing class assignment is consumed as project evidence, not independently re-researched.
