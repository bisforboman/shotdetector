### Fixed

- `DetectionOptions.FreezeDetect` through an ffmpeg executable older than FFmpeg 8 (6.1, for one) gave an empty
  `FreezeSpans`, without an error: those versions log freezedetect's lines as `[freezedetect @`, which weren't read
  (issue #708).
