### New

- Frame sources: `TrimReferenceTone` on `Detect(YuvVideo, ...)` and `DetectAsync`, with the tone the host found in the
  audio it decoded given as `YuvVideo.ReferenceTone` (FrameReader's `ReferenceToneFinder` finds it without FFmpeg): the
  file path's trim and result, byte for byte, and the segments back in `DetectionResult.ReferenceTone`. Without them
  it is refused as before, and ranges (`ScoreRange`, `Merge`) refuse it, as they refuse a time range (issue #629,
  docs/decisions/2026-10-31-frame-source-tone.md).
