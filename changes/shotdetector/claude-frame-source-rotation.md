### New

- Frames from the caller (`ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)`, `DetectAsync`, `ScoreRange`,
  `Merge`) take a display rotation: `YuvVideo.Rotation` (degrees counterclockwise, any quarter turn, as
  `DetectedVideo.Rotation` reports a file's). The frames stay as decoded and are turned upright after converting, as a
  rotated file's, so a phone's MP4 gives `Detect(path)`'s result, size and `Rotation` (issue #608).
