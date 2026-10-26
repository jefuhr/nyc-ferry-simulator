# Atmosphere: night and fog

Owner: **night-fog** (M3, plan items C3 and C4), with `World/Sky/`. Assembly `FerrySim.Unity.World`, namespace `FerrySim.World`.

A run at night looks and reads like night on the water, and a run in fog like fog:
- the sky, the sun and the ambient light follow the run's clock;
- the learner's vessel shows its navigation lights;
- the aids flash their characters on the run's clock and are seen at range;
- the bridges show their navigation lights;
- fog is set per scenario as a visibility, lights fade with range and visibility, and the learner's vessel sounds its fog signal.

Units are SI. The vessel frame is Unity's on the vessel: x starboard, y up from the waterline, z forward from midships (docs/conventions.md). Sector bearings are relative bearings, degrees clockwise from right ahead, as the Rules state them.

## Layout

| File | Content |
|---|---|
| `NavigationLightRules.cs` | Light kinds, colours and statuses; sectors and their edges (`LightSector`, `LightSectorEdge`); the layouts (`VesselLightLayout.Generic`, `.ForGreybox`); Rule 22's ranges and the lights by status (`NavigationLightRules`) |
| `SignalLightOptics.cs` | Allard's law, Annex I's intensities and the eye's threshold (`SignalLightOptics`); the visibility as the meteorological optical range (`MeteorologicalVisibility`) |
| `SignalLight.cs` | One light drawn as a point (`SignalLight`); the colours against Annex I's chromaticity regions (`SignalColours`) |
| `Resources/FerrySimAtmosphere/SignalLight.shader` | The point: sector and Allard's law per vertex, additive, depth-tested |
| `NavigationLights.cs` | A vessel's lights, reusable on any hull |
| `BridgeLights.cs` | A bridge's nominal 33 CFR 118.65 lights |
| `AidLights.cs` | The imported aids driven on the run's clock, with their points |
| `FogSignalSchedule.cs` | Rule 35's schedule and the step observer that records the signals |
| `FogSignal.cs` | The learner's whistle: the generated tone and the 3D sound on the run's clock |
| `RunVisibility.cs` | A runner's visibility: its scenario's, or its recording's in a replay |
| `Atmosphere.cs` | Joins it all to the run |
| `../Sky/SunController.cs`, `../Sky/Daylight.cs` | The sky on the run's clock and in fog; the sun-altitude rules; the run's light for the panel (`IDaylightSource`) |

Outside this folder, by grant: `Sim/ScenarioSettings.cs`, `RunRecorder.cs`, `ReplayPlayer.cs` (the visibility), `Sim/VesselSimulationRunner.cs` (`Replay`), `Sim/IDaylightSource.cs` (the panel's contract), `UI/Panel/WheelhouseView.cs` and `WheelhouseUI.cs` (the palette hook), `World/Import/Runtime/DepthColour.shader` and `SolidColour.shader` (night and fog behind a keyword), and the `FerrySim.World.Import.Runtime` reference in `FerrySim.Unity.World.asmdef`.

## How a scene gets it

`SunController` is already in the World and Slice scenes. In play mode it finds the scene's `VesselSimulationRunner` (or uses the one it is given) and, when there is one, adds an `Atmosphere` next to itself. A scene without a runner, such as the World scene and its tests, is left exactly as it was. No scene is rebuilt.

`Atmosphere`:
- on every run start puts `NavigationLights` (child `Navigation lights`) on the runner's vessel, laid out on its greybox from the run's vessel file, so a K-class run carries the K-class's lights;
- puts the whistle (child `Fog signal whistle`, a `FogSignal`) at the masthead;
- adds `BridgeLights` to every `BridgeGreybox`;
- takes over the imported aids' `BuoyFlasher`s;
- every frame (Update at execution order 100, after the runner's step and the sky) switches the lights, sets the air they are seen through, and turns the import's chart shaders' night and fog on or off.

**The import's chart geometry.** The world import draws terrain, chart structures and landings with unlit shaders (`World/Import/Runtime/DepthColour.shader`, `SolidColour.shader`), which take no light and no fog: at night they would stay daylight-bright, in fog sharp. While a run is dark (`SunController.ChartLight` under 1) or foggy, `Atmosphere` turns on the global keyword `FERRYSIM_CHART_LIGHTING`. Those two shaders then multiply their colour by `_FerrySimChartLight` and draw it through the run's fog (`_FerrySimChartFog`, `_FerrySimChartFogColour`), all set by `SunController`. An aid's lens is a light: `AidLights` marks it (`_FerrySimEmissive` 1 in a property block) and it is not dimmed. With the keyword off, the variant is the import's shader code verbatim. `ChartShaderTests` renders the same geometry with that variant and with the import's shaders as they were (kept as test fixtures) and finds every byte the same. The keyword is off by day in clear air, in any scene without a run, and whenever `Atmosphere` is disabled or destroyed.

For traffic hulls: add a `NavigationLights` to the hull's root (origin at the reference point, +z the bow) and call `ConfigureGeneric(length, beam, mastheadHeight, freeboard, status)`; set `On` from `Daylight.ShowsNavigationLights`. `Status` is underway, at anchor or moored. Every `SignalLight` is seen through the air `Atmosphere` sets each frame (`SignalLight.SetOptics`); a scene without an `Atmosphere` must call it itself, or the lights draw as if in a vacuum.

## Time of day

The sky follows the run: every frame `SunController.Follow(runner)` shows the scenario's start (`runner.ScenarioStartUtc`) plus the run's simulated time (`Driver.SimulationTime`, from the step count), in pack runs, calm runs and replays alike. A paused run's sky stands still; a replay starts at its recording's `scenario_start_utc`, so it draws the sky it was recorded under. Outside a run, the inspector's date and time and `SetUtc` work as before.

The sun-altitude rules (`Daylight`, on the sun's geometric altitude):

| What | Rule | Sun altitude |
|---|---|---|
| Navigation lights | Rule 20(b), from sunset to sunrise; 20(c), also in restricted visibility | below −0.833° (NOAA's sunrise and sunset, as `SolarDay`) |
| Bridge lights | 33 CFR 118.55(a): "from sunset to sunrise and at other times when the visibility is less than one mile" | the same |
| Aid lights' long-range points | an aid's daylight control, approximated by the sun | below −0.833° |
| The panel's night palette | the end of civil twilight | below −6° |

The panel: `SunController` is the scene's `IDaylightSource` (`Sim/IDaylightSource.cs`, `IsNight`). `WheelhouseUI` finds it like its other sources and every frame offers the panel the palette it suggests (`WheelhouseView.SuggestPalette`). The view applies a suggestion only when it changes (at dusk, at dawn, or at a run in another light), so a palette the captain chose by hand holds until then. A scene without a sky (the UI's own scenes) gets no suggestion.

## Navigation lights

Inland Navigation Rules, 33 CFR part 83 (the East River is inland water), and Annex I, part 84; text as on eCFR, current to 2026-10-22, read 2026-10-25.

- **Rule 23(a)**: "A power-driven vessel underway shall exhibit: (i) A masthead light forward; (ii) A second masthead light abaft of and higher than the forward one; except that a vessel of less than 50 meters in length shall not be obliged to exhibit such light but may do so; (iii) Sidelights; and (iv) A sternlight." Under 50 m the second is not shown; from 50 m it is.
- **Rule 21**:
  - (a) "Masthead light means a white light placed over the fore and aft centerline of the vessel showing an unbroken light over an arc of the horizon of 225 degrees and so fixed as to show the light from right ahead to 22.5 degrees abaft the beam on either side of the vessel".
  - (b) "Sidelights mean a green light on the starboard side and a red light on the port side each showing an unbroken light over an arc of the horizon of 112.5 degrees and so fixed as to show the light from right ahead to 22.5 degrees abaft the beam on its respective side."
  - (c) "Sternlight means a white light placed as nearly as practicable at the stern showing an unbroken light over an arc of the horizon of 135 degrees and so fixed as to show the light 67.5 degrees from right aft on each side of the vessel."
  - (e) "All-round light means a light showing an unbroken light over an arc of the horizon of 360 degrees."
- **Rule 22** ranges, the minimum: 50 m or more, masthead 6 miles, sidelight, sternlight and all-round 3; 12 to 50 m, masthead 5 (3 under 20 m), the others 2; under 12 m, masthead 2, sidelight 1, sternlight and all-round 2. The T-class (26.0 m) and K-class (29.6 m): masthead 5 nm, the others 2 nm.
- **Rule 30**: at anchor, "(a) ... (i) In the fore part, an all-round white light ...; (ii) At or near the stern and at a lower level ..., an all-round white light. (b) A vessel of less than 50 meters in length may exhibit an all-round white light where it can best be seen instead". Under 50 m, one light; from 50 m, two.
- **Moored**: Rule 3(i), "The word underway means that a vessel is not at anchor, or made fast to the shore, or aground." The Rules prescribe no lights for a power-driven vessel made fast, so moored shows none.
- **Annex I edges, 84.15(a)**: sidelights' intensities "shall decrease to reach practical cut-off between 1 and 3 degrees outside the prescribed sectors" ahead, and masthead lights, sternlights and the sidelights abaft the beam keep full intensity "up to 5 degrees within the limits of the sectors", "may decrease by 50 percent up to the prescribed limits" and "shall decrease steadily to reach practical cut-off at not more than 5 degrees outside". Modelled as: right ahead, full to the edge and linear to zero 2° outside, so dead ahead both sidelights show; every other edge full to 5° inside, half at the edge, zero 2° outside.
- **Annex I placement** (84.02, 84.03, 84.19), checked by the tests:
  - the masthead light over the centreline, forward of amidships, clear of the other lights;
  - sidelights at least 1 m below it, never ahead of it, at or near the sides;
  - the high-speed-craft rule, an end-elevation base angle of at least 27°: about 40° on the T-class greybox and 38° on the K-class;
  - two masthead lights at least a quarter of the length apart, the after one at least 2 m higher (4.5 m is used, which also meets the International Rules);
  - two anchor lights, the forward one at least 4.5 m above the after one and 6 m above the hull.
- **Colours, 84.13**: white (1.0, 0.85, 0.65) and green (0.0, 1.0, 0.35) in linear sRGB lie inside the CIE regions. Red and yellow regions lie outside the sRGB gamut, so red is sRGB's own red primary (x 0.640, y 0.330; the region starts at x 0.660) and yellow (1.0, 0.3, 0.0) the nearest gamut edge.

## Bridge lights

The cached ENC cells over the slice (the LIGHTS layer of every cell in `noaa-enc`, clipped to the slice's box, read 2026-10-25) have no light on or near the Brooklyn, Manhattan or Williamsburg bridges: the slice's charted lights are all aids, and the bridges' BRIDGE objects carry clearances only. So each bridge carries the nominal set of **33 CFR 118.65** on its greybox, and every light's name says "nominal 33 CFR 118.65 set on the greybox; no bridge lights are charted in the ENC":
- (a) "the center of the navigable channel under each span will be marked by a range of two green lights", each showing 360°, "mounted just below the outermost edge of the bridge span structure": one at each face of the span, 0.5 m under the deck, midway between the greybox's towers;
- (b) "When the navigable channel extends from pier to pier ... each end of such piers shall be lighted with a red light. Each such light shall show through a horizontal arc of 180° ... as low as practicable but not lower than 2 feet above navigable high water to show 90° on either side of a line parallel to the axis of the channel": both ends of both towers, on their channel side, 2 ft above mean high water;
- 118.60: fixed lights, "visible ... at a distance of at least 2,000 yards"; Annex I's intensity for 2,000 yd is 0.83 cd;
- 118.55(a): shown from sunset to sunrise and when the visibility is under one mile (taken as a nautical mile).

The towers are the greybox's estimates (World/README.md), so the pier lights' positions are too. The optional main-channel white lights of 118.65(c) are not shown.

## Aids

Each imported light's `BuoyFlasher` is driven with `SetSimulationTime(run time)` every frame: lit on the run's clock, paused with it, the same in a replay. A long-range point at each lamp shows while the lamp is lit, from sunset to sunrise.

**Ranges.** The chart's nominal range (VALNMR) is not carried by the import: the prefabs keep the character, colour and height. The chart gives a range for only 2 of the 14 lights, Army Rack Lower Fog Light (14 M) and Roosevelt Island Reef Light 17 (6 M). Until the import carries it, every aid point uses an estimate by colour: white 4 nm, red and green 3 nm, others 2 nm.

**Garbage.** `LightCharacteristic.IsLit` (World/Import) allocates on its fallback path, a light without a chart SIGSEQ: `TryValidateFallback` uses LINQ. In the slice that is one light, Army Rack Lower Fog Light (Fl W 2s). `BuoyFlasher.Update` already pays this every frame; driving it here pays it once more. Lights with a chart sequence allocate nothing (tested).

## Fog

`ScenarioSettings.visibilityMetres` is the visibility in metres. 0, the default, is unlimited; otherwise at least 10 m. A run that sets it records `visibility_m` in its telemetry header, and a replay restores it (`RecordedRun.VisibilityMetres`). A run that does not set it records the same bytes as before and draws exactly the scene's own haze (exponential-squared, 0.00007/m): both are tested.

The visibility is the **meteorological optical range** (MOR): "the length of path in the atmosphere required to reduce the luminous flux in a collimated beam ... to 5 per cent of its original value" (WMO, Guide to Instruments and Methods of Observation, WMO-No. 8, chapter on visibility). By **Koschmieder's law** an object's contrast against the horizon falls as exp(−σd), so σ = −ln(0.05)/V ≈ 3.0/V. Unity's exponential fog leaves exp(−density·d) of the object's colour, so the fog density is σ (`MeteorologicalVisibility.ExtinctionCoefficient`). URP measures that distance along the view axis, not radially, so objects near the screen's edges are fogged a little less than they should be.

The fog's colour is lit by the sky: a grey as bright as the brighter of the sky's horizon and zenith, keeping a quarter of the horizon's hue (`SunController.FogColour`). The sky is drawn through the fog:
- the zenith through a 300 m fog layer and the horizon through 5 km, each blended toward the fog colour by the fog's opacity over that path;
- the sun's disk and direct light through the layer's slant path.

The fog still meets the drawn horizon. These are greybox estimates of a fog layer, not a radiative model.

**Colour spaces.** In this linear-colour project, Unity converts `RenderSettings.fogColor` and `SetColor` values from sRGB to linear, but passes `Shader.SetGlobalColor` values (the sky's globals) as they are. Measured in the night captures: fogged objects faded to sRGB 13/255 against a sky of 63/255. So in fog, `SunController` gives the fog colour as the horizon's sRGB (`Color.gamma`), and the scene fades to exactly the horizon the sky draws. The unlimited haze keeps the colour it always had. Signal lights pass their linear colours with `SetVector`.

## Lights in fog: Allard's law

A light of intensity I candelas gives an eye d metres away the illuminance E = I·exp(−σd)/d² (Allard's law). The eye sees it when E reaches a threshold. **Annex I, 84.14(a)** is the same law solved for the intensity: "I = 3.43 × 10⁶ × T × D² × K^−D where ... T is threshold factor 2 × 10⁻⁷ lux, D is range of visibility (luminous range) of the light in nautical miles, K is atmospheric transmissivity. For prescribed lights the value of K shall be 0.8, corresponding to a meteorological visibility of approximately 13 nautical miles." So:
- every light's intensity is Annex I's for its range (sidelight 2 nm, 4.3 cd; masthead 5 nm, 52 cd);
- in unlimited visibility the air is Annex I's (K = 0.8 per nm, σ = 1.20 × 10⁻⁴ /m, about 13.4 nm MOR), so a light is seen exactly at its Rule 22 range;
- in fog the air is the scenario's (σ = 3.0/V), and the range shrinks. At night a sidelight is seen at about 650 m in 500 m fog and about 350 m in 200 m fog: lights carry past the visibility at night, because a light needs a threshold illuminance and an object 5% contrast.

The threshold is Annex I's 2 × 10⁻⁷ lux at night (the sun below −8°), an estimated 1 × 10⁻³ lux by day (above +2°), log-linear between. By day a 52 cd masthead light is seen at about 230 m.

Drawing: each light is a camera-facing point of 2 to 6 pixels (`SignalLight.shader`), worked out per vertex from the light's origin and axes and the global `_FerrySimLightOptics` (σ, threshold). It is drawn only while E reaches the threshold, at a brightness of 0.25 + 0.375·log₁₀(E/threshold), capped at 1, and a size growing over three decades. It is additive, not fogged (the fog is already in σ), and depth-tested, moved half a metre toward the eye so its own mounting does not hide it. `SignalLight.RatioToThreshold` is the same sum in C#.

## Fog signal

In restricted visibility the learner's vessel sounds **Rule 35**: "In or near an area of restricted visibility, whether by day or night, ... (a) A power-driven vessel making way through the water shall sound, at intervals of not more than 2 minutes, one prolonged blast. (b) A power-driven vessel underway but stopped and making no way through the water shall sound, at intervals of not more than 2 minutes, two prolonged blasts in succession, with an interval of about 2 seconds between them."
- **Rule 32(c)**: "The term prolonged blast means a blast of from 4 to 6 seconds' duration." A blast here is 5 s.
- **Schedule** (`FogSignalSchedule`):
  - one signal every 120 s, start to start, on the physics steps (every 12,000), from the run's first step;
  - (a) when the speed through the water is at least 0.2 kn, sternway included, else (b), decided at the signal's step;
  - recorded by a step observer on the run's driver, so a replay, or the same run at another frame rate, gives the same signals at the same times.
- **Restricted visibility**: Rule 3(l) gives no figure ("any condition in which visibility is restricted by fog, mist, falling snow, heavy rainstorms, sandstorms, or any other similar causes"). Here it is a visibility under one nautical mile, the only figure in these regulations (118.55(a)). The same threshold switches the lights on by day (Rule 20(c)).
- **Sound**: a tone made in code, no asset. The fundamental is 350 Hz, inside **Annex III, 86.01(b)(iii)**'s band for a vessel under 75 m, "250-700 Hz". Its second to fourth harmonics are at 1/2, 1/3 and 1/6, with 80 ms attack and 150 ms release, on a 3D `AudioSource` at the masthead (86.01(e)(ii): "as high as practicable"). It pauses with the run and stops when the run ends.
- **Events**: `FogSignal.Recorder.Events` holds the run's signals (step, time, kind). The recording keeps no record of its own: `FogSignalSchedule.Recompute(RecordedRun)` works the same signals out again from the recording's `visibility_m` and its `stw_kn` at each signal's step. The live recorder decides on the speed through the water as the recording writes it, knots to four decimals, so the two agree exactly. The debrief has no place for them yet (UI/Debrief); persisting or showing them is an open question.
- **The horn button**: the captain's horn (`HelmInputController.HornChanged`) plays nothing yet. Wiring it to this whistle belongs in the slice (Input is not visible from World).

## Estimates

| What | Value | Basis |
|---|---|---|
| Light positions on the learner's greybox | masthead 1.5 m above the wheelhouse roof on the centreline; sidelights at the cabin top's edges, 0.5 m up, abreast the wheelhouse; sternlight 1 m above the deck's after edge | greybox, checked against Annex I; the real lanterns are a crew question |
| Generic layout (traffic) | masthead 0.25 L forward; sidelights 0.1 L forward at the sides | greybox, meets 84.02 and 84.03 |
| Sector edges | 2° cut-off outside; 5° inside, 50% at the limit | within 84.15's bounds |
| Daylight threshold | 1 × 10⁻³ lux above +2°, log-linear from 2 × 10⁻⁷ at −8° | estimate; the Rules give the night figure only |
| Point brightness and size | 0.25 at threshold to 1 at 100×; 2 to 6 px | rendering choice |
| Aid ranges without the chart's | W 4, R and G 3, others 2 nm | estimate; USCG lighted aids are typically 3 to 5 nm |
| Bridge green lights | 0.5 m under the deck edge | 118.65(a) says just below |
| Restricted visibility | under 1 nm | 118.55(a)'s figure; Rule 3(l) gives none |
| Making way | 0.2 kn through the water | estimate |
| Fog signal | 120 s start to start, 5 s blasts, 2 s gap, 350 Hz | within Rules 35, 32(c) and Annex III |
| Fog layer | 300 m deep; horizon path 5 km | greybox sky model |

## Tests

EditMode (`Slice/Tests/EditMode/`):

| Test | Checks |
|---|---|
| `NavigationLightRulesTests` | Rule 21's sectors and 84.15's edges, bearings, Rule 22's ranges, 84.14's intensities, the lights by status, the T- and K-class greybox layouts against Annex I |
| `SignalLightOpticsTests` | Allard's law against Annex I, the MOR's 5%, ranges shrinking with visibility, day and night thresholds, restricted visibility, edge inputs |
| `FogSignalScheduleTests` | Rule 35's interval and patterns, making way, signals only in restricted visibility, the same signals at any frame rate, and again from the recording alone |
| `ScenarioVisibilityTests` | The default, validation, the recording key (only when set, nothing else changed), replay, old recordings |
| `SkyClockTests` | The sun on the run's clock and not the wall clock, still while paused, the same sky and fog in a replay, fog only when asked for, the lights' and the palette's sun rules |

PlayMode (`Slice/Tests/PlayMode/`), in the slice scene:

| Test | Checks |
|---|---|
| `NightFogSceneTests` | Lights by night and none by day (vessel, bridges, aids, the chart geometry's keyword, the lenses marked); the sky and the aids on the run's clock, still while paused; a K-class's own lights; the drawn sidelights' colours from each bow (pixels); a light seen inside its fog range and not beyond (pixels); Rule 35 in fog and nothing in clear weather; fog only when asked for; the panel's palette by the sky, a hand choice holding until the light changes; no garbage per frame |
| `ChartShaderTests` | The import's chart shaders with the keyword off draw byte for byte what they drew before (against verbatim fixtures); on, they dim, fog, and leave a lens undimmed (pixels) |
| `NightFogEvidenceTests` | The captures for the crew and the frame cost (artifacts/night-fog/) |

E2E (`Tests/E2E/NightFogTripTests`): the second trip leg, DUMBO up-river under both bridges at night in 500 m fog, recorded and replayed through the scene's runner. Every replayed frame shows the same sky, fog, lights and whistle at the same step, and the fog signals are the same.
