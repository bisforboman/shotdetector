### New

- FrameReader: `MediaProbe.ProbeJson(path)`, ffprobe's `-print_format json -show_format -show_streams` (ported from
  ffprobe 8.1): the same keys and values, value types included, with every field ffprobe prints (profile, level,
  aspect ratios, colour, field order, disposition, tags, side data, decoder options, ...) and unknown values left out
  as ffprobe's JSON does (issue #193).
