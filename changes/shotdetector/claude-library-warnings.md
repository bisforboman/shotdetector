### New

- `DetectionResult.Warnings`: what the caller should know about how the video was read; `shotdetect` prints them on
  stderr. So far one: an https HLS playlist decoded by the ffmpeg executable, whose segment downloads don't check
  certificates (in-process decoding checks them).
