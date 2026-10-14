# ShotDetector.Native: where the libraries come from, packaging, CLI, platforms (2026-10-07)

### Question

How should FFmpeg's shared libraries ship, so in-process decoding works without installing them?

### Choices

- Source: our own minimal LGPL build (decoders, demuxers, swscale) in CI; BtbN's LGPL builds (Windows and glibc
  Linux only, large).
- Packaging: one package per platform; one package for all.
- CLI: the tool and binaries bundle them; library only.
- Platforms: win-x64, linux-x64 + linux-arm64, linux-musl-x64, osx-arm64.

### Answer

**Our own minimal build, one package per platform, bundled with the CLI, all five platforms.** No AV1 software
decoder (dav1d is an external library), as in OpenCV on Linux; Auto falls back to the executable for it.
