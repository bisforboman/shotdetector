# ShotDetector

A small C# port of [PySceneDetect](https://github.com/Breakthrough/PySceneDetect)'s ContentDetector,
AdaptiveDetector and ThresholdDetector (ported from scenedetect 0.7.1). Frames are decoded by piping
`ffmpeg -f rawvideo -pix_fmt bgr24`; all detection logic is plain C#.

```
dotnet run -c Release --project src/ShotDetector -- -i video.mp4 [-d adaptive|content|threshold] [--csv shots.csv] [--json shots.json] [--stats stats.csv] [--ffmpeg-resize]
```

Output matches `scenedetect -i video.mp4 detect-adaptive list-scenes`: the start frame is 1-based,
the end frame is inclusive, and timecodes are `HH:MM:SS.mmm`. `--csv` writes the same scene list CSV
as scenedetect (minus its leading "Timecode List" row), and `--stats` writes the per-frame metrics
file that `scenedetect -s` writes. Defaults match the scenedetect CLI:
content threshold 27, adaptive threshold 3, min-content-val 15, frame window 2, fade threshold 12,
min scene length 0.6s.

## Verifying against PySceneDetect

```
python tools/compare.py video.mp4 [more.mp4 ...] [--detector adaptive|content|threshold|both|all] [--tolerance 2] [--ffmpeg-resize] [--report table.md] [--stats]
dotnet test
```

`tools/make-samples.ps1` recreates the test clips in the gitignored `samples/`:
- a synthetic clip with hard cuts between ffmpeg test sources, including one 12-frame shot;
- the Sintel and Big Buck Bunny trailers (Blender open movies, CC-BY) in several codecs, containers
  and resolutions (h264 mp4/mov/m4v/mkv, Theora ogg, 270p–1080p);
- the Sintel trailer re-timed to 23.976, 29.97 and 60 fps.

Results on all 10 clips and all three detectors, against scenedetect 0.7.1:
- the cuts are the same frames;
- the scene list CSVs are identical cell for cell, timecodes and seconds included;
- with `--stats`, every per-frame metric is printed identically ([docs/verification-stats.md](docs/verification-stats.md)).

Timings are in [docs/verification-table.md](docs/verification-table.md). With `--ffmpeg-resize`,
cuts on real footage differ (2–6 per trailer).

Speed on a 1920x1080, 5012-frame clip: scenedetect 13.6 s, ShotDetector 12.9 s, and 3.3 s with
`--ffmpeg-resize`. Exact mode is bound by ffmpeg converting full-size frames to BGR (about 9 s on its own).

## Where this differs from PySceneDetect

- **Downscaling:** by default ffmpeg sends full-size frames and `CvResize.cs` replicates
  `cv2.resize(INTER_LINEAR)` bit for bit, at the size PySceneDetect picks (`max(w,h)/256`). That's
  slow for big video because a 1080p frame is 6 MB through the pipe. `--ffmpeg-resize` lets
  ffmpeg's bilinear scaler downscale instead. It's faster and averages over the whole footprint,
  whereas cv2 samples only 2x2 pixels and aliases. Content_val therefore comes out lower on fine
  detail, and cuts can differ.
- **Colour conversion:** ffmpeg is told to ignore the stream's colour tags and use BT.601 (with
  bicubic chroma for the default exact resize), because that's what OpenCV's ffmpeg backend does. Converting
  BT.709-tagged video "correctly" shifts content_val enough to flip borderline cuts.
- **No edge component:** `delta_edges` (Canny + dilate) is not ported. Its default weight is 0, so
  default scores are unaffected, but the `--stats` file has no `delta_edges` column.
- **Timestamps:** scenedetect prints times from OpenCV's frame positions (container pts → ms →
  rounded to µs, with Python's exact-binary rounding), not from frame/fps. `PyTime.cs` reproduces
  that, taking the pts from an extra demux-only ffprobe pass. That assumes OpenCV's best-effort
  timestamps equal the sorted packet pts, which held for every sample but may not for streams with
  missing or broken timestamps.
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
- **Not ported:** Histogram/Hash detectors, frame skip, crop, stats file.
