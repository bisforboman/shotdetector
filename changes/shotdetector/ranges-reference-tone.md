### New

- Frame-source ranges take `DetectionOptions.TrimReferenceTone` with the caller's tone (`YuvVideo.ReferenceTone`):
  `ScoreRange`, `ScoreRangeAsync`, `Merge`, `FirstRangeToScoreAgain` and `RangeFrames` with the earlier ranges. The merge
  equals `Detect` with the same tone for any split (shots, cuts, scene list, the trimmed range's stats, black and freeze
  spans, `DetectionResult.ReferenceTone`), and a stopped run's merge equals `Detect` on frames that end there. Ranges
  scored without the option, or with another tone, don't merge with it (their fingerprints differ).
