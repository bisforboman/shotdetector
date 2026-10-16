### New

- https URLs decode in-process on Linux and macOS too, with FrameReader.Native's https (OpenSSL on Linux, the
  system's TLS on macOS); before, they went through the ffmpeg executable there.
