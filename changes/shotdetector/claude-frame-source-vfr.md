### New

- Frames from the caller at a variable frame rate: with every frame's timestamp given up front (`YuvVideo.FrameTimestamps`,
  a file's packets' pts in presentation order), `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)`,
  `DetectAsync`, `ScoreRange` and `Merge` take frames at any rate, as `Detect(path)` takes a variable-rate file, in
  place of the constant-rate check: each frame must be at its entry (a range's too, which identifies its frames),
  `StartTime` finds its frame from them as the file path does, and a frame the decoder dropped is taken as a file's run
  takes it. Without them, the constant rate is still required (issue #609).
