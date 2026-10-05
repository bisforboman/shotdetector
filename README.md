# ShotDetector

Shot/cut detection for video in C#: a faithful port of [PySceneDetect](https://github.com/Breakthrough/PySceneDetect)'s
ContentDetector, AdaptiveDetector and ThresholdDetector (scenedetect 0.7.1) that gives identical
results, about 1.5x faster than scenedetect at 1080p with the optional fast path. Video is decoded by [ffmpeg](https://ffmpeg.org), which must be
installed and on `PATH`; all detection logic is plain C#.

- `src/ShotDetector`: the library, NuGet package `ShotDetector` (MIT).
- `src/ShotDetector.FastYuv`: optional fast path for large video, NuGet package
  `ShotDetector.FastYuv` (LGPL-2.1-or-later, since it ports FFmpeg code).
- `src/ShotDetector.Cli`: the `shotdetect` command-line tool, used for development and comparison
  (core only, not packaged).

## Library

```csharp
using ShotDetector;

var result = ShotDetection.Detect("video.mp4");      // adaptive detector, scenedetect's defaults
foreach (var shot in result.Shots)
    Console.WriteLine($"{shot.Number}: {shot.Start.Timecode()} - {shot.End.Timecode()}");

// Other detectors and settings; Export uses the shot list.
var fades = ShotDetection.Detect("video.mp4", new DetectionOptions { Detector = DetectorKind.Threshold });
Export.SaveImages(result, "thumbs");
File.WriteAllText("shots.csv", Shots.Csv(result.Shots));

// Optional, from ShotDetector.FastYuv (LGPL): ~1.7x faster on 1080p, identical results.
var fast = ShotDetection.Detect("video.mp4", new DetectionOptions { Yuv420Converter = new ShotDetector.FastYuv.SwscaleYuv420() });
```

`Detect`, `Export.SaveImages` and `Export.SplitVideo` take an optional `CancellationToken`: cancelling
stops at the next frame (or clip), kills the ffmpeg process and throws `OperationCanceledException`.

To get shots while a long video is still being decoded, stream them (same results as `Detect`):

```csharp
await foreach (var shot in ShotDetection.DetectStreamAsync("video.mp4", options, cancellationToken))
    Console.WriteLine($"{shot.Number}: {shot.Start.Timecode()} - {shot.End.Timecode()}");
```

A shot arrives once the cut that ends it is confirmed (a few frames later); the last one at the end.

`DetectionOptions.Progress` (an `IProgress<DetectionProgress>`) reports frames done out of the
expected total about 10 times a second. ffmpeg and ffprobe are found on `PATH`, or in
`DetectionOptions.FfmpegDirectory` (CLI: `--ffmpeg-dir`).

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
`Timecode()`. They print exactly as scenedetect prints them.

## Command line

```
dotnet run -c Release --project src/ShotDetector.Cli -- -i video.mp4 [-d adaptive|content|threshold] [--csv shots.csv] [--json shots.json] [--stats stats.csv] [--ffmpeg-resize]
```

Output matches `scenedetect -i video.mp4 detect-adaptive list-scenes`: the start frame is 1-based,
the end frame is inclusive, and timecodes are `HH:MM:SS.mmm`. `--csv` writes the same scene list CSV
as scenedetect (minus its leading "Timecode List" row), and `--stats` writes the per-frame metrics
file that `scenedetect -s` writes. Defaults match the scenedetect CLI:
content threshold 27, adaptive threshold 3, min-content-val 15, frame window 2, fade threshold 12,
min scene length 0.6s. `--help` lists all options.

## Using the shot list

```
dotnet run -c Release --project src/ShotDetector.Cli -- -i video.mp4 --save-images thumbs --split-video clips
```

- `--save-images <dir>` writes `{video}-Scene-{NNN}-{II}.jpg`, 3 per shot (`--num-images`): one a
  frame in from the start, one mid-shot, one a frame before the end. They are the same frames
  scenedetect's `save-images` picks (checked on 120 thumbnails), encoded by ffmpeg instead of
  OpenCV, so similar but not byte-identical files.
- `--split-video <dir>` writes `{video}-Scene-{NNN}.mp4` with scenedetect's `split-video` ffmpeg
  command (libx264 veryfast CRF 22, AAC). The clips came out byte-identical to scenedetect's.

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

1920x1080, 5012 frames, 16-core machine (expect ±20% between runs):

| | Wall time | Peak memory (incl. ffmpeg) |
|---|---|---|
| scenedetect 0.7.1 | 15 s | 212 MB |
| ShotDetector + FastYuv | 10 s | 205 MB (65 MB ours + ~140 MB ffmpeg) |
| ShotDetector (core only) | 17 s | 336 MB |
| ShotDetector `FfmpegResize` (not exact) | 5 s | ~60 MB |

On 480p video all of these take about the same time (3.3–3.7 s for the Sintel trailer).
All exact variants give identical results.

What makes the FastYuv path faster:
- ffmpeg sends raw yuv420p (no colour conversion, half the bytes of BGR) through a named pipe with
  an 8 MB buffer (Windows' redirected stdout uses 4 KB);
- cv2.resize only reads ~150k of the 2M pixels of a 1080p frame, so only those are converted to
  BGR (`SwscaleYuv420.cs`, a bit-exact port of swscale's converter), fused into the resize;
- the resize and the HSV + difference scoring run on several cores; reading overlaps processing;
- ffmpeg gets 4 decoder threads (`--threads`): more only adds ~25 MB each without making the run faster.

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
- **Variable frame rate:** handled as PySceneDetect handles it. The fps is the *average* frame rate
  (what OpenCV reports, through PySceneDetect's `framerate_to_fraction`), min-scene-len is measured
  in time between frames, and printed frame numbers are `round(time × average fps)`, not decode
  order, so they can repeat or skip on VFR video.
- **min_scene_len:** ContentDetector reproduces PySceneDetect's float-seconds comparison on
  µs-rounded frame times, so a gap of exactly min_scene_len frames is sometimes rejected (as it is
  there). It assumes constant frame rate starting at 0; variable frame rate isn't handled.
- **Flash filter:** only MERGE mode (the default) is ported, not SUPPRESS.
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
- **Not ported:** Histogram/Hash detectors, frame skip, crop.

## License

MIT ([LICENSE](LICENSE)), except `src/ShotDetector.FastYuv`, which is LGPL-2.1-or-later because it
ports FFmpeg's libswscale. The MIT library contains ports from PySceneDetect (BSD 3-Clause) and
OpenCV (Apache 2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
