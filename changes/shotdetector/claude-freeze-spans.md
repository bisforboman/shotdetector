### New

- Still pictures on the detection pass, as ffmpeg's freezedetect gives them: `DetectionOptions.FreezeDetect = new
  FreezeDetectOptions()` (freezedetect's `n` and `d` as `Noise` and `MinDuration`, its defaults) gives
  `DetectionResult.FreezeSpans`. Each `FreezeSpan` has the still's first frame and freeze_start, and the first frame that
  differs, freeze_end and freeze_duration (null for a still running to the end, as freezedetect prints no freeze_end).
  Equal to `ffmpeg -vf freezedetect`'s on the executable and in-process paths, on a Stream and on frames from the caller;
  with it the video is read in one pass (docs/decisions/2026-11-01-freeze-spans.md).
