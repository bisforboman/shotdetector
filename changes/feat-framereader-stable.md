### New

- **FrameReader is stable** (1.5.0): under semantic versioning with package validation, like ShotDetector, and
  ShotDetector depends on the `FrameReader` package instead of carrying its own copy of FrameReader.dll, so its XML
  documentation (IntelliSense) comes with it (issue #81).
- `ReferenceToneOptions.SearchDuration`, `DetectionOptions.ReferenceToneSearchDuration` and the CLI's `--tone-search`:
  search only the first minutes for reference tone, then stop decoding (issue #81).
- `FFmpegLibraries.HasEncoder`, `HasFilter` and `HasMuxer`: check a system FFmpeg build at startup; FrameReader's
  README lists what each reader and writer needs from one (issue #81).
