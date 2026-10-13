### Fixed

- ShotDetector.FastYuv on video that changes resolution mid-stream through the ffmpeg executable (in-process was
  fixed in 1.12.0): once a frame of another size turns up, the file is read again on the exact pipeline, so the
  results equal the core path's and scenedetect's. Only such files are slower.
