### New

- Frame sources: 10-bit 4:2:0 (`yuv420p10le`: HEVC Main 10, 10-bit H.264) and 8-bit 4:2:2 (`yuv422p`, `yuvj422p`)
  frames, through FastYuv's converters, with the same results as `Detect(path)`, deinterlaced too. Before, these
  formats were refused. 10-bit samples come in the same `YuvFrame` byte planes (16-bit little-endian, strides in
  bytes). Their chroma is interpolated from `YuvVideo.ChromaLocation`, or from a frame's own
  `YuvFrame.ChromaLocation`, as the file path follows each frame's siting (issue #627,
  docs/decisions/2026-10-31-frame-source-formats.md).
