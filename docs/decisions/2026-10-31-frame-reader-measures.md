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

## What "equal" is held to (slice 1, 2026-10-31)

Found while building `LoudnessMeter`, with no new question for the owner:

- **ebur128's two kernels.** On x86-64 with AVX, ebur128 filters two or more channels with
  `ff_ebur128_filter_channels_avx`, which adds one term of the biquad in another order than the C kernel
  (`b1·x + (y2 − a1·y0)` against `(b1·x + y2) − a1·y0`). The C kernel serves mono, and every channel count on ARM
  (FFmpeg builds with `-std=c11`, so GCC doesn't contract to FMA there). The meter follows x86-64, the repository's
  reference: the C order for mono, the AVX order from two channels. The difference stays in the last bits of the block
  powers. The histogram hides it unless a block's loudness sits within about 1e-13 LU of a bin's edge, so no test can
  tell the two orders apart, and the mutation list says so.
- **The bins' energies depend on the host.** ebur128 computes them with `ff_exp10`, the exp2 of whatever libm the
  process loads, and glibc's builds differ in the last bit. The generic x86-64 build (Ubuntu 24.04, glibc 2.39 to
  2.44 alike) is one ulp off correct rounding in 11 of the 8001 bins. Ubuntu 26.04's runners, presumably on its
  x86-64-v3 build compiled with FMA, round the first of those correctly (CI's first run of #655 failed on it, bin 67:
  1.3680437930569386e-07 against 1.3680437930569383e-07). .NET's `Math.Pow(2, x)` is off in 7. No table matches
  every host, so the meter takes 2^x correctly rounded (double-double, the same on every platform, wasm included) and
  copies no libm's errors. Tests hold 13 hard bins to mpmath's values and every bin within one ulp of the host's libm.
  So I and LRA are ebur128's doubles when the bins a programme fills have the same energy on its host. A different
  energy in a filled bin moves I by about an ulp. None of the clips here, on this machine or on CI's runner, fill such
  a bin: an even rarer case than the AVX kernel's.
- **What the tests hold.** Integrated loudness, range, its ends and sample peak are compared with
  `AudioFilter(ebur128)` with `Assert.Equal` on doubles, on 14 generated clips (mono to 7.1, 32 to 96 kHz, a clip
  shorter than a block, a partial last block, silence). True peak is held within 0.05 dB of tools/true_peak_reference.py's
  ideal reconstruction, and within 0.1 dB of ebur128 where the clip has no energy near Nyquist.
