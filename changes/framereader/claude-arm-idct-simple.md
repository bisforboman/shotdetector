### New

- `FrameDecoderOptions.CodecOptions`: FFmpeg decoder options, as ffmpeg's before `-i`. `idct` = `simple` decodes
  MPEG-1/2 and MJPEG with the C IDCT, so ARM gives x86's frames (ARM's default IDCT rounds differently). A name the
  decoder doesn't have is an `ArgumentException`, as are `max_pixels` and `threads` (set through `MaxPixels` and
  `Threads`, which they would otherwise override).
