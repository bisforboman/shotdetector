### New

- FrameReader.Native reads URLs: http (and its proxy, over tcp) on every platform, https on Windows through Windows'
  own TLS (Schannel; elsewhere https would need a TLS library linked in). Only these network protocols are in the build.
- `FFmpegLibraries.HasProtocol(name)`: whether the libraries can read a URL with that scheme.
- `FrameDecoderOptions`, `AudioReaderOptions` and `ProbeOptions` take a `CancellationToken` that also stops a read
  FFmpeg waits in, such as a stalled network read (FFmpeg's interrupt callback); `Remux.Copy`'s token does too. A
  cancelled read is an `OperationCanceledException`.
