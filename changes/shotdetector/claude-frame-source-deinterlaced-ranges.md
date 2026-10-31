### New

- Ranges: `ScoreRange`, `ScoreRangeAsync` and `Merge` take deinterlacing (`Deinterlace` On, or Auto on an interlaced
  field order, with FastYuv's `Yadif`). Before, they refused it. A range is given one more frame on each side for yadif:
  from `firstFrame - 2` to two frames past its end. The merge, and a run resumed from earlier ranges' scores, give the
  sequential deinterlaced result byte for byte, shots told as they come too (issue #628,
  docs/decisions/2026-10-31-deinterlaced-ranges.md).
- Ranges: `ShotDetection.RangeFrames(video, options, firstFrame, endFrame)` gives the frames to give `ScoreRange` for
  a range, one frame on each side or two when deinterlacing, so a host no longer encodes those margins
  (docs/decisions/2026-10-31-range-frames.md).
