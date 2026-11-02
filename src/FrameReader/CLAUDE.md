# src/FrameReader: notes beside the code

FrameReader's types and what it handles: README.md here. Its releases: CHANGELOG.md here (tags
`framereader-vX.Y.Z`). ShotDetector depends on it one way, through its public API only; its tests are in
`tests/FrameReader.Tests`.

## Gotchas (each cost time once)

- FFmpeg.AutoGen lays structs out for 8-byte `long`: on Windows every AVIOContext field from `checksum` (an `unsigned
  long`) on is 8 bytes off, so don't write `seekable` and the like through it (StreamInput passes callbacks instead).
- VideoWriter against `ffmpeg -c:v libx264`: on AVX-512 CPUs x264's AVX-512 code with .NET's tiered JIT differ about
  1 run in 250, so CI's VideoWriter step sets `DOTNET_TieredCompilation=0`
  (docs/decisions/2026-10-10-videowriter-s-rare-ci-mismatch.md).
