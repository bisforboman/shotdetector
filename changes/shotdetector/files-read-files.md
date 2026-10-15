### Fixed

- An input path reads as a file only (FFmpeg's `protocol_whitelist=file,crypto`, with the executable and in-process): an
  FFmpeg pseudo-protocol string passed as a path, such as `concat:a.mp4|b.mp4` or `subfile:...`, is refused rather than
  interpreted. Network URLs (`scheme://host`) are unaffected.
