### Fixed

- `FrameDecoder` (and so `VideoFrameReader`): MPEG-1/2 and MJPEG decode with FFmpeg's C IDCT, so ARM gives the same
  frames as x86 (ARM's NEON IDCT rounds differently). Unchanged on x86, whose default IDCT gives these frames.
