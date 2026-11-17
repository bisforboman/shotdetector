### New

- Adaptive: `DetectionOptions.IgnoreRepeatedFrames` (`--ignore-repeated-frames`, `ignore-repeated-frames` in a config
  file's `[detect-adaptive]`) leaves repeated frames (content_val below 0.5: pulldown, encodes that repeat frames) out
  of the neighbour mean, so motion next to a repeat no longer scores as a cut. Not in scenedetect, so off by default;
  with it, cuts can differ from scenedetect's (issue #633).
