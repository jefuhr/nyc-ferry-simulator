# Audio preparation

`audio_prep.py` uses Python 3.11+ and its standard library and the already installed `ffmpeg` CLI. No package installation, network access, raw-media copy or transcription is involved. Only the consented `sheets/audio` directory is used. The source recordings contain no voices according to the owner; automated preparation does not independently establish that fact.

From the repository root:

```sh
python3 -m unittest discover -s tools/audio-prep/tests -v
python3 tools/audio-prep/audio_prep.py \
  --sheets "$HOME/FerrySim-reference/v04/sheets" \
  --report artifacts/ferry-audio-real/preparation.json
```

Create the report directory first if it does not exist. `recipes.json` covers every runtime slot: seven real replacements and six explicit procedural fallbacks. Running the command again rebuilds every real replacement and updates its manifest provenance. Placeholder entries preserve the existing manifest and procedural generation.

## Recipe contract and privacy

Sources are sheet-relative filename **patterns**, not literal filenames: private vessel identifiers in original filenames must not enter version control. Every active source requires exactly one match under `audio/` and an exact SHA-256 match. Ambiguous patterns, changed contents, path traversal and symlinks outside the approved directory are rejected. Error reporting never persists ffmpeg's private input filenames. Public artifacts use neutral slot names and role-only credit.

`start_frame` and `end_frame` are frames at `source_rate`, with the end exclusive. They are applied before resampling and channel conversion. Seconds can be recovered exactly by dividing frames by `source_rate`. `crossfade_frames`, `fade_frames`, and manifest loop points use the output `sample_rate`. Stereo sources use the arithmetic mean of left and right; all outputs are mono PCM16 at 22050 Hz, matching the existing procedural slots. Resampling uses ffmpeg's SWR with dithering disabled. Optional two-pole high/low-pass filters are explicit in each recipe.

For loops, the final crossfade overlaps the last N frames with the first N frames using cosine/sine equal-power weights. Output length is the resampled trim length minus N frames. The circular result is rotated to the smallest adjacent-sample difference inside that overlap; this preserves the complete crossfade and introduces no fade-to-silence notch. Manifest loop points are `0` through the finished frame count, end exclusive. The seam must be below **0.002 full scale** after PCM16 quantization. This numerical check is not a listening-quality claim.

One-shots use symmetric sine-squared fades and zero-valued first/last samples. Their manifest loop points remain `0/0`. All trims must be at least 400 ms so EBU R128 can produce an integrated measurement.

## Loudness and safety limits

The following are presentation mix targets, not measured real-world sound pressure levels. Existing runtime base gains and engine/load mapping are unchanged.

| Slot group | Target | Reason |
|---|---:|---|
| Idle and high engine, clutch transitions | −26 LUFS | Consistent primary engine bed, with headroom for twin engines and other sources |
| Loaded low end and propeller texture | −30 LUFS | Supporting layers should remain below the primary engine bed |
| Water wash | −32 LUFS | Background bed with room for existing speed gain |

Measurement uses ffmpeg `ebur128=peak=true`, after filtering, crossfade and fades. Constant gain is selected from integrated LUFS, and the finished quantized WAV is measured again. Outputs outside ±0.5 LUFS or with a sample peak above −1 dBFS are rejected; there is no hidden limiter, dynamic compression or hard clipping. The peak ceiling is a sample-peak check, not a guaranteed codec/true-peak ceiling. No denoising or acoustic source separation is claimed. WAVs contain only a PCM header and samples, with no source metadata.

The report includes final frame count, SHA-256, byte size, LUFS, gain, peak and seam, plus ffmpeg version. Byte identity is tested for repeated runs using the same toolchain (validated with ffmpeg 9.0.2); different ffmpeg versions/architectures require a fresh comparison. Batch processing prepares and validates all replacements before publishing files and provenance.

## Selection limits and owner review

- Idle and clutch crops are candidates from the labelled clutch/reverse/idle recording. A per-second energy survey located stable material and load transitions; it cannot identify gearbox events or engine rpm. Confirm the specific events by listening before accepting the mix.
- The high layer uses a labelled approximately 1900 rpm underway window. No stable mid-rpm recording is identified, so `engine-mid` remains procedural.
- Full-ahead exhaust occupies the existing loaded low-end slot after a 30–240 Hz band limit. A separate voice would duplicate the load response and require runtime wiring. Its use is a mix decision, not a measured isolated engine component.
- The propeller slot uses a band-limited full-ahead engine recording as a load/shaft texture proxy. It is **not** an isolated propeller recording.
- Water comes from 64–88 s, entirely within the second take, beyond the join around 38 s.
- The entire 1945 rpm recording is excluded because it contains the documented extinguisher-latch rattle. The 1897 and 2023 rpm references are unused alternatives.
- Steering alarm is deferred: there is no runtime alarm event or slot to consume it. No orphan slot or new alarm behavior was introduced.
- Horn, wind, gull, fender and barge-creak remain procedural for lack of suitable sources. The original `ProceduralClips` generator rebuilds these fallback sounds in memory; this tool does not manufacture WAVs for them.

Listening review must include the idle and both clutch timestamps, engine-low exhaust texture, propeller attribution, repeated loop joins, transitions through the synthetic mid layer, wheelhouse/chase balance, paired-engine phasing, mute/pause/resume, horn audibility and the water loop. Automation proves signal/import properties, not perceptual acceptance.
