### Fixed

- Frames from the caller (`Detect(YuvVideo, frames)`) that stop before the video ends, at a variable frame rate: the last
  shot ended at the frames read over the average rate (4.740 s for 75 frames of a 1250/79 fps video), not where
  `Detect(path)` stopped at the same frame by its end time ends it (the last frame's time plus a frame at the average
  rate, 3.5832 s). Frames ending before `YuvVideo.FrameTimestamps`' last entry now end there, as scenedetect ends a
  stopped run; runs read to the last entry, and constant-rate frames, are unchanged.
