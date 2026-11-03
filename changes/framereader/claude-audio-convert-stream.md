### New

- `AudioConvertOptions.Stream`: which audio stream `Audio.Convert` converts, counted among the input's audio streams
  from 0 as ffmpeg's `-map 0:a:N` counts them (the file `ffmpeg -i IN -map 0:a:1 -vn OUT` writes, for 1); null, as
  before, the best one. A number past the last audio stream fails as `AudioReaderOptions.Stream` does (issue #716).
