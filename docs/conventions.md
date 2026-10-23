# Conventions

These rules are the contract between the sim core, the data pipeline, the CLI and Unity. Most simulator bugs are sign, unit or frame mistakes, so every subsystem follows this page exactly. If something here is wrong or missing, raise it with the coordinator rather than working around it locally.

## Units

- The core works in SI units only: metres, kilograms, seconds, watts, newtons, radians.
- Knots, feet, horsepower, rpm and degrees appear only at boundaries: data import, parameter files (in `asPublished`), and the UI.
- Exact conversion factors live in `core/FerrySim.Core/Contracts/Units.cs`:
  - 1 knot = 1852/3600 m/s
  - 1 ft = 0.3048 m
  - 1 hp = 745.69987158227022 W (mechanical horsepower)
  - 1 rpm = 2π/60 rad/s
- Geodetic latitude and longitude are the one exception to "radians everywhere". They are WGS84 decimal degrees, carried in `GeodeticPosition`.

## Frames

**World frame:** a local north-east-down (NED) tangent plane through the shared origin.
- The origin is WGS84 latitude 40.7000°, longitude −74.0150°, ellipsoidal height 0, near The Battery. It is defined in `Contracts/Geodesy/LocalTangentPlane.cs` as `WorldOrigin`.
- The simulation world is the horizontal (north, east) part of the plane, for points on the ellipsoid surface.
- Convert with `LocalTangentPlane.World`, which goes through ECEF. Never use a flat lat/lon scaling: across the harbor it is off by tens of metres.
- Earth curvature is real at harbor scale. The Rockaway landing area lies about 35 m below the tangent plane. Rendering (Cesium) handles curvature; the 2D physics does not need to.

**Body frame** (Fossen convention):
- x is forward and y is starboard, measured from the reference point: centreline, midships, at the design waterline.
- z is down.

**Angles:**

| Angle | Positive direction |
|---|---|
| Heading ψ | Clockwise from true north, in [0, 2π). 0 = north, π/2 = east |
| Yaw rate r | Turning to starboard |
| Roll φ | Starboard side down |
| Pitch θ | Bow up |
| Rudder angle | Starboard rudder: the bow swings to starboard when going ahead |

**Velocities:**
- `VesselState` holds body-frame velocities over the ground: surge u, sway v, heave w.
- Hydrodynamic forces use velocity relative to the water: ν_r = ν − ν_current, with the current rotated into the body frame.
- Never add the current to position as a separate kinematic drift on top of a model that already uses relative velocity. The conformance tests catch this.

**Flow directions:**
- Current and wind vectors point the way the medium is moving toward (north and east components, m/s). NOAA current "set" already follows this convention.
- Meteorological wind direction is where the wind blows from. Convert it at the data boundary with `HorizontalVector.FromSpeedFrom`.

**Unity:** Unity is left-handed and y-up.
- The bridge maps world (north, east, down) to Unity (x = east, y = −down, z = north).
- A heading ψ is a Unity yaw of ψ in degrees about +y, because both turn clockwise seen from above.
- Unity never feeds positions back into the physics. It only renders interpolated state.

## Vertical datums

- Depths and terrain use NAVD88 metres; CUDEM is NAVD88. Water levels from NOAA CO-OPS are requested in NAVD88 too.
- Depth at a point and time = water level(t) − seabed elevation.
- Depth under keel = depth − `maxDraft`. If `maxDraft` is missing, use `draft` and flag it.
- Cesium and Google tiles work in WGS84 ellipsoidal heights. NAVD88 zero sits roughly 32 m below the ellipsoid around New York (geoid undulation). The data pipeline publishes the exact offset for each slice, and the Unity world uses it to place the water surface. Never hard-code 32.

## Time

- Simulation time is seconds since scenario start, a double.
- Each scenario carries an absolute UTC start time, used for tides, currents, sun position and AIS replay. The UI displays America/New_York local time.
- Physics runs on a fixed 100 Hz clock (`SimulationClock`).
- Time is always derived from the step count (`SimulationClock.TimeAtStep`), never accumulated.

## Determinism

The same parameters, environment, initial state and input sequence must give bit-identical states on the same runtime.

In physics code, never use:
- wall-clock time
- unseeded randomness
- iteration order over hash-based collections
- multithreaded reductions

Floating-point results can differ slightly between runtimes (CoreCLR in tests, Mono or IL2CPP in Unity). Cross-runtime comparisons use a tolerance (1e-9 relative), not bit equality.

## Contact geometry

Fixed structures the hull can touch (landing faces, piers, bulkheads) reach the core through `IWorldGeometry` (`Contracts/WorldGeometry.cs`). Pass it with the four-argument `ISimulationFactory.Create`; the three-argument form means open water.

- A `ContactSegment` is a straight piece of structure in world (north, east) metres. The structure is solid on the right-hand side walking from `Start` to `End` seen from above; the water is on the left. `WaterNormal` points into the water.
- `ContactSurface` values are per metre of contact length: stiffness (N/m per m), damping (N·s/m per m), Coulomb friction, and the compression at which a fender bottoms out (m).
- Segment `Id`s are stable, so telemetry and debriefs can name what was hit.
- Queries must be deterministic (same segments in the same order) and allocation-free. `SegmentListGeometry` is the simple reference implementation.
- Landing faces come from the data pipeline's `landing_faces.geojson`. The Unity world and the CLI build segments from it, and never hand-place them inside the core.

## Operator inputs

`ControlInputs` is device-independent and normalized:

| Input | Range | Meaning |
|---|---|---|
| `PortLever`, `StarboardLever` | [−1, 1] | −1 full astern, 0 neutral, +1 full ahead |
| `Helm` | [−1, 1] | +1 hard over to starboard |

- Input devices only produce these values.
- Detents, clutch-in at idle, shift delays and neutral interlocks belong to the core's control model, so every device behaves like the real control heads.
- Whether the real steering is follow-up (wheel position = rudder angle) or non-follow-up (jog lever = rudder rate) is still open. The control model owns that mapping.

## Vessel parameter files (`vessels/*.json`)

- Property names are camelCase. Unknown properties are load errors.
- Every number is a `ParameterValue` with these fields:

| Field | Required | Content |
|---|---|---|
| `value` | yes | The number, in the SI unit named by `unit` |
| `unit` | yes | One of `m m2 m3 kg kg*m2 s W N N*m m/s m/s2 rad rad/s rad/s2 1 count` |
| `asPublished` | no | The original figure, for example `85'4"` or `815 hp at 2,100 rpm each` |
| `source` | yes | A key in the file's `sources` map. For `estimated` values, free text stating the basis |
| `confidence` | yes | `published`, `ais-derived`, `crew-reported` or `estimated` |
| `note` | no | Caveats and conflicts |

- Text facts (engine model and so on) use `ParameterText`, with `value`, `source`, `confidence` and `note`.
- `VesselParameters` is a partial class. The contract part holds identity and published particulars. The dynamics code adds its model sections (hull, propellers, rudders, control model) as other parts of the same class, in its own files.
- When a better source replaces a value, update `value`, `source` and `confidence`. Move the old figure into `note` if the conflict matters.

## Code style

- C# in the core targets `netstandard2.1` at `LangVersion 9.0`, because the same files compile inside Unity 6. That rules out:
  - file-scoped namespaces
  - global usings
  - `record struct`
  - `init` accessors
  - required members
  - collection expressions
- The core has no `UnityEngine` references (`noEngineReferences` in the asmdef).
- Serialization lives in `FerrySim.Core.Serialization`, so the physics assembly stays dependency-free.
- Indent with tabs. Exceptions: YAML, and Unity-serialized assets (`.editorconfig` has the details).
- Tests use NUnit's constraint model (`Assert.That(x, Is.EqualTo(y).Within(tol))`), which works in both the .NET test projects and Unity's NUnit-based Test Framework.

## Shared contracts and who may change them

| Path | Owner | Notes |
|---|---|---|
| `core/FerrySim.Core/Contracts/**` | coordinator | Request changes; don't edit |
| `core/dotnet/FerrySim.Core.Tests/Contracts/**` | coordinator | Conformance tests every simulation must pass |
| `core/dotnet/*.csproj`, `core/dotnet/FerrySim.slnx`, `core/FerrySim.Core/package.json`, `*.asmdef` | coordinator | Project wiring |
| `docs/conventions.md` | coordinator | This file |

## Checks

| What | Command |
|---|---|
| Core tests | `dotnet test core/dotnet --logger "trx;LogFilePrefix=core" --results-directory artifacts/core` |
| Every check at once | `scripts/check.sh` (once it exists); writes results under `artifacts/` |
