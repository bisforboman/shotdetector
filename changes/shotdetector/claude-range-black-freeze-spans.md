### New

- Ranges take `DetectionOptions.BlackDetect` and `FreezeDetect`: `ScoreRange`/`ScoreRangeAsync` keep each frame's
  blackdetect judgement and freezedetect comparison in `RangeScores`, and `Merge` gives `BlackSpans` and `FreezeSpans`
  equal to `Detect`'s on the whole video, however it is split. A range that starts inside a still picture is scored
  again given the ranges before it: `ShotDetection.FirstRangeToScoreAgain(video, ranges, options)` names it, and the new
  `ShotDetection.RangeFrames(video, earlier, options, firstFrame, endFrame)` gives its frames, from the still's first
  frame. A resumed run's range uses the same overload. `RangeScores.ToBytes` writes format version 2 with either
  option, and version 1, as before, without them; both are read.
