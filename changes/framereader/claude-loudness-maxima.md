### New

- `LoudnessResult.MaxMomentary` and `MaxShortTerm`: the highest momentary (400 ms) and short-term (3 s) loudness,
  every 100 ms as ebur128's framelog prints them, from `LoudnessMeter` (`Loudness.Measure`, FFmpeg's own filter, keeps
  no maximum and leaves them NaN). For short-form limits such as EBU R128 s1's.
