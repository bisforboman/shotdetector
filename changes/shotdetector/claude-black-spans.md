### New

- Black spans on the detection pass, as ffmpeg's blackdetect gives them, without a second decode:
  `DetectionOptions.BlackDetect = new BlackDetectOptions()` (blackdetect's `d`, `pic_th` and `pix_th` as `MinDuration`,
  `PictureThreshold` and `PixelThreshold`, its defaults) gives `DetectionResult.BlackSpans`. Each `BlackSpan` has its
  first frame, black_end's frame and blackdetect's black_start and black_end in seconds, and prints as its log line.
  Equal to `ffmpeg -vf blackdetect`'s on the executable and in-process paths, in parallel chunks, on a Stream and on
  frames from the caller (`YuvVideo`); the frames as detection reads them (whole, every frame, deinterlaced when
  deinterlacing, within the time range). Not for ranges yet (docs/decisions/2026-11-01-black-spans.md).
