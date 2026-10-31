### New

- Frame sources: `YuvFrame.ColorSpace` and `FullRange` give a frame's own colour tags (null, the default, means the
  video's). Each frame is converted with its own, as `Detect(path)` in-process and OpenCV convert them, so a spliced
  stream whose colour matrix or range changes midway gives the file's stats byte for byte. Before, every frame was
  converted with `YuvVideo`'s tags. A frame whose colours FastYuv doesn't take is refused with `InvalidInput`
  (issue #626).
