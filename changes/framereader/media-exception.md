### New

- `MediaException`: the base of `FrameReaderException` and ShotDetector's `ShotDetectionException` (issue #239), so one
  `catch (MediaException)` covers both packages, each keeping its own `Reason`. `FrameReaderException` is therefore an
  `InvalidOperationException` now: a `catch (InvalidOperationException)` also catches FrameReader's failures.
- FFmpeg.AutoGen's dependency is `[8.1.0, 9.0.0)`: 9.x binds FFmpeg 9's libraries, which FrameReader doesn't load.
