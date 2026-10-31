# The macOS native build moves to macos-15, still for macOS 14 (2026-10-30)

GitHub retires the macos-14 runner image on 2026-11-02 and cancels jobs on it during scheduled brownouts before then
(actions/runner-images#13518): the osx-arm64 FFmpeg build in .github/workflows/native.yml was cancelled twice on
#613, and release.yml builds the same way. native.yml set no macOS minimum, so the dylibs took the build machine's.

### Choices

1. **macos-15 with `MACOSX_DEPLOYMENT_TARGET=14.0`**: the libraries still load on macOS 14, and CI checks each dylib's
   `minos`.
2. macos-15 alone: FrameReader.Native's next release needs macOS 15.
3. macos-latest with the deployment target pinned: the image can change under CI unannounced.

### Answer

**1** (the owner, 2026-10-30). A new cache key rebuilds FFmpeg with macOS 15's toolchain; the next FrameReader release
carries it (changes/framereader).

Since 2026-10-31 ([2026-10-31-pinned-windows-and-macos-runners.md](2026-10-31-pinned-windows-and-macos-runners.md)) the
AOT and release binaries are on macos-15 too.
