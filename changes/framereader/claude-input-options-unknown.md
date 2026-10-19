### Fixed

- `InputOptions` (`FrameDecoderOptions`, `ProbeOptions`, `AudioReaderOptions`, `AudioConvertOptions`, `RemuxOptions`):
  a name no demuxer or protocol takes, such as a misspelt one, was ignored without a word. It is an `ArgumentException`
  now, naming the option, as the README promised and as `CodecOptions` and ffmpeg's command line do. FrameReader's own
  defaults (`scan_all_pmts`, `tls_verify`, `ca_file`) never count, so `tls_verify=0` still works for any input.
