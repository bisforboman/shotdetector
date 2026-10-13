### Breaking changes

- Renamed where one name meant two things: `VideoInfo.FrameCount` and `DetectedVideo.FrameCount` (OpenCV's
  CAP_PROP_FRAME_COUNT) are `OpenCvFrameCount`, apart from FrameReader's container count; `DetectionOptions.Duration`
  (a scenedetect time, "30s" or frames) is `RangeDuration`, apart from `VideoInfo.Duration`; and
  `ShotDetectionError.FfmpegLibrariesNotFound` is `LibrariesNotFound`, as FrameReader's. The CLI's options and output
  are unchanged.
