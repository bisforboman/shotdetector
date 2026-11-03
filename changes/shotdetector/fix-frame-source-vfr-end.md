### Fixed

- Frames from the caller (`Detect(YuvVideo, frames)`) that stop before the video ends: the last shot ended at the
  frames read over the average rate (4.740 s for 75 frames of a 1250/79 fps variable-rate video), not where
  `Detect(path)` stopped at the same frame by its end time ends it (the last frame's time plus a frame at the average
  rate, 3.5832 s); at a constant rate off its time base's grid (29.97 fps in 1/1000, 24 fps) they differed in the last
  digits. Frames ending before `YuvVideo.FrameTimestamps`' last entry now end there, as scenedetect ends a stopped run;
  give the timestamps to stop a run early (without them a stop looks like the video's end). Runs read to the last
  entry are unchanged.
