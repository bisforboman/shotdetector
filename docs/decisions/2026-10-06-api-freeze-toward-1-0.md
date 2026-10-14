# API freeze toward 1.0 (2026-10-06)

### Question

Which parts of the public API should 1.0 promise?

### Choices and answers (all four as recommended)

1. **Detector classes, `IDetector`, `ContentScorer`, `EdgeDetector`: internal.** Users go through
   `ShotDetection` with `DetectorKind`; internals stay free to change. (Alternative: keep public as "advanced".)
2. **`VideoReader`: trimmed to the essentials.** `FrameAt`, `SeekFrame`, `PositionAfterDecoding`, `FrameCountHint`
   internal. (Alternatives: hide it behind a VideoInfo record; keep as is.)
3. **`DetectAsync` added** next to `Detect`. (Alternative: callers wrap `Detect` in `Task.Run`.)
4. **`FrameTime`'s raw fields hidden**; `ToString()` is the timecode, `FrameTime.Frame` the one public factory.
   (Alternative: keep as is.)
