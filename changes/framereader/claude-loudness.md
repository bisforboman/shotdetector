### New

- `Loudness.Measure`/`MeasureAsync`: EBU R128 loudness (`LoudnessResult`: integrated, range and its ends, sample and
  true peak), `ffmpeg -af ebur128=peak=true+sample`'s figures at full precision, the last partial 100 ms block included.
- `AudioFilter.Result(filter, option)`: an analysis filter's read-only results (ebur128's `integrated`, `true_peak`, ...)
  after the chunks read so far.
