# Changelog

Versions are git tags (`vX.Y.Z`); each one publishes the NuGet packages and a GitHub release. Until
1.0, minor versions may change the public API.

## Unreleased

- `MinSceneLength` takes every scenedetect format: frames, `0.6s`, `0.6` and `HH:MM:SS.mmm`.
- The shot list CSV starts with scenedetect's `Timecode List:` row (an empty row when there are no cuts);
  `Shots.Csv(shots, includeCutList: false)` / CLI `--skip-cuts` leaves it out. CLI `-q/--quiet`.
- `SplitVideo` takes `SplitOptions` (CLI `--split-copy`, `--split-high-quality`, `--split-crf`,
  `--split-preset`, `--split-args`, `--split-expand`), scenedetect's split-video options.
- Fix: split-video now always drops subtitle streams (`-sn`), as scenedetect 0.7.1 does too; only the
  0.6.4 mode did before.
- `DetectionOptions.FrameRate` / CLI `--frame-rate`: scenedetect's `-f/--frame-rate` override, including
  the rate of image sequences (`frames/%04d.png`).
- `DecodeThreads` is nullable; the default (null) is 8 decoder threads on the yuv420p fast path (~13% faster
  on HD, ~70 MB more at 1080p) and 4 elsewhere, where the pipe is the bottleneck.
- MPEG program streams (`.mpg`, MPEG-2) gave shifted cuts: packets without a pts were dropped from the
  frame timestamps. Per-frame timestamps now come from a decoding pass for such files, as OpenCV sees them.
- Real-world and mutation checks in CI (`tools/realworld.py`, `tools/mutation/`), nightly real-world run.

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
