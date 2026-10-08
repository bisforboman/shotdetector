# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes the NuGet packages and a GitHub release. Until
1.0, minor versions may change the public API.

## Unreleased

### New

- `DetectionOptions.Yadif` (`IYadif`), with `Yadif` in ShotDetector.FastYuv (LGPL, a port of FFmpeg's yadif):
  in-process deinterlacing computes only the rows the resize reads, in parallel, instead of FFmpeg's yadif on whole
  frames. Identical results for 8-bit video. For 9 to 16 bits it follows FFmpeg's C code, since FFmpeg 8.1's x86
  SIMD yadif gives a different result on every run there.

### Faster

- Deinterlacing with FastYuv's `Yadif`: an 80 s 1080i25 MPEG-2 4:2:2 MXF takes 7.5 s instead of 11.6 s with 2 CPUs,
  and 5.0 s instead of 5.5 s with all of them (using a third less CPU time); 5.1 s and 4.0 s without deinterlacing.

## 0.9.0 – 2026-10-08

Broadcast and ARM: deinterlacing in-process, which decoder ran and a startup check, more in the probe (issue #42),
NEON on ARM, and the scenedetect 0.6.4 mode removed. CI now checks interlaced 1080i XDCAM HD, AVC-Intra and ProRes.
Note for interlaced video: scenedetect's current OpenCV can't read it (one black frame throughout), so compare with
deinterlacing on (README, "Where this differs").

### Breaking changes

- `VideoDecoder.InProcess` without loadable FFmpeg libraries throws with `Reason = ShotDetectionError.FfmpegLibrariesNotFound`
  (new), no longer `FfmpegNotFound`, which now means the executable only (issue #42).
- `VideoInfo` has two more positional members, `Container` and `HasAudio` (issue #42).
- The scenedetect 0.6.4 compatibility mode is gone: `PySceneDetectVersion.V0_6_4` and CLI `--compat 0.6.4`. 0.7.1
  stays the default and only mode (`PySceneDetectVersion` remains, for the next scenedetect release); use
  ShotDetector 0.8 to reproduce 0.6.4.

### New

- Deinterlacing in-process: our FFmpeg build (ShotDetector.Native, the CLI) adds libavfilter with only yadif, and
  the in-process decoder runs it as ffmpeg's `-vf yadif` does, so deinterlaced detection no longer needs the ffmpeg
  executable. Identical results (tested against the executable, from the start and after a seek, and against a
  lossless yadif copy). Not faster: on an 80 s 1080i25 MPEG-2 4:2:2 MXF with 2 CPUs, 13.3 s in-process vs 13.4 s
  with the executable, yadif itself being most of the cost (issue #42).
- Which decoder a run uses: `DetectionProgress.DecodesInProcess` and `.Pipeline`, in every report from a first one
  sent before any frame, so `DetectStreamAsync` callers see it too (issue #42).
- `ShotDetection.CanDecodeInProcess(ffmpegDirectory)`: whether FFmpeg's libraries load, for a worker's startup check.
- `VideoInfo.Container` (FFmpeg's demuxer name) and `HasAudio` (null for streamed input), so a probe can replace a
  separate ffprobe call (issue #42).

### Faster

- ARM (Apple Silicon, Graviton, Ampere): the HSV conversion has a NEON path and the resize's vertical pass and the
  frame differences fall back to 128-bit vectors, where they ran scalar. On a 2-CPU Neoverse-N2, in-process
  decoding is now faster than scenedetect on all four benchmark films (Big Buck Bunny 0.95x, was 1.16x) with less
  CPU. CI runs the whole test suite on ARM, and the benchmark covers ARM too.

### Fixed

- In-process decoding with a start time on MPEG program streams (packets without timestamps) began up to a GOP late:
  libavformat's seek lands after the wanted frame there. Such files now decode from the start and drop the frames
  before it, as the executable's results (since 0.7.0).

## 0.8.0 – 2026-10-07

Automatic deinterlacing and a public probe (issue #35). Results are unchanged unless you turn deinterlacing on.

### Breaking changes

- `DetectionOptions.Deinterlace` is a `DeinterlaceMode` (`Off`, `On`, `Auto`) instead of a `bool`: `Deinterlace = true`
  becomes `Deinterlace = DeinterlaceMode.On`. The default is still off.

### New

- `DeinterlaceMode.Auto` (CLI `--deinterlace auto`): deinterlace when the video stream is flagged interlaced (its
  field order), so a worker no longer needs ffprobe to decide (issue #35).
- `ShotDetection.Probe` / `ProbeAsync`: a video's properties from its headers (size, frame rate, frame count,
  duration, codec, pixel format, field order, rotation) as a `VideoInfo`, in milliseconds, in-process when FFmpeg's
  libraries load. `VideoReader` has `FieldOrder`, `Codec`, `Duration`, `Deinterlaces` and `Info` too (issue #35).

## 0.7.0 – 2026-10-07

ShotDetector now decodes in-process by default and needs no ffmpeg installed for video files: the CLI bundles
FFmpeg's libraries, and the new `ShotDetector.Native.<rid>` packages carry them for library users. On 2 CPUs it is
faster than scenedetect on every benchmark film, with less CPU and memory (README "Performance"). Results are
unchanged.

### Changed

- In-process decoding is the default when FFmpeg 8's shared libraries load (`VideoDecoder.Auto`, the new default;
  CLI `--decoder auto`), else the ffmpeg executable as before. Same results either way. `--decoder process` keeps
  the executable. FFmpeg's libraries load once per process: a later `FfmpegDirectory` no longer errors, the first
  copy is used.

### New

- `ShotDetector.Native.<rid>` packages (LGPL) for win-x64, linux-x64, linux-arm64, linux-musl-x64 and osx-arm64:
  FFmpeg 8.1.3's libraries built by us for decoding only (7-10 MB per platform). The shotdetect tool and binaries
  bundle them. Byte-identical stats to the FFmpeg 8.1 executable on H.264, HEVC and VP9.
- Probing in-process: with the libraries available, a video file's properties and packet timestamps (and MPEG-PS
  frame timestamps) are read with them instead of ffprobe, identical by test. With ShotDetector.Native or the CLI,
  detecting shots in a file needs no ffmpeg at all (streams, URLs, image sequences, rotated video, deinterlacing,
  AV1 and the image/clip exports still use the executable).
- The libraries are looked for next to the app first (`runtimes/<rid>/native/`, then the app's folder), then in
  the system's usual places. In `Auto`, a codec they can't decode (AV1: no software decoder in our build) is
  decoded by the ffmpeg executable instead. FFmpeg's log is quiet in-process. The CLI summary says how it decoded.
- Benchmark (`benchmark.yml`, `tools/bench.py`): the default and executable modes, CPU time and the whole process
  tree's memory, on four films including Sintel at 1080p; the README's performance tables come from it.

## 0.6.1 – 2026-10-07

Speed only: every result is unchanged (byte-identical stats on Sintel, Big Buck Bunny and Tears of Steel).
In-process decoding (`--decoder inprocess`, FFmpeg 8.1's libraries) is now faster than scenedetect on most
films; the default ffmpeg-executable path is up to 41% faster on small video.

### Faster

- In-process decoding, on Linux with 2 CPUs (wall time against scenedetect 0.7.1): Sintel 1080p 4.6 s vs
  5.1 s, Tears of Steel 32.1 s vs 33.2 s, Big Buck Bunny within 4% (was 1.7x), at the same or less CPU (issue #12):
  - it converts only the row slices the resize reads, set up from the frame's colour tags (tested exact for
    BT.709, full range, 4:2:2, 4:4:4, 10-bit and odd heights);
  - the exact resize (every pipeline) has no bounds checks in the horizontal pass and a 256-bit vertical pass;
    stage timing showed it cost more than decoding at 640x360;
  - HSV conversion runs 8 pixels at a time (AVX2; tested on all 2^24 colours).
- Default path (ffmpeg executable), small video: ffmpeg sends whole frames when the resize reads at least a
  quarter of the pixels, instead of filtering them (remap): on Linux with 2 CPUs, 37% faster on 640x360 and 41% on
  426x240, with less memory; 1080p keeps the filter.

## 0.6.0 – 2026-10-07

Every result is still identical to scenedetect's; in-process decoding and deinterlacing are tested equal to the
ffmpeg executable and to a lossless yadif copy.

### Changed

- ffmpeg, ffprobe and input failures throw `ShotDetectionException` (with a `Reason`: FfmpegNotFound,
  InvalidInput, DecodeFailed, ExportFailed) instead of a plain `InvalidOperationException`. It derives from
  `InvalidOperationException`, so `catch` blocks still work; only exact type checks (such as xunit's
  `Assert.Throws<InvalidOperationException>`) see the difference. A file without a video stream now says so
  (issue #17).
- Default decoder threads: one fewer than the CPUs available (1 in a 2-CPU container, where it used the same wall
  time as more for ~9% less CPU), still at most 4, or 8 on the yuv420p path.

### New

- In-process decoding, opt-in: `DetectionOptions.Decoder = VideoDecoder.InProcess` / `--decoder inprocess`
  decodes with FFmpeg 8.1's shared libraries (FFmpeg.AutoGen bindings, MIT) instead of the ffmpeg executable.
  Same frames and results. On Linux with 2 CPUs: 1.2-1.5x faster than the ffmpeg executable for 9-33% less
  CPU; it decodes with all the CPUs, as OpenCV does (`--threads 1` uses the least CPU) (issue #12). Streams, URLs, image
  sequences, rotated video and deinterlacing still use the executable.
- `Deinterlace` / `--deinterlace`: ffmpeg's yadif before analysis, for interlaced sources; results equal
  scenedetect's on a lossless `-vf yadif` copy, seeking included (issue #16).
- Tracing: `ShotDetection.ActivitySourceName`, one span per detection with the video's size, frame rate,
  pipeline, decoder, frames and shots (issue #19).
- Packages: SourceLink, symbol packages (.snupkg), deterministic CI builds; release packages and binaries carry
  GitHub build attestations (`gh attestation verify <file> --repo bisforboman/shotdetector`, issue #18).

### Faster

- Scoring: frames as small as scenedetect's (at most 256 wide) no longer split across threads, which cost 3-4x
  their CPU; the resize reads the sampled rows directly; difference sums use 256-bit vectors. Same results.
  On Linux with 2 CPUs: 17% faster, 9% less CPU than 0.5.0 (issue #12).

### Fixed

- Correction to 0.5.0: "on 2 CPUs it now uses less CPU than scenedetect" holds on the Windows machine it was
  measured on (ffmpeg 7.1), not on Linux: there, with 2 CPUs, ShotDetector uses ~1.5x scenedetect's CPU and
  ~1.6x its wall time (issue #12). The README has both measurements.

## 0.5.0 – 2026-10-06

Every new output and option below is byte-identical to scenedetect 0.7.1's (checked in CI).

### Breaking changes

- API review toward 1.0: the detector classes, `IDetector`, `ContentScorer` and `EdgeDetector` are internal
  (use `ShotDetection` with `DetectorKind`); `VideoReader.FrameAt`, `SeekFrame`, `PositionAfterDecoding` and
  `FrameCountHint` are internal; `FrameTime`'s raw fields and `FrameTime.Pts` are internal and `ToString()` is
  the timecode; `SaveImages`/`SplitVideo` return `IReadOnlyList<string>`.
- `SplitVideo` takes `SplitOptions` before the `CancellationToken` (pass the token by name).
- `DecodeThreads` is nullable; the default (null) is 8 decoder threads on the yuv420p fast path (~13% faster
  on HD, ~70 MB more at 1080p) and 4 elsewhere.
- The shot list CSV starts with scenedetect's `Timecode List:` row (an empty row when there are no cuts), as
  scenedetect's does; `Shots.Csv(shots, includeCutList: false)` / CLI `--skip-cuts` leaves it out.
- `DetectionResult` has a new `Cuts` parameter (scenedetect's cut list, which keeps the cuts of dropped or merged
  shots).

### New

- Timeline exports: `Timeline.Edl`, `Fcpx`, `Fcp7`, `Otio` and `Qp` / CLI `--save-edl`, `--save-fcp`,
  `--save-otio`, `--save-qp` (scenedetect's save-edl, save-fcp, save-otio, save-qp).
- `Shots.Html` / CLI `--save-html`: scenedetect's save-html (export-html) page, with thumbnails.
- Several detectors in one run (`DetectionOptions.Detectors` with per-detector `DetectorSettings`; CLI repeated
  `-d`, with `-t`/`-m`/`--filter-mode` per detector), `DropShortScenes`, `MergeLastScene`, `FilterMode`
  (suppress), `Downscale`.
- `ShotDetection.LoadScenes` / CLI `--load-scenes`: shots from a scene list CSV, as scenedetect's load-scenes.
- CLI `--config <file>`: scenedetect's config file (scenedetect.cfg) as defaults; unknown keys are listed.
  `DetectionOptions.AddLastScene` (threshold detector; on by default, as in scenedetect).
- File name templates: `ImageOptions.FileName`, `SplitOptions.FileName` (CLI `--image-filename`,
  `--split-filename`); CLI `-o/--output` and `$VIDEO_NAME` in output paths.
- `SplitOptions` (CLI `--split-copy`, `--split-high-quality`, `--split-crf`, `--split-preset`, `--split-args`,
  `--split-expand`): scenedetect's split-video options.
- `DetectionOptions.FrameRate` / CLI `--frame-rate`: scenedetect's `-f/--frame-rate`, including the rate of
  image sequences (`frames/%04d.png`).
- `MinSceneLength` takes every scenedetect format: frames, `0.6s`, `0.6` and `HH:MM:SS.mmm`.
- `ShotDetection.DetectAsync`; CLI `-q/--quiet`.

### Faster

- The MIT core pipes only the pixels the exact resize reads (ffmpeg's remap filter after its own BGR conversion;
  `FramePipeline.SampledBgr`): identical results, 0.44 MB instead of 6.2 MB per 1080p frame. On 2 CPUs it now uses
  less CPU than scenedetect (was 2.1x) and is on par in wall time; faster than scenedetect with more CPUs (#7).
  *Correction: measured on Windows with ffmpeg 7.1 only; on Linux with 2 CPUs it uses ~1.5x scenedetect's CPU
  (issue #12, see the README).*

### Fixed

- MPEG program streams (`.mpg`, MPEG-2) gave shifted cuts: packets without a pts were dropped from the frame
  timestamps. Per-frame timestamps now come from a decoding pass for such files, as OpenCV sees them.
- When the decoder drops frames (damaged video), later frames got the wrong times and frame numbers; each frame
  now takes its own time, as in scenedetect.
- A streamed input that doesn't start at 0 (e.g. a TS starting at 1.4 s) had every time shifted by its start.
- split-video now always drops subtitle streams (`-sn`), as scenedetect 0.7.1 does; only the 0.6.4 mode did.

### Repository

- Real-world (whole films, nightly) and mutation checks in CI; a weekly benchmark; `main` is protected, with
  17 required checks.

## 0.4.0 – 2026-10-06

- Streamed input: `Detect(Stream)`, `DetectStreamAsync(Stream)`, URLs, `DetectionOptions.Streaming`
  and `ProbeBytes`; the CLI reads standard input with `-i -`. `DetectionResult.VideoPath` is null for
  a `Stream`.
- Time range (`StartTime`, `EndTime`, `Duration`), `FrameSkip` and `Crop`, matching scenedetect's
  `time`, `--frame-skip` and `--crop`; `ImageOptions` for `SaveImages` (size, scale, format).
- API: test-only members are internal; `VideoReader.Pipeline` is the `FramePipeline` enum;
  `DetectionResult.MinSceneLength` is `MinSceneLengthFrames`; `SaveImages` takes `ImageOptions`.
- Package icon, changelog, Dependabot.

## 0.3.0 – 2026-10-05

- All five scenedetect detectors: `Histogram` (`detect-hist`, bit-exact) and `Hash` (`detect-hash`,
  exact except on flat frames where the hash is rounding noise) join adaptive, content and threshold.
- `ShotDetection.DetectStreamAsync`: shots as an `IAsyncEnumerable<Shot>` while the video decodes.
- `CancellationToken` on `Detect`, `SaveImages` and `SplitVideo`; cancelling kills ffmpeg.
- `DetectionOptions.Progress` (frames done / expected) and `FfmpegDirectory`; a clear error when
  ffmpeg is missing.
- PySceneDetect 0.6.4 compatibility mode (`Compatibility = PySceneDetectVersion.V0_6_4`).
- Fix: video with a rotation tag (portrait phone clips) was read with swapped dimensions.
- Runs on Alpine (musl); tested in CI.
- The `shotdetect` CLI ships as the `ShotDetector.Cli` dotnet tool and as Native AOT binaries for
  Linux, Alpine, Windows and macOS on the GitHub release.

## 0.2.0 – 2026-10-05

- Colour conversion follows the video's colour tags (BT.709, BT.2020, full range), as scenedetect
  does on Linux with FFmpeg 8; `ShotDetector.FastYuv` handles every matrix and range.
- Fix: scene list timecodes now match scenedetect digit for digit (OpenCV's millisecond positions,
  Python's rounding).

## 0.1.0 – 2026-10-05

First release: `ShotDetector` (MIT) with the adaptive, content and threshold detectors, identical
to scenedetect 0.7.1 on every clip in CI, and `ShotDetector.FastYuv` (LGPL-2.1-or-later), the
optional yuv420p fast path ported from FFmpeg's swscale.
