# Ferry Audio

The runtime rig adds twin engine rooms, propellers, the held horn, the existing own-ship fog signal, nearby traffic fog signals, water, wind, gulls and contact sounds. `HarborBootstrap` and `SliceBootstrap` create it at runtime; scenes need no audio edits. All clips currently use this project's original procedural placeholders. They are audible illustrations, not calibrated recordings of a vessel.

## Design and boundaries

`FerryAudioRig` reads the same `IVesselTelemetrySource` / `IEnvironmentReadoutSource` that the panels read: the runner's per-engine `EngineSpeed`, `ShaftSpeed`, `Gear`, `Throttle`, water-relative speed, world wind and ground velocity. Its fixed-step observer only captures gear transitions into bounded queues, so a shift between drawn frames is not lost. It never writes controls, physics, recorder or replay data. Audio settings stay in the session; they are not recorded or persisted.

There is no AudioMixer. A code-side `AudioBus` exposes **Master**, **Engines**, **Horn & signals** and **Ambience** gains, plus mute. This keeps the asset generation reproducible and avoids committing an opaque hand-authored mixer. Every engine-room source has an `AudioLowPassFilter`. In `CameraRig.Mode.Wheelhouse`, engine gain is 0.32 and cutoff 850 Hz, wind is 0.18 and wash 0.55; outside/chase gains are 1 and cutoff 22 kHz. These are presentation estimates awaiting listening review.

The pure `EngineSoundMapping` uses SI speeds and the current vessel's idle/rated engine speeds and gear ratio. Complementary smoothstep idle/mid/high weights sum to one. Each layer's pitch rises monotonically with engine speed within 0.75–1.3. Throttle and shaft speed add gain and a low-frequency layer only in ahead/astern; shaft motion also drives the propeller layer. Engine-room mounts are estimates at x = ±0.25 beam, y = −0.4 m, z = −0.22 length from midships, following Unity's x starboard / y up / z forward vessel frame. They need crew confirmation. Nothing changes vessel parameters.

`HelmInputController.HornChanged` drives an 80 ms attack, seamless held loop and 150 ms release. A released horn can be retriggered without resetting its gain. The horn uses `VesselLightLayout.FromModel`'s masthead mount, with the existing greybox fallback. `FogSignal` keeps its recorder and exact Rule 35 schedule; only its clip/lifetime and looping playback change to use the same provider horn, including replacements shorter than one prolonged blast. Neither audio mute nor disabling the rig removes that pre-existing fog recorder.

In restricted visibility, up to **eight nearest moving AIS vessels within 2,000 m of the listener** sound the existing Rule 35 one/two prolonged pattern. The audible range is a bounded presentation estimate, not a certified acoustic or navigation range; a linear attenuation curve reaches zero at the boundary. The phase is a fixed integer hash of MMSI over the existing 120 s interval, independent of frame rate and RNG. The pattern is latched for a selected vessel's current cycle. AIS supplies ground speed rather than water-relative speed or navigational status, so the existing 0.2 kn making-way threshold applied to AIS ground speed is explicitly an audio estimate. Static/moored candidates remain silent; no anchor/bell status is invented. Traffic uses the existing read-only `TrafficView.Hulls` snapshots and globe-local transforms, so it adds no traffic update/model-selection hook, geodesy, or ECEF-to-float cast.

The source budget is fixed: 12 engine/propeller/clutch sources, two own horn/fog sources, five ambience sources and eight traffic signals = **27**, below the hard cap of **32**. One-shots use dedicated sources, never `PlayOneShot` (which can overlay unbounded voices). Each engine's clutch queue holds 64 changes and plays them in order on its one source. Pathological sustained overflow is exposed as `DroppedClutchEvents`; it never adds a voice or blocks physics. The project's existing 32 real voices need no settings change.

Wash follows absolute speed through water; wind follows apparent wind (the environment's wind minus the vessel's ground velocity). Gulls use a private seeded xorshift generator, every estimated 8–28 s only within 500 m of a harbor landing. A one-step contact is detected through the runner's existing contact latch: fender squeaks are limited to one per 0.75 s and barge creaks to one per 3 s, with gain proportional to the square root of force / 100 kN. The contact source at the starboard hull side is a positional proxy because the panel latch has no contact point. These timings, gains, sounds and positions are estimates, not measured acoustic evidence. Pausing freezes/stops progression and pauses sources; ending a run clears pending one-shots.

## Clip slots

`clips.json` is version 1. Each slot carries its optional `file`, `loop`, `loopStartSample`, `loopEndSample`, `baseGain`, `source`, `date` and `edits`. File paths are relative to `Audio/Clips/`. Missing WAVs deliberately use the generated placeholder. Unknown/duplicate slots, unsafe paths, non-finite gains, missing provenance and invalid loop points are refused.

| ID / default WAV filename | Loop | Base gain | Purpose |
|---|---|---:|---|
| `engine-idle` / `engine-idle.wav` | Yes | 0.55 | Idle layer, shared by both engines |
| `engine-mid` / `engine-mid.wav` | Yes | 0.55 | Mid-speed layer |
| `engine-high` / `engine-high.wav` | Yes | 0.55 | High-speed layer |
| `engine-low` / `engine-low.wav` | Yes | 0.35 | Loaded low end |
| `propeller` / `propeller.wav` | Yes | 0.45 | Shaft/prop wash layer |
| `clutch-engage` / `clutch-engage.wav` | No | 0.45 | Ahead/astern gear entry |
| `clutch-disengage` / `clutch-disengage.wav` | No | 0.35 | Neutral/shifting gear entry |
| `horn` / `horn.wav` | Yes | 0.80 | Shared held horn, own fog and traffic signals |
| `water-wash` / `water-wash.wav` | Yes | 0.35 | Speed-relative water |
| `wind` / `wind.wav` | Yes | 0.25 | Apparent wind |
| `gull` / `gull.wav` | No | 0.25 | Landing gull |
| `fender` / `fender.wav` | No | 0.35 | Contact squeak |
| `barge-creak` / `barge-creak.wav` | No | 0.30 | Contact creak |

## Drop in a real WAV

1. Put only the approved, finished WAV under `unity/Assets/FerrySim/Audio/Clips/`, using the table's filename or update that slot's `file`. Do not copy private raw reference media, personal information or third-party sound into this public repository.
2. For a loop, set `loopStartSample` to the first interleaved **sample frame** and `loopEndSample` to the exclusive end frame in the original WAV. `0 / 0` means the full file; a specified end must exceed start and remain inside the file. Sample frames are shared across channels, not individual channel samples. A held horn needs a seamless sustain region; attack and release are applied by the rig. Short one-shots should include their finished fades.
3. Update `baseGain`, `source`, `date` (`YYYY-MM-DD`) and `edits` with the recording's reviewed provenance. Crew evidence uses only a role such as "project crew reviewer" and a date, never a person's name. Record trimming, denoising, normalization and loop work honestly.
4. Let Unity import the file, or choose **FerrySim > Audio > Rebuild Clip Catalog**. No C# change is needed. The same rebuild runs before every player build.

`AudioClipPostprocessor` forces mono spatial audio, preserves sample rate and uses Vorbis quality 0.7. Loops at least 10 s long stream without preload; shorter loops and one-shots decompress on load. `WaveLoop` creates a byte-deterministic WAV of a requested loop region under `Audio/Resources/FerrySimAudio/Loops/`, copying PCM/IEEE-float sample frames without normalization or resampling. The resource catalog references the original or cropped imported clip, so a shipping player includes replacements even though the scene never references them. Commit replacement WAVs, their `.meta` files, the updated manifest/catalog, and any generated loop WAVs together after review. No new package or download is involved.

`AudioClipProvider` resolves the catalog and caches all slots once per process. `World` references the isolated `FerrySim.Audio.Clips` assembly, which itself references no FerrySim assembly. The main audio assembly references World/Input/Sim/Traffic/Core/Clips, never Slice, Harbor or Vessels. Clip creation, manifest parsing, file I/O and allocation happen during composition/import, outside per-frame/per-step loops.

## Controls and measurements

**V** toggles all audio mute on the separate `Audio` action map, including while the helm map is held by a picker, except while a text field has focus. Typing a landing name must not change mute. The free key is checked against every existing action binding and the camera/bow/picker reservations. Per-group/master gains are exposed through `FerryAudioRig.Bus` for integration; no optional volume keys are assigned.

Pass **`-ferrysim-audio-volume 0`** to Unity or a player for silent measurements. Accepted values are finite invariant-culture numbers in `[0,1]`; the ordinary default is 1, and invalid/missing values are errors. Existing `-runTests` and `-ferrysim-smoke` automation modes default to 0 for speaker safety, with an explicit volume argument taking precedence. Master zero and mute leave the complete graph, loops, scheduling, telemetry mapping and step observer running; only source gains become zero. `GraphEnabled = false` is a separate test/integration switch that stops render audio. Unity's native mixer can virtualize inaudible sources, so silent measurements prove the full managed graph's cost, not the cost of audible native output on every device.

EditMode tests cover mapping continuity/pitch/load, clutch queues, horn envelope, traffic phase/voice budget, listening position, gain controls, launch parsing, manifest/fallback, generated WAV import/loop cropping, seeded gulls, contact limits and the keyboard map. PlayMode tests load `Harbor.unity` additively and exercise mounts, held horn, original fog steps, calibrated `GC.Alloc` with the one-allocation positive control, and recording parity at full/zero/muted/disabled audio. Production artifact filenames contain wall time/run numbers; the parity test compares complete stable-name `RunRecorder` output bytes and control trace bytes as well as the actual runner's rows and state digest, without normalizing simulation data. XML/logs and recordings stay under `artifacts/ferry-audio/`.

Shipping-player measurements and the whole-repository gate are recorded in `artifacts/ferry-audio/report.md`. They are separate from listening/physical-speaker acceptance, which remains unverified until the real clips and crew review arrive. VHF is outside this wave.
