# Delivery QC: scoping note (issue #636)

Status: **scoping only**, 2026-10-31. No feature code. The idea (issue #636): one command that reports what a
broadcaster or post house checks on a file before accepting it (black and frozen spans, bars and tone at the head and
tail, pixels outside broadcast range, loudness per EBU R128 / ITU-R BS.1770, letterbox), as HTML, JSON or CSV with an
exit code for pipelines. This note says what exists, what the outside reference is, what is missing, a first slice,
the risks, and a verdict with the questions only the owner can answer.

## 1. What already exists

### FrameReader (this repo, MIT; its natives LGPL)

- **Reference tone**: `ReferenceTone.Find`/`FindAsync` and the streaming `ReferenceToneFinder`
  (`src/FrameReader/ReferenceTone.cs`, `src/FrameReader/ReferenceToneFinder.cs`): 1 kHz line-up tone as
  `ToneSegment(Start, End, Level)`, interrupted line-up (EBU stereo, GLITS) joined into one segment. Defined and
  accepted in [docs/reference-tone.md](../reference-tone.md). This is the "tone" of "bars and tone", done.
- **Audio reading**: `AudioReader` (`src/FrameReader/AudioReader.cs`): float samples of any stream FFmpeg decodes,
  from a path or a `Stream`, resampled or down-mixed on request.
- **FFmpeg's own loudness filter, in-process**: `AudioFilter` (`src/FrameReader/AudioFilter.cs`) runs an `-af` graph
  over an `AudioReader`, and its `Metadata` holds what analysis filters attach per frame (`lavfi.r128.I`, ...). Our
  native build enables `ebur128`, `loudnorm`, `silencedetect` and `astats` (`tools/native/build-ffmpeg.sh`, the
  `--enable-filter=` line; `src/FrameReader.Native/README.md`), and `tests/FrameReader.Tests/AudioFilterTests.cs`
  already holds `ebur128=metadata=1`'s per-frame metadata equal to ffmpeg's command line (`MetadataIsFfmpegs`). So **EBU R128 loudness exists today as FFmpeg's own
  code**, not as a port: there is nothing to make exact, it is the reference.
- **Waveform**: `WaveformBuilder`, `WaveformData` (`src/FrameReader/WaveformBuilder.cs`): peaks for a report's audio lane.
- **Spectral stats**: `SpectralStats.cs` (`aspectralstats`), used by the tone finder.

### ShotDetector and its CLI (MIT)

- The CLI already reaches most of the audio side, one option at a time (`src/ShotDetector.Cli/Program.cs`, help text):
  `--find-tone`, `--trim-tone`, `--waveform`, and `--audio-filter <graph>`, which prints analysis filters' results
  with their times. `shotdetect in.mxf --audio-filter "ebur128=peak=true+sample:metadata=1"` is a loudness measurement
  today, but as a stream of per-frame metadata, not a verdict.
- Shot list, per-frame stats, `--probe`/`--probe-json` (container, streams): the "context" rows of the issue.
- `YuvVideo`/`YuvFrame` (`src/ShotDetector/YuvFrames.cs`): the public frame-source input, 8-bit 4:2:0 planes with pts.
  This is the type shotdetector-web's picture meter reads, so its code can move here without an adapter.
- **Missing here**: no picture measures at all (no black, no levels, no broadcast range, no letterbox, no bars), and
  the native build has none of FFmpeg's video analysis filters (`signalstats`, `blackdetect`, `cropdetect`,
  `freezedetect` are not in the `--enable-filter=` lists).

### ShotDetector.FastYuv (LGPL-2.1)

Nothing QC-specific: it converts YUV to BGR. A QC pass measures the YUV planes before any conversion, so FastYuv is
not needed by the picture measures (shotdetector-web's meter runs before FastYuv converts).

### shotdetector-web's core (github.com/bisforboman/shotdetector-web, MIT; read at `06aad0f`, 2026-10-31)

- **`PictureMeter` / `PictureMeasures`** (`src/ShotDetectorWeb.Core/PictureMeasures.cs`, 809 lines), decided in its
  `docs/decisions/2026-10-30-picture-measures.md`: one `Vector128` pass per frame over `ShotDetector.YuvFrame` giving
  `signalstats`' YMIN, YAVG, YMAX and BRNG, `blackdetect`'s spans, `cropdetect`'s bars (`reset=1:skip=0`), colour bars
  by their definition (`BarColours`: SMPTE/EBU seven bars at 75% or 100%, BT.601 or BT.709, what FFmpeg's
  `smptebars`, `smptehdbars`, `pal75bars`, `pal100bars` draw), and each frame's picture type. About 2% of a run.
  **Checked exactly** against FFmpeg (`tools/NativeCheck/PictureCheck.cs`: every frame's figures equal, YAVG to the six
  digits FFmpeg prints). Its dependency on ManagedCodecs is only `PictureType` (the frame type): the measures
  themselves need only `YuvFrame`.
- **`Loudness` / `LoudnessMeter`** (`src/ShotDetectorWeb.Core/Loudness.cs`, 317 lines): a managed BS.1770-4 meter
  (K-weighting biquads for any rate, 400 ms/3 s blocks every 100 ms, gating, LRA, true peak by 4x oversampling), so it
  runs on browser-wasm without FFmpeg. Checked **within tolerances**, not exactly (`tools/NativeCheck/AudioCheck.cs`:
  0.1 LU on I and LRA, 0.1 dB on true and sample peaks), because its samples come from ManagedCodecs' AAC decoder
  (within 3e-7 of FFmpeg's) and its true-peak oversampler is not FFmpeg's (below).
- `PictureSummary.cs`: the page's card (spans, crop, aspect). The web's report is the closest thing to the QC report.

### What an exact loudness would be checked against

FFmpeg's `ebur128` filter (`libavfilter/f_ebur128.c`, release/8.1, read for this note), the one our native build
already ships:

- Options: `peak=sample|true` (flags), `metadata=1` (per-frame `lavfi.r128.M`, `.S`, `.I`, `.LRA`, `.LRA.low`,
  `.LRA.high`, and with peaks `lavfi.r128.sample_peaks_chN`, `.true_peaks_chN`, `.sample_peak`, `.true_peak`),
  `dualmono`, `panlaw`, `framelog`; read-only results `integrated`, `range`, `lra_low`, `lra_high`, `sample_peak`,
  `true_peak`; the log's "Summary" at uninit prints them to one decimal.
- **True peak is swresample's resampling to 192 kHz** (`out_sample_rate` 192000 on an `SwrContext`), not BS.1770's
  Annex 2 FIR at 4x. So a managed port is exact on I and LRA only if it reproduces the filter's block arithmetic, and
  exact on true peak only if it reproduces swresample's polyphase filter at 192 kHz: that is why shotdetector-web's
  meter is held to tolerances. **Through FrameReader's `AudioFilter` the numbers are FFmpeg's by construction**, and the
  check is the same graph through the ffmpeg executable (CI's FFmpeg 8.1), equal to the three decimals of the metadata.
- The conformance material is EBU Tech 3341 (meter) and 3342 (LRA) with the EBU loudness test set. Their pages
  (`tech.ebu.ch`) are outside this session's allowed network, so the test set's terms and file list are not verified
  here; generated signals (FFmpeg's `sine` at known levels, in the native build) cover the shapes those tests check.

## 2. The external integration point

There is no host application to plug into: delivery QC is a command of ours. The outside contracts are the references
the figures are held to, all verified from source:

- **FFmpeg 8.1's analysis filters** as the oracle (the repo's rule: exact, or a documented difference). `ebur128` above
  (LGPL-2.1, "Copyright (c) 2012 Clément Bœsch"); for pictures, `vf_blackdetect.c`, `vf_signalstats.c` and
  `vf_cropdetect.c`, which shotdetector-web read and checks against; `freezedetect` (`vf_freezedetect.c`) for frozen
  spans, which nothing measures yet.
- **How it plugs into FrameReader**: an `-af` graph string on `AudioFilter(AudioReader, string graph)`, results read
  from `Metadata` per chunk (`TryRead`), the last chunk's `lavfi.r128.I`/`LRA` being the whole file's. Through the
  executable path, `ffmpeg -af ebur128=...` with `metadata=print` (as AudioCheck does). Video analysis filters would
  need `--enable-filter=signalstats,blackdetect,cropdetect,freezedetect` in the native build (a FrameReader release)
  if they were to run in-process as filters; porting them as managed measures over `YuvFrame` (shotdetector-web's way)
  needs no native change.
- **Pipelines**: an exit code and a JSON report are the integration point for users' scripts; there is no standard QC
  report schema to follow (EBU's QC items, tech.ebu.ch/qc, are a catalogue, not a format; not reachable from here).

## 3. The gap, and a first slice

Missing in this repo, in the order a report needs them:

1. **A loudness verdict**: I, LRA, true peak, sample peak from `ebur128`, against a target (EBU R128: -23 LUFS
   ±0.5 LU (±1 for live), true peak ≤ -1 dBTP; ATSC A/85: -24 LKFS) with pass/fail. Exists as raw metadata only.
2. **Picture measures over the frames**: black spans, YMIN/YAVG/YMAX, BRNG, letterbox, colour bars. Exist in
   shotdetector-web, not here. Moving them needs a YUV frame source for files: `Detect(string)` reads BGR through the
   executable, so either the in-process path's decoded YUV frames (FrameDecoder) or FrameReader's `VideoFrameReader`
   in yuv420p, a second decode. 8-bit 4:2:0 only, like `YuvFrame` (10-bit and 4:2:2, common in delivery masters
   (ProRes, XDCAM, AVC-Intra), would need the meter widened).
3. **Freeze detection**: nowhere yet; `freezedetect` is the oracle.
4. **The command**: one pass that runs these, a report (JSON first, HTML later) and thresholds into an exit code.

**First slice (one PR)**: `shotdetect --loudness` (or a `qc` subcommand with only loudness), built on FrameReader's
`AudioFilter` with `ebur128=peak=true+sample:metadata=1`: prints integrated, LRA (and its low/high), true and sample
peak per channel as text or JSON, `--loudness-target -23` (default EBU R128) and a non-zero exit code outside
tolerance. Tests: the same graph through the ffmpeg executable on generated clips (`sine` at known levels, silence,
5.1 with an LFE) equal to the metadata's three decimals (as `AudioFilterTests.MetadataIsFfmpegs` already does for
`ebur128=metadata=1`); the exit code on each side of the limit. Like `--audio-filter` today it loads FFmpeg's libraries
(`FFmpegLibraries.Load`, `Program.cs`), so it is in-process only; an executable fallback (`-af ... metadata=print`)
can follow if wanted. No change to detection, so parity is untouched.
One PR, a changelog fragment under `changes/shotdetector/`.

**Size of the whole idea**: about 6 to 8 PRs. Loudness verdict (1); picture measures moved into ShotDetector or
FrameReader with an exact FFmpeg check (2, the move and its YUV source); freeze detection (1); the `qc` command
with JSON and exit code (1); HTML report (1); 10-bit/4:2:2 measures (1 to 2, optional). shotdetector-web then takes
the shared measures from the package instead of its copy (a PR there).

## 4. Risks

- **Licensing.** ShotDetector, FrameReader and shotdetector-web are MIT; FastYuv and FrameReader.Native (our FFmpeg
  build) are LGPL-2.1. FFmpeg's `ebur128` is LGPL-2.1+ and already inside FrameReader.Native: using it through
  `AudioFilter` changes nothing. Porting FFmpeg filters into MIT C# (the picture measures, `freezedetect`) is the
  question shotdetector-web already answered by measuring what the filters measure from their definitions (integer
  counts) and checking against them, not translating their code; a port of `ebur128`'s or swresample's code to make
  true peak exact would be a derivative of LGPL code and belong in an LGPL assembly like FastYuv. No GPL piece is
  needed (the native build stays `--disable-gpl`).
- **External maintainers.** Nothing goes upstream: FFmpeg is consumed, not changed, so acceptance isn't a factor. The
  risk is the other way: FFmpeg changing a filter's output between versions (the pinned 8.1 and `upstream.yml` already
  watch that).
- **Runtime.** In-process loudness needs FrameReader.Native for the RID (`win-x64`, `linux-x64`, `linux-arm64`,
  `linux-musl-x64`, `osx-arm64`); elsewhere (osx-x64, win-arm64) the executable path with the user's ffmpeg. AOT:
  FrameReader, ShotDetector and the CLI are `IsAotCompatible`, `AudioFilter` included; the release's AOT binaries keep
  working. Trimming: nothing reflective. Picture measures in managed `Vector128` run everywhere, browser-wasm too
  (shotdetector-web's numbers: about 2% of a run natively and under wasm AOT); x64 and ARM give equal integer counts
  on the same frames, but ARM's frames themselves can differ (CLAUDE.md, ARM results), so the measures are as exact as
  the decode is.
- **Audience.** Niche (broadcast, archives, post). Commercial QC (Baton, Vidchecker, Aurora) covers captions, codec
  compliance, ad markers; this is a scriptable free first pass, which is how the issue positions it.
- **Two implementations.** Loudness through FFmpeg here and managed in shotdetector-web would give two answers (within
  0.1 LU). Fine if documented; one shared managed meter would need the exact-true-peak question settled.

## 5. Verdict

**Needs the owner's decision**, with the first slice ready to do: the loudness verdict is one small PR on code that
exists and is exact by construction, but the rest depends on where the picture measures live, which is a product
decision across two repositories.

Decision questions (not asked here):

1. A `qc` subcommand of `shotdetect`, options on it (`--loudness`, `--picture`), or a separate tool/package?
2. Should shotdetector-web's `PictureMeter`/`PictureMeasures` move into this repo so both use one implementation, and
   into which package: ShotDetector (it reads `YuvFrame`) or FrameReader (the general library, would need its own
   frame type)?
3. Loudness: FFmpeg's `ebur128` through `AudioFilter` (exact by construction, needs the natives or the executable), or
   shotdetector-web's managed meter moved here (runs anywhere, within 0.1 LU, true peak not FFmpeg's)?
4. Is freeze detection (`freezedetect`) in scope?
5. Is 10-bit and 4:2:2 (most delivery masters) required before calling it delivery QC, or a later step?
6. Which targets ship as presets (EBU R128, ATSC A/85, streaming -14/-16 LUFS), and is the exit code per check or one?
7. Priority: the issue ranks it 8 of 9 by adoption; does it go in the backlog's Maybe (another change adds those rows)
   or Next?
