# Ferry Audio

The runtime rig adds twin engine rooms, propellers, the held horn, fog signals, water, wind, gulls, contact sounds and a wheelhouse alarm sounder. `HarborBootstrap` and `SliceBootstrap` create it at runtime. Eight slots use processed crew recordings: engine-idle, engine-high, engine-low, propeller, clutch-engage, clutch-disengage, water-wash and alarm-steering. Engine-mid, horn, wind, gull, fender, barge-creak and alarm-general use procedural placeholders. The recordings are presentation textures awaiting listening review. The idle and clutch event labels remain provisional.

## Design and boundaries

`FerryAudioRig` reads the same `IVesselTelemetrySource` / `IEnvironmentReadoutSource` that the panels read: the runner's per-engine `EngineSpeed`, `ShaftSpeed`, `Gear`, `Throttle`, water-relative speed, world wind and ground velocity. Its fixed-step observer captures gear transitions into bounded queues so it can play shifts that occur between drawn frames. The rig is a read-only consumer of controls, physics, recordings and replays. The settings integration owns persistence.

The code-side `AudioBus` exposes **Master**, **Engines**, **Horn & signals**, **Ambience** and **Alarms** gains, plus mute. Every engine-room source has an `AudioLowPassFilter`. In `CameraRig.Mode.Wheelhouse`, engine gain is 0.32 and cutoff 850 Hz, wind is 0.18 and wash 0.55. Outside and chase gains are 1 with a 22 kHz cutoff. These are presentation estimates awaiting listening review.

The sounder polls C1's `IInstrumentReadout` event log with its own cursor. On log overflow or a new run it resynchronises from the current `Phase` and `SounderOn` flags. A sounding steering alarm takes priority over other groups. The clip provides the cadence. Acknowledge, silence, condition clear, pause and run end stop the voice. Ring-back and sources without instrument data are silent. The source sits at the estimated wheelhouse eye position with gain 1.0 inside and 0.25 outside, in addition to spatial attenuation. It bypasses the engine low-pass filter. The mount and levels are presentation estimates.

The pure `EngineSoundMapping` uses SI speeds and the current vessel's idle/rated engine speeds and gear ratio. Complementary smoothstep idle/mid/high weights sum to one. Each layer's pitch rises monotonically with engine speed within 0.75–1.3. Throttle and shaft speed add gain and a low-frequency layer only in ahead/astern; shaft motion also drives the propeller layer. Engine-room mounts are estimates at x = ±0.25 beam, y = −0.4 m, z = −0.22 length from midships, following Unity's x starboard / y up / z forward vessel frame. They need crew confirmation. Nothing changes vessel parameters.

`HelmInputController.HornChanged` drives an 80 ms attack, seamless held loop and 150 ms release. A released horn can be retriggered without resetting its gain. The horn uses `VesselLightLayout.FromModel`'s masthead mount, with the existing greybox fallback. `FogSignal` keeps its recorder and exact Rule 35 schedule; only its clip/lifetime and looping playback change to use the same provider horn, including replacements shorter than one prolonged blast. Neither audio mute nor disabling the rig removes that pre-existing fog recorder.

In restricted visibility, up to **eight nearest moving AIS vessels within 2,000 m of the listener** sound the existing Rule 35 one/two prolonged pattern. The audible range is a bounded presentation estimate, not a certified acoustic or navigation range; a linear attenuation curve reaches zero at the boundary. The phase is a fixed integer hash of MMSI over the existing 120 s interval, independent of frame rate and RNG. The pattern is latched for a selected vessel's current cycle. AIS supplies ground speed rather than water-relative speed or navigational status, so the existing 0.2 kn making-way threshold applied to AIS ground speed is explicitly an audio estimate. Static/moored candidates remain silent; no anchor/bell status is invented. Traffic uses the existing read-only `TrafficView.Hulls` snapshots and globe-local transforms, so it adds no traffic update/model-selection hook, geodesy, or ECEF-to-float cast.

The fixed budget is **28** sources against a cap of **32**: 12 engine, propeller and clutch sources, two own horn and fog sources, five ambience sources, eight traffic signals and one alarm sounder. One-shots use dedicated sources. Each engine's clutch queue holds 64 changes and plays them in order on its source. `DroppedClutchEvents` reports overflow.

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
| `alarm-steering` / `alarm-steering.wav` | Yes | 1.00 | Prepared steering panel tone |
| `alarm-general` / `alarm-general.wav` | Yes | 1.00 | Procedural placeholder for other alarm groups |

## Prepared crew recordings

The preparation tool, sample-frame recipes and privacy rules are documented in [`tools/audio-prep/README.md`](../../../../tools/audio-prep/README.md). Preparation requires the private C9 manifest and verifies consent, screening, withdrawal and the pinned sheet hash before decoding. Source patterns omit private vessel identifiers. Finished clips use role-only credit, `crew recording (private), 2026-11`.

Engine and clutch targets are −26 LUFS, supporting low and propeller layers −30 LUFS, water −32 LUFS and steering alarm −24 LUFS. These are presentation targets measured with ffmpeg ebur128 after editing. Loops use equal-power overlap and explicit full-file sample-frame loop points. One-shots have 30 ms fades. The exhaust recording supplies the loaded low-end slot and the full-ahead engine recording supplies the propeller proxy.

The steering recording has a continuous tone near 2197 Hz (about 0.455 ms per carrier cycle) and a secondary component near 2380 Hz. No slower beep period is identifiable. The 1.989 s loop uses a stable window and a waveform-aligned overlap. Confirm by ear when the panel sounds and whether it ever pulses (SCR-05 and SCR-08).

| Request | Recording still needed | Existing slot |
|---|---|---|
| SND-01 | Stable mid-rpm engine | `engine-mid` |
| SND-01 | Isolated propeller or wash | `propeller` or `water-wash` |
| SND-01 | Horn inside and on deck | `horn` (shared clip) |
| SND-01 | Wind and gulls | `wind` and `gull` |
| SND-01 | Fender contact and barge creak | `fender` and `barge-creak` |
| SCR-08 | Steering failure and its trigger | `alarm-steering` |
| SCR-08 | Shallow-water, grounding and contact alarm recordings | `alarm-general` (shared clip) |

## Drop in a real WAV

For a new approved recording, replace the slot's placeholder entry in `tools/audio-prep/recipes.json` with the pinned source pattern and trim recipe, then run the tool with `--only` for that slot. Set the filename in `clips.json` if it differs from the default in the table.

1. Put only the approved, finished WAV under `unity/Assets/FerrySim/Audio/Clips/`, using the table's filename or update that slot's `file`. Do not copy private raw reference media, personal information or third-party sound into this public repository.
2. For a loop, set `loopStartSample` to the first interleaved **sample frame** and `loopEndSample` to the exclusive end frame in the original WAV. `0 / 0` means the full file; a specified end must exceed start and remain inside the file. Sample frames are shared across channels, not individual channel samples. A held horn needs a seamless sustain region; attack and release are applied by the rig. Short one-shots should include their finished fades.
3. Update `baseGain`, `source`, `date` (`YYYY-MM-DD`) and `edits` with the recording's reviewed provenance. Crew evidence uses only a role such as "project crew reviewer" and a date, never a person's name. Record trimming, denoising, normalization and loop work honestly.
4. Let Unity import the file, or choose **FerrySim > Audio > Rebuild Clip Catalog**. No C# change is needed. The same rebuild runs before every player build.

`AudioClipPostprocessor` forces mono spatial audio, preserves sample rate and uses Vorbis quality 0.7. Loops at least 10 s long stream without preload; shorter loops and one-shots decompress on load. `WaveLoop` creates a byte-deterministic WAV of a requested loop region under `Audio/Resources/FerrySimAudio/Loops/`, copying PCM/IEEE-float sample frames without normalization or resampling. The resource catalog references the original or cropped imported clip, so a shipping player includes replacements even though the scene never references them. Commit replacement WAVs, their `.meta` files, the updated manifest/catalog, and any generated loop WAVs together after review. No new package or download is involved.

`AudioClipProvider` resolves the catalog and caches all slots once per process. `World` references the isolated `FerrySim.Audio.Clips` assembly, which itself references no FerrySim assembly. The main audio assembly references World/Input/Sim/Traffic/Core/Clips, never Slice, Harbor or Vessels. Clip creation, manifest parsing, file I/O and allocation happen during composition/import, outside per-frame/per-step loops.

## Controls and measurements

**V** is the default `Audio/Mute` binding. The audio map remains live when the picker holds the helm, with mute suppressed while a text field has focus. The rig registers its private action asset with `InputBindingService` so rebinds apply immediately, and unregisters it on destruction.

Settings integration applies the saved C5 values to `FerryAudioRig.Bus.Master`, `FerryAudioRig.Bus.Muted` and `FerryAudioRig.Bus.Alarms`. Subscribe to `FerryAudioRig.MuteChangedFromKey` (`Action<bool>`) to save the new mute state after a key press. The alarm gain scales only the sounder and follows Master and Mute.

Pass **`-ferrysim-audio-volume 0`** to Unity or a player for silent measurements. Accepted values are finite invariant-culture numbers in `[0,1]`; the ordinary default is 1, and invalid/missing values are errors. Existing `-runTests` and `-ferrysim-smoke` automation modes default to 0 for speaker safety, with an explicit volume argument taking precedence. Master zero and mute leave the complete graph, loops, scheduling, telemetry mapping and step observer running; only source gains become zero. `GraphEnabled = false` is a separate test/integration switch that stops render audio. Unity's native mixer can virtualize inaudible sources, so silent measurements prove the full managed graph's cost, not the cost of audible native output on every device.

EditMode tests cover the alarm sequence, cursor resynchronisation, gain, voice budget, clip import and a live `Audio/Mute` rebind, alongside the existing audio logic tests. PlayMode tests load `Harbor.unity` additively. A fixture raises a C1 shallow-water alarm to check sounder playback during calibrated `GC.Alloc` measurement and recording parity at full, zero, muted and disabled audio. The parity test compares complete stable-name `RunRecorder` output bytes and control trace bytes as well as the runner's rows and state digest. Evidence and the owner listening checklist are under `artifacts/sound-gaps/`.

Original shipping-player measurements and the whole-repository gate are recorded in `artifacts/ferry-audio/report.md`. Replacement-specific verification and the unresolved shipping-size comparison are recorded in `artifacts/ferry-audio-real/report.md`. Automated checks are separate from listening/physical-speaker acceptance, which remains unverified until the processed clips receive crew listening review. VHF is outside this wave.
