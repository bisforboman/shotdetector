### Fixed

- ARM: MPEG-1/2 and MJPEG video decode as on x86 (FFmpeg's C IDCT; ARM's own rounds differently), through the
  ffmpeg executable too, so with ShotDetector.FastYuv their results equal x86's. Unchanged on x86.
