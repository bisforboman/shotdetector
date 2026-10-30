# Frame sources: which rotations YuvVideo.Rotation takes (2026-10-30)

Issue #608, from shotdetector-web (its backlog row "Rotation"): the frame-source path (`Detect(YuvVideo,
IEnumerable<YuvFrame>, ...)`, `DetectAsync`, `ScoreRange(Async)`, `Merge`) analysed frames as given, while the file path
turns a rotated file's frames after converting (`VideoReader.Turn`, as `cv::rotate`) and reports the upright size, so a
phone's MP4 couldn't get the file's result. The issue asked for `YuvVideo.Rotation` in degrees counterclockwise, as
`DetectedVideo.Rotation`, taking 0, 90, 180 or 270, anything else an `ArgumentException`. But a file's
`DetectedVideo.Rotation` is the display matrix's angle (`av_display_rotation_get`, -180 to 180): a file made with
`ffmpeg -display_rotation 270` reports -90, one with 180 reports -180.

## Question

Which values does `YuvVideo.Rotation` take?

## Choices

1. **Any quarter turn**: 0, ±90, ±180, ±270 (-90 is 270), anything else an `ArgumentException`;
   `DetectedVideo.Rotation` reports the value given, so a file's own value passes straight through.
2. **0, 90, 180, 270 only**, as the issue says: a caller holding a file's -90 or -180 normalises it first.

## Answer

**Any quarter turn** (the owner, 2026-10-30).

## How

- The frames are given as decoded (`YuvVideo.Width` and `Height` are the decoded size); the reader turns them after
  converting by the file path's `Turn` (`CvResize.ResizeYuvTurned` through the resize; without one the whole frame
  converted, then turned, as ffmpeg's transpose or flips after `format=bgr24`), and reports the upright size, as a file's.
- `Rotation` is in `Merge`'s fingerprint, so ranges scored with another rotation are refused.
- Tests: FrameInputTests on rotated copies of the test clip (90, 180, -90; content, adaptive, hash; cropped; without a
  resize) against `Detect(path)`, and a rotated clip's ranges merged; benchmarks/wasm checks each rotation's CSVs on
  WebAssembly against the native run's.
