# ShotDetector's API may change for the frame-reader split (2026-10-08)

While analysing the frame-reader library (docs/frame-reader-library.md), the user: the frozen API "isn't that
important yet. No one is using this but me, yet." So the extraction may change ShotDetector's public API where that
makes the split cleaner (a 2.0.0 under semantic versioning, with package validation's baseline moved); its results
must still not change.
