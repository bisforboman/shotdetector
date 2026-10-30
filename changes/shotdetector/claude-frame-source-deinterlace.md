### New

- Frames from the caller (`ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)`, `DetectAsync`) can be
  deinterlaced: `Deinterlace = On` or `Auto` with `Yadif` (ShotDetector.FastYuv's) gives a file's deinterlaced result.
  `YuvVideo.FieldOrder` is the stream's field order (ffprobe's `field_order`), which `Auto` goes by and
  `DetectedVideo.FieldOrder` reports; `YuvFrame.Interlaced` and `YuvFrame.TopFieldFirst` are each frame's flags
  (ffprobe's `interlaced_frame` and `top_field_first`), which set yadif's parity. Without `Yadif` it is an
  `ArgumentException` when it would deinterlace; `Auto` on a progressive or unknown field order runs as before. Ranges
  (`ScoreRange`, `Merge`) refuse deinterlacing (issue #614).
