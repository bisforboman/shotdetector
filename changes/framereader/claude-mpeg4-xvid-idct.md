### New

- `VideoStreamInfo.ExtraData` (the codec's extradata, as ffprobe's `-show_data` prints it) and
  `ProbeOptions.FirstVideoPacket` / `MediaInfo.FirstVideoPacket` (the first video packet's bytes, reading up to that
  packet only): what a decoder sees first, e.g. an MPEG-4 Part 2 stream's encoder signature.
