### New

- `ImageEncoder.Encode(DecodedFrame, ...)` encodes a frame from `FrameDecoder.TakeFrame` through ffmpeg's own graph:
  the decoded frame, then a size (`ImageFilterOptions.Width` and `Height`, or `MaxWidth`) or a filter chain of your own
  (`Filter`, ffmpeg's `-vf` syntax), then the conversion to the encoder's format. The file is what
  `ffmpeg [-ss T] -i X [-vf ...] -frames:v 1 out.jpg` writes for that frame, byte for byte with the same FFmpeg build,
  scaled or not (where an encoded `VideoFrame` matches ffmpeg only from the same raw frame). JPEG, PNG and WebP;
  10-bit, interlaced, rotated and anamorphic sources are tested.
