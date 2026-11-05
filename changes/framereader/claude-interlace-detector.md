### New

- `InterlaceDetector`: FFmpeg's idet on frames of yours, without FFmpeg: whether the picture is top field first,
  bottom field first or progressive, frame by frame and through idet's four-frame history, and which fields repeat
  (telecine), as the totals idet prints at the end (`IdetTotals`), at its thresholds (`IdetOptions`); sized for
  FrameReader's frames (`For(decoder)`, `For(frame)`) or for planes you describe.
