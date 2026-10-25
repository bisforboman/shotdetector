### New

- `ShotDetection.ScoreRange` (and `ScoreRangeAsync`) and `ShotDetection.Merge`: score a long video's frames in ranges, each on its own runtime (browser Web Workers, no shared memory), carry each range's `RangeScores` between them as bytes (`ToBytes`, `FromBytes`), and merge them into exactly the result `Detect` gives for the whole video; ranges that don't fit together, or whose overlap frames differ from the next range's, are refused.
