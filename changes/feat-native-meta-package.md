### New

- `ShotDetector.Native`: a meta-package depending on every platform's `ShotDetector.Native.<rid>` at its version, so
  one reference covers Windows, Linux (x64, ARM64, musl) and macOS; a publish for one runtime still copies only its
  libraries (issue #244).
