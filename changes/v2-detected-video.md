### Breaking changes

- `DetectionResult.Video` is a `DetectedVideo`: the video as the detection read it, immutable, with no hidden
  probing. `VideoReader` is internal. Renamed on the way: `Width`/`Height` (the analysed size) are
  `ProcessedWidth`/`ProcessedHeight`, and `SourceWidth`/`SourceHeight` are `Width`/`Height`, so `Width` always means
  the video's own; `Fps` is `FrameRate`; `Deinterlaces`, `DecodesInProcess` and `Streaming` are `Deinterlaced`,
  `DecodedInProcess` and `Streamed`. `Info`, `Streams`, `HasAudio` and `Pipeline` are gone: `ShotDetection.Probe` (or
  FrameReader's `MediaProbe.Probe`) gives the streams.
