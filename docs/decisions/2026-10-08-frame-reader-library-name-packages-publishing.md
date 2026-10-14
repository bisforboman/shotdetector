# Frame-reader library: name, packages, publishing (2026-10-08)

Asked before starting the split (docs/frame-reader-library.md), recommended option first each time:

- Name: **FrameReader** (project, namespace; package id later), over ShotDetector.Video and FastFrames.
- Native libraries: **keep ShotDetector.Native** (the library loads from the same files), over new
  FrameReader.Native.<rid> packages.
- Publishing: **internal until the API settles** (ShotDetector's package carries FrameReader.dll), over a prerelease
  package from the start.
- **Build our own** over contributing to FFMediaToolkit (FFmpeg 7, no macOS, no native packages).
