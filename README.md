# ShotDetector

[![CI](https://github.com/bisforboman/shotdetector/actions/workflows/ci.yml/badge.svg)](https://github.com/bisforboman/shotdetector/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.svg?label=ShotDetector)](https://www.nuget.org/packages/ShotDetector)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.FastYuv.svg?label=ShotDetector.FastYuv)](https://www.nuget.org/packages/ShotDetector.FastYuv)
[![NuGet](https://img.shields.io/nuget/v/ShotDetector.Cli.svg?label=ShotDetector.Cli)](https://www.nuget.org/packages/ShotDetector.Cli)
[![NuGet](https://img.shields.io/nuget/v/FrameReader.svg?label=FrameReader)](https://www.nuget.org/packages/FrameReader)

Shot/cut detection for video in C#: a faithful port of [PySceneDetect](https://github.com/Breakthrough/PySceneDetect)'s
detectors (scenedetect 0.7.2: adaptive, content, threshold, histogram and hash) that gives identical
results, faster and lighter than scenedetect ([Performance](#performance)). Video is decoded in-process by FFmpeg's
libraries (the `FrameReader.Native` packages carry them) or by the [ffmpeg](https://ffmpeg.org) executable; all
detection logic is plain C#.

- `src/ShotDetector`: the library, NuGet package `ShotDetector` (MIT).
- `src/ShotDetector.FastYuv`: optional fast path for large video, NuGet package
  `ShotDetector.FastYuv` (LGPL-2.1-or-later, since it ports FFmpeg code).
- `src/ShotDetector.Cli`: the `shotdetect` command-line tool, as the .NET tool `ShotDetector.Cli` and
  as self-contained Native AOT binaries on each GitHub release (core only, MIT).

ShotDetector reads video with [FrameReader](src/FrameReader/README.md), a library of its own in this repository:
frames, audio, writing and probing in-process through FFmpeg's libraries, for any .NET app (NuGet `FrameReader`, MIT,
with its own versions and changelog; FFmpeg's libraries in `FrameReader.Native`).

## Install

```
dotnet add package ShotDetector                 # the library
dotnet add package ShotDetector.FastYuv         # optional, LGPL: x86's results on ARM
dotnet add package FrameReader.Native.linux-x64  # optional, LGPL: FFmpeg's libraries for in-process decoding
dotnet tool install -g ShotDetector.Cli         # the shotdetect command (needs the .NET 10 SDK; bundles those libraries)
```

Or `shotdetect` without .NET: [the release binaries](#the-release-binaries).

When FFmpeg 8's shared libraries are available, video files are read and decoded in-process, with no ffmpeg or
ffprobe needed: faster and lighter, with the same results (see Performance). The `FrameReader.Native.<rid>`
packages (`win-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`) carry them, built by us for
decoding only; the CLI tool and binaries bundle them. A "shared" FFmpeg 8 build, or `ffmpeg-libs` on Alpine, works
too. FFmpeg 9's libraries (libavcodec 63) don't load in-process: with only those, `Auto` uses the executable, and an
FFmpeg 9 executable gives the same results (CI's comparison with scenedetect runs on it). We move to FFmpeg 9's
libraries when OpenCV's wheels do, since scenedetect's frames come from OpenCV's FFmpeg.

The ffmpeg and ffprobe executables (on `PATH`, or in `DetectionOptions.FfmpegDirectory` / `--ffmpeg-dir`) are still
needed without those libraries, for URLs whose protocol the libraries lack (FrameReader.Native has http and
https), and for the clip export (`--split-video`); the images (`--save-images`, `--save-html`'s thumbnails) are written
in-process when the video was decoded there. Files of every kind decode
in-process, including image sequences, rotated phone video, AV1 (dav1d in our build) and deinterlacing; a codec the
libraries can't decode and a rotation that isn't a quarter turn use the executable.

Releases are built by GitHub Actions from the tagged commit: packages carry SourceLink and symbol packages, and
every package and binary has a signed build attestation (`gh attestation verify <file> --repo bisforboman/shotdetector`,
the file a package or a binary's archive).

### The release binaries

Each [GitHub release](https://github.com/bisforboman/shotdetector/releases/latest) has `shotdetect` as a Native AOT
binary, `shotdetect-vX.Y.Z-<rid>.tar.gz` (`.zip` on Windows) for `linux-x64`, `linux-arm64`, `linux-musl-x64`
(Alpine), `osx-arm64` and `win-x64`. Nothing needs to be installed, neither .NET nor ffmpeg: the archive holds
`shotdetect` with FFmpeg's libraries next to it (our build, LGPL, with its license files), which it decodes with. Keep
them in one folder. Unpack and run:

```
# Linux, Alpine and macOS (the archive has no folder of its own)
mkdir shotdetect && tar -xzf shotdetect-vX.Y.Z-linux-x64.tar.gz -C shotdetect
xattr -dr com.apple.quarantine shotdetect   # macOS, for an archive downloaded in a browser (below)
./shotdetect/shotdetect -i video.mp4

# Windows (PowerShell)
Expand-Archive shotdetect-vX.Y.Z-win-x64.zip shotdetect
.\shotdetect\shotdetect.exe -i video.mp4
```

The macOS binary isn't notarized (that needs an Apple Developer account), so Gatekeeper blocks it and its libraries
when the archive came through a browser (a "cannot be opened" or "not verified" message) until `xattr` clears the
quarantine; `curl` downloads aren't quarantined.

Put the folder on `PATH` to run `shotdetect` from anywhere; `shotdetect --version` says which release it is. Like
the .NET tool, the binaries still need the ffmpeg and ffprobe executables (on `PATH`, or `--ffmpeg-dir`) for what
the libraries don't do: the clip export (`--split-video`), URLs whose protocol the libraries lack (they have http and
https), and DASH, which the libraries can't read (`--decoder process`). The images (`--save-images`, `--save-html`'s
thumbnails) are written in-process too.

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

// Optional, from ShotDetector.FastYuv (LGPL): identical results, and x86's on ARM too.
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
`Histogram` and `Hash` (CLI `-d adaptive|content|threshold|hist|hash`), with scenedetect's defaults: its command
line's (`scenedetect detect-*`), which differ from its Python API's in places: detect-threshold's `MinOutLength` is
0.1 s, where `ThresholdDetector()` takes 0.
Several can run at once and their cuts are combined, as with several `detect-*` commands:
`Detectors = [new(DetectorKind.Content) { Threshold = 30 }, new(DetectorKind.Threshold)]` (CLI
`-d content -t 30 -d threshold`: `-t`, `-m` and `--filter-mode` after a `-d` belong to it). Also as in
scenedetect: `DropShortScenes`, `MergeLastScene`, `FilterMode = FlashFilterMode.Suppress` and
`Downscale` (CLI `--drop-short-scenes`, `--merge-last-scene`, `--filter-mode`, `--downscale`).
`DetectionResult.Cuts` is scenedetect's cut list, which keeps the cuts of dropped or merged shots.

Like scenedetect's `time`, `--frame-skip` and `--crop`: `StartTime`, `EndTime` and `RangeDuration`
("HH:MM:SS.mmm", "12.5s", or a frame number; CLI `-s`, `-e`, `--duration`; or as `TimeSpan`s: `Start`, `End`,
`Duration`, and `MinSceneDuration`/`MinOutDuration` for the minimum lengths), `FrameSkip` (analyse
every (n+1)th frame; CLI `--frame-skip`) and `Crop` (inclusive pixel corners; CLI `--crop x0 y0 x1 y1`; the result's `Video.Crop` gives them back, ordered and clamped)
give the same shots as scenedetect with those options. On variable frame rate video a start time
picks the first frame at or after it (scenedetect's own seek is an artifact of OpenCV's frame
counting there). `Export.SaveImages` takes an `ImageOptions` (count, margin, width/height/scale,
jpg/png/webp).

`DetectionOptions.Progress` (an `IProgress<DetectionProgress>`) reports frames done out of the
expected total about 10 times a second (`ProgressStep = 0.1`: once per 10 %, for a log). ffmpeg and ffprobe are found on `PATH`, or in
`DetectionOptions.FfmpegDirectory` (CLI: `--ffmpeg-dir`).

Interlaced sources (1080i broadcast): `Deinterlace = DeinterlaceMode.On` (CLI `--deinterlace`) runs ffmpeg's yadif
before analysis, so there is no need to re-encode first; results equal scenedetect's on a lossless `ffmpeg -vf yadif`
copy. `DeinterlaceMode.Auto` (`--deinterlace auto`) does so only when the stream is flagged interlaced. With
ShotDetector.FastYuv, `Yadif = new ShotDetector.FastYuv.Yadif()` deinterlaces in-process only the rows the resize
reads (same results, about a third faster with few cores). At 9 to 16 bits (10-bit AVC-Intra, ProRes) FFmpeg 8.1's
x86 SIMD yadif gives a slightly different picture on every run (its C code doesn't), so without FastYuv, as with
`ffmpeg -vf yadif`, the stats of such a file can differ from run to run; FastYuv's `Yadif` follows the C code and is
deterministic (equal to `ffmpeg -cpuflags 0 -vf yadif`).

`ShotDetection.Probe(path)` / `ProbeAsync` reads a video's headers in milliseconds, without ffprobe when FFmpeg's
libraries load: size, frame rate, frame count, duration, codec, pixel format, field order (`IsInterlaced`),
rotation, container name and whether there is an audio stream, plus `Streams`: every stream (video, audio,
subtitles, data) with its codec and codec tag, language, title, default/forced flags, bit rate, duration and audio
details (sample rate, channels, layout, sample format), as ffprobe's `-show_streams` gives them. The same
properties are on `DetectionResult.Video` after a detection.

Reference tone (line-up tone, a steady 1 kHz sine, often with colour bars, before or after the programme):
`ShotDetection.FindReferenceTone(path)` lists its runs (start, end, level in dBFS) in a video's or an audio-only file's
audio, and `TrimReferenceTone = true` leaves it out of detection: detection starts after a run that begins in the first
5 s and stops before one that ends in the last 5 s; the runs found are in `DetectionResult.ReferenceTone`. Line-up
that cuts one channel now and then (EBU, GLITS) is one run; beeps, music and runs shorter than
`ReferenceTone.MinDuration` (5 s; `DetectionOptions.ReferenceTone` is FrameReader's `ReferenceToneOptions`) don't count. Not in scenedetect, and off by default. It reads the audio in-process,
so FFmpeg's libraries are needed, and a file path (not a Stream or URL).

Which decoder a run uses is in every `DetectionProgress` report (`DecodesInProcess`), from a first report
sent before any frame, so `DetectStreamAsync` callers see it too. `ShotDetection.CanDecodeInProcess()` tells a worker at
startup whether FFmpeg's libraries load; `VideoDecoder.InProcess` without them throws `ShotDetectionException` with
`Reason = LibrariesNotFound`, where `Auto` falls back to the executable.

Failures from ffmpeg, ffprobe or the input throw `ShotDetectionException` (an `InvalidOperationException`) whose
`Reason` tells a deployment problem (`FfmpegNotFound`, `LibrariesNotFound`) from bad input (`InvalidInput`), a
decode failure (`DecodeFailed`) or a failed export (`ExportFailed`). ShotDetector's methods never throw FrameReader's
`FrameReaderException`, even when FrameReader fails inside them (decoding in-process, reading audio): that one is the
`ShotDetectionException`'s `InnerException`. Both are a `FrameReader.MediaException` (since 3.0, issue #239), so code
that also calls FrameReader directly catches either with one `catch (MediaException)`, and each keeps its own `Reason`.

Frames from another decoder (a managed one, in the browser): `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>,
options)` takes decoded 8-bit 4:2:0 planes (with strides) and the video's properties as FFmpeg reports them (average
frame rate, time base and start pts, first frame's colour tags), and gives the file's result, byte for byte, with the
same converter. Nothing on that path loads FFmpeg or starts ffmpeg, so it needs `Yuv420Converter` (ShotDetector.FastYuv's
`SwscaleYuv420`): there is no other conversion without FFmpeg. A constant frame rate (checked), or any rate with every frame's
timestamp given up front (`YuvVideo.FrameTimestamps`: variable frame rate, as a file's); one frame size;
a display rotation (`YuvVideo.Rotation`) turns the frames upright as a file's; deinterlacing takes FastYuv's `Yadif`
(the stream's `YuvVideo.FieldOrder` for `Auto`, each frame's `Interlaced` and `TopFieldFirst` flags for its parity; not
for ranges); `FfmpegResize` isn't available, the time range, crop, downscale, frame skip and stats
are (docs/decisions/2026-10-24-managedcodecs-showcase.md). A long video can be split across runtimes with no shared
memory (Web Workers): `ShotDetection.ScoreRange` scores a range of frames (from the frame before it to one past its
end), its `RangeScores` travel as bytes (`ToBytes`, `FromBytes`), and `ShotDetection.Merge` gives exactly `Detect`'s
result for the whole video, refusing ranges that don't fit together (docs/parallel-detection.md, "Frame-source ranges").
A range from frame 0 tells `OnShot` its shots as `Detect` would, and one scored after the earlier ranges' `RangeScores`
(a run resumed where it stopped) tells the rest of the whole video's.

Each detection is an OpenTelemetry-ready span: `.AddSource(ShotDetection.ActivitySourceName)` shows
"ShotDetection.Detect" with the video's size, frame rate, pipeline, frames and shots.

Runs wherever .NET 10 and ffmpeg do, including Alpine (`apk add ffmpeg`), which CI tests.

ShotDetector reproduces scenedetect 0.7.2 (`Compatibility = PySceneDetectVersion.V0_7_2`, the default) and 0.7.1
(`V0_7_1`, `--compat 0.7.1`): 0.7.2 changed detect-hist's and detect-hash's defaults, compares a minimum length in
frames as a frame count, measures AdaptiveDetector's from the frame it cuts at, and ignores fade-outs shorter than
0.1 s in detect-threshold (`MinOutLength`). When the default moves to a newer scenedetect, the previous release stays
available there (see "Versions"). The scenedetect 0.6.4 mode was dropped in 0.9.0; use ShotDetector 0.8 for it.

`Shot.Start`/`End` are `FrameTime`s: `FrameNum` (0-based; `End` is exclusive), `Seconds`,
`Timecode()` (also what `ToString()` gives). They print exactly as scenedetect prints them.

A runnable example of the library end to end (probe, shots, reference tone, a waveform with FrameReader) is in
[examples/ShotDetector.Example](examples/ShotDetector.Example/Program.cs): `dotnet run --project examples/ShotDetector.Example -- video.mp4`.

[examples/VideoRag](examples/VideoRag/README.md) turns a video into searchable chunks for RAG, one per shot with its
times, through Microsoft.Extensions.DataIngestion (preview) into an in-memory vector store; a sample, not a package.

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
min scene length 0.6s. `--help` lists all options, `--version` prints the version.

`--probe` only describes the input (container, video, every stream: codecs, sizes, languages, channel layouts)
and `--probe-json` prints the same as JSON. `--waveform peaks.dat` writes the audio's waveform as BBC audiowaveform's .dat
(peaks.js loads it; `--waveform-zoom`, `--waveform-pixels-per-second`, `--waveform-bits`, `--waveform-split-channels`). `--audio-filter "<graph>"` runs an ffmpeg `-af` graph over the audio
(analysis results such as silencedetect's go to standard output) and `--audio-out <file>` writes the result, encoded
by the extension. `--find-tone` only lists reference tone (1 kHz line-up tone) in the audio, also of audio-only files; `--trim-tone`
detects shots without it at the start and end (`--tone-min-duration` sets the shortest run, 5 s by default;
`--tone-search 180` searches only the first 3 minutes, much faster on a feature film, and then finds no tone at the end).

## Using the shot list

```
shotdetect -i video.mp4 --save-images thumbs --save-html thumbs/video-Scenes.html --split-video clips
```

- `--save-images <dir>` writes `{video}-Scene-{NNN}-{II}.jpg`, 3 per shot (`--num-images`): one a
  frame in from the start, one mid-shot, one a frame before the end. They are the same frames
  scenedetect's `save-images` picks (checked on 120 thumbnails), encoded by ffmpeg instead of
  OpenCV, so similar but not byte-identical files. When the video was decoded in-process the images are encoded there
  too (FrameReader's `ImageEncoder`): byte for byte the files the ffmpeg executable of the same FFmpeg build writes
  (a JPEG's comment names libavcodec's version), else that executable writes them. `--image-quality <0-100>` (JPEG, WebP) and
  `--image-compression <0-9>` (PNG) are scenedetect's `--quality` and `--compression`: PNG's and WebP's mean the
  same as OpenCV's, JPEG's is mapped onto ffmpeg's scale (`ImageOptions.Quality`, `Compression`).
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

CI (`.github/workflows/ci.yml`) checks every pull request on Linux against scenedetect 0.7.2 and 0.7.1 (each in its
compatibility mode), for the core path
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

The Windows OpenCV wheel converts BT.709-tagged video with BT.601 coefficients (it ignores the matrix tag), so
on Windows compare.py passes our CLI the hidden `--always-bt601`, which does the same: exact there too.
With `--ffmpeg-resize`, cuts on real footage differ (2–6 per trailer).

## Performance

`benchmark.yml` (`tools/bench.py`) measures it every week on GitHub-hosted Linux runners, x64 and ARM, with all 4
CPUs and pinned to 2 (a small container, issue #7): wall time, CPU time and peak memory of the content detector on
four Blender films (1080p down to 240p), for ShotDetector decoding in-process (the default), ShotDetector with the
ffmpeg executable, and scenedetect; also with per-frame stats (`--stats`, scenedetect's `-s`), which compute
edges for every frame. **The current numbers are in the summaries of the
[latest benchmark runs](https://github.com/bisforboman/shotdetector/actions/workflows/benchmark.yml).** In them, in-process decoding has been faster than scenedetect on these films, on both runners
and CPU counts, with less memory and similar or less CPU time; the ffmpeg executable costs more CPU and memory than
decoding in-process (its own process and frame queues, issue #12). All give identical results.

**History:** every run on `main` also commits its numbers as JSON to the
[`bench-data`](https://github.com/bisforboman/shotdetector/tree/bench-data) branch, one file per runner under `runs/`
(the schema is in its README, from [benchmarks/bench-data.md](benchmarks/bench-data.md)), so the trend can be charted.

In-process decoding uses FFmpeg's shared libraries inside our process, as OpenCV does: the `FrameReader.Native`
packages or the CLI's bundled ones, or a system install. It is the default whenever they load
(`Decoder = VideoDecoder.Auto`; from `FfmpegDirectory` / `--ffmpeg-dir`, else next to the app, else the system's
usual places); otherwise the executable is used. `--decoder inprocess` insists on the libraries, `--decoder
process` on the executable. A URL decodes in-process when the libraries have its protocol (FrameReader.Native: http
and https), as a stream; other URLs use the executable. Through the executable, an http or https HLS playlist's https
segments, keys and variants are fetched without their certificates checked (FFmpeg doesn't pass the check on to them;
in-process, FrameReader does), so when the libraries fail to open an http or https URL (a refused certificate, or a
format they lack, such as DASH in FrameReader.Native, which leaves out libxml2), `Auto` reports that failure rather
than handing the URL to the executable: `--decoder process` (`Decoder = VideoDecoder.FfmpegProcess`) reads it there,
and for an http or https playlist the result then says so (`DetectionResult.Warnings`; `shotdetect` prints it on stderr as soon as it is known), as does
`DetectionOptions.Warnings` as soon as it is known (an `IProgress<string>`, also for `DetectStreamAsync`).

How it keeps up while computing exactly what OpenCV and scenedetect compute:
- only the pixels the exact `cv2.resize` port reads are converted to BGR (in-process, the row slices that hold
  them; with the executable, ffmpeg's `remap` filter keeps just those, or whole frames when that is cheaper);
- the resize, BGR to HSV and the frame differences are vectorised (AVX2), with the same integer arithmetic;
- decoding overlaps scoring, with all the CPUs (at most 16) as decoder threads, as OpenCV (`--threads` to change);
  HD video read sequentially in-process also converts its frames on a thread apart from the decoding one.

**Parallel detection**, the default for `Detect` and the CLI from 4 CPUs on, scores a long local file in chunks on
several cores at once (half the CPUs, at most 4 chunks, for files of two minutes or more), then runs the cut decisions
once over all the scores, so the result is exactly the sequential one; CI compares the two byte for byte on every
sample and on three films. It pays where one decoder can't keep the cores busy: more cores work at once for about the
same CPU time (the
[weekly benchmark](https://github.com/bisforboman/shotdetector/actions/workflows/benchmark.yml) has both). It needs a
file, decoded in-process or by the ffmpeg executable (a process per chunk): streams, URLs, a time range, frame skip and
MPEG-PS read sequentially, and so does any
file a chunk can't reproduce (a seek that lands late, dropped frames, a size change). `DetectedVideo.Chunks` says which
ran. Each chunk has its own decoder (they share the frame buffers), so memory grows with the chunks; frames over 3840x2160
read sequentially by default. `DetectionOptions.Mode` chooses: `Auto` (the default) chunks for `Detect` from 4 CPUs on
but not for `DetectStreamAsync`, so its shots still come as they are found; `Sequential` never chunks (CLI
`--sequential`; services running several detections at once may want it); `Parallel` chunks from 4 CPUs on and at any
frame size (CLI `--parallel`), `DetectStreamAsync` too, whose shots then all come at the end. The older
`Parallel = true` still works and is obsolete ([docs/parallel-detection.md](docs/parallel-detection.md)).

The FastYuv package (LGPL) converts the raw yuv420p, yuv422p or 10-bit yuv420p planes (from ffmpeg, or decoded
in-process) with a bit-exact port of x86 swscale's conversion, only the pixels the resize reads. Files in other pixel
formats (10-bit 4:2:2, 4:4:4) or sizes it leaves to FFmpeg (an odd decoded height at 8 bits, an odd width at 10) take
the default conversion, still with identical results. In-process on x64 it
isn't needed for exactness (the default path converts only those rows with FFmpeg's own swscale, and costs a little
less CPU); there its point is ARM, where it gives x86's results.

## Where this differs from PySceneDetect

- **Reference tone:** scenedetect has nothing like `TrimReferenceTone` / `--trim-tone`; with it, results equal
  scenedetect's with `-s`/`-e` at the tone's end and start. Off by default.

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
  cuts (compare.py passes the hidden `--always-bt601` there to match). CI compares against scenedetect on Linux
  with the latest stable ffmpeg (9.0).
- **ARM (Apple Silicon, ARM Linux):** ShotDetector's own code gives the same results on every platform, but FFmpeg's
  conversion of the decoded frames to BGR rounds differently on ARM (its x86 and ARM assembly differ), so by default
  per-frame stats differ slightly from x86's and now and then a cut moves. For x86's results on ARM, add
  ShotDetector.FastYuv (`Yuv420Converter = new ShotDetector.FastYuv.SwscaleYuv420()`), a port of the x86
  conversion: CI checks that ARM then gives x64's results exactly on even-sized 8-bit 4:2:0 and 4:2:2 video and on
  10-bit 4:2:0 of even width (nearly all video), through both decoders. ShotDetector decodes MPEG-1/2, MJPEG and MPEG-4 Part 2 with FFmpeg's C IDCT on
  every platform, which gives x86's default frames; ARM's own IDCT doesn't (Xvid's MPEG-4 aside, which FFmpeg decodes
  with the Xvid IDCT everywhere). Other formats (10-bit 4:2:2, odd sizes FastYuv leaves to FFmpeg) can still differ
  on ARM ([docs/fastyuv-10bit-422.md](docs/fastyuv-10bit-422.md)). The `shotdetect` binaries don't include FastYuv (LGPL), so on ARM they give the default results
  ([docs/decisions/2026-10-17-arm-results.md](docs/decisions/2026-10-17-arm-results.md)).
  scenedetect itself differs on ARM, because opencv-python's ARM wheels replace parts of OpenCV with Arm's
  KleidiCV and Carotene libraries: `cv2.resize` for downscales under 3x (scenedetect's usual ones) and BGR to HSV
  round differently, by 1 or 2 on some pixels, which moves per-frame stats and now and then a cut. macOS on Intel
  is different again: its wheel has no FFmpeg (Apple's decoders instead). The nightly `parity-os` workflow shows Windows (exact)
  and macOS (informational).
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
- **Damaged video (best effort):** when the decoder drops frames it can't decode, every frame still gets its own
  time and frame number, as with OpenCV (numbers skip). Error concealment depends on the decoder's thread count, so
  ShotDetector uses OpenCV's (the CPUs, at most 16). With several threads, FFmpeg's decoders also conceal damage
  differently from run to run, in scenedetect as in ShotDetector, so on corrupt files the stats and sometimes a cut
  can vary between runs of either tool, and a truncated stream can vary in its last frame or two.
  `--threads 1` (`DetectionOptions.DecodeThreads = 1`) narrows this but doesn't remove it: damaged MPEG-1/2 (and
  h264 with several slices) can still vary through the ffmpeg executable, and in-process when frames are converted on
  their own thread (the default from 1280x720 up), because areas the decoder never conceals keep whatever an earlier
  frame left in that buffer. In-process with the conversion on the decoding thread, one thread repeats and on MPEG-2
  equals scenedetect at one thread. The thread count itself changes such results, so two machines with different
  CPU counts can disagree.
  docs/decisions/2026-10-22-damaged-files-best-effort.md has the measurement. After damage a late frame's pts can go
  back, and libavcodec's best-effort timestamp then takes the packets' dts, which can repeat a frame's time or run
  behind it; OpenCV labels each frame by its own pts. In-process, ShotDetector does too; through the ffmpeg
  executable it can't (ffmpeg sets every frame's pts to the best effort), so there such frames' numbers slide by a
  frame or more until the next gap (docs/decisions/2026-10-28-damaged-frame-pts.md). On a **resolution change**
  mid-stream (two encodes joined), OpenCV scales the new frames to the stream's first size in its one conversion, and scenedetect
  numbers frames by their time, so where the timestamps start over its frame numbers do too: later frames overwrite
  earlier stats rows metric by metric (except the threshold detector's average, which scenedetect keeps and also
  detects with) and the cuts are sorted. ShotDetector does the same (checked in CI), with ShotDetector.FastYuv
  too: the new size's frames take the exact conversion (in-process; through the ffmpeg executable, the file is read
  again from the start on that pipeline once a new size turns up), so only they, or that file, are slower.
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

## Security

- **No shell.** ffmpeg and ffprobe are started with an argument list, never through a shell, so quotes, `;` or `$(...)`
  in a file name stay part of the name.
- **Inputs are FFmpeg's.** A path or URL is opened by FFmpeg. A path reads as a file only: FFmpeg's pseudo-protocol
  strings (`concat:`, `subfile:`, `data:`...) are refused. A remote playlist (HLS) names further URLs. FFmpeg's own checks keep a remote playlist from reaching local files; on
  top, an http(s) URL here reads only http, https, tcp, tls and crypto (rtsp: rtsp, rtp, udp, tcp, tls), by the
  executable and in-process alike, an https URL's server certificate is checked against the system's trusted ones
  (FFmpeg 8 doesn't by default), in-process also for the segments a playlist names (the ffmpeg executable doesn't pass
  the check on to them),
  and a read that waits more than 30 s fails (cancellation stops it sooner). A
  playlist can still
  name other http addresses, including ones on your network. Pass untrusted URLs only where that's acceptable, and
  untrusted paths only as file paths you've checked.
- **Raw ffmpeg arguments are yours.** `SplitOptions.Args` (`--args`) goes to ffmpeg as options, which can write files;
  don't fill it from untrusted input.
- **Decoding untrusted media in-process** runs FFmpeg's decoders inside your process, without the separate process the
  executable gives (a frame larger than 8192 x 8192 pixels fails to decode, by either path, so a crafted file can't
  exhaust memory). Keep FFmpeg current: our native packages follow its point releases (the weekly upstream check), and
  `Decoder = VideoDecoder.FfmpegProcess` keeps decoding out of your process.

## Versions

From 1.0, semantic versioning (before it, minor versions could change the API); packing the libraries fails on a
breaking change against the last stable release (the SDK's package validation):

- **Major**: removing or changing public API, or changing default results (cuts, scene list CSVs, stats) for
  any reason other than the one below.
- **Minor**: new options and outputs, speed and memory, and following a new scenedetect release: the default
  compatibility mode tracks the newest scenedetect, since matching it is the point (the previous release stays
  available as a `PySceneDetectVersion` value; the one before it becomes `[Obsolete]` in that minor and goes at the
  next major: docs/decisions/2026-10-31-retiring-scenedetect-modes.md).
- **Patch**: fixes that make results match scenedetect where they didn't.

## License

MIT ([LICENSE](LICENSE)), except `src/ShotDetector.FastYuv`, which is LGPL-2.1-or-later because it
ports FFmpeg's libswscale. The MIT library contains ports from PySceneDetect (BSD 3-Clause) and
OpenCV (Apache 2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
