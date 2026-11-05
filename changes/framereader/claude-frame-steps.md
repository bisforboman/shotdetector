### New

- `FrameSteps.Of(pts, timeBase, frameRate)`: the steps between a picture's frames in display order, at its frame
  rate, from `MediaInfo.PacketTimestamps` or timestamps of yours: the steps that aren't one frame (a variable frame
  rate), and the gaps over 1.5 frames with the frames missing in each (`FrameGap`: dropped frames).
