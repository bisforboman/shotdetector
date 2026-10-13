### Breaking changes

- ShotDetector's `StreamInfo`, `StreamType` and `FieldOrder` are gone: `VideoInfo` and `VideoReader` use
  FrameReader's types of the same names, so code using both packages is no longer ambiguous (CS0104, issue #194).
  `StreamInfo.Duration` is FrameReader's exact value (`DurationPts` in `TimeBase`), no longer rounded to ffprobe's
  six decimals.

### New

- FrameReader: `StreamInfo` has a public constructor, for filling one from another source.
