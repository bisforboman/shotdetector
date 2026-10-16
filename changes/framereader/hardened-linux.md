### New

- FrameReader.Native on Linux (x64, arm64, musl) is built hardened (FFmpeg's `--toolchain=hardened`: stack protector,
  `_FORTIFY_SOURCE`, full RELRO, PIC), so a bug in a decoder fed untrusted media is harder to exploit in-process.
