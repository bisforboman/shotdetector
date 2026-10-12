### New

- ShotDetector's `StreamInfo`, `StreamType` and `FieldOrder` are `[Obsolete]`: ShotDetector 2.0 uses FrameReader's
  types of the same names instead, since both packages defining them makes code using both ambiguous (CS0104,
  issue #194). Until then, `FrameReader.MediaProbe.Probe(path)` gives FrameReader's, or alias them
  (`using StreamInfo = FrameReader.StreamInfo;`).
