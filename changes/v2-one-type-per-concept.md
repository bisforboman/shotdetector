### Breaking changes

- `IYadif` is gone: `DetectionOptions.Yadif` takes FrameReader's `IRowDeinterlacer`, which ShotDetector.FastYuv's
  `Yadif` implements (so a FrameReader `FrameDecoder` takes it too). `Yadif = new Yadif()` reads as before.
- `ReferenceToneSegment` is gone: `FindReferenceTone` and `DetectionResult.ReferenceTone` give FrameReader's
  `ToneSegment` (the same fields). `ReferenceToneMinDuration` and `ReferenceToneSearchDuration` are
  `DetectionOptions.ReferenceTone`, FrameReader's `ReferenceToneOptions` (the same defaults, with `Frequency` and
  `MaxGap` too): `ReferenceTone = new() { SearchDuration = TimeSpan.FromMinutes(3) }`.
