# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes the NuGet packages and a GitHub release. Until
1.0, minor versions may change the public API.

## Unreleased

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
  Same frames and results; about 40% less CPU for decoding on Linux (issue #12). Streams, URLs, image
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
