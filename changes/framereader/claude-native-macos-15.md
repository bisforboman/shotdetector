### Fixed

- `FrameReader.Native.osx-arm64` is built on macOS 15, as GitHub retires its macOS 14 build machines on 2026-11-02
  (their brownouts already cancel builds). Its FFmpeg libraries still load on macOS 14 and newer; CI checks that.
