### New

- Frame sources: `YuvFrame.Width` and `Height` say a frame's size (0, the default: the video's). A frame of another size
  than `YuvVideo`'s (a stream whose resolution changes midway) is refused with `ShotDetectionException`
  (`InvalidInput`) by `Detect`, `DetectAsync` and `ScoreRange`, where it used to be read at the video's size and give
  other results without a word (`Detect(path)` scales it to the stream's size, which the frame source can't yet;
  issue #625, docs/decisions/2026-10-31-frame-source-size-change.md).
