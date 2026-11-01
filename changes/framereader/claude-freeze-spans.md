### New

- `FreezeDetector`: FFmpeg's freezedetect on frames of yours, without FFmpeg: still pictures (`FrozenSpan`, no end for
  one running at the end, as freezedetect) from the mean absolute difference over every plane, with its `n` and `d`
  (`FreezeOptions`), sized for FrameReader's frames (`For(decoder)`, `For(frame)`) or for planes you describe;
  `Finish(frames)` answers as of a number of frames.
