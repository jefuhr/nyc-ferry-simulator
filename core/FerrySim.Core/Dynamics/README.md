# Vessel dynamics

`VesselDynamicsFactory.Create` returns `VesselDynamicsSimulation`: a deterministic three-degree-of-freedom (surge, sway, yaw) manoeuvring model of a twin-screw, twin-rudder catamaran in MMG-style modular form, with engines, gears, a ZF-style control model, wind, current and shallow-water effects, and a light heave/roll/pitch layer for feel and visuals. It covers the NYC Ferry T-class (Incat Crowther IC16019, 26 m) and K-class (IC16112, 29.6 m) from `vessels/*.json`.

Every model coefficient is a parameter with provenance in the vessel file. Nearly all are engineering estimates today. Calibration against AIS tracks and crew figures should change only parameters, never code.

Conventions (units, frames, signs) follow [docs/conventions.md](../../../docs/conventions.md).

## Contents

- [Structure](#structure)
- [States, current and integration](#states-current-and-integration)
- [Equations of motion](#equations-of-motion)
- [Hull](#hull)
- [Propellers](#propellers)
- [Engines and transmission](#engines-and-transmission)
- [Control model](#control-model)
- [Rudders](#rudders)
- [Wind](#wind)
- [Shallow water and grounding](#shallow-water-and-grounding)
- [Heave, roll and pitch](#heave-roll-and-pitch)
- [Telemetry](#telemetry)
- [Parameters and calibration](#parameters-and-calibration)
- [Results and plausibility bands](#results-and-plausibility-bands)
- [Known limitations](#known-limitations)
- [References](#references)

## Structure

| File | Role |
|---|---|
| `VesselParameters.Dynamics.cs` | The dynamics sections of `VesselParameters` (a partial class) and their section types |
| `VesselDynamicsSimulation.cs` | `ISimulation`: state, integration, environment sampling, telemetry |
| `HullModel.cs` | Resistance, linear manoeuvring derivatives and cross-flow drag |
| `PropellerCurves.cs` | Wageningen B-series polynomials and the four-quadrant curves |
| `PropellerModel.cs` | Thrust and torque, slipstream speed, prop walk, thrust deduction |
| `EngineModel.cs`, `Driveline.cs` | Torque curve, governor, turbo lag; clutch and shaft dynamics |
| `ControlModel.cs` | Lever bands and shift sequencing (`EngineControl`), steering gear (`SteeringGear`) |
| `RudderModel.cs` | Rudder inflow and forces |
| `WindageModel.cs` | Wind loads |
| `ShallowWaterModel.cs` | Finite-depth correction factors |
| `MotionResponse.cs` | Heave, roll and pitch layer |
| `PhysicalConstants.cs` | Water and air properties; required-parameter checks |

Each module is a public class constructed from `VesselParameters`, so it can be tested on its own. Tests live in `core/dotnet/FerrySim.Core.Tests/Dynamics/`.

## States, current and integration

The hydrodynamic state is the velocity of the reference point relative to the water, in body axes: ν_r = (u, v, r). The reference point is the contract's: centreline, midships of the waterline length, design waterline.

The current V_c comes from `IEnvironment.SampleCurrent` at the vessel position. For a uniform current, the relative-velocity equations of motion are exactly the calm-water ones (Fossen 2011, §10.3; the Froude-Krylov force of an accelerating current cancels the rigid-body term). So:

- every hydrodynamic force uses ν_r;
- the velocity over the ground is ν = ν_r + Rᵀ(ψ)V_c, which is what `VesselState` reports;
- position integrates that ground velocity, η̇ = R(ψ)ν.

The current is never added to position as a separate drift. A vessel moving with the water feels no hydrodynamic force; a vessel at rest over the ground in a current is carried downstream. The conformance tests check both.

Each 10 ms step (`SimulationClock.FixedTimeStep`) runs in this order:

1. Shallow-water factors from the depth sampled at the end of the previous step.
2. Steering gear and both engine controls advance one step.
3. Both drivelines (engine, clutch, shaft) advance in four 2.5 ms substeps, with the propeller inflow held at the start-of-step hull velocity.
4. The rigid body (N, E, ψ, u, v, r) advances with classical fourth-order Runge-Kutta, with shaft speeds, rudder angle and environment held for the step. Thrust and rudder forces are re-evaluated at every stage.
5. The heave/roll/pitch layer advances (semi-implicit Euler).
6. Time becomes `SimulationClock.TimeAtStep(stepCount)`. The environment is sampled once (current, wind, depth) at the new position, and that sample is reused by the next step.

Determinism:

- doubles throughout;
- no allocation, randomness, collections or threads on the step path (a test asserts zero allocated bytes over 10 000 steps);
- every iteration count is fixed, including the propeller-curve fit done once at construction.

`Reset` starts with both engines running at idle, both clutches in neutral, shafts stopped and rudders amidships.

## Equations of motion

MMG form at midships (Yasukawa and Yoshimura 2015, eq. 4), with the centre of gravity at x_G:

```
(m + m_x) u̇ − (m + m_y) v r − x_G m r²             = X
(m + m_y) v̇ + x_G m ṙ + (m + m_x) u r              = Y
(I_zG + x_G² m + J_z) ṙ + x_G m v̇ + x_G m u r      = N
```

where X = X_H + X_P + X_R + X_W, and likewise Y and N, sum the hull, propeller, rudder and wind modules.

Inputs:

- m comes from `particulars.displacement`;
- I_zG = m k_zz² with k_zz from `massProperties.yawRadiusOfGyration`;
- m_x, m_y and J_z are the hull's added masses, with m_y and J_z scaled by the shallow-water factor.

The 2×2 sway-yaw block is solved directly. As in MMG, the Munk moment is part of the hull derivative N'_v and is not a separate added-mass Coriolis term.

## Hull

Calm-water resistance at speed through the water V = |u|:

```
R(V) = ½ρSV² [ f_v ((1 + k) C_F + C_A) + f_w s_R C_R(Fn) ]
C_F  = 0.075 / (log₁₀Re − 2)²          (ITTC 1957, Re = VL/ν, floored at 10⁵)
Fn   = V / √(gL)
```

- S, the form factor (1 + βk) and C_A are parameters.
- C_R(Fn) is a tabulated hump shape typical of round-bilge semi-displacement catamarans: peak near Fn 0.45 to 0.5, falling at high speed (after Insel and Molland 1992 and the Southampton series of Molland, Wellicome and Couser 1994).
- The calibration scale s_R sets its level.
- f_v and f_w are the shallow-water factors.
- Going astern, the resistance is multiplied by `asternResistanceFactor`.

The vessel's own still-air drag is part of the calibrated resistance, as in trials analysis (see [Wind](#wind)).

Manoeuvring forces, for both demihulls together:

```
X_H += ½ρLT (X'_vv v² + X'_vr v rL + X'_rr r²L²)
Y_H  = ½ρLT f_lin (Y'_v |u| v + Y'_r u rL)       − ½ρ C_D f_cf 2T ∫ (v + xr)|v + xr| dx
N_H  = ½ρL²T f_lin (N'_v u v + N'_r |u| rL)      − ½ρ C_D f_cf 2T ∫ x (v + xr)|v + xr| dx
```

L is the waterline length and T the hull draft of one demihull. The integral runs over [−L/2, L/2] in 20 strips.

The linear terms are the MMG derivatives, written with |u| and u instead of U:

- they behave like slender-body lift (∝ u·v);
- they vanish at zero headway;
- they change sign correctly going astern (fore-aft symmetry).

The cross-flow term carries the forces at low speed, in drift, when crabbing and when pivoting.

The derivatives are Clarke, Gedling and Hine (1983) regressions for one demihull, converted to the MMG basis, doubled for two hulls and multiplied by 1.2 for catamaran interaction. For comparison, Duman and Bal (2022) report CFD values of Y'_v −0.39 to −0.58 and N'_v −0.26 for the Delft 372 catamaran.

Added masses:

- m_y and J_z come from strip theory, using a double-body rectangular section coefficient (DNV-RP-C205), both demihulls;
- m_x is about 5% of the displacement.

## Propellers

Both propellers have the same geometry. Diameter, blade count and gear ratio come from `machinery`. Shaft speed ω is positive in the sense that drives the boat ahead, for either hand. For propeller i, at lateral position y_i = ∓s/2:

```
V_A = (1 − w)(u − r y_i)
β   = atan2(V_A, 0.35 ω D)                       (0.7πnD = 0.35ωD)
T   = ½ρ(V_A² + (0.35ωD)²)(π/4)D² C_T*(β)
Q   = ½ρ(V_A² + (0.35ωD)²)(π/4)D³ C_Q*(β)
X_P = Σ(1 − t)T,   N_P = −Σ y_i(1 − t)T + x_P Y_walk,   Y_P = Σ Y_walk
```

The thrust deduction t has separate ahead and astern values. Prop walk is a side force proportional to thrust. Its sign follows the propeller's hand:

- a right-handed propeller walks the stern to starboard going ahead and to port going astern;
- outward-turning means the starboard propeller is right-handed;
- so the walk always adds to the twist from differential thrust.

The four-quadrant curves C_T*(β) and C_Q*(β) are built at construction:

1. **Ahead quadrant** (0 ≤ β ≤ β at zero thrust): the Wageningen B-series open-water polynomials (Oosterveld and van Oossanen 1975; coefficients from Bernitsas, Ray and Kinley 1981) at the vessel's P/D, A_E/A₀ and Z. A test checks the implementation against the B4-70 chart values.
2. **Other quadrants**: the blade-element "L-model" of Häusler et al. (2013), with α = φ − β and φ = atan(P/D / 0.7π):
   - c_L = (a₁ sin 2α + a₂ cos 2α) · k(α), with k(α) = k_rev + (1 − k_rev)(1 + cos α)/2 so that trailing-edge-first (astern) flow has less lift;
   - c_D = d₀ + d₁ cos 2α + d₂ sin 2α;
   - C_T* = c_L cos β − c_D sin β, and C_Q* = 0.35 (c_L sin β + c_D cos β).

   The five coefficients come from a weighted linear least-squares fit to the same B-series curve, with a prior that c_D(90°) equals the flat-plate value `bladeNormalDragCoefficient`.
3. **Blending**: the two are blended over 30° beyond each edge of the ahead quadrant. The fitted model's error at the ahead bollard point is applied as a ratio at the astern bollard point too, blended out over 60° either side of β = π. Astern-to-ahead ratios therefore follow the blade-element model, while their level follows the B-series.
4. **Tabulation**: the curves are tabulated every 2.5° and evaluated with periodic Catmull-Rom interpolation, so they are continuous all the way round.

Tests check:

- signs in all four quadrants;
- drag from a locked or windmilling propeller;
- an astern/ahead bollard ratio between 0.55 and 0.8 (0.69 with the shipped values);
- continuity;
- passivity: thrust power never exceeds shaft power.

The slipstream speed for the rudders comes from momentum theory: V_jet = sgn·√|V_A|V_A| + 8T/(ρπD²)|. It is negative when the flow through the disc runs forward (astern thrust).

The pitch ratio is the design match. At rated rpm and rated power, the B-series propeller absorbs the full torque at the speed taken as maximum: 26 kn for the T-class, 30 kn for the K-class. That gives P/D 1.243 and 1.264, ordinary values for fast ferries.

## Engines and transmission

Each engine has:

- a full-load brake-torque curve T_max(ω) (piecewise linear);
- friction that rises linearly from idle to rated speed;
- a PI governor;
- a turbocharger lag.

```
ω_set   = ω_idle + throttle·(ω_rated − ω_idle)
T_cmd   = K_p(ω_set − ω_e) + I,   K_p = J/τ_gov,  K_i = K_p/T_i,  J = I_e + I_s/g²
T_avail = (T_max(ω_e) + T_f(ω_e)) · (b₀ + (1 − b₀)B),   Ḃ = (clamp(T_cmd/(T_max + T_f), 0, 1) − B)/τ_turbo
T_comb  = clamp(T_cmd, 0, T_avail)                       (conditional integration for anti-windup)
```

The governor is stiff (τ_gov 0.05 s), as electronic governors are. The rpm response lag therefore comes from what the engine can deliver: boost build-up, inertia and propeller load. A test checks the lag, the absence of overspeed and the absence of hunting.

The shaft (propeller, entrained water, shaft and gear output, inertia I_s) and the engine (I_e) are joined by the hydraulic clutch of the selected direction d = ±1. The clutch has three modes:

- **Open**: both bodies spin freely. I_s ω̇_s = −Q − T_sf. The water windmills the propeller ahead when the boat is moving.
- **Locked**: ω_s = d ω_e / g and (I_s + I_e g²) ω̇_s = d g η^{±1}(T_comb − T_f) − Q − T_sf. The lock holds while the torque it needs stays within capacity.
- **Slipping**: the clutch transmits its capacity C · sgn(slip). It locks, conserving angular momentum, when the speeds meet.

Capacity is C_max × clutch pressure × an anti-stall factor. The factor falls from 1 at 0.85 × idle to 0 at 0.5 × idle, so a clutch dragging the engine down slips instead of stalling it. This is an assumption to confirm. In a crash stop the astern clutches reverse the windmilling propellers within a fraction of a second, then the engines bog down, with the clutches slipping, until the boat has slowed enough for the propellers to absorb the power.

## Control model

Each lever drives one `EngineControl`; the helm drives the `SteeringGear`.

Lever bands, as fractions of travel either way:

| Lever position | Result |
|---|---|
| \|lever\| < `clutchInLever` (0.12) | Neutral |
| `clutchInLever` to `throttleStartLever` (0.20) | In gear at idle (the ahead and astern idle detents) |
| Above `throttleStartLever` | Throttle demand rises linearly to 1 at full lever |

A hysteresis of `detentHysteresis` (0.02) keeps a noisy input device from chattering the clutch at the detent.

Shift sequence: shift phases (`ShiftPhase`) run in this order:

1. **Releasing**: throttle to idle, then the engaged clutch releases (`clutchReleaseTime`).
2. **NeutralDwell**: only when engaging opposite to the last direction that carried torque. Waits until `neutralDwell` has passed since that release, whatever path the lever took.
3. **AwaitingInterlock**: until engine speed ≤ `shiftInterlockSpeed`.
4. **Filling**: `clutchFillTime` with no torque.
5. **Engaging**: pressure ramps over `clutchEngagementTime`; the clutch slips until the speeds meet.
6. **Engaged**: at full pressure. After `throttlePause` the throttle follows the lever. The clutch can still slip under overload (a windmilling propeller being brought round), which shows in the shaft speed rather than the gear state.

With the shipped values:

- from neutral, a clutch carries torque about 0.4 s after the lever reaches the detent, and throttle follows from about 1.5 s;
- ahead to astern takes at least 2.6 s before the astern clutch takes up torque and 3.2 s to full pressure, longer at speed while the clutch slips.

Gear telemetry shows `Shifting` in every phase except `Neutral` and `Engaged`.

Steering gear:

- **Follow-up** (shipped): the helm orders δ = helm × `hardOverAngle`.
- **Non-follow-up**: the helm moves the rudder at the steering rate in its direction, and the rudder stays put when the helm is centred.

Either way the rudder slews at `steeringRate` (7°/s) to ±`hardOverAngle` (35°). Both rudders move together. Whether the real helm is follow-up or a jog lever is still open (docs/conventions.md); `controls.steeringMode` selects it.

## Rudders

For each rudder, behind its propeller at (x_R, ±s/2), MMG (Yasukawa and Yoshimura 2015) generalised to all inflow directions:

```
u_wash = V_A + κ(V_jet − V_A)            κ = slipstreamFactor if V_jet ≥ 0, else upstreamInflowFactor
u_R    = ε · sgn(s)√|s|,   s = η u_wash|u_wash| + (1 − η) V_A|V_A|
v_R    = γ_R (v + l'_R L r)  in normal flow (u_R ≥ 0),   v + x_R r  going astern
F_N    = sgn(f_n) · ½ρ A_R (u_R² + v_R²) C_N(α)
X_R = (1 − t_R) F_N sin δ,   Y_R = (1 + a_H) F_N cos δ,   N_R = (x_R + a_H x'_H L) F_N cos δ − y_R X_R
```

- f_n is the inflow component normal to the rudder plane, and α the incidence between flow and chord.
- C_N = f_α sin α up to the stall angle, with f_α = 6.13Λ/(Λ + 2.25) (Fujii), then eases linearly to a flat plate's 1.2 at 90°.
- In reversed flow (going astern, trailing edge first) the lift slope is × `reversedFlowLiftFactor`, the stall angle × 0.6, and the hull interaction terms are dropped.

At zero speed the slipstream still flows over the rudder, so a kick ahead with helm turns the boat. Going astern, starboard rudder swings the stern to starboard (bow to port), weakly, as on real boats.

## Wind

Three-coefficient model (Fossen 2011, §10.1). For an air velocity w relative to the vessel in body axes, with γ the angle the wind comes from relative to the bow and q = ½ρ_a|w|²:

```
X = −q A_F c_x cos γ,   Y = −q A_L c_y sin γ,   N = −q A_L L_OA c_n sin 2γ + x_LA Y
```

The true wind comes from `IEnvironment.SampleWind`. The applied load is:

```
F = F(Rᵀ(W − V_g)) − F(−Rᵀ V_g) + F(−ν_r)
```

This is the wind-resistance increment of ISO 15016 and ITTC 7.5-04-01-01.1: the apparent-wind load minus the load of the vessel's own motion over the ground in still air. Because the calibrated calm-water hull model already contains the vessel's own still-air drag, the formula then adds back that drag for the motion through the water.

- With no current this equals the plain apparent-wind load.
- With no true wind and no motion through the water it is exactly zero.

The second property is what the contract test `Neutral_MovingWithUniformCurrent_StaysWithTheWater` requires. A pure apparent-wind model would fail that test, because still air would drag a boat drifting at 1.5 m/s behind the water by several centimetres per second. The cost is that a boat drifting with a current loses the small aerodynamic force of its own drift (about 100 N at 1.5 m/s).

The heeling moment uses the lateral area's height above the waterline.

## Shallow water and grounding

`IEnvironment.SampleDepth` drives four groups of factors. Depth is clamped to `minimumDepthRatio` × hull draft (1.05) before any of them are evaluated, so they stay finite at, below and far below the draft, and on dry land.

| Effect | Factor |
|---|---|
| Return flow (Lackenby 1963) on both resistance parts | (1 + 0.1242·max(0, A_X/h² − 0.05))² |
| Depth-critical hump on residuary resistance | 1 + a·w(L/h)·exp(−((Fn_h − 0.95)/0.2)²), with Fn_h = V/√(gh) and w(L/h) rising from 0 at L/h = 0.5 to 1 at L/h = 3 |
| Linear derivatives, cross-flow drag, sway added mass and yaw inertia | 1 + c(T/h)²/(1 − T/h), with c = 0.4, 0.5 and 0.3 |

The manoeuvring factors give about 1.5× at h/T 1.5 and 2.6× at h/T 1.2 for the derivatives, of the order of the shallow-water corrections of Kijima et al. (1990). Turning slows and stopping lengthens in shallow water.

Resistance is not monotonic in depth at high speed: water around Fn_h ≈ 1 costs more than shallower supercritical water. That is the real depth-critical effect fast ferries meet in the harbour.

Grounding:

- depth under keel is `depth − maxDraft`, or the hull draft when `maxDraft` is missing (flagged by `KeelDraftIsHullDraft`);
- below zero, `IsAground` is true;
- there is no contact force yet, so the boat keeps moving over the shoal.

## Heave, roll and pitch

Damped oscillators at the natural periods, driven toward quasi-static targets. They do not feed back into the horizontal dynamics.

- **Roll**: φ_eq = K / (m g GM_T). K is the heeling moment about the centre of gravity: hull lateral force at `lateralForceDepth`, rudder and propeller forces at the rudders' `forceDepth`, and wind at `lateralAreaHeight`. The boat heels outward in a turn (about 1.5 to 2° hard over at speed) and to leeward in a beam wind.
- **Pitch**: θ_eq = running trim(Fn) + u̇(h_G + shaft depth)/(g GM_L), so the bow lifts when accelerating.
- **Heave**: running sinkage(Fn).

## Telemetry

| Field | Source |
|---|---|
| Engine speed, shaft speed (negative astern) | Drivelines |
| Gear | Control phase: `Neutral`, `Ahead`/`Astern` when engaged, `Shifting` otherwise |
| Throttle | Governor demand after the control logic (0 idle, 1 rated) |
| Rudder angles | Steering gear (both equal) |
| Speed through water | u_r, negative going astern |
| SOG, COG | Ground velocity; COG is the heading when stopped |
| Depth under keel | depth − keel draft (`maxDraft` when present) |
| Contact force | 0 until a contact model exists |

`VesselDynamicsSimulation` also exposes:

- `IsAground`, `KeelDraft`, `KeelDraftIsHullDraft`;
- `PortShiftPhase` and `StarboardShiftPhase`;
- `RudderOrder`;
- `LastForces`, the per-module force breakdown, for debug overlays.

## Parameters and calibration

The sections of `vessels/*.json`:

| Section | Holds |
|---|---|
| `massProperties` | Yaw radius of gyration, centre of gravity |
| `hull` | Resistance, added masses, derivatives |
| `propellers` | P/D, area ratio, wake, thrust deduction, prop walk, shaft inertia, slipstream factors |
| `engines` | Idle speed, torque curve, inertia, friction, governor, turbo |
| `transmission` | Gear efficiency, clutch capacity and timings |
| `controls` | Lever bands, dwell and pause, interlock, steering mode, hard over, rate |
| `rudders` | Area, aspect ratio, position, stall, MMG interaction coefficients |
| `windage` | Areas, coefficients, centre |
| `shallowWater` | Correction coefficients |
| `motionResponse` | Metacentric heights, periods, damping, running attitude |

Estimated values were also added to existing contract fields where the contract already has a home for them:

- `particulars.displacement`, for both classes;
- `machinery.gearRatio` and `machinery.propellerDiameter`, for the K-class;
- `performance.maxSpeed`, for the T-class, as the assumed 26 kn anchor.

Every number has `value`, an SI `unit`, a `source` (free-text basis for estimates) and `confidence`. Values that crew could confirm have a note starting "Ask crew".

Calibration knobs, roughly in order of influence:

| Observation | Parameters |
|---|---|
| Speed against rpm, AIS speed-power | `hull.residuaryResistanceScale`, `propellers.pitchRatio`, `engines.torqueCurve` |
| Acceleration and stopping (AIS) | `particulars.displacement`, `engines.turbochargerTimeConstant`, `hull.asternResistanceFactor` |
| Turning circles from AIS tracks | `hull.derivativeNr`, `hull.derivativeYv`, `hull.crossFlowDragCoefficient`, `rudders.area` |
| Lever and shift timing (crew) | `controls.*`, `transmission.clutchFillTime` |
| Twisting rate and walk with split engines | `propellers.reversedFlowLiftFactor`, `propellers.asternThrustDeduction`, `hull.crossFlowDragCoefficient` |
| Speed in shallow stretches (AIS against charted depth) | `shallowWater.criticalSpeedAmplitude` |

If `pitchRatio`, `expandedAreaRatio` or the blade count changes, the four-quadrant curves refit automatically at construction.

## Results and plausibility bands

`StandardManeuverTests` runs the standard manoeuvres in calm deep water and writes `artifacts/core/dynamics-report.md`. Standard approach: steady straight running at service speed, reached from rest.

| Result | T-class | K-class |
|---|---|---|
| Full ahead, calm deep water | 26.0 kn at 2097 rpm | 30.0 kn at 2096 rpm (published 30.0, ±5% required) |
| Service speed (lever) | 25.0 kn (0.96) | 24.0 kn (0.79) |
| Idle in gear (lever 0.2) | 8.0 kn | 9.1 kn |
| Turn, hard over at service speed: advance / transfer / tactical diameter | 4.90 / 2.76 / 5.75 L | 4.35 / 2.41 / 5.14 L |
| Time to 90° / 180° | 13.4 / 24.6 s | 14.3 / 25.6 s |
| Steady turn: speed loss / heel outward | 29% / 1.5° | 22% / 1.9° |
| 10/10 zig-zag: first / second overshoot | 5.3° / 5.5° | 5.1° / 5.4° |
| Crash stop from service speed: head reach / time | 3.72 L / 16.0 s | 3.40 L / 17.1 s |
| Pivot, ahead idle and astern idle: yaw rate / walk | 2.4 °/s / 1.2 m/s | 2.3 °/s / 1.3 m/s |
| Rest to service speed, full ahead | 24.4 s | 16.0 s |

The report has the full speed-against-lever table in 0.1 steps.

**T-class maximum speed.** Only a 25 kn running speed is published. The model takes 25 kn at about 90% of rated power, with power rising as speed to the 2.7 power above the hump. That gives 26.0 kn at full power, and the resistance scale is set to reach it. The deep-draft Rockaway variant makes 27 kn on 1380 hp, so a River-class boat on 803 bhp stays below that.

Bands. No public full-scale trials of comparable 25 to 35 m twin-screw catamarans were found. The bands are therefore built from:

- the IMO manoeuvring standards for conventional ships (MSC.137(76): advance ≤ 4.5 L, tactical diameter ≤ 5 L, 10/10 first overshoot ≤ 10° and second ≤ 25° for L/V < 10 s, stopping track reach ≤ 15 L);
- the Delft 372 catamaran results of Duman and Bal (2022), at 20° rudder and Fn 0.37: advance 3.2 L, tactical diameter 2.7 to 3.0 L, speed loss 27 to 36%, 20/20 overshoots up to 72°;
- first-principles bounds.

The IMO limits are widened where small fast craft differ: at 25 kn the 5 s hard-over execution covers about 2.5 L, against about 1 L for the ships the criteria were written for. All bands are provisional until calibration.

| Manoeuvre | Band | Basis |
|---|---|---|
| Advance | 2.0 to 6.0 L | IMO 4.5 L plus about 1.5 L for steering execution; Delft 372 3.2 L at 20° |
| Tactical diameter | 2.0 to 7.0 L | IMO 5 L widened for directionally stable twin hulls at high Fn; below 2 L at 70% speed the lateral acceleration would exceed about 0.4 g |
| Transfer | 0.8 to 3.5 L | About half the tactical diameter |
| Time to 180° | 10 to 45 s | Execution plus half a circle of 2 to 7 L at the reduced turning speed |
| Speed loss in the steady turn | 15% to 45% | Delft 372 27% to 36% |
| Heel in the turn | 0.2° to 6°, outward | Displacement catamaran with GM_T about 10 m |
| 10/10 first / second overshoot | 1° to 15° / 1° to 25° | IMO 10° / 25°; zero would mean no yaw inertia; catamarans can overshoot more (Delft 372) |
| Crash stop head reach | 1.5 to 10 L | IMO 15 L for ships; fast catamarans stop much shorter (R/W ≈ 0.1 at speed, about 1 m/s² from the hull alone); 0.3 g average gives about 1.2 L |
| Crash stop time | 10 to 60 s | Same reasoning |
| Idle-split pivot | 1 to 8 °/s, walk below 2 m/s | Thrust moment at the propeller separation against cross-flow yaw damping (C_D 0.3 to 1.2); fixed-pitch astern thrust 0.6 to 0.75 of ahead leaves a forward walk |
| Rest to service speed | 10 to 90 s | Power-to-weight 22 to 25 kW/t; kinetic energy over usable power gives about 8 s as an absolute floor |

## Known limitations

- **Contact.** No contact, fender or collision forces (the contract has no world geometry yet). Grounding is only reported.
- **Out of scope.** No waves, vessel wakes, other vessels, bank or channel effects.
- **Cavitation.** No cavitation or ventilation: the open-water curves hold at any blade loading, so thrust breakdown at high loading (full power at low speed, crash stops) is not represented.
- **Current.** The current is sampled at the reference point only: no yaw moment from current shear across the hull (eddies at slips), and no added-mass force from spatial gradients.
- **Coefficients are estimates.**
  - Hull: from monohull regressions with an interaction factor; no captive or CFD data for these hulls.
  - Propellers: the B-series stands in for the actual Michigan Wheel designs.
  - Engine: the torque curve is typical, not digitised from the Baudouin datasheet.
- **Load case.** One loading condition (the estimated operating displacement); passenger load does not change mass or draft.
- **Wind.** Uniform at 10 m height: no gusts, height profile or shielding by structures.
- **Water and air.** Density (1020 kg/m³, brackish harbour water), viscosity and air density are constants until `IEnvironment` provides them.
- **Depth under keel.** Ignores dynamic sinkage and trim, as the contract defines it.
- **Heave, roll and pitch.** Decoupled from the horizontal model.
- **Low-speed handling.** Idle-in-gear speed, astern speed and the walk in an idle split are sensitive to estimated low-speed resistance and astern thrust. They are the first things to check against crew figures.
- **Control model.** The anti-stall clutch modulation and the shift timings are assumptions about the control system.

## References

- Bernitsas, M.M., Ray, D. and Kinley, P. (1981). KT, KQ and efficiency curves for the Wageningen B-series propellers. University of Michigan, Report 237. Coefficients as distributed in Fossen's Marine Systems Simulator (`wageningen.m`).
- Clarke, D., Gedling, P. and Hine, G. (1983). The application of manoeuvring criteria in hull design using linear theory. Trans. RINA 125.
- DNV (2010). DNV-RP-C205, Environmental conditions and environmental loads (added-mass coefficients).
- Duman, S. and Bal, S. (2022). Turn and zigzag manoeuvres of Delft catamaran 372 using CFD-based system simulation method. Ocean Engineering 264, 112265.
- Fossen, T.I. (2011). Handbook of Marine Craft Hydrodynamics and Motion Control. Wiley.
- Häusler, A.J., Saccon, A., Hauser, J., Pascoal, A.M. and Aguiar, A.P. (2013). Four-quadrant propeller modeling: a low-order harmonic approximation. IFAC CAMS.
- Hoerner, S.F. (1965). Fluid-Dynamic Drag.
- IMO (2002). Resolution MSC.137(76), Standards for ship manoeuvrability.
- Insel, M. and Molland, A.F. (1992). An investigation into the resistance components of high speed displacement catamarans. Trans. RINA 134.
- ISO 15016:2015 and ITTC 7.5-04-01-01.1, speed/power trials: wind resistance correction.
- ITTC 7.5-04-02-01, Full scale manoeuvring trials.
- Kijima, K., Nakiri, Y., Tsutsui, Y. and Matsunaga, M. (1990). Prediction method of ship manoeuvrability in deep and shallow waters. MARSIM & ICSM 90.
- Lackenby, H. (1963). The effect of shallow water on ship speed. The Shipbuilder and Marine Engine-Builder 70.
- Molland, A.F., Wellicome, J.F. and Couser, P.R. (1994). Resistance experiments on a systematic series of high speed displacement catamaran forms. Ship Science Report 71, University of Southampton.
- Oosterveld, M.W.C. and van Oossanen, P. (1975). Further computer-analyzed data of the Wageningen B-screw series. International Shipbuilding Progress 22.
- SOLAS Regulation II-1/29, steering gear.
- Yasukawa, H. and Yoshimura, Y. (2015). Introduction of MMG standard method for ship maneuvering predictions. J. Marine Science and Technology 20, 37–52.
