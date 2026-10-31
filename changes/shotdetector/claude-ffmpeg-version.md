### Fixed

- The ffmpeg executable before FFmpeg 7.1 (Ubuntu 24.04's 6.1, for one) failed on every video whose stream names its
  chroma location ("Error applying option 'in_chroma_loc' to filter 'scale'"), though the docs said any version works.
  8-bit 4:2:0 video now reads with it, with the same results; other formats (10-bit, 4:2:2, ...) stop with a
  message asking for FFmpeg 7.1 or newer (`ShotDetectionError.FfmpegNotFound`). FFmpeg 7.1 and newer are unchanged.
