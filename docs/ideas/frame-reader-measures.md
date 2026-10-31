# Managed measures in FrameReader: scoping note (issue #646)

Status: **scoping only**, 2026-10-31. No feature code. The owner chose (2026-10-31, in shotdetector-web's idea
review) to move shotdetector-web's managed measures into FrameReader. Then the web page's QC report
(bisforboman/shotdetector-web#94) and this repository's delivery QC (#636, [delivery-qc.md](delivery-qc.md)) share one
implementation, checked against FFmpeg once. This note covers what moves, the API, how closely the managed meters must
match FFmpeg (with numbers from running both), where the checks live, what it means for FrameReader's versions and its
split count, and the slices. The owner answered the five design questions on 2026-10-31:
[2026-10-31-frame-reader-measures.md](../decisions/2026-10-31-frame-reader-measures.md).

Read for this: shotdetector-web at `main` (2026-10-31), `src/ShotDetectorWeb.Core/PictureMeasures.cs` (915 lines),
`Loudness.cs` (317), `Mp4Audio.cs` (331), `tools/NativeCheck/PictureCheck.cs` and `AudioCheck.cs`; this repository's
`src/FrameReader/Loudness.cs` and `AudioFilter.cs` (#642); FFmpeg's `libavfilter/f_ebur128.c` and
`x86/f_ebur128_init.c` at `release/8.1`.

## 1. What moves

| shotdetector-web today | Moves to FrameReader as | Stays in the web |
|---|---|---|
| `LoudnessMeter` + `TruePeakMeter` (`Loudness.cs`): BS.1770-4 K-weighting for any rate, 400 ms and 3 s blocks every 100 ms, gating, LRA, true peak by Annex 2 oversampling (4x below 96 kHz, 2x below 192 kHz) | `LoudnessMeter`, giving the existing `LoudnessResult`. Integrated loudness and LRA change to FFmpeg's histogram (section 3) | its `Loudness` record (replaced by `LoudnessResult`) |
| `Mp4Audio`'s per-channel sample peak and full-scale count (in its `Measurer`) | part of `LoudnessMeter`'s result, per channel | `Mp4Audio` itself: MP4 demuxing and AAC decoding are ManagedCodecs', not FrameReader's |
| `SilenceFinder` (`Mp4Audio.cs`): silencedetect's default mode | `SilenceFinder` | `AudioOptions`, `AudioMeasures`, `AudioTrack` (the web's report shape) |
| `PictureMeter` (`PictureMeasures.cs`): one `Vector128` pass per frame, giving signalstats' YMIN, YAVG, YMAX and BRNG, blackdetect's black pixels, cropdetect's lines, and colour bars | `PictureMeter`, reading planes rather than `YuvFrame` (section 2) | the picture type (`ManagedCodecs.PictureType`): what the decoder says, not a measure |
| `PictureMeasures`: spans (`BlackSpans`, `BarsSpans`), crop (`Crop`, `FrameCrop`), `Take`, `Join`, `ToBytes`/`FromBytes` (ranges measured in parallel, joined byte for byte) | `PictureMeasures`, all of it: ShotDetector's chunked `Detect` splits a file the same way | `Types()`, `TypeChar` (with the picture type); `PictureSummary` (the page's card) |
| `BarColours`: the seven bars by definition (SMPTE/EBU, 75% and 100%, BT.601 and BT.709) | as it is (internal) | |

Reference tone and the waveform are FrameReader's already (`ReferenceToneFinder`, `WaveformBuilder`; the web calls
them). With this move, everything the web measures outside its decoders comes from FrameReader.

All of it is managed C# with no FFmpeg (`Vector128` is wasm SIMD under AOT), so it runs on browser-wasm. It goes in
the FrameReader package itself, like `WaveformBuilder` (the owner's choice, question 5). Trimming keeps FFmpeg.AutoGen
out of an app that uses only these types; ci.yml's `wasm-trim` job already checks that for the frame-source path.

## 2. The public API

Next to `Loudness.Measure(AudioReader)`, which runs FFmpeg's `ebur128` in-process, there are **streaming meters**:
samples are pushed in and figures read out at the end. This is how `WaveformBuilder` and `ReferenceToneFinder` work,
and it is what the browser needs: the web decodes a packet, folds it into each meter and drops it (question 2).

```csharp
namespace FrameReader;

// Audio. Samples interleaved, as AudioChunk.Samples and WaveformBuilder.Add take them.
public sealed class LoudnessMeter
{
    public LoudnessMeter(int sampleRate, int channels, ChannelWeights? weights = null);
    public void Add(ReadOnlySpan<float> interleaved);
    public LoudnessMeasures Finish();                    // once; Add after it throws
}

public sealed record LoudnessMeasures(
    LoudnessResult Loudness,                             // the record Loudness.Measure gives: -70 for silence
    IReadOnlyList<ChannelPeaks> Channels);

public readonly record struct ChannelPeaks(double SamplePeak, double TruePeak, long FullScale);

public sealed class SilenceFinder
{
    public SilenceFinder(int sampleRate, int channels, double level = -60, TimeSpan? duration = null);
    public void Add(ReadOnlySpan<float> interleaved);
    public IReadOnlyList<SilenceSpan> Finish();          // in samples from the first; the caller adds its start
}

public readonly record struct SilenceSpan(long StartSample, long EndSample);

// Picture. 8-bit 4:2:0 planes, the format the web's decoders and ShotDetector's YuvFrame give.
public sealed record PictureFormat(int Width, int Height, bool FullRange, Rational TimeBase, int Rotation = 0);

public sealed class PictureMeter
{
    public PictureMeter(PictureFormat format);
    public PictureMeter(PictureFormat format, int firstFrame, int endFrame);   // a range, as the web splits a run
    public void Add(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, int uStride,
                    ReadOnlySpan<byte> v, int vStride, long pts);
    public PictureMeasures Measures { get; }
}

public sealed class PictureMeasures { /* the web's members, less the picture type (section 1) */ }
```

Points that shaped it:

- **One result record for both loudness meters.** `LoudnessResult` already holds I, LRA, its low and high ends,
  sample peak and true peak. The managed meter fills it the same way: -70 LUFS when nothing passes the absolute gate
  (the web's meter returns null there; ebur128 starts at -70 and stays there), and negative infinity for peaks in
  digital silence. A caller that switches meters doesn't change code. The per-channel peaks and the full-scale count
  come on top, because the web's sound panel shows them. FFmpeg's metadata has per-channel peaks as well, so
  `Loudness.Measure` could give them later.
- **The names.** The web's `record Loudness` collides with FrameReader's `static class Loudness`: the comparison tool
  for section 3 hit CS0104 at once. FrameReader keeps `Loudness` (shipped in #642), and the web drops its record.
  Meter classes are named `...Meter` (`LoudnessMeter`, `PictureMeter`), finders `...Finder` (`ReferenceToneFinder`,
  `SilenceFinder`).
- **Channel weights.** BS.1770-4 weights the surround channels 1.41 and the LFE 0. ebur128 decides by the layout's
  channel ids: 0 for `LOW_FREQUENCY` and `LOW_FREQUENCY_2`, 1.41 for the side and back channels (`BACK_MASK`), and 1
  for the others. The web's meter infers this from ManagedCodecs' AAC order (the LFE fourth in 5.1 and 7.1). A
  managed meter sees only interleaved floats, so its default is FFmpeg's default layout for the channel count. That is
  what `AudioReader` gives a stream without a layout, and the web's AAC order matches it for 5.1 and 7.1.
  `ChannelWeights` lets a caller with another layout say so; `AudioReader` could later expose its layout to fill it.
- **Planes, not `YuvFrame`** (question 3). FrameReader can't reference ShotDetector, and moving `YuvFrame` here would
  break ShotDetector's API. The meter takes spans and strides. A ShotDetector caller hands over
  `frame.Y.Span, frame.YStride, ...`, and the web hands over ManagedCodecs' planes. `PictureFormat` holds what the
  meter needs from `YuvVideo` (size, range, rotation, time base). Being additive, it is a FrameReader minor release
  with no ShotDetector change.
- **Times.** `SilenceFinder` reports samples, not times, so the caller puts them on its own timeline. The web adds its
  edit-list start, and #636 uses the stream's start. The picture spans are in pts and frames, as today.
- **Not in scope:** 10-bit and 4:2:2 planes (delivery masters, [delivery-qc.md](delivery-qc.md) section 3: a later
  widening), freezedetect, and an FFmpeg-backed picture meter. The native build has no video analysis filters
  (question 4 kept it that way).

## 3. Exact or within a tolerance: numbers from running both

### How it was measured

A throwaway console tool, which isn't committed, measured each clip both ways. One way was FrameReader's
`Loudness.Measure` (our FFmpeg 8.1 build, the `FrameReader.Native.linux-x64` 2.17.1 package, `ebur128=peak=true+sample`).
The other was the web's `LoudnessMeter`, copied unchanged and fed the same decoded samples from an `AudioReader`. The
clips are float WAVs, so both meters see identical samples and every difference comes from the meters. They were 11
generated with ffmpeg's `sine`, `aevalsrc` and `anoisesrc` (32, 44.1, 48 and 96 kHz; mono, stereo and 5.1; silence; a
0.35 s clip shorter than one block; a 3.45 s clip with a partial last block; sines near Nyquist with abrupt and faded
edges; white noise) plus Sintel's trailer (AAC decoded to float once). The reference column is each clip resampled 32
times with a long Kaiser window (`scipy.signal.resample_poly`, beta 12), an ideal reconstruction to judge the two true
peaks by.

| Clip | I FFmpeg | I managed | ΔI | LRA FFmpeg | LRA managed | ΔLRA | TP FFmpeg | TP managed | TP reference | Sample peak |
|---|---|---|---|---|---|---|---|---|---|---|
| sine 1 kHz, 48 kHz stereo | -38.070 | -38.067 | +0.003 | 0.000 | 0.000 | 0 | -38.074 | -38.074 | | equal |
| pink, 48 kHz stereo | -25.078 | -25.073 | +0.005 | 0.100 | 0.100 | 0 | -14.369 | -14.369 | | equal |
| pink with 5 s steps, 44.1 kHz | -20.941 | -20.936 | +0.005 | 8.610 | 8.615 | +0.005 | -10.266 | -10.299 | -10.269 | equal |
| pink, 96 kHz | -19.959 | -19.954 | +0.005 | 0.090 | 0.081 | -0.009 | -10.146 | -10.212 | -10.125 | equal |
| brown, 32 kHz mono | -20.547 | -20.542 | +0.005 | 0.250 | 0.248 | -0.002 | -5.791 | -5.804 | -5.799 | equal |
| noise, 48 kHz 5.1 | -13.431 | -13.426 | +0.005 | 0.040 | 0.037 | -0.003 | -8.432 | -8.941 | -8.926 | equal |
| white, 48 kHz stereo | | | | | | | -7.744 | -8.551 | -8.522 | equal |
| 0.35 s (no whole block) | -70 | -70 (null) | 0 | 0 | 0 | 0 | -13.066 | -13.246 | -13.245 | equal |
| 3.45 s sine (partial last block) | -27.080 | -27.074 | +0.006 | 0 | 0 | 0 | -27.073 | -27.073 | | equal |
| silence | -70 | -70 (null) | 0 | 0 | 0 | 0 | -inf | -inf | | equal |
| 12 kHz sine, abrupt edges, 48 kHz | 3.260 | 3.264 | +0.004 | 0 | 0 | 0 | 0.513 | 0.026 | -0.087 (sine) | equal |
| 11.025 kHz sine, abrupt edges, 44.1 kHz | -0.580 | -0.570 | +0.010 | 0 | 0 | 0 | -3.341 | -3.812 | -3.93 (sine) | equal |
| 12 kHz, 20 kHz, 997 Hz sines, faded in and out | | | | | | | -6.021 | -6.021 | -6.021 | equal |
| Sintel's trailer, 48 kHz stereo | -12.835 | -12.820 | +0.015 | 14.610 | 14.617 | +0.007 | 1.157 | 1.158 | 1.158 | equal |

(White noise and the faded sines were run for true peak only. Time per clip was the same order for both, 0.01 to
0.7 s; it isn't a benchmark.)

### What explains the differences

- **I and LRA: ebur128's histogram.** `f_ebur128.c` doesn't average block powers. It counts each 400 ms (or 3 s)
  block in a histogram bin 0.01 LU wide, from -70 to +10 LUFS, at `(int)((loudness + 70) * 100)`, which truncates
  toward the bin's lower edge. It then computes I from the bins' energies (`count × energy(bin)`) above the relative
  gate's bin, and the LRA ends as bin loudnesses (multiples of 0.01). So FFmpeg's I runs on average 0.005 LU below a
  plain mean, which is the steady +0.005 above, and its LRA moves in 0.01 steps. Sintel's +0.015 is the same effect
  over louder, more varied blocks. The relative gate itself comes from the running mean of the exact powers
  (`sum_kept_powers / nb_kept_powers`), as in the web's meter.
- **True peak: two definitions.** ebur128 resamples to 192 kHz with swresample's default filter and takes the
  largest magnitude. The web's meter interpolates as BS.1770-4 Annex 2 describes (a 16-tap windowed sinc per phase,
  4 phases below 96 kHz). On steady signals both find the true value (the faded sines: -6.021 everywhere). Where the
  signal has energy near Nyquist (white noise, 5.1 noise, the abrupt-edged sines, the short clip), swresample
  overshoots by 0.2 to 0.8 dB over the reference, and the Annex 2 meter stays within 0.03 dB of it. The exception is
  96 kHz, where the managed meter oversamples only twice and reads 0.09 dB low. On programme audio (Sintel) the two
  agree to 0.001 dB. That is why the web's own check, at 0.1 dB on AAC-coded content, never saw it.
- **Sample peak** is the same number both ways: the largest magnitude of the same floats.

### What the meters must match (question 1)

- **Integrated loudness, LRA and its ends: equal to FFmpeg's.** The managed meter reproduces ebur128's arithmetic,
  as the picture measures reproduce signalstats'. That means the per-sample running sums of the K-weighted squares
  (`sum += z² - cached`, in double, in FFmpeg's order), the block power starting from 1e-12, the 0.01 LU histogram,
  the relative gate's bin, and the percentile walk (10th up from the gate, 95th down from the top). The aim is the
  same doubles. The test compares at full precision (`AudioFilter.Result`), so a difference would show.
- **Sample peak: equal.**
- **True peak: BS.1770-4 Annex 2, documented as differing from ebur128's.** It is checked within 0.1 dB against
  ebur128 on programme material (the samples, a film's soundtrack) and within 0.05 dB against an ideal reconstruction
  on the generated clips above. The difference (FFmpeg reads up to 0.8 dB higher on high-frequency content, because
  swresample overshoots) goes in FrameReader's README and the decision. Matching it would mean porting swresample's
  192 kHz filter, a derivative of LGPL code for an assembly of its own, to reproduce an overestimate. The 96 kHz case
  (2x oversampling, 0.09 dB low here) is the meter's to fix in slice 1: oversample 4x at every rate below 192 kHz,
  which Annex 2 allows.
- **Silences: equal** to silencedetect's on the same samples. The comparison is strict and in single precision
  (`s < level && s > -level`), the duration rounded to samples, as the web's `SilenceFinder` reads
  `af_silencedetect.c`.

The web's own check keeps its tolerances where its samples differ from FFmpeg's (ManagedCodecs' AAC is within 3e-7
of FFmpeg's decode). The same meter on samples that differ can flip a block across a 0.01 LU bin edge, so AudioCheck
stays at 0.1 LU there. The exactness above is the meter's, on identical samples.

## 4. Where the checks live (question 4)

In **`tests/FrameReader.Tests`**, on CI's FFmpeg 8.1, with no change to the native build:

- **Loudness and silences** in-process. The same samples go through the managed meter and through
  `AudioFilter(reader, "ebur128=peak=true+sample")` / `"silencedetect=n=-60dB:d=2"`, and the results are compared as
  section 3 says. Clips come from ffmpeg's generators at the rates and layouts above (5.1 and 7.1 for the channel
  weights; mono; a clip under one block; a partial last block; silence), so no file is committed. The generated clips'
  reference true peaks are fixed numbers in the test, computed once with the script used here.
  `RealWorldAudioTests` adds the soundtracks realworld.yml already downloads.
- **Picture** against the ffmpeg executable, because our build has no `signalstats`, `blackdetect` or `cropdetect`.
  This is PictureCheck's graph (`signalstats=stat=brng,cropdetect=reset=1:skip=0,metadata=print`, `blackdetect` at
  d=0 and 2 s, `cropdetect` with its defaults, `-copyts`) on clips made with `smptehdbars`, `smptebars`, `pal75bars`,
  black and letterboxed padding, full and limited range, and rotation tags. The frames reach the meter as
  `VideoFrameReader` yuv420p planes from the same file, so they are FFmpeg's frames. CI's test jobs already have
  ffmpeg on the PATH (BtbN 8.1).
- **Joining ranges** (`Join` over ranges equal to the whole run's `ToBytes`, byte for byte) and the colour-bar
  definition as unit tests. Each quirk reproduced from an FFmpeg filter gets a line in `tools/mutation/mutations.psd1`
  (the histogram's truncation, blackdetect's `pixel_black_th_i` rounding, cropdetect skipping the last line, BRNG
  counting a luma pixel once).

shotdetector-web's NativeCheck then keeps only what depends on its decoders: ManagedCodecs' samples and frames
against FFmpeg's, and the measures within tolerance on its own decode. It calls the package for the measures.

## 5. FrameReader's versions and split count

- **Semver.** Everything is new public API, with nothing changed or removed: a **FrameReader minor release**
  (`changes/framereader/<branch>.md` fragments, `### New`). Package validation against `FrameReaderApiBaselineVersion`
  passes because it's additive. ShotDetector needs no release for the move. Its delivery QC (#636) takes a dependency
  on the new FrameReader version when it uses the picture meter.
- **Split count** ([frame-reader-product.md](../frame-reader-product.md), "When to reconsider"). The audio and picture
  meters are FrameReader features. The picture meter counts as **needing both products** in the window where #636's
  ShotDetector PR uses it, and so does the audio meter if #636 uses it. At the FrameReader release that ships them,
  the count is recorded as information, as since 2026-10-31.
- **The split question isn't reopened.** Of [2026-10-31-split-trigger-criteria.md](../decisions/2026-10-31-split-trigger-criteria.md)'s
  conditions, the nearest is "a consumer wanting an FFmpeg-free FrameReader surface on its own". The web is such a
  consumer, but it takes the surface from the FrameReader package as it does `WaveformBuilder`. The owner chose that
  over a package of its own (question 5), so the condition isn't met. A later request for a separate FFmpeg-free
  package would meet it.
- **The web's side.** shotdetector-web moves to the new FrameReader version (its `Directory.Packages.props`), deletes
  its copies, and keeps `Mp4Audio` as the glue from ManagedCodecs' decoders to the meters. That is a PR in that
  repository, after the release.

## 6. Slices

1. **`LoudnessMeter`** (FrameReader): the web's meter with ebur128's histogram for I and LRA, `ChannelWeights`, 4x
   oversampling below 192 kHz, per-channel peaks and full-scale counts. Its tests run against `AudioFilter` and the
   reference peaks (section 4), with mutation lines, a README section saying where true peak differs and why, and a
   `framereader` fragment. One PR. Risk to settle in it: on x64, ebur128 runs an AVX kernel
   (`ff_ebur128_filter_channels_avx`) for two or more channels and the C one otherwise. The tests show whether the
   AVX kernel's doubles are the C order's (no FMA in an AVX1 kernel, so they should be). ci.yml's ARM and x64 jobs run
   the same tests. If the kernels differ, the decision says what "equal" is held to.
2. **`SilenceFinder`** (FrameReader), against `silencedetect` in-process. A small PR. It could ride with slice 1, but
   it is kept apart so each PR has one comparison.
3. **`PictureMeter`, `PictureMeasures`, `PictureFormat`** (FrameReader): the web's code with planes in place of
   `YuvFrame` and without the picture type, with tests against the ffmpeg executable (section 4). One PR, the largest
   (about 900 lines moved, plus the tests).
4. **FrameReader release** with the three slices (the owner says when; a release PR folds the fragments and records
   the split count).
5. **shotdetector-web adopts the package** (a PR there: delete the copies, keep NativeCheck's decoder checks, AudioCheck
   at its tolerances on AAC). Not this repository's work. Its #94 QC report builds on it.
6. **#636 uses it.** The delivery-QC command's picture slice reads frames as yuv420p (in-process `FrameDecoder` or
   `VideoFrameReader`) and feeds `PictureMeter`. Loudness stays on `Loudness.Measure` (FFmpeg's own, already merged as
   #642), so the CLI's figures remain ebur128's. A `--managed-loudness` switch isn't planned. With slice 1 the two
   meters agree on I and LRA, so the web and the CLI give the same integrated loudness for the same samples.

Each of slices 1 to 3 is light in CI terms only if FrameReader's detection path doesn't reach it (tools/ci-changes.sh
decides from the built assemblies). The new types aren't used by ShotDetector until slice 6, so they are expected to
be light.
