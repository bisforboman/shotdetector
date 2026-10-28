### Fixed

- Damaged files, in-process: a frame whose pts goes back after damage is labelled by its own pts, as OpenCV does,
  instead of the best-effort timestamp (the packets' dts from there on), which repeated a frame's time and moved every
  later frame number and cut a frame late until the next gap. Through the ffmpeg executable it can't be (ffmpeg gives
  each frame the best effort); README's damaged-video section says so.
