### New

- `AudioReaderOptions.Stream`: which audio stream to read, counted among the input's audio streams from 0 as ffmpeg's
  `-map 0:a:N` counts them (a delivery's stereo and 5.1 mixes: 0 and 1), the same samples as `-map 0:a:N`; null, as
  before, the best one. Loudness, silences and the reference tone of every track of a file, through the readers they
  already take.
