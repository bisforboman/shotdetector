### Fixed

- `VideoFrameReader` at odd sizes: `FrameFormat.Yuv420p` at a frame's own size left the last chroma row as zeros when the
  height was odd, and the RGB formats rounded the last column differently from ffmpeg when the width was odd (issue
  #656). The output is now ffmpeg's `-f rawvideo` byte for byte there too. The conversion writes into aligned, zeroed
  rows, as the scale filter's buffers are. A frame already in the output's format and size passes through, as the scale
  filter passes it. On ARM, swscale doesn't write an odd width's last RGB column, so ffmpeg gives 0 there; the reader now
  gives 0 too, where before it gave whatever the buffer held.
