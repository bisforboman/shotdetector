### Fixed

- Deinterlacing 9 to 16-bit video (10-bit AVC-Intra, ProRes) without ShotDetector.FastYuv gives the same stats and
  cuts on every run, through the ffmpeg executable and in-process: yadif runs on one thread there
  (`-vf yadif=threads=1`), as FFmpeg 8.1's x86 yadif varies from run to run with several. The frames equal FFmpeg's C
  code and FastYuv's `Yadif`. For scenedetect's lossless copy of such a file, use `ffmpeg -vf yadif=threads=1`.
