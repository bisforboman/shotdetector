### New

- `DetectionResult.Warnings`: what the caller should know about how the video was read. So far the https HLS warning
  `shotdetect` prints: a playlist decoded by the ffmpeg executable, whose segment downloads don't check certificates.
