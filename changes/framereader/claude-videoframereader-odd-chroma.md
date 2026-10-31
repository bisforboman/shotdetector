### Fixed

- `VideoFrameReader` at odd sizes: `FrameFormat.Yuv420p` at a frame's own size left the last chroma row as zeros when the
  height was odd, and the RGB formats rounded the last column differently from ffmpeg when the width was odd (issue
  #656). The output is now ffmpeg's `-f rawvideo` byte for byte there too. The conversion writes into aligned rows, as
  the scale filter's buffers are, and a frame already in the output's format and size passes through, as the scale
  filter passes it.
