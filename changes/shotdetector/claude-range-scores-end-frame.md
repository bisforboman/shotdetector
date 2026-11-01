### New

- Ranges: `RangeScores.EndFrame` is one past the last frame whose scores are final: `FirstFrame + FrameCount`, or one
  less when the range was deinterlaced, whose last frame isn't final. The next range, or a run resumed from a stopped
  one, starts at `EndFrame - 1` at the latest, so a host no longer works that out from `RangeFrames`' margins. Merge's
  and ScoreRange's refusals of a range that starts too late now name the frame to start at
  (docs/decisions/2026-11-01-range-scores-end-frame.md).
