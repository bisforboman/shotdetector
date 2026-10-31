### New

- `SilenceFinder`: silences in samples you decode yourself (no FFmpeg), a chunk at a time, as `silencedetect` finds them
  in float samples, to the sample (`SilenceSpan`: first silent instant and the first after it, with times).
