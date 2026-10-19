### New

- `FrameDecoder.ChromaLocation`, `ColorSpace` and `ColorRange`: the current frame's chroma location, colour matrix and
  range, with ffprobe's names (its frame entries), null when unspecified. They are what the decoder gave, which can
  differ from `VideoStreamInfo`'s, the container's header when it has one (an h264 stream's own tags win in the frames).
