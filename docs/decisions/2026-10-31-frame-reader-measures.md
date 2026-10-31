# Managed measures in FrameReader (2026-10-31)

Issue #646: shotdetector-web's managed measures move into FrameReader, so the web's QC report (shotdetector-web#94)
and delivery QC (#636) share one implementation. The scoping note
[docs/ideas/frame-reader-measures.md](../ideas/frame-reader-measures.md) has the reading and the numbers behind these
questions. All five were put to the owner with the recommended option first.

## Questions, choices and answers

1. **How closely the managed loudness meter must match FrameReader's `Loudness.Measure` (FFmpeg's ebur128).**
   Measured on 16 clips fed identical samples (the question said 14; two were added while it was asked): integrated
   loudness and LRA differ by up to 0.015 LU, because ebur128 counts each block in a 0.01 LU histogram bin, truncated.
   Sample peak is equal. True peak differs by up to 0.8 dB on high-frequency content: ebur128's 192 kHz swresample
   overshoots, while the managed BS.1770-4 Annex 2 meter stays within 0.03 dB of an ideal 32x reconstruction.
   - **I and LRA exact (reproduce the histogram), sample peak exact, true peak stays Annex 2**, checked within 0.1 dB
     on programme material, the high-frequency difference documented. *Chosen.*
   - Everything within tolerances (0.1 LU; true peak within 1 dB).
   - Everything exact, porting swresample's resampler for true peak (LGPL-derived, its own assembly; inherits the
     overshoot).
2. **The public API next to `Loudness.Measure(AudioReader)`.**
   - **Streaming meters**: `LoudnessMeter(rate, channels).Add(interleaved)` / `Finish()` giving `LoudnessResult`
     (-70 for silence) plus per-channel peaks, and `SilenceFinder` the same way, like `WaveformBuilder` and
     `ReferenceToneFinder`. *Chosen.*
   - `Loudness.Measure(ReadOnlySpan<float>, rate, channels)` over all the samples at once.
   - An option on `Measure(AudioReader)` (still FFmpeg's decode, so not for the browser).
3. **How FrameReader's picture meter takes frames** (`PictureMeter` reads ShotDetector's `YuvFrame`, which FrameReader
   can't reference).
   - **Planes with strides and pts, plus a `PictureFormat`** (size, range, rotation, time base). Additive: a minor
     FrameReader release. *Chosen.*
   - Move `YuvFrame`/`YuvVideo` into FrameReader (breaks ShotDetector's API, a major release).
   - Keep the picture meter out of FrameReader, in ShotDetector.
4. **Where the checks against FFmpeg live.**
   - **`tests/FrameReader.Tests`**: loudness and silences against `AudioFilter` in-process, and picture against the
     ffmpeg 8.1 executable CI already has. No native build change. The web keeps only its decoder checks. *Chosen.*
   - The same, plus signalstats, blackdetect and cropdetect in our FFmpeg build (a FrameReader.Native release).
   - Left in shotdetector-web's NativeCheck.
5. **Which package.**
   - **The FrameReader package itself**, like `WaveformBuilder`: a minor release, the split count recorded as usual,
     the split question not reopened. *Chosen.*
   - A package of its own (`FrameReader.Measures`), which would meet a split-question condition
     ([2026-10-31-split-trigger-criteria.md](2026-10-31-split-trigger-criteria.md)).

All five: the owner, 2026-10-31.

## Why

Exactness is the repository's rule wherever it costs only arithmetic. ebur128's histogram is arithmetic, so I and LRA
can match FFmpeg to the digit. True peak is the one place where matching FFmpeg would mean copying an LGPL resampler
to reproduce an overestimate, so it follows the standard and the difference is written down. The rest keeps the move
additive: one FrameReader minor release per slice set, no ShotDetector break, no native change.
