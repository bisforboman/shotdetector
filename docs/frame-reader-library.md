# Frame-reader library (idea)

Status (2026-10-09): milestones 1 (FFmpegLibraries, FrameReaderException), 2 (MediaProbe: the values ffprobe
prints, packet timestamps, Stream input) and 3 (FrameDecoder: demux, decode, seek, both deinterlacings with an
IRowDeinterlacer hook, autorotate, Stream input; Width/Height/PixelFormat/GetPlane on the current frame) done.
ShotDetector keeps its conversions (FrameWriter: the pipelines' exact layouts) and its ffprobe-text formatting
(InProcessProbe), and maps FrameReaderException to its own reasons (InProcess.Map). The public frame API is in:
VideoFrameReader (a size and FrameFormat, the bytes of ffmpeg's `scale=W:H:flags=bicubic,format=F`, checked byte for
byte) and the library's README. FrameReader still has InternalsVisibleTo ShotDetector for the AVFrame, on-demand rows
and a prefixed Stream (its sampled-row and crop conversions go beyond the public API). Milestone 5 (benchmarks) done: FrameReader's README has the numbers (x64 and ARM). Milestone 6: exact seeking and thumbnails done (VideoFrameReader.Seek/TryReadAt, the frame `ffmpeg -ss T` gives, byte for byte; with deinterlacing only before the first read); audio next; hardware decoding skipped for now (decisions.md). Answers to "Before writing code" 1-3 and
build-vs-contribute in docs/decisions.md. For Claude Code: read this file and CLAUDE.md, then start at
"Before writing code". Record every answer the user gives in `docs/decisions.md` as usual.

## The idea

A general .NET library for reading video frames and metadata fast, in-process, through FFmpeg's libraries:
ShotDetector's in-process decoding, made reusable for other analysis tools (thumbnails, motion, OCR, ML input).

Process-based wrappers such as [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) are fine for transcoding,
where ffmpeg does all the work. They are slow for analysis that reads every frame: a new ffmpeg/ffprobe process
per call (`Snapshot`, `Analyse`), frames through pipes, and an object or bitmap per frame. The gap is not the raw
bindings (FFmpeg.AutoGen and Sdcb.FFmpeg exist) but an idiomatic, low-allocation .NET layer on top of them.

## Starting point: ShotDetector already has most of it

- `InProcessDecoder` (FFmpeg.AutoGen, FFmpeg 8.1 ABI): demux and decode, swscale, deinterlacing (yadif graph or
  `IYadif`), autorotate, native library loading and its error messages.
- `InProcessProbe`: stream properties, packet timestamps, rotation, the same values ffprobe prints.
- `StreamInput`: a custom AVIOContext for `Stream` input.
- `VideoReader.InProcess`: decoding and conversion on their own thread, eight buffers ahead.
- `ShotDetector.Native.<rid>`: decode-only FFmpeg 8.1.3 builds for five platforms (`native.yml`,
  `tools/native/build-ffmpeg.sh`).

So the work is mostly extraction and a general public API, not new decoding code.

## Analysis (2026-10-08, before starting)

Prior art, checked on nuget.org and GitHub:

| Library | What it is | Status |
|---|---|---|
| FFMediaToolkit (MIT) | The closest: FFmpeg.AutoGen-based reading (and writing), `TryGetNextFrame` into a `Span<byte>`/buffer, output pixel format (default Bgr24), seek by time, `Stream` input via `MediaFile.Load` | 4.8.1, last commit 2025-09-09; FFmpeg 7.x (AutoGen 7.1.1), netstandard2.0/2.1; Windows and Linux only (macOS "not supported"); no native packages (bring your own FFmpeg); resize not documented |
| Sdcb.FFmpeg | Bindings with some helpers, not a frame-reader API | 7.0.0 (2024-04), last push 2025-03; FFmpeg 7 |
| FFmpeg.AutoGen | Raw bindings (what we use) | 9.0.1.1 (2026-08): FFmpeg 9 bindings exist |
| FFMpegCore | Process-based (spawns ffmpeg/ffprobe per call) | 5.5.0 (2026-09), active; the benchmark's comparison point |
| OpenCvSharp4 `VideoCapture` | Frames as `Mat` through OpenCV's FFmpeg | Active; a heavy dependency, OpenCV's conversions |
| SeeShark | Cameras, not files | 4.1.0 (2025-11) |

So the gap is real but narrower than "nothing exists": FFMediaToolkit covers basic in-process reading. What a new
library would add: FFmpeg 8.1 (and a path to 9), macOS and ARM, ready-made decode-only native packages for five
platforms, Native AOT, swscale to a requested size and format in one step (a 160x90 Gray8 reader never sees full
frames), steady-state zero allocation with the decode thread ahead, deinterlacing and autorotate, MPEG-PS
timestamps, and the ffprobe-equivalent probe. Worth deciding up front whether that set is enough to justify a new
package rather than contributing to FFMediaToolkit (which would mean its FFmpeg version, netstandard and API).

Feasibility of extracting from ShotDetector:

- Feasible: all the code involved is internal; parity, mutation and AOT jobs guard ShotDetector's results. Most of it
  is proven on five platforms already. ShotDetector's API may change where that makes the split cleaner (see below).
- The hard part is milestone 3: `InProcessDecoder` mixes the general decoder with ShotDetector's writers
  (`WriteSampledBgr`, `WriteYuv420`, the CLI-matching swscale context), and the row-wise yadif is woven into the
  sliced conversion (only the rows the resize reads). The library needs a lower-level hook (decoded planes, or a
  conversion callback) for ShotDetector to keep byte-identical frames.
- `IYadif` is public in ShotDetector and FastYuv's `Yadif` is LGPL: the hook can move to the library (or ShotDetector
  adapts it), and the LGPL port stays out of the MIT library either way.
- Errors: the decoder throws `ShotDetectionException` (`FfmpegLibrariesNotFound`, `DecodeFailed`, ...). The library
  needs its own exception; ShotDetector maps it, or exposes the library's, whichever is simpler.
- Native packages: cheapest is that the library loads from the same files as ShotDetector.Native.<rid> (the loader
  is name-agnostic: next to the app, runtimes/<rid>/native, a given folder). New <Name>.Native.<rid> packages mean
  publishing twice or migrating existing users.
- Rough size: milestones 1, 2 and 4 small to medium (moving code and its tests), 3 the large one, 5 (benchmarks)
  medium.

## Constraints

- ShotDetector's results must not change: parity, real-world, mutation and AOT jobs stay green, and frames stay
  byte-identical. Its public API may change if that makes the split cleaner: nobody but the user depends on it yet
  (user, 2026-10-08). Under semantic versioning that means 2.0.0, and package validation needs its baseline moved
  (or `ApiBaselineVersion` emptied) in the same PR.
- ShotDetector's exactness tricks (the CLI-equivalent swscale flags and colour tags, `FramePipeline` layouts, the
  remap sampling for `CvResize`, `PacketOf` frame labels, MPEG-PS timestamps from a decoding pass) are about
  matching scenedetect/OpenCV. Decide per item whether it belongs in the library or stays in ShotDetector on top
  of a lower-level hook (for example access to the decoded frame's planes).
- Same repository for now: `src/<LibraryName>/` and `tests/<LibraryName>.Tests/`, a neutral namespace, and a
  one-way dependency (ShotDetector references the library, never the reverse). Keep the project self-contained
  so `git filter-repo --subdirectory-filter` can move it to its own repository later.
- Licensing: the library MIT like ShotDetector, FFmpeg's libraries LGPL and dynamically loaded as now. Check that
  nothing LGPL from ShotDetector.FastYuv is pulled into the MIT library by accident.

## Before writing code

Ask the user (multiple-choice prompt, then decisions.md):

1. The library's name and namespace.
2. Native packages: keep `ShotDetector.Native.<rid>` and have the library load from it, or new
   `<LibraryName>.Native.<rid>` packages (and what happens to ShotDetector.Native for existing users).
3. Published on NuGet from the first version (prerelease), or internal to the repo until the API settles.
4. What the library promises about pixels: "the same bytes as the ffmpeg command line" as an option, or only
   correct conversion.

Then map `InProcessDecoder`, `InProcessProbe`, `StreamInput` and `VideoReader.InProcess`: which parts are general,
which are ShotDetector-specific. Propose the split and a public API sketch, and wait for approval.

## API direction

- Open once, keep the demuxer and decoder open; no process spawning anywhere in the library.
- Callers ask for a target size and pixel format (Gray8, Bgr24, Rgb24, Yuv420p...) and swscale does it, so a
  160x90 Gray8 reader never sees full-size frames.
- No per-frame allocations in steady state: pooled buffers, frames as `ReadOnlySpan<byte>` /
  `ReadOnlyMemory<byte>` with a documented lifetime ("valid until the next read" unless copied).
- Frame index and timestamp (PTS as `TimeSpan`) on each frame; decode threads and lookahead configurable.
- Probing (duration, streams, fps, size, codec, rotation) without decoding.
- `IDisposable` and correct native cleanup, including on exceptions and on abandoned enumeration.
- Clear errors when the libraries are missing or the wrong version (reuse ShotDetector's messages).

## Milestones (stop after each for review)

1. Project skeleton; native library loading and version check moved; ShotDetector uses it. All green.
2. Probing moved (`InProcessProbe`), with its ffprobe comparison tests.
3. The decoder core moved, behind the new frame API; ShotDetector's pipeline-specific writers stay in ShotDetector.
   Frames byte-identical (existing tests plus parity CI).
4. `Stream` input (`StreamInput`) moved.
5. Benchmarks (BenchmarkDotNet, plus the desktop rules in CLAUDE.md: pinned CPUs, CPU time, interleaved runs):
   the library vs FFMpegCore's raw-frame pipe vs the ffmpeg executable, at SD/720p/1080p, measuring fps,
   allocations and peak memory. Keep FFMpegCore in the benchmark project only.
6. Later: exact seeking and thumbnails (VideoReader's seek is the reference), audio, opt-in hardware decoding
   (never used by ShotDetector unless bit-exact).

## Quality bar

As the rest of the repository (CLAUDE.md): nullable, warnings as errors, XML docs on every public member, small
generated test clips rather than committed media, a README for the library (purpose, quick start, frame lifetime
rules, native libraries per platform), a backlog row per milestone, PRs through the normal pipeline.
