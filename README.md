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
dotnet tool install -g ShotDetector.Cli         # the shotdetect command
```

ffmpeg and ffprobe must be on `PATH` (or set `DetectionOptions.FfmpegDirectory` / `--ffmpeg-dir`).

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

The video can come in as a stream too: `Detect(Stream)` and `DetectStreamAsync(Stream)` pipe the bytes
straight into ffmpeg (no temporary file), so an upload or a pipe can be analysed while it arrives, and a
URL (`http`, `rtsp`, ...) or `Streaming = true` reads an input once, start to end, as a live feed. The
CLI reads standard input with `-i -`. The container's headers must come first: mkv, webm, ts, mov, or
an mp4 written with `-movflags +faststart` (a plain mp4 gives a clear error). Timestamps come from
ffmpeg as frames arrive, the frame count isn't known up front and `StartTime` isn't available; the
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

Interlaced sources (1080i broadcast): `Deinterlace = true` (CLI `--deinterlace`) runs ffmpeg's yadif before
analysis, so there is no need to re-encode first; results equal scenedetect's on a lossless `ffmpeg -vf yadif` copy.

Failures from ffmpeg, ffprobe or the input throw `ShotDetectionException` (an `InvalidOperationException`) whose
`Reason` tells a deployment problem (`FfmpegNotFound`) from bad input (`InvalidInput`), a decode failure
(`DecodeFailed`) or a failed export (`ExportFailed`).

Each detection is an OpenTelemetry-ready span: `.AddSource(ShotDetection.ActivitySourceName)` shows
"ShotDetection.Detect" with the video's size, frame rate, pipeline, frames and shots.

Runs wherever .NET 10 and ffmpeg do, including Alpine (`apk add ffmpeg`), which CI tests.

To reproduce scenedetect 0.6.4 instead of 0.7.1 (for consistency with older results), set
`Compatibility = PySceneDetectVersion.V0_6_4` (CLI: `--compat 0.6.4`). 0.6.4 analyses frames at a
different size (a whole-number downscale factor from the width, e.g. 854x480 at 285x160), counts
time in frames at OpenCV's average frame rate (no real timestamps, so variable frame rate video is
treated as constant), and rounds fade cuts differently. Cuts, CSVs, stats and exports match 0.6.4
exactly in that mode; CI checks both releases. (0.6.4's own threshold detector is nondeterministic
on Linux, where repeated runs give different results, so CI compares 0.6.4 on the content and adaptive
detectors; on Windows all three matched 0.6.4 exactly.)

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

CI (`.github/workflows/ci.yml`) checks every push on Linux, against scenedetect 0.7.1 and 0.6.4 (in
the matching compatibility mode), for the core path and the FastYuv path, with the latest stable
ffmpeg. On every sample clip and every detector:
- the cuts are the same frames;
- the scene list CSVs are identical cell for cell, timecodes and seconds included;
- every per-frame stat (`-s` / `--stats`) is printed identically, `delta_edges` included.

The comparison table is in each run's job summary. The same check runs locally:

```
pwsh tools/make-samples.ps1
python tools/compare.py samples/*.mp4 [--detector adaptive|content|threshold|both|all] [--stats] [--fast-yuv]
    [--compat 0.6.4 --scenedetect-python path/to/python] [--weights 1 1 1 1] [--report table.md]
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

Results depend on the platform and the ffmpeg build more than one would hope, so here are both.

**Linux, 2 CPUs** (GitHub runner, Alpine container with ffmpeg 8.1.2 for ShotDetector, scenedetect on the
host; Sintel trailer 1080p H.264, 1253 frames, content detector):

| | Wall time | CPU time |
|---|---|---|
| scenedetect 0.7.1 | 1.00x | 1.00x |
| ShotDetector 0.5.0 (core, MIT) | 1.55x | 1.51x |
| ShotDetector, unreleased (core, MIT) | 1.31x | 1.24x |
| ShotDetector, unreleased + FastYuv (LGPL) | 1.40x | 1.40x |

(Relative to scenedetect on the same runner, best of 3; runners differ in absolute speed.) Most of what
remains is the ffmpeg command line itself, which spends ~35% more CPU than OpenCV's in-process FFmpeg on
the same decoding and conversion (issue #12). Here the core path is faster than FastYuv, which still pipes
whole yuv420p frames.

**Windows desktop** (ffmpeg 7.1), 1920x1080 H.264, 2880 frames (2 minutes), content detector, pinned to
2 CPUs and with all 16; best of 2, wall times vary ±10% between runs. Memory is the peak of the whole
process tree (ShotDetector plus its ffmpeg, scenedetect's Python with OpenCV's FFmpeg inside):

| | 2 CPUs | 16 CPUs |
|---|---|---|
| scenedetect 0.7.1 | 19 s, 236 MB | 9.3 s, 231 MB |
| ShotDetector (core, MIT) | 19 s, 305 MB | 8.7 s, 284 MB |
| ShotDetector + FastYuv (LGPL) | 20 s, 268 MB | 5.1 s |

All exact variants give identical results. CPU time on 2 CPUs on this machine: ShotDetector 26 s
(ffmpeg 20, ours 6), scenedetect 30 s; on Linux the balance tips the other way (above).

How the core path keeps up without converting pixels itself:
- ffmpeg converts each frame to BGR exactly as OpenCV does, then its `remap` filter keeps only the
  pixels the exact `cv2.resize` port reads (512 x 288 of a 1080p frame: 0.44 MB instead of 6.2 MB per
  frame through the pipe, which used to cost as much CPU as the decoding);
- the resize and the HSV + difference scoring run on several cores; reading overlaps processing.

The FastYuv path goes further: ffmpeg sends raw yuv420p (no conversion at all) and
`SwscaleYuv420.cs`, a bit-exact port of swscale's converter, converts only the pixels the resize reads.
Decoding is then the bottleneck, so ffmpeg gets 8 decoder threads there (`--threads`; 4 on the core path).

In-process decoding (`Decoder = VideoDecoder.InProcess`, CLI `--decoder inprocess`) decodes with FFmpeg's
shared libraries inside our process, as OpenCV does, instead of running the ffmpeg executable: the same
frames (CI compares them), with ~40% less CPU for decoding and converting on Linux. It needs FFmpeg 8.1's
shared libraries (a "shared" build; `--ffmpeg-dir` / `FfmpegDirectory` names their folder). Streams and URLs,
image sequences and rotated video still use the executable.

Memory: most of it is ffmpeg's own frame queues and decoder threads (whole decoded frames); our
process takes ~50 MB. `--threads 1` (`DecodeThreads = 1`) lowers ffmpeg's share on many-core machines,
at the cost of decoding speed.

Files in other pixel formats (10-bit, 4:2:2/4:4:4, full-range MJPEG) or with an odd height take a
slower path where ffmpeg converts whole frames, still with identical results.

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

## License

MIT ([LICENSE](LICENSE)), except `src/ShotDetector.FastYuv`, which is LGPL-2.1-or-later because it
ports FFmpeg's libswscale. The MIT library contains ports from PySceneDetect (BSD 3-Clause) and
OpenCV (Apache 2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
