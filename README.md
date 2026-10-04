# ShotDetector

A small C# port of [PySceneDetect](https://github.com/Breakthrough/PySceneDetect)'s ContentDetector
and AdaptiveDetector (ported from scenedetect 0.7.1). Frames are decoded by piping
`ffmpeg -f rawvideo -pix_fmt bgr24`; all detection logic is plain C#.

```
dotnet run -c Release --project src/ShotDetector -- -i video.mp4 [-d adaptive|content] [--csv shots.csv] [--json shots.json] [--cv-resize]
```

Output matches `scenedetect -i video.mp4 detect-adaptive list-scenes`: the start frame is 1-based,
the end frame is inclusive, and timecodes are `HH:MM:SS.mmm`. Defaults match the scenedetect CLI:
content threshold 27, adaptive threshold 3, min-content-val 15, frame window 2, min scene length 0.6s.

## Verifying against PySceneDetect

```
python tools/compare.py video.mp4 --detector adaptive --tolerance 2 [--cv-resize]
dotnet test
```

Test clips used during development (all in the gitignored `samples/`):
- `synthetic.mp4`, made by `tools/make-sample.ps1`: hard cuts between ffmpeg test sources, including one 12-frame shot.
- [Sintel trailer 480p](https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4) and
  [Big Buck Bunny trailer 480p](https://download.blender.org/peach/trailer/trailer_480p.mov), both Blender open movies (CC-BY).

With `--cv-resize`, per-frame content_val is identical to scenedetect's stats file on all three, and
both detectors produce exactly the same cuts. With the default ffmpeg downscale, the synthetic clip
still matches, but on the trailers 2–6 cuts per run differ.

## Where this differs from PySceneDetect

- **Downscaling (default):** frames are resized to the same size PySceneDetect picks
  (`max(w,h)/256`), but by ffmpeg's bilinear scaler. This averages over the whole footprint, while
  `cv2.resize(INTER_LINEAR)` samples only 2x2 pixels and aliases. Content_val therefore comes out
  lower on fine detail, which can move ContentDetector cuts. `--cv-resize` makes ffmpeg send
  full-size frames and replicates cv2's resize bit for bit (`CvResize.cs`). It's slower because a
  1080p frame is 6 MB through the pipe.
- **Colour conversion:** ffmpeg is told to ignore the stream's colour tags and use BT.601 (with
  bicubic chroma for `--cv-resize`), because that's what OpenCV's ffmpeg backend does. Converting
  BT.709-tagged video "correctly" shifts content_val enough to flip borderline cuts.
- **No edge component:** `delta_edges` (Canny + dilate) is not ported. Its default weight is 0, so
  default scores are unaffected.
- **min_scene_len:** ContentDetector reproduces PySceneDetect's float-seconds comparison on
  µs-rounded frame times, so a gap of exactly min_scene_len frames is sometimes rejected (as it is
  there). It assumes constant frame rate starting at 0; variable frame rate isn't handled.
- **Flash filter:** only MERGE mode (the default) is ported, not SUPPRESS.
- **Faithfully kept quirks:** AdaptiveDetector measures min_scene_len from the current frame rather
  than the cut frame (so cuts are allowed `window` frames early), and a merge still pending at the
  end of the video is dropped.
- **Not ported:** ThresholdDetector (fades), Histogram/Hash detectors, frame skip, crop, stats file.
