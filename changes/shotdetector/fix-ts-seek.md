### Fixed

- A start time (`StartTime`, `-s`) in an MPEG-TS file lost frames: the seek, which in a file without an index can
  land after the wanted frame (at the next keyframe, up to a GOP late, or past the last one: no frames at all), now
  starts over from further back when it does, on both decoders. The results match scenedetect's again.
