# ShotDetector

[![CI](https://github.com/bisforboman/shotdetector/actions/workflows/ci.yml/badge.svg)](https://github.com/bisforboman/shotdetector/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.svg?label=ShotDetector)](https://www.nuget.org/packages/ShotDetector)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.FastYuv.svg?label=ShotDetector.FastYuv)](https://www.nuget.org/packages/ShotDetector.FastYuv)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.Cli.svg?label=ShotDetector.Cli)](https://www.nuget.org/packages/ShotDetector.Cli)

Shot/cut detection for video in C#: a faithful port of [PySceneDetect](https://github.com/Breakthrough/PySceneDetect)'s
detectors (scenedetect 0.7.1: adaptive, content, threshold, histogram and hash) that gives identical
results, about 1.5x faster than scenedetect at 1080p with the optional fast path. Video is decoded by [ffmpeg](https://ffmpeg.org), which must be
installed and on `PATH`; all detection logic is plain C#.

- `src/ShotDetector`: the library, NuGet package `ShotDetector` (MIT).
- `src/ShotDetector.FastYuv`: optional fast path for large video, NuGet package
  `ShotDetector.FastYuv` (LGPL-2.1-or-later, since it ports FFmpeg code).
- `src/ShotDetector.Cli`: the `shotdetect` command-line tool, as the .NET tool `ShotDetector.Cli` and
  as self-contained Native AOT binaries on each GitHub release (core only, MIT).

## Install

```
dotnet add package ShotDetector                 # the library
dotnet add package ShotDetector.FastYuv         # optional, LGPL: faster on large video
dotnet add package ShotDetector.Native.linux-x64  # optional, LGPL: FFmpeg's libraries for in-process decoding
dotnet tool install -g ShotDetector.Cli         # the shotdetect command (bundles those libraries)
```

When FFmpeg 8's shared libraries are available, video files are read and decoded in-process, with no ffmpeg or
ffprobe needed: faster and lighter, with the same results (see Performance). The `ShotDetector.Native.<rid>`
packages (`win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`) carry them, built by us for
decoding only; the CLI tool and binaries bundle them. A "shared" FFmpeg build, or `ffmpeg-libs` on Alpine, works
too.

The ffmpeg and ffprobe executables (on `PATH`, or in `DetectionOptions.FfmpegDirectory` / `--ffmpeg-dir`) are still
needed without those libraries, and for URLs and the image and clip exports. Files of every kind decode
in-process, including image sequences, rotated phone video, AV1 (dav1d in our build) and deinterlacing; a codec the
libraries can't decode, a rotation that isn't a quarter turn and rotated video that is deinterlaced use the executable.

Releases are built by GitHub Actions from the tagged commit: packages carry SourceLink and symbol packages, and
every package and binary has a signed build attestation (`gh attestation verify <file> --repo bisforboman/shotdetector`).

## Library

```csharp
using ShotDetector;

var result = ShotDetection.Detect("video.mp4");      // adaptive detector, scenedetect's defaults
foreach (var shot in result.Shots)
    Console.WriteLine($"{shot.Number}: {shot.Start} - {shot.End}");   // timecodes, e.g. 00:00:02.000

// Without blocking the calling thread (web apps, UIs):
var later = await ShotDetection.DetectAsync("video.mp4");

// Other detectors and settings; Export uses the shot list.
var fades = ShotDetection.Detect("video.mp4", new DetectionOptions { Detector = DetectorKind.Threshold });
Export.SaveImages(result, "thumbs");
File.WriteAllText("shots.csv", Shots.Csv(result.Shots));

// Optional, from ShotDetector.FastYuv (LGPL): ~1.7x faster on 1080p, identical results.
var fast = ShotDetection.Detect("video.mp4", new DetectionOptions { Yuv420Converter = new ShotDetector.FastYuv.SwscaleYuv420() });
```

`Detect`, `DetectAsync`, `Export.SaveImages` and `Export.SplitVideo` take an optional `CancellationToken`: cancelling
stops at the next frame (or clip), kills the ffmpeg process and throws `OperationCanceledException`.

To get shots while a long video is still being decoded, stream them (same results as `Detect`):

```csharp
await foreach (var shot in ShotDetection.DetectStreamAsync("video.mp4", options, cancellationToken))
    Console.WriteLine($"{shot.Number}: {shot.Start.Timecode()} - {shot.End.Timecode()}");
```

A shot arrives once the cut that ends it is confirmed (a few frames later); the last one at the end.

The video can come in as a stream too: `Detect(Stream)` and `DetectStreamAsync(Stream)` read the bytes as they
come (no temporary file; in-process with FFmpeg's libraries, else piped into ffmpeg), so an upload or a pipe can be
analysed while it arrives, and a
URL (`http`, `rtsp`, ...) or `Streaming = true` reads an input once, start to end, as a live feed. The
CLI reads standard input with `-i -`. That works when the container's headers come first: mkv, webm, ts,
mov, or an mp4 written with `-movflags +faststart`. An ordinary mp4 keeps them at the end, so it is copied to a
temporary file first (deleted afterwards) and read as a file, with the same results as its path. Timestamps
come from ffmpeg as frames arrive, the frame count isn't known up front and `StartTime` isn't available; the
exports need a path.

All five scenedetect detectors are available: `DetectorKind.Adaptive`, `Content`, `Threshold`,
`Histogram` and `Hash` (CLI `-d adaptive|content|threshold|hist|hash`), with scenedetect's defaults.
Several can run at once and their cuts are combined, as with several `detect-*` commands:
`Detectors = [new(DetectorKind.Content) { Threshold = 30 }, new(DetectorKind.Threshold)]` (CLI
`-d content -t 30 -d threshold`: `-t`, `-m` and `--filter-mode` after a `-d` belong to it). Also as in
scenedetect: `DropShortScenes`, `MergeLastScene`, `FilterMode = FlashFilterMode.Suppress` and
`Downscale` (CLI `--drop-short-scenes`, `--merge-last-scene`, `--filter-mode`, `--downscale`).
`DetectionResult.Cuts` is scenedetect's cut list, which keeps the cuts of dropped or merged shots.

Like scenedetect's `time`, `--frame-skip` and `--crop`: `StartTime`, `EndTime` and `Duration`
("HH:MM:SS.mmm", "12.5s", or a frame number; CLI `-s`, `-e`, `--duration`), `FrameSkip` (analyse
every (n+1)th frame; CLI `--frame-skip`) and `Crop` (inclusive pixel corners; CLI `--crop x0 y0 x1 y1`)
give the same shots as scenedetect with those options. On variable frame rate video a start time
picks the first frame at or after it (scenedetect's own seek is an artifact of OpenCV's frame
counting there). `Export.SaveImages` takes an `ImageOptions` (count, margin, width/height/scale,
jpg/png/webp).

`DetectionOptions.Progress` (an `IProgress<DetectionProgress>`) reports frames done out of the
expected total about 10 times a second. ffmpeg and ffprobe are found on `PATH`, or in
`DetectionOptions.FfmpegDirectory` (CLI: `--ffmpeg-dir`).

Interlaced sources (1080i broadcast): `Deinterlace = DeinterlaceMode.On` (CLI `--deinterlace`) runs ffmpeg's yadif
before analysis, so there is no need to re-encode first; results equal scenedetect's on a lossless `ffmpeg -vf yadif`
copy. `DeinterlaceMode.Auto` (`--deinterlace auto`) does so only when the stream is flagged interlaced. With
ShotDetector.FastYuv, `Yadif = new ShotDetector.FastYuv.Yadif()` deinterlaces in-process only the rows the resize
reads (same results, about a third faster with few cores).

`ShotDetection.Probe(path)` / `ProbeAsync` reads a video's headers in milliseconds, without ffprobe when FFmpeg's
libraries load: size, frame rate, frame count, duration, codec, pixel format, field order (`IsInterlaced`),
rotation, container name and whether there is an audio stream, plus `Streams`: every stream (video, audio,
subtitles, data) with its codec and codec tag, language, title, default/forced flags, bit rate, duration and audio
details (sample rate, channels, layout, sample format), as ffprobe's `-show_streams` gives them. The same
properties are on `DetectionResult.Video` after a detection.

Which decoder a run uses is in every `DetectionProgress` report (`DecodesInProcess`, `Pipeline`), from a first report
sent before any frame, so `DetectStreamAsync` callers see it too. `ShotDetection.CanDecodeInProcess()` tells a worker at
startup whether FFmpeg's libraries load; `VideoDecoder.InProcess` without them throws `ShotDetectionException` with
`Reason = FfmpegLibrariesNotFound`, where `Auto` falls back to the executable.

Failures from ffmpeg, ffprobe or the input throw `ShotDetectionException` (an `InvalidOperationException`) whose
`Reason` tells a deployment problem (`FfmpegNotFound`, `FfmpegLibrariesNotFound`) from bad input (`InvalidInput`), a
decode failure (`DecodeFailed`) or a failed export (`ExportFailed`).

Each detection is an OpenTelemetry-ready span: `.AddSource(ShotDetection.ActivitySourceName)` shows
"ShotDetection.Detect" with the video's size, frame rate, pipeline, frames and shots.

Runs wherever .NET 10 and ffmpeg do, including Alpine (`apk add ffmpeg`), which CI tests.

ShotDetector reproduces scenedetect 0.7.1 (`Compatibility = PySceneDetectVersion.V0_7_1`, the default). When
the default moves to a newer scenedetect, the previous release stays available there (see "Versions"). The
scenedetect 0.6.4 mode was dropped in 0.9.0; use ShotDetector 0.8 for it.

`Shot.Start`/`End` are `FrameTime`s: `FrameNum` (0-based; `End` is exclusive), `Seconds`,
`Timecode()` (also what `ToString()` gives). They print exactly as scenedetect prints them.

## Command line

```
shotdetect -i video.mp4 [-d adaptive|content|threshold|hist|hash] [--csv shots.csv] [--json shots.json] [--stats stats.csv]
```

Output matches `scenedetect -i video.mp4 detect-adaptive list-scenes`: the start frame is 1-based,
the end frame is inclusive, and timecodes are `HH:MM:SS.mmm`. `--csv` writes the same scene list CSV
as scenedetect, byte for byte (`--skip-cuts` leaves out its "Timecode List" row, `-q` the printed
table), and `--stats` writes the per-frame metrics file that `scenedetect -s` writes. `-i -` reads
standard input; `--frame-rate` overrides the frame rate (scenedetect `-f`), also for image
sequences such as `frames/%04d.png`. Defaults match the scenedetect CLI:
content threshold 27, adaptive threshold 3, min-content-val 15, frame window 2, fade threshold 12,
min scene length 0.6s. `--help` lists all options.

## Using the shot list

```
shotdetect -i video.mp4 --save-images thumbs --save-html thumbs/video-Scenes.html --split-video clips
```

- `--save-images <dir>` writes `{video}-Scene-{NNN}-{II}.jpg`, 3 per shot (`--num-images`): one a
  frame in from the start, one mid-shot, one a frame before the end. They are the same frames
  scenedetect's `save-images` picks (checked on 120 thumbnails), encoded by ffmpeg instead of
  OpenCV, so similar but not byte-identical files.
- `--save-html <file>` writes scenedetect's `save-html` page (cut list and shot table with the
  thumbnails), byte-identical to scenedetect's; `--html-no-images`, `--html-image-width/height`.
  In the library: `Shots.Html`.
- `--save-edl`, `--save-fcp` (`--fcp-format fcpx|fcp7`), `--save-otio` and `--save-qp` write scenedetect's
  timeline exports for editors and encoders (CMX 3600 EDL, Final Cut Pro XML, OpenTimelineIO, x264 QP
  keyframes), byte-identical to scenedetect's except the EDL's comment line, which names ShotDetector.
  Like scenedetect, the OTIO clips declare an available range of 1980 frames whatever the video's
  length. In the library: `Timeline.Edl`, `Fcpx`, `Fcp7`, `Otio`, `Qp`.
- `--split-video <dir>` writes `{video}-Scene-{NNN}.mp4` with scenedetect's `split-video` ffmpeg
  command (libx264 veryfast CRF 22, AAC, subtitles dropped), and its options: `--split-copy`,
  `--split-high-quality`, `--split-crf`, `--split-preset`, `--split-args`, `--split-expand`
  (`SplitOptions` in the library). The clips came out byte-identical to scenedetect's for every
  option.
- `--load-scenes <csv>` takes the shots from a scene list CSV (ours or scenedetect's) instead of
  detecting, for re-exporting an edited list (`ShotDetection.LoadScenes`); `--load-scenes-column`
  picks the start column (`Start Frame` by default, or a timecode/seconds column). Byte-identical to
  scenedetect's load-scenes, also with time ranges, `--merge-last-scene` and `--drop-short-scenes`.
- `--config <file>` reads a scenedetect config file (scenedetect.cfg): its `[global]`, `[detect-*]` and
  output sections become the defaults, which command-line options override. Keys ShotDetector doesn't
  have are listed on stderr. (`-c` stays `--min-content-val`, as in `detect-adaptive -c`.)
- File names follow scenedetect's templates (`--image-filename`, `--split-filename`;
  `ImageOptions.FileName`, `SplitOptions.FileName`), with the same variables (`$VIDEO_NAME`,
  `$SCENE_NUMBER`, `$IMAGE_NUMBER`, `$FRAME_NUMBER`, `$TIMESTAMP_MS`, `$TIMECODE`, `$START_TIME`, ...).
  `-o <dir>` puts outputs given as relative paths there, and `$VIDEO_NAME` works in any output path.

## Verifying against PySceneDetect

CI (`.github/workflows/ci.yml`) checks every pull request on Linux against scenedetect 0.7.1, for the core path
and the FastYuv path, with the latest stable
ffmpeg. On every sample clip and every detector:
- the cuts are the same frames;
- the scene list CSVs are identical cell for cell, timecodes and seconds included;
- every per-frame stat (`-s` / `--stats`) is printed identically, `delta_edges` included.

The comparison table is in each run's job summary. The same check runs locally:

```
pwsh tools/make-samples.ps1
python tools/compare.py samples/*.mp4 [--detector adaptive|content|threshold|both|all] [--stats] [--fast-yuv]
    [--compat 0.7.1 --scenedetect-python path/to/python] [--weights 1 1 1 1] [--report table.md]
dotnet test
```

`tools/make-samples.ps1` recreates the test clips in the gitignored `samples/`:
- a synthetic clip with hard cuts between ffmpeg test sources, including one 12-frame shot;
- the Sintel and Big Buck Bunny trailers (Blender open movies, CC-BY) in several codecs, containers
  and resolutions (h264 mp4/mov/m4v/mkv, Theora ogg, 270p–1080p; one tagged BT.709);
- the Sintel trailer re-timed to 23.976, 29.97 and 60 fps, two variable frame rate versions
  (24 → 48 fps, and phone-like jittery timestamps), with phone-style rotation tags (90°, 180°), and
  as full-range, MJPEG and 4:4:4 video.

On Windows, compare.py differs from scenedetect on BT.709-tagged video: the Windows OpenCV wheel
ignores colour tags (see below). With `--ffmpeg-resize`, cuts on real footage differ (2–6 per trailer).

## Performance

Measured by `benchmark.yml` (weekly, `tools/bench.py`) on GitHub-hosted Linux runners, x64 and ARM (4 CPUs each),
content detector, best of 3, on 1.0.0's code (2026-10-09). ShotDetector runs as installed: in-process decoding
with its bundled FFmpeg libraries (the default), and, for comparison, with the ffmpeg executable (`--decoder
process`, FFmpeg 9.0).
Each cell is wall time, CPU time and peak memory; CPU time counts every process (ffmpeg included), memory is the
peak of the whole process tree. All give identical results.

**Pinned to 2 CPUs** (a small container, issue #7):

| Video | Runner | scenedetect 0.7.1 | ShotDetector (in-process) | ShotDetector (ffmpeg executable) |
|---|---|---|---|---|
| Sintel trailer, 1920x1080, 1253 frames | x64 | 6.4 s, 11.3 s, 140 MB | **5.9 s**, 10.4 s, 113 MB | 7.5 s, 11.8 s, 177 MB |
| | ARM | 5.2 s, 8.6 s, 132 MB | **4.3 s**, 7.5 s, 114 MB | 5.9 s, 9.0 s, 167 MB |
| Tears of Steel, 1280x534, 17620 frames | x64 | 43.7 s, 72.3 s, 103 MB | **42.5 s**, 72.6 s, 90 MB | 55.5 s, 91.6 s, 161 MB |
| | ARM | 39.6 s, 59.9 s, 94 MB | **32.3 s**, 54.4 s, 86 MB | 42.1 s, 68.0 s, 150 MB |
| Big Buck Bunny, 640x360, 14315 frames | x64 | 13.4 s, 22.4 s, 93 MB | **12.8 s**, 23.1 s, 81 MB | 15.2 s, 28.4 s, 135 MB |
| | ARM | 10.6 s, 19.4 s, 85 MB | **9.2 s**, 16.2 s, 80 MB | 11.7 s, 21.5 s, 129 MB |
| Elephants Dream, 426x240, 15691 frames | x64 | 10.1 s, 16.3 s, 90 MB | **7.9 s**, 14.1 s, 79 MB | 10.4 s, 19.0 s, 126 MB |
| | ARM | 8.5 s, 15.3 s, 83 MB | **6.5 s**, 11.4 s, 78 MB | 8.8 s, 15.9 s, 118 MB |

**All 4 CPUs:**

| Video | Runner | scenedetect 0.7.1 | ShotDetector (in-process) | ShotDetector (ffmpeg executable) |
|---|---|---|---|---|
| Sintel trailer 1080p | x64 | 3.7 s, 11.2 s, 155 MB | **3.3 s**, 10.2 s, 126 MB | 4.2 s, 13.1 s, 205 MB |
| | ARM | 2.9 s, 8.6 s, 149 MB | **2.5 s**, 7.4 s, 126 MB | 3.1 s, 9.5 s, 196 MB |
| Tears of Steel | x64 | 25.3 s, 70.3 s, 110 MB | **23.7 s**, 70.8 s, 95 MB | 31.1 s, 94.8 s, 170 MB |
| | ARM | 22.7 s, 59.3 s, 100 MB | **18.6 s**, 53.9 s, 92 MB | 24.4 s, 71.4 s, 161 MB |
| Big Buck Bunny | x64 | 8.3 s, 22.1 s, 95 MB | **7.2 s**, 21.1 s, 84 MB | 8.6 s, 28.5 s, 143 MB |
| | ARM | 8.0 s, 25.6 s, 88 MB | **5.6 s**, 16.4 s, 83 MB | 6.8 s, 22.2 s, 135 MB |
| Elephants Dream | x64 | 6.7 s, 16.1 s, 92 MB | **6.2 s**, 12.9 s, 83 MB | 7.2 s, 19.8 s, 130 MB |
| | ARM | 7.0 s, 22.7 s, 85 MB | **5.6 s**, 12.3 s, 83 MB | 5.7 s, 17.0 s, 124 MB |

Runners differ in absolute speed; compare within a row. In-process decoding is faster than scenedetect on every
film, runner and CPU count: 3-22% on x64, 13-30% on ARM (NEON). Its CPU time is from 3% more to 20% less than
scenedetect's on x64 and 9-46% less on ARM, and it uses less memory everywhere (2-29 MB). The ffmpeg executable
costs 13-53% more CPU than decoding in-process (issue #12) and 1.5-1.8x the memory (its own frame queues).

In-process decoding uses FFmpeg's shared libraries inside our process, as OpenCV does: the `ShotDetector.Native`
packages or the CLI's bundled ones, or a system install. It is the default whenever they load
(`Decoder = VideoDecoder.Auto`; from `FfmpegDirectory` / `--ffmpeg-dir`, else next to the app, else the system's
usual places); otherwise the executable is used. `--decoder inprocess` insists on the libraries, `--decoder
process` on the executable. URLs use the executable (our build has no network protocols).

How it keeps up while computing exactly what OpenCV and scenedetect compute:
- only the pixels the exact `cv2.resize` port reads are converted to BGR (in-process, the row slices that hold
  them; with the executable, ffmpeg's `remap` filter keeps just those, or whole frames when that is cheaper);
- the resize, BGR to HSV and the frame differences are vectorised (AVX2), with the same integer arithmetic;
- decoding overlaps scoring, with all the CPUs as decoder threads in-process (`--threads` to change).

The FastYuv package (LGPL) is an alternative for the executable path: ffmpeg sends raw yuv420p and a bit-exact
port of swscale's converter converts only the pixels the resize reads. Files in other pixel formats (10-bit,
4:2:2/4:4:4, full-range MJPEG) or with an odd height convert whole frames, still with identical results.

## Where this differs from PySceneDetect

- **Downscaling:** by default ffmpeg sends full-size frames and `CvResize.cs` replicates
  `cv2.resize(INTER_LINEAR)` bit for bit, at the size PySceneDetect picks (`max(w,h)/256`). That's
  slow for big video because a 1080p frame is 6 MB through the pipe. `--ffmpeg-resize` lets
  ffmpeg's bilinear scaler downscale instead. It's faster and averages over the whole footprint,
  whereas cv2 samples only 2x2 pixels and aliases. Content_val therefore comes out lower on fine
  detail, and cuts can differ.
- **Colour conversion follows the video's colour tags** (BT.709, BT.2020, full range, ...;
  BT.601 limited range when untagged), as OpenCV does with FFmpeg 8. Note that scenedetect itself
  is not consistent here: the opencv-python wheels for Linux bundle FFmpeg 8 and follow the tags,
  while the Windows wheels (FFmpeg 7.1) convert everything as BT.601. ShotDetector matches
  scenedetect on Linux; on Windows they differ only for tagged video, by enough to flip borderline
  cuts. CI compares against scenedetect on Linux with the latest stable ffmpeg (9.0).
- **Timestamps:** scenedetect prints times from OpenCV's frame positions (container pts → ms →
  rounded to µs, with Python's exact-binary rounding), not from frame/fps. `FrameTime.cs` reproduces
  that, taking the pts from an extra demux-only ffprobe pass. That assumes OpenCV's best-effort
  timestamps equal the sorted packet pts, which held for every sample but may not for streams with
  missing or broken timestamps.
- **Interlaced video, not deinterlaced:** opencv-python 5.0.0.93 (FFmpeg 8) can't convert interlaced frames to BGR
  (swscale: "Cannot convert interlaced to progressive frames") and returns the same nearly black image for every
  frame, so scenedetect finds nothing (or a fade at frame 0) on interlaced MPEG-2, AVC-Intra or ProRes. ShotDetector
  analyses the real frames instead of reproducing that. With `DeinterlaceMode.On` or `Auto` the results equal
  scenedetect's on a deinterlaced copy, which OpenCV reads correctly (checked in CI on 1080i XDCAM HD).
- **10-bit video (Linux):** OpenCV's bundled FFmpeg converts 10-bit frames to 8-bit BGR with ±1
  differences in red and green on some pixels that no ffmpeg command-line option reproduces (tested
  FFmpeg 8.1 and 9.0, every swscale flag and dither mode). Per-frame stats differ slightly; the cuts
  were identical on every 10-bit clip tested. On Windows, 10-bit matched exactly.
- **Damaged video:** when the decoder drops frames it can't decode, every frame still gets its own
  time and frame number, as with OpenCV (numbers skip; identical to scenedetect on the damaged files
  tested). Error concealment on damaged data can differ in a few pixels, since OpenCV runs FFmpeg's
  decoder with other threading. On a **resolution change** mid-stream, OpenCV returns the last frame
  from before the change over and over, so scenedetect sees no cuts after it; ShotDetector keeps
  decoding (ffmpeg scales the new frames to the first size) and finds them.
- **Variable frame rate:** handled as PySceneDetect handles it. The fps is the *average* frame rate
  (what OpenCV reports, through PySceneDetect's `framerate_to_fraction`), min-scene-len is measured
  in time between frames, and printed frame numbers are `round(time × average fps)`, not decode
  order, so they can repeat or skip on VFR video.
- **min_scene_len:** ContentDetector reproduces PySceneDetect's float-seconds comparison on
  µs-rounded frame times, so a gap of exactly min_scene_len frames is sometimes rejected (as it is
  there). It assumes constant frame rate starting at 0; variable frame rate isn't handled.
- **Faithfully kept quirks:** AdaptiveDetector measures min_scene_len from the current frame rather
  than the cut frame (so cuts are allowed `window` frames early), and a merge still pending at the
  end of the video is dropped.
- **ThresholdDetector:** only the FLOOR method (fades to black) is ported, which is all the CLI
  uses. `--fade-bias` takes the detector's -1..1 meaning; the scenedetect CLI passes its value
  through unscaled although its option accepts -100..100.
- **Edges:** `EdgeDetector.cs` ports cv2.Canny (aperture 3, L1 gradient) and cv2.dilate bit for bit.
  As in PySceneDetect, edges are only computed when weighted (`-w h s l e`, `-k` kernel size) or
  when writing stats, because they cost time.
- **CLI spelling:** `-w/--weights` takes four numbers and `-k/--kernel-size` matches scenedetect's
  options; `--frame-window` is long-only here because `-f` is `--fade-bias`.
- **detect-hist and detect-hash:** `DetectorKind.Histogram` (`--detector hist`) is bit-exact
  (`EdgeDetector`-style ports of calcHist, normalize and compareHist). `DetectorKind.Hash`
  (`--detector hash`) ports the grayscale conversion and INTER_AREA resize bit-exactly but computes
  the DCT in double precision, so on flat frames (fades to black), where the hash is DCT rounding
  noise in scenedetect too, a few hash values and the odd cut inside a fade differ.
- **Not ported:** frame skip, crop.

## Versions

From 1.0, semantic versioning (before it, minor versions could change the API); packing the libraries fails on a
breaking change against the last stable release (the SDK's package validation):

- **Major**: removing or changing public API, or changing default results (cuts, scene list CSVs, stats) for
  any reason other than the one below.
- **Minor**: new options and outputs, speed and memory, and following a new scenedetect release: the default
  compatibility mode tracks the newest scenedetect, since matching it is the point (the previous release stays
  available as a `PySceneDetectVersion` value).
- **Patch**: fixes that make results match scenedetect where they didn't.

## License

MIT ([LICENSE](LICENSE)), except `src/ShotDetector.FastYuv`, which is LGPL-2.1-or-later because it
ports FFmpeg's libswscale. The MIT library contains ports from PySceneDetect (BSD 3-Clause) and
OpenCV (Apache 2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
